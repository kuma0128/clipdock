import QuickLook
import SwiftUI
import UniformTypeIdentifiers

@main
struct ClipDockApp: App {
    @State private var downloads = Downloads()
    var body: some Scene {
        WindowGroup { ContentView(downloads: downloads) }
    }
}

private struct ContentView: View {
    let downloads: Downloads
    @Environment(\.scenePhase) private var scenePhase
    @State private var preview: URL?

    var body: some View {
        NavigationStack {
            List {
                Section {
                    VStack(alignment: .leading, spacing: 12) {
                        Label("リンクから保存", systemImage: "arrow.down.to.line")
                            .font(.title3.bold())
                        Text("動画や音声のリンクをコピーして、ペーストすると保存が始まります。")
                            .font(.subheadline).foregroundStyle(.secondary)
                        PasteButton(supportedContentTypes: [.url, .plainText], payloadAction: paste)
                            .labelStyle(.titleAndIcon)
                            .disabled(downloads.busy)
                            .accessibilityIdentifier("pasteLink")
                    }
                    .padding(.vertical, 8)
                    if downloads.busy {
                        DownloadProgress(downloads: downloads)
                    }
                    if let error = downloads.error {
                        Label(error, systemImage: "exclamationmark.circle")
                            .font(.subheadline).foregroundStyle(.red)
                    }
                }
                Section {
                    if downloads.files.isEmpty {
                        Text("保存したファイルがここに表示されます。")
                            .font(.subheadline).foregroundStyle(.secondary)
                    }
                    ForEach(downloads.files, id: \.self) { file in
                        HStack {
                            Button {
                                preview = file
                            } label: {
                                Label(file.lastPathComponent, systemImage: "play.circle")
                                    .multilineTextAlignment(.leading)
                            }
                            .buttonStyle(.borderless)
                            Spacer()
                            ShareLink(item: file) { Image(systemName: "square.and.arrow.up") }
                                .buttonStyle(.borderless)
                                .accessibilityLabel("共有")
                        }
                    }
                } header: {
                    Text("保存したファイル")
                } footer: {
                    Text("「ファイル」アプリの「このiPhone内」→「ClipDock」からも開けます。")
                }
            }
            .navigationTitle("ClipDock")
            .quickLookPreview($preview)
            .onChange(of: scenePhase) { _, phase in
                if phase == .active { downloads.refresh() }
            }
        }
    }

    private func paste(_ providers: [NSItemProvider]) {
        guard let provider = providers.first else { return }
        if provider.canLoadObject(ofClass: NSURL.self) {
            provider.loadObject(ofClass: NSURL.self) { object, _ in
                let text = (object as? URL)?.absoluteString ?? ""
                Task { @MainActor in downloads.paste(text) }
            }
        } else {
            provider.loadObject(ofClass: NSString.self) { object, _ in
                let text = object as? String ?? ""
                Task { @MainActor in downloads.paste(text) }
            }
        }
    }
}

private struct DownloadProgress: View {
    let downloads: Downloads

    var body: some View {
        HStack {
            Text(downloads.status)
            Spacer()
            Button("キャンセル", action: downloads.cancel)
        }
        if let percent = downloads.percent {
            ProgressView(value: Double(percent), total: 100)
        } else {
            ProgressView()
        }
    }
}
