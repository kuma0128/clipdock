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
                        Label("Save from a link", systemImage: "arrow.down.to.line")
                            .font(.title3.bold())
                        Text("Copy a video or audio link, then tap Paste to save it.")
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
                        Text("Your saved files will appear here.")
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
                                .accessibilityLabel(Text("Share \(file.lastPathComponent)"))
                        }
                    }
                } header: {
                    Text("Saved files")
                } footer: {
                    Text("You can also open these files in Files → On My iPhone → ClipDock.")
                }
            }
            .navigationTitle(Text(verbatim: "ClipDock"))
            .tint(Color(uiColor: .link))
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
            Button("Cancel", action: downloads.cancel)
        }
        if let percent = downloads.percent {
            ProgressView(value: Double(percent), total: 100)
        } else {
            ProgressView()
        }
    }
}
