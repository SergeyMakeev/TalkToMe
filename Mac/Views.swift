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
            if !app.insertionDetails.isEmpty {
                Button("Copy insertion details") { app.copy(app.insertionDetails) }.font(.caption)
            }
            if !app.prefs.review && !app.accessibility.trusted {
                HStack {
                    Text("macOS has not confirmed access for this running copy.")
                    Spacer()
                    Button("Enable insertion…") { app.openAccessibility() }
                }
            }
            Text("Source transcript").font(.headline)
            ScrollView { Text(app.source).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading).padding(6) }
                .frame(minHeight: 50, maxHeight: 120).border(Color.secondary.opacity(0.3))
            Text("Dictation result").font(.headline)
            TextEditor(text: $app.english).font(.body).border(Color.secondary.opacity(0.3))
            HStack {
                Button("Copy result") { app.copy(app.english) }.disabled(app.english.isEmpty)
                Button("Copy transcript") { app.copy(app.source) }.disabled(app.source.isEmpty)
                Spacer()
                Button("Retry processing") { app.retry() }.disabled(!app.canRetry)
                Button("Cancel") { app.cancel() }.disabled(app.phase == .idle)
            }
            Button("Copy debug report") { app.copyDebugReport() }
        }.padding(20).frame(minWidth: 540, minHeight: 360)
        .sheet(isPresented: $app.settingsOpen) { PreferencesView(app: app, preferences: app.prefs) }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in app.refreshPermissions() }
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
    @State private var confirmAccessReset = false
    @State private var resettingAccess = false
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
    private var downloadMB: Int {
        let bytes = ((try? p.requiredModels()) ?? []).filter { !$0.installed }.reduce(Int64(0)) { $0 + $1.size }
        return Int(ceil(Double(bytes) / 1_000_000))
    }
    private func installAndSave(verifyOnly: Bool = false) {
        guard download == nil else { return }
        var draft = p
        draft.glossary = glossary.components(separatedBy: .newlines).map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        if draft.prompt == standard { draft.prompt = "" }
        download = Task { @MainActor in
            defer { download = nil }
            do {
                try draft.validate()
                for asset in try draft.requiredModels() where verifyOnly || !asset.installed {
                    feedback = "\(asset.installed ? "Checking" : "Downloading") \(asset.name)…"
                    _ = try await asset.install { percent in
                        Task { @MainActor in
                            if download != nil && feedback.contains(asset.name) { feedback = "Downloading \(asset.name) · \(percent)%" }
                        }
                    }
                }
                try Task.checkCancellation()
                if verifyOnly { feedback = "Selected models verified." }
                else { try app.save(draft, key: draft.translation == .server ? key : "", remove: draft.translation == .server && remove) }
            } catch { feedback = Task.isCancelled ? "Download cancelled." : error.localizedDescription }
        }
    }
    private var models: some View {
        Form {
            if let small = ModelDownload.catalog["base"], let large = ModelDownload.catalog["speech"] {
                Picker("Listen with", selection: $p.whisper) {
                    Text("Whisper Base · smaller and faster · 61 MB").tag(small.path)
                    Text("Whisper Large v3 Turbo · 575 MB").tag(large.path)
                    if let parakeet = ModelDownload.catalog["parakeet"] { Text("Parakeet TDT v3 · 714 MB").tag(parakeet.path) }
                    if p.whisper != small.path && p.whisper != large.path && !p.parakeet { Text("Custom speech model").tag(p.whisper) }
                }
            }
            Picker("Translate and edit with", selection: $p.translation) {
                ForEach(Translation.allCases, id: \.self) { Text($0.label).tag($0) }
            }
            Text("For accuracy, try Whisper Large v3 Turbo and Qwen 2B or 4B. Qwen 0.8B can miss words and instructions.").font(.caption).foregroundStyle(.secondary)
            if !p.processText { Text("Text processing is off. The original transcript is inserted without an editing model.").font(.caption).foregroundStyle(.secondary) }
            if p.translation == .translategemma {
                Text("Russian → English translation. English stays unchanged. Editing instructions and glossary are not applied.").font(.caption).foregroundStyle(.secondary)
                Link("TranslateGemma license (Gemma terms)", destination: URL(string: "https://ai.google.dev/gemma/terms")!)
            }
            if p.parakeet { Link("Parakeet by NVIDIA · CC BY 4.0", destination: URL(string: "https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3")!) }
            if downloadMB > 0 { Text("Download \(downloadMB) MB once to use these models offline.") }
            else if let assets = try? p.requiredModels(), !assets.isEmpty {
                Text("Selected models are ready for offline use.")
                Button("Verify downloaded models") { installAndSave(verifyOnly: true) }
            }
        }
    }
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("TalkToMe Settings · \(AccessibilityAccess.version)").font(.title2.bold())
            TabView {
                models.tabItem { Text("Models") }
                Form {
                    Picker("Microphone", selection: $p.microphone) {
                        Text("System default").tag("")
                        ForEach(devices, id: \.uniqueID) { Text($0.localizedName).tag($0.uniqueID) }
                        if !p.microphone.isEmpty && !devices.contains(where: { $0.uniqueID == p.microphone }) {
                            Text("Unavailable microphone").tag(p.microphone)
                        }
                    }
                    Picker("Spoken language", selection: $p.language) {
                        Text("English / Russian").tag("auto"); Text("English").tag("en"); Text("Russian").tag("ru")
                    }
                    LabeledContent("Recording shortcut") {
                        TextField("Recording shortcut", text: $p.shortcut).labelsHidden()
                        Menu("Presets") {
                            ForEach([Preferences.defaultRecordingShortcut, "Control+Shift+Space", "Control+Option+F9", "Ctrl+Numpad0"], id: \.self) { shortcut in
                                Button(shortcut) { p.shortcut = shortcut }
                            }
                        }.fixedSize()
                    }
                    TextField("Cancel shortcut", text: $p.cancelShortcut)
                    Text("Option+Space works without Fn or a numeric keypad. Choose another shortcut if it is already in use.").foregroundStyle(.secondary)
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
                        Button("Accessibility access…") { app.openAccessibility() }
                    }
                    Text(app.accessibility.message).foregroundStyle(.secondary)
                    if !app.accessibility.trusted || !app.accessibility.canPostEvents {
                        Text("If the switch is already on, remove TalkToMe with the minus button, add this copy again, and quit and reopen it. Preview updates can invalidate an earlier approval.").foregroundStyle(.secondary)
                        Button(resettingAccess ? "Resetting approval…" : "Reset TalkToMe’s Accessibility approval…") { confirmAccessReset = true }
                            .disabled(resettingAccess)
                    }
                    HStack {
                        Button("Recheck access") { app.refreshPermissions() }
                        Button("Show this app") { NSWorkspace.shared.activateFileViewerSelecting([Bundle.main.bundleURL]) }
                        Button("Copy access diagnostics") {
                            app.refreshPermissions()
                            app.copy(app.accessibility.diagnostics)
                            feedback = "Copied app identity and permission status."
                        }
                    }
                }.tabItem { Text("Dictation") }
                Form {
                    path("Speech model", $p.whisper)
                    if !p.parakeet {
                        path("Speech detector", $p.vad)
                        Text("Leave the speech detector empty to disable silence filtering.").foregroundStyle(.secondary)
                        Stepper("CPU threads · \(p.threads)", value: $p.threads, in: 1...128)
                    } else {
                        Text("Parakeet detects the language automatically and manages its CPU threads. Whisper’s speech detector and glossary prompt are not used.").foregroundStyle(.secondary)
                    }
                    Toggle("Use Metal acceleration", isOn: $p.useMetal)
                    Button("Use recommended speech models") {
                        p.whisper = ModelDownload.catalog["base"]?.path ?? ""
                        p.vad = ModelDownload.catalog["activity"]?.path ?? ""
                    }
                }.tabItem { Text("Speech") }
                if p.translation == .server { Form {
                    TextField("Server URL (including /v1)", text: $p.serverURL)
                        .onChange(of: p.serverURL) { _ in key = ""; remove = false }
                    TextField("Served model or alias", text: $p.model)
                    SecureField("New API key", text: $key).disabled(remove)
                    Toggle("Remove saved key for this server", isOn: $remove)
                    Text("Leave the key blank to keep its Keychain entry.").foregroundStyle(.secondary)
                    TextField("Request timeout (seconds)", value: $p.timeout, format: .number.grouping(.never))
                    Toggle("Start a local llama-server when needed", isOn: $p.manageServer)
                    Group {
                        path("Custom llama-server (optional)", $p.executable); path("Translation model (GGUF)", $p.gguf)
                        TextField("Context size", value: $p.contextSize, format: .number.grouping(.never))
                        TextField("GPU layers (all or a number)", text: $p.gpuLayers)
                    }.disabled(!p.manageServer)
                }.tabItem { Text("Server") } }
                VStack(alignment: .leading) {
                    Picker("Text processing", selection: $p.processing) {
                        ForEach(TextProcessing.allCases, id: \.self) { Text($0.label).tag($0) }
                    }
                    Text(!p.processText ? "Keep the original transcript in the spoken language. No editing model runs or downloads." :
                        p.translation == .translategemma ? "TranslateGemma only translates. Choose Qwen or your own server for Light/Heavy cleanup and the engineering dictionary." :
                        !p.prompt.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && p.prompt != standard ? "Custom instructions override Light/Heavy rules and the engineering dictionary. Restore default to use them." :
                        p.processing == .heavy ? "Organize speech into clear English paragraphs or lists, preserving details and uncertainty. Use digits for quantities. Qwen 4B performed better than 2B in our restructuring checks." :
                        "Fix grammar, word order, fillers and repetitions. Keep the sequence of ideas. Use digits for quantities and conventional technical spellings.")
                        .font(.caption).foregroundStyle(.secondary)
                    Toggle("Use built-in engineering dictionary", isOn: $p.engineeringTerms)
                        .disabled(!p.processText || p.translation == .translategemma || (!p.prompt.isEmpty && p.prompt != standard))
                    HStack {
                        Text("Advanced instructions").font(.headline)
                        Spacer(); Button("Restore default") { p.prompt = standard }
                    }
                    TextEditor(text: $p.prompt).border(Color.secondary.opacity(0.3)).disabled(!p.processText || p.translation == .translategemma)
                    Text("Personal glossary · one term per line").font(.headline)
                    TextEditor(text: $glossary).border(Color.secondary.opacity(0.3))
                    LabeledContent("Temperature · \(p.temperature, specifier: "%.2f")") { Slider(value: $p.temperature, in: 0...2, step: 0.01) }
                }.padding().tabItem { Text("Editing") }
                VStack(alignment: .leading, spacing: 16) {
                    Text("Debug information").font(.headline)
                    Text("Copy versions, model selections, permission status, recent processing timings and available macOS crash backtraces. You can reopen TalkToMe after a crash and copy the report here.")
                    Button("Copy debug report") { app.copyDebugReport(); feedback = "Debug report copied." }
                    Button("Open diagnostics folder") { Diagnostics.openFolder() }
                    Button("Open macOS crash reports") { Diagnostics.openCrashReports() }
                    Text("Reports exclude dictation, audio, prompts and credentials. Original macOS crash files may include additional private information; they are never uploaded automatically.").font(.caption).foregroundStyle(.secondary)
                    Spacer()
                }.padding().tabItem { Text("Diagnostics") }
            }.formStyle(.grouped).disabled(download != nil)
            HStack {
                if download != nil { ProgressView().controlSize(.small) }
                Text(feedback).foregroundStyle(.secondary).textSelection(.enabled)
                Spacer()
                Button(download == nil ? "Cancel" : "Cancel download") {
                    if let download { download.cancel() } else { app.settingsOpen = false }
                }.keyboardShortcut(.cancelAction)
                Button(downloadMB > 0 ? "Download and save" : "Save") { installAndSave() }
                    .keyboardShortcut(.defaultAction).disabled(download != nil)
            }
        }.padding(18).frame(width: 680, height: 580)
        .onDisappear { download?.cancel() }
        .confirmationDialog("Reset TalkToMe’s Accessibility approval?", isPresented: $confirmAccessReset, titleVisibility: .visible) {
            Button("Reset approval", role: .destructive) {
                resettingAccess = true
                Task { @MainActor in
                    defer { resettingAccess = false }
                    do {
                        try await AccessibilityAccess.resetApproval()
                        feedback = "Approval reset. Enable TalkToMe in Accessibility, then quit and reopen the app."
                        app.openAccessibility()
                    } catch { feedback = error.localizedDescription }
                }
            }
            Button("Cancel", role: .cancel) { }
        } message: {
            Text("Clears only TalkToMe’s saved Accessibility approval. You will need to enable access again in System Settings.")
        }
    }
}
