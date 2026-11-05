import SwiftUI
import AVFoundation

@MainActor final class Dictation: ObservableObject {
    enum Phase { case idle, recording, processing }
    @Published private(set) var phase = Phase.idle { didSet { refreshMenu?() } }
    @Published private(set) var prefs = Preferences()
    @Published private(set) var status = "Ready. Focus a text field and use the recording shortcut."
    @Published private(set) var source = ""
    @Published var english = ""
    @Published var settingsOpen = false
    var refreshMenu: (() -> Void)?
    private let keys = Hotkeys()
    private var speech = SpeechEngine()
    private let editor = EnglishEditor()
    private var mic: Capture?
    private var target: Destination?
    private var audio: [Float] = []
    private var language = ""
    private var pressed = false
    private var revision = UUID()
    private var task: Task<Void, Never>?
    private var job: SpeechJob?
    private var timer: Timer?
    private var observer: NSObjectProtocol?
    private let tones = ["start","stop"].map { name in
        Bundle.main.url(forResource: name, withExtension: "wav").flatMap { NSSound(contentsOf: $0, byReference: true) }
    }
    var canRetry: Bool { phase == .idle && !audio.isEmpty }
    init() {
        do { let saved = try Preferences.load(); try saved.validate(); prefs = saved }
        catch { status = error.localizedDescription }
        do { try keys.setRecord(prefs.shortcut) } catch { status = error.localizedDescription }
        keys.action = { [weak self] id, down in self?.handleKey(id, down) }
        observer = NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didActivateApplicationNotification,
            object: nil, queue: .main) { [weak self] notification in
                let app = notification.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication
                MainActor.assumeIsolated {
                    if self?.target?.pid != app?.processIdentifier { self?.target = nil }
                }
            }
    }
    private func handleKey(_ id: UInt32, _ down: Bool) {
        if id == 2 { if down { cancel() }; return }
        guard !settingsOpen, pressed != down else { return }
        pressed = down
        switch phase {
        case .idle where down: start()
        case .recording where prefs.toggle == down: finish()
        default: break
        }
    }
    func configure() {
        guard phase == .idle else { return }
        pressed = false
        settingsOpen = true
    }
    func save(_ p: Preferences, key: String, remove: Bool) throws {
        try p.validate()
        do {
            try keys.setRecord(p.shortcut)
            try keys.enableCancel(p.cancelShortcut)
            keys.disableCancel()
            if remove || !key.isEmpty { try Secrets.write(remove ? "" : key, account: p.keyAccount) }
            try p.save()
        } catch {
            keys.disableCancel()
            try? keys.setRecord(prefs.shortcut)
            throw error
        }
        if p.serverURL != prefs.serverURL || p.manageServer != prefs.manageServer || p.executable != prefs.executable ||
            p.gguf != prefs.gguf || p.model != prefs.model || p.contextSize != prefs.contextSize || p.gpuLayers != prefs.gpuLayers ||
            remove || !key.isEmpty { editor.stop() }
        if p.whisper != prefs.whisper || p.useMetal != prefs.useMetal { speech = SpeechEngine() }
        prefs = p
        status = "Settings saved."
        settingsOpen = false
    }
    private func cue(_ index: Int) {
        if prefs.sounds, let tone = tones[index] { tone.stop(); tone.play() }
    }
    private func start() {
        let capture = Capture()
        let id = UUID()
        let destination = prefs.review ? nil : Destination.capture()
        do {
            try keys.enableCancel(prefs.cancelShortcut)
            try capture.start(deviceID: prefs.microphone, minutes: prefs.minutes) { [weak self] in
                guard let self, self.revision == id, self.phase == .recording else { return }
                self.cancel()
                self.status = "Microphone disconnected. Check the input in Settings."
            }
            revision = id
            mic = capture
            target = destination
            audio.removeAll()
            source = ""; english = ""
            phase = .recording
            status = "Recording · \(prefs.toggle ? "Press the shortcut again" : "Release the shortcut") to finish."
            cue(0)
            timer = Timer.scheduledTimer(withTimeInterval: Double(prefs.minutes * 60), repeats: false) { [weak self] _ in
                Task { @MainActor in if self?.revision == id { self?.finish() } }
            }
        } catch {
            keys.disableCancel()
            status = error.localizedDescription
            refreshMenu?()
        }
    }
    private func stopMicrophone() throws -> [Float] {
        timer?.invalidate(); timer = nil
        guard let mic else { return [] }
        self.mic = nil
        defer { cue(1) }
        return try mic.stop()
    }
    func finish() {
        guard phase == .recording else { return }
        do {
            audio = try stopMicrophone()
            process(insert: true)
        } catch {
            keys.disableCancel()
            phase = .idle
            status = error.localizedDescription
        }
    }
    func retry() {
        guard canRetry else { return }
        do { try keys.enableCancel(prefs.cancelShortcut); process(insert: false) }
        catch { status = error.localizedDescription }
    }
    private func process(insert: Bool) {
        let id = UUID()
        revision = id
        phase = .processing
        let work = SpeechJob()
        job = work
        let p = prefs
        task = Task {
            defer {
                if revision == id { keys.disableCancel(); target = nil; phase = .idle; task = nil; job = nil }
            }
            do {
                if source.isEmpty || p.language != language {
                    status = "Transcribing speech…"
                    let result = try await speech.recognize(audio, p, work)
                    try Task.checkCancellation()
                    source = result; language = p.language; english = ""
                }
                guard !source.isEmpty else { audio.removeAll(); status = "No speech detected."; return }
                status = "Writing English with llama-server…"
                let text = try await editor.edit(source, p)
                try Task.checkCancellation()
                english = text
                if insert, let destination = target,
                   try await destination.insert(text, valid: { self.target != nil && self.revision == id }) {
                    status = "Text sent to the focused field."
                } else { status = "Ready to review and copy." }
            } catch { if revision == id { status = error.localizedDescription } }
        }
    }
    func cancel() {
        guard phase != .idle else { return }
        revision = UUID()
        job?.cancel(); task?.cancel(); task = nil; job = nil
        _ = try? stopMicrophone()
        target = nil; audio.removeAll(); source = ""; english = ""
        keys.disableCancel()
        phase = .idle; status = "Cancelled."
    }
    func copy(_ text: String) {
        guard !text.isEmpty else { return }
        let board = NSPasteboard.general
        board.clearContents(); board.setString(text, forType: .string)
        status = "Copied."
    }
    func quit() {
        cancel()
        editor.stop()
        if let observer { NSWorkspace.shared.notificationCenter.removeObserver(observer) }
    }
}
