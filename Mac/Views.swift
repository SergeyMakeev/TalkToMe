import SwiftUI
import AVFoundation

struct Panel: View {
    @ObservedObject var app: Dictation
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Label(app.prefs.shortcut, systemImage: "waveform").font(.title3.bold())
                Spacer()
                Button("Settings…") { app.configure() }.disabled(app.phase != .idle)
            }
            Text(app.status).foregroundStyle(.secondary).textSelection(.enabled)
            Text("Source transcript").font(.headline)
            ScrollView { Text(app.source).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading).padding(6) }
                .frame(minHeight: 50, maxHeight: 120).border(Color.secondary.opacity(0.3))
            Text("Written English").font(.headline)
            TextEditor(text: $app.english).font(.body).border(Color.secondary.opacity(0.3))
            HStack {
                Button("Copy English") { app.copy(app.english) }.disabled(app.english.isEmpty)
                Button("Copy transcript") { app.copy(app.source) }.disabled(app.source.isEmpty)
                Spacer()
                Button("Retry processing") { app.retry() }.disabled(!app.canRetry)
                Button("Cancel") { app.cancel() }.disabled(app.phase == .idle)
            }
        }.padding(20).frame(minWidth: 540, minHeight: 360)
        .sheet(isPresented: $app.settingsOpen) { PreferencesView(app: app, preferences: app.prefs) }
    }
}

struct PreferencesView: View {
    @ObservedObject var app: Dictation
    @State private var p: Preferences
    @State private var glossary: String
    @State private var key = ""
    @State private var remove = false
    @State private var feedback = ""
    @State private var download: Task<Void, Never>?
    @State private var devices = Capture.devices
    private let standard = (try? EnglishEditor.defaultPrompt) ?? ""
    init(app: Dictation, preferences: Preferences) {
        self.app = app
        var draft = preferences
        if draft.prompt.isEmpty { draft.prompt = (try? EnglishEditor.defaultPrompt) ?? "" }
        _p = State(initialValue: draft)
        _glossary = State(initialValue: preferences.glossary.joined(separator: "\n"))
    }
    func path(_ title: String, _ binding: Binding<String>) -> some View {
        LabeledContent(title) {
            TextField(title, text: binding).labelsHidden()
            Button("Choose…") {
                let picker = NSOpenPanel()
                picker.canChooseDirectories = false
                if picker.runModal() == .OK, let url = picker.url { binding.wrappedValue = url.path }
            }
        }
    }
    func privacy(_ pane: String) {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_" + pane) { NSWorkspace.shared.open(url) }
    }
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("TalkToMe Settings").font(.title2.bold())
            TabView {
                Form {
                    Picker("Microphone", selection: $p.microphone) {
                        Text("System default").tag("")
                        ForEach(devices, id: \.uniqueID) { Text($0.localizedName).tag($0.uniqueID) }
                        if !p.microphone.isEmpty && !devices.contains(where: { $0.uniqueID == p.microphone }) {
                            Text("Unavailable microphone").tag(p.microphone)
                        }
                    }
                    Picker("Spoken language", selection: $p.language) {
                        Text("Automatic").tag("auto"); Text("English").tag("en"); Text("Russian").tag("ru")
                    }
                    TextField("Recording shortcut", text: $p.shortcut)
                    TextField("Cancel shortcut", text: $p.cancelShortcut)
                    Stepper("Recording limit · \(p.minutes) min", value: $p.minutes, in: 1...60)
                    Toggle("Press to toggle recording", isOn: $p.toggle)
                    Toggle("Play start and end tones", isOn: $p.sounds)
                    Toggle("Review each result before inserting", isOn: $p.review)
                    HStack {
                        Button("Microphone access…") {
                            if AVCaptureDevice.authorizationStatus(for: .audio) == .notDetermined {
                                AVCaptureDevice.requestAccess(for: .audio) { _ in DispatchQueue.main.async { devices = Capture.devices } }
                            } else { privacy("Microphone") }
                        }
                        Button("Accessibility access…") {
                            if !AXIsProcessTrusted() { _ = AXIsProcessTrustedWithOptions(["AXTrustedCheckOptionPrompt": true] as CFDictionary) }
                            privacy("Accessibility")
                        }
                    }
                }.tabItem { Text("Dictation") }
                Form {
                    path("Whisper GGML model", $p.whisper)
                    path("Speech detector", $p.vad)
                    Text("Leave the speech detector empty to disable silence filtering.").foregroundStyle(.secondary)
                    Toggle("Use Metal acceleration", isOn: $p.useMetal)
                    Stepper("CPU threads · \(p.threads)", value: $p.threads, in: 1...128)
                    Button(download == nil ? "Download recommended models (575 MB)" : "Cancel download") {
                        if let download { download.cancel(); return }
                        download = Task { @MainActor in
                            defer { download = nil }
                            do {
                                feedback = "Downloading Whisper…"
                                let speech = try await ModelDownload.speech.install()
                                feedback = "Downloading the speech detector…"
                                let vad = try await ModelDownload.activity.install()
                                try Task.checkCancellation()
                                p.whisper = speech; p.vad = vad
                                feedback = "Models verified. Save to use them."
                            } catch { feedback = Task.isCancelled ? "Download cancelled." : error.localizedDescription }
                        }
                    }
                }.tabItem { Text("Speech") }
                Form {
                    TextField("Server URL (including /v1)", text: $p.serverURL)
                        .onChange(of: p.serverURL) { _ in key = ""; remove = false }
                    TextField("Served model or alias", text: $p.model)
                    SecureField("New API key", text: $key).disabled(remove)
                    Toggle("Remove saved key for this server", isOn: $remove)
                    Text("Leave the key blank to keep its Keychain entry.").foregroundStyle(.secondary)
                    TextField("Request timeout (seconds)", value: $p.timeout, format: .number.grouping(.never))
                    Toggle("Start a local llama-server when needed", isOn: $p.manageServer)
                    Group {
                        path("llama-server executable", $p.executable); path("Qwen GGUF", $p.gguf)
                        TextField("Context size", value: $p.contextSize, format: .number.grouping(.never))
                        TextField("GPU layers (all or a number)", text: $p.gpuLayers)
                    }.disabled(!p.manageServer)
                }.tabItem { Text("Server") }
                VStack(alignment: .leading) {
                    HStack {
                        Text("Editing instructions").font(.headline)
                        Spacer(); Button("Restore default") { p.prompt = standard }
                    }
                    TextEditor(text: $p.prompt).border(Color.secondary.opacity(0.3))
                    Text("Glossary · one term per line").font(.headline)
                    TextEditor(text: $glossary).border(Color.secondary.opacity(0.3))
                    LabeledContent("Temperature · \(p.temperature, specifier: "%.2f")") { Slider(value: $p.temperature, in: 0...2, step: 0.01) }
                }.padding().tabItem { Text("Editing") }
            }.formStyle(.grouped)
            HStack {
                Text(feedback).foregroundStyle(.secondary).textSelection(.enabled)
                Spacer()
                Button("Cancel") { app.settingsOpen = false }.keyboardShortcut(.cancelAction)
                Button("Save") {
                    do {
                        var draft = p
                        draft.glossary = glossary.components(separatedBy: .newlines).map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
                        if draft.prompt == standard { draft.prompt = "" }
                        try app.save(draft, key: key, remove: remove)
                    } catch { feedback = error.localizedDescription }
                }.keyboardShortcut(.defaultAction).disabled(download != nil)
            }
        }.padding(18).frame(width: 680, height: 580)
        .onDisappear { download?.cancel() }
    }
}
