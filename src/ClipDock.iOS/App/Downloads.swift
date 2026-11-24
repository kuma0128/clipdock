import AVFoundation
import Foundation
import Observation

private enum SaveError: LocalizedError {
    case single, format, download, storage, engine

    var errorDescription: String? {
        switch self {
        case .single: return "公開されている動画や音声を1件ずつ指定してください。"
        case .format: return "この配信形式はiPhone版で保存できません。"
        case .download: return "取得できませんでした。通信状況と、ログインせずに開けるリンクかどうかを確認してください。"
        case .storage: return "ファイルを保存できません。iPhoneの空き容量を確認してください。"
        case .engine: return "保存エンジンを起動できませんでした。アプリを入れ直してください。"
        }
    }
}

private struct MediaResult: Decodable {
    let title: String?
    let files: [String]?
    let error: String?
}

private func fetchMedia(_ link: URL, into folder: URL, task: Task<Void, Never>,
                        onProgress: @escaping @MainActor @Sendable (Int) -> Void) async throws -> MediaResult {
    let json: String? = await withCheckedContinuation { continuation in
        DispatchQueue.global(qos: .userInitiated).async {
            var previous = -2
            let result = CDDownload(link.absoluteString, folder.path) { value in
                guard !task.isCancelled else { return false }
                if value != previous {
                    DispatchQueue.main.async {
                        if !task.isCancelled { onProgress(value) }
                    }
                    previous = value
                }
                return true
            }
            // Deliver all queued progress updates before switching to file verification.
            DispatchQueue.main.async { continuation.resume(returning: result) }
        }
    }
    try Task.checkCancellation()
    guard let json, let media = try? JSONDecoder().decode(MediaResult.self, from: Data(json.utf8)) else {
        throw SaveError.engine
    }
    switch media.error {
    case nil: return media
    case "single": throw SaveError.single
    case "format": throw SaveError.format
    default: throw SaveError.download
    }
}

private func finishedMedia(_ files: [URL], in folder: URL) async throws -> URL {
    guard let first = files.first else { throw SaveError.format }
    if files.count == 1 {
        let asset = AVURLAsset(url: first)
        guard let tracks = try? await asset.load(.tracks),
              tracks.contains(where: { $0.mediaType == .video || $0.mediaType == .audio }) else { throw SaveError.format }
        return first
    }

    guard files.count == 2 else { throw SaveError.format }
    let composition = AVMutableComposition()
    for (file, type) in zip(files, [AVMediaType.video, .audio]) {
        let asset = AVURLAsset(url: file)
        guard let source = try await asset.loadTracks(withMediaType: type).first,
              let track = composition.addMutableTrack(withMediaType: type, preferredTrackID: kCMPersistentTrackID_Invalid)
        else { throw SaveError.format }
        try track.insertTimeRange(try await source.load(.timeRange), of: source, at: .zero)
        if type == .video { track.preferredTransform = try await source.load(.preferredTransform) }
    }
    let output = folder.appendingPathComponent("merged.mp4")
    guard let export = AVAssetExportSession(asset: composition, presetName: AVAssetExportPresetPassthrough),
          export.supportedFileTypes.contains(.mp4) else { throw SaveError.format }
    try Task.checkCancellation()
    do {
        try await export.export(to: output, as: .mp4)
    } catch {
        try Task.checkCancellation()
        throw SaveError.format
    }
    return output
}

@MainActor
@Observable
final class Downloads {
    private(set) var files: [URL] = []
    private(set) var status = ""
    private(set) var percent: Int?
    private(set) var error: String?
    var busy: Bool { transfer != nil }

    private let manager = FileManager.default
    private let documents = URL.documentsDirectory
    private let staging = URL.temporaryDirectory.appendingPathComponent("ClipDock")
    private var transfer: Task<Void, Never>?

    init() {
        // A force-quit can leave an unfinished transfer in the app's private temporary folder.
        try? manager.removeItem(at: staging)
        refresh()
    }

    func refresh() {
        do {
            let keys: Set<URLResourceKey> = [.isRegularFileKey, .contentModificationDateKey]
            let urls = try manager.contentsOfDirectory(at: documents, includingPropertiesForKeys: Array(keys),
                                                       options: [.skipsHiddenFiles])
            files = urls.compactMap { url -> (URL, Date)? in
                guard let values = try? url.resourceValues(forKeys: keys), values.isRegularFile == true else { return nil }
                return (url, values.contentModificationDate ?? .distantPast)
            }.sorted { $0.1 > $1.1 }.map { $0.0 }
        } catch {
            self.error = SaveError.storage.localizedDescription
        }
    }

    func paste(_ text: String) {
        guard !busy else { return }
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.rangeOfCharacter(from: .whitespacesAndNewlines) == nil,
              let link = URL(string: trimmed, encodingInvalidCharacters: false),
              ["http", "https"].contains(link.scheme?.lowercased() ?? ""),
              let host = link.host, !host.isEmpty, link.user == nil, link.password == nil else {
            error = "動画ページや動画・音声ファイルのHTTP(S)リンクをコピーしてください。"
            return
        }
        error = nil
        status = "リンクを確認中…"
        percent = nil
        transfer = Task { await save(link) }
    }

    func cancel() {
        transfer?.cancel()
        status = "キャンセル中…"
    }

    private func save(_ link: URL) async {
        guard let task = transfer else { return }
        let folder = staging.appendingPathComponent(UUID().uuidString)
        defer {
            try? manager.removeItem(at: folder)
            transfer = nil
        }
        do {
            try manager.createDirectory(at: folder, withIntermediateDirectories: true)
            let media = try await fetchMedia(link, into: folder, task: task) { value in
                self.percent = value < 0 ? nil : value
                if value >= 0 { self.status = "ダウンロード中…" }
            }
            status = "ファイルを確認中…"
            percent = nil
            let tracks = (media.files ?? []).map { folder.appendingPathComponent($0) }
            let result = try await finishedMedia(tracks, in: folder)
            try Task.checkCancellation()
            try store(result, title: media.title ?? "ClipDock")
            refresh()
        } catch {
            if !Task.isCancelled {
                self.error = (error as? SaveError ?? .storage).localizedDescription
            }
        }
    }

    private func store(_ source: URL, title: String) throws {
        let invalid = CharacterSet(charactersIn: "/:\\").union(.controlCharacters)
        var name = title.components(separatedBy: invalid).joined(separator: " ")
            .trimmingCharacters(in: .whitespacesAndNewlines.union(CharacterSet(charactersIn: ".")))
        if name.isEmpty { name = "ClipDock" }
        while name.utf8.count > 180 { name.removeLast() }
        if name.isEmpty { name = "ClipDock" }
        var number = 1
        while true {
            let suffix = number == 1 ? "" : " (\(number))"
            let destination = documents.appendingPathComponent(name + suffix).appendingPathExtension(source.pathExtension)
            do {
                try manager.moveItem(at: source, to: destination)
                return
            } catch CocoaError.fileWriteFileExists {
                number += 1
            }
        }
    }
}
