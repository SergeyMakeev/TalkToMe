import SwiftUI
import AVFoundation

@MainActor final class Dictation: ObservableObject {
    enum Phase { case idle, recording, processing }
    @Published private(set) var phase = Phase.idle { didSet { refreshMenu?() } }
    @Published private(set) var prefs = Preferences()
    @Published private(set) var status = "Ready. Focus a text field and use the recording shortcut."
    @Published private(set) var source = ""
    @Published var english = ""
    @Published private(set) var insertionDetails = ""
    @Published var settingsOpen = false
    @Published private(set) var accessibility = AccessibilityAccess.current()
    var refreshMenu: (() -> Void)?
    private let keys = Hotkeys()
    private let speech = SpeechEngine()
    private let editor = EnglishEditor()
    private var mic: Capture?
    private var target: Destination?
    private var targetApp: pid_t?
    private var targetLookup: Task<Result<Destination, InsertionProblem>, Never>?
    private var targetProblem: InsertionProblem?
    private var audio: [Float] = []
    private var language = ""
    private var pressed = false
    private var revision = UUID()
    private var task: Task<Void, Never>?
    private var job: SpeechJob?
    private var timer: Timer?
    private var observer: NSObjectProtocol?
    private var closing = false
    private let tones = ["start","stop"].map { name in
        Bundle.main.url(forResource: name, withExtension: "wav").flatMap { NSSound(contentsOf: $0, byReference: true) }
    }
    var canRetry: Bool { phase == .idle && !audio.isEmpty }
    init() {
        do { prefs = try Preferences.load(); try prefs.validate() }
        catch { Diagnostics.error("settings.load", error); status = error.localizedDescription }
        do { try keys.setRecord(prefs.shortcut) } catch { Diagnostics.error("hotkey.register", error); status = error.localizedDescription }
        keys.action = { [weak self] id, down in self?.handleKey(id, down) }
        observer = NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didActivateApplicationNotification,
            object: nil, queue: .main) { [weak self] notification in
                let app = notification.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication
                MainActor.assumeIsolated {
                    if let pid = self?.targetApp, pid != app?.processIdentifier {
                        self?.clearTarget(problem: .focusChanged)
                    }
                }
            }
    }
    private func handleKey(_ id: UInt32, _ down: Bool) {
        guard !closing else { return }
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
        guard !closing, phase == .idle else { return }
        pressed = false
        refreshPermissions()
        settingsOpen = true
    }
    func refreshPermissions() { accessibility = AccessibilityAccess.current() }
    func openAccessibility() {
        refreshPermissions()
        if !accessibility.trusted { _ = AXIsProcessTrustedWithOptions(["AXTrustedCheckOptionPrompt": true] as CFDictionary) }
        else if !accessibility.canPostEvents { _ = CGRequestPostEventAccess() }
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") { NSWorkspace.shared.open(url) }
    }
    func save(_ p: Preferences, key: String, remove: Bool) throws {
        guard !closing else { throw CancellationError() }
        try p.validate()
        do {
            try keys.setRecord(p.shortcut)
            try keys.enableCancel(p.cancelShortcut)
            keys.disableCancel()
            if remove || !key.isEmpty {
                let oldKey = try Secrets.read(p.keyAccount)
                try Secrets.write(remove ? "" : key, account: p.keyAccount)
                do { try p.save() }
                catch { try Secrets.write(oldKey, account: p.keyAccount); throw error }
            } else { try p.save() }
        } catch {
            keys.disableCancel()
            try? keys.setRecord(prefs.shortcut)
            throw error
        }
        if p.translation != prefs.translation || (p.translation.bundled && p.useMetal != prefs.useMetal) ||
            p.serverURL != prefs.serverURL || p.manageServer != prefs.manageServer || p.executable != prefs.executable ||
            p.gguf != prefs.gguf || p.model != prefs.model || p.contextSize != prefs.contextSize || p.gpuLayers != prefs.gpuLayers ||
            remove || !key.isEmpty { editor.stop() }
        // SpeechEngine reloads on its serial queue. Keep one owner so shutdown
        // also waits for work from sessions cancelled just before changing models.
        if p.whisper != prefs.whisper || p.vad != prefs.vad || p.glossary != prefs.glossary { language = "" }
        prefs = p
        status = "Settings saved."
        settingsOpen = false
    }
    private func cue(_ index: Int) {
        if !closing, prefs.sounds, let tone = tones[index] { tone.stop(); tone.play() }
    }
    private func start() {
        Diagnostics.event("recording.start", "Speech=\(Diagnostics.model(prefs)); editor=\(prefs.translation.rawValue); Metal requested=\(prefs.useMetal)")
        let capture = Capture()
        let id = UUID()
        refreshPermissions()
        let pid = NSWorkspace.shared.frontmostApplication?.processIdentifier
        Diagnostics.event("target.capture.start")
        let destination = prefs.review ? nil : Destination.capture()
        Diagnostics.event("target.capture.finished")
        let recovery = destination.flatMap { result -> FocusRecovery<Destination>? in
            guard case .failure = result, let pid else { return nil }
            return Destination.recovery(for: pid)
        }
        do {
            try prefs.validate()
            try keys.enableCancel(prefs.cancelShortcut)
            Diagnostics.event("microphone.start")
            try capture.start(deviceID: prefs.microphone, minutes: prefs.minutes) { [weak self] in
                guard let self, self.revision == id, self.phase == .recording else { return }
                self.cancel()
                Diagnostics.event("microphone.disconnected")
                self.status = "Microphone disconnected. Check the input in Settings."
            }
            revision = id
            mic = capture
            clearTarget()
            if let destination {
                targetApp = pid
                switch destination {
                case .success(let field): target = field
                case .failure(let problem): targetProblem = problem
                }
                if let recovery { targetLookup = Task { await recovery.resolve(destination) } }
            }
            audio.removeAll()
            insertionDetails = ""
            source = ""; english = ""
            phase = .recording
            status = "Recording · \(prefs.toggle ? "Press the shortcut again" : "Release the shortcut") to finish."
            cue(0)
            Diagnostics.event("recording.ready")
            timer = Timer.scheduledTimer(withTimeInterval: Double(prefs.minutes * 60), repeats: false) { [weak self] _ in
                Task { @MainActor in if self?.revision == id { self?.finish() } }
            }
        } catch {
            Diagnostics.error("recording.failed", error)
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
        Diagnostics.event("recording.stop")
        let started = ProcessInfo.processInfo.systemUptime
        do {
            audio = try stopMicrophone()
            Diagnostics.timing("audio_finalize", since: started)
            Diagnostics.event("audio.ready", String(format: "%.2f seconds", Double(audio.count) / 16000))
            process(insert: true)
        } catch {
            Diagnostics.error("recording.stop.failed", error)
            clearTarget()
            keys.disableCancel()
            phase = .idle
            status = error.localizedDescription
        }
    }
    func retry() {
        guard !closing, canRetry else { return }
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
        let lookup = targetLookup
        task = Task {
            let started = ProcessInfo.processInfo.systemUptime
            defer {
                Diagnostics.timing("processing_total", since: started)
                if revision == id { keys.disableCancel(); clearTarget(); phase = .idle; task = nil; job = nil }
            }
            do {
                if source.isEmpty || p.language != language {
                    status = "Transcribing speech…"
                    Diagnostics.event("speech.start")
                    let speechStart = ProcessInfo.processInfo.systemUptime
                    let result = try await speech.recognize(audio, p, work)
                    Diagnostics.timing("speech", since: speechStart)
                    try Task.checkCancellation()
                    source = result; language = p.language; english = ""
                }
                guard !source.isEmpty else { Diagnostics.event("processing.finished", "No speech"); audio.removeAll(); status = "No speech detected."; return }
                if p.translation != .transcript { status = "Writing English…" }
                Diagnostics.event("editing.start", p.translation.rawValue)
                let editStart = ProcessInfo.processInfo.systemUptime
                let text = try await editor.edit(source, p)
                Diagnostics.timing("editing", since: editStart)
                try Task.checkCancellation()
                english = text
                if let lookup {
                    let resolved = await lookup.value
                    try Task.checkCancellation()
                    // Switching apps cancels the lookup and clears targetApp,
                    // even if a successful result was already queued.
                    if targetApp != nil {
                        switch resolved {
                        case .success(let field): target = field; targetProblem = nil
                        case .failure(let problem): targetProblem = problem
                        }
                    }
                    targetLookup = nil
                }
                refreshPermissions()
                if p.review { status = "Review mode is on. Ready to review and copy." }
                else if !insert { status = "Reprocessed. Use Copy result to insert it." }
                else if let destination = target {
                    Diagnostics.event("insertion.start")
                    let insertStart = ProcessInfo.processInfo.systemUptime
                    insertionDetails = "TalkToMe \(AccessibilityAccess.version)\n"
                    let outcome = try await destination.insert(text, valid: { self.target != nil && self.revision == id }, report: {
                        if self.revision == id { self.insertionDetails += $0 + "\n" }
                    })
                    Diagnostics.timing("insertion", since: insertStart)
                    Diagnostics.event("insertion.finished", outcome.message)
                    if revision == id { insertionDetails += "Outcome: \(outcome.message)\n"; status = outcome.message }
                } else { status = InsertionOutcome.blocked(targetProblem ?? .focusChanged).message }
                Diagnostics.event("processing.finished", "Completed")
            } catch {
                if error is CancellationError { Diagnostics.event("processing.cancelled") }
                else { Diagnostics.error("processing.failed", error) }
                if revision == id { status = error.localizedDescription }
            }
        }
    }
    private func clearTarget(problem: InsertionProblem? = nil) {
        targetLookup?.cancel(); targetLookup = nil
        target = nil; targetApp = nil; targetProblem = problem
    }
    func cancel() {
        guard phase != .idle else { return }
        Diagnostics.event("recording.cancel")
        revision = UUID()
        job?.cancel(); task?.cancel(); task = nil; job = nil
        _ = try? stopMicrophone()
        clearTarget(); audio.removeAll(); source = ""; english = ""
        keys.disableCancel()
        phase = .idle; status = "Cancelled."
    }
    func copy(_ text: String) {
        guard !text.isEmpty else { return }
        let board = NSPasteboard.general
        board.clearContents(); board.setString(text, forType: .string)
        status = "Copied."
    }
    func copyDebugReport() { copy(Diagnostics.report(prefs)) }
    // Exercise the real app owner and Quit path without requesting microphone access.
    func checkShutdown(_ files: [String], cancelInference: Bool) {
        settingsOpen = false
        do {
            guard files.count == 2 || files.count == 3 else { throw Failure("Expected a speech model, sample WAV and optional speech detector.") }
            var p = Preferences()
            p.whisper = files[0]; p.vad = files.count == 3 ? files[2] : ""; p.language = "en"; p.glossary = []
            let file = try AVAudioFile(forReading: URL(fileURLWithPath: files[1]))
            guard let pcm = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(file.length)),
                  file.processingFormat.sampleRate == 16000, file.processingFormat.channelCount == 1 else { throw Failure("Expected 16 kHz mono WAV.") }
            try file.read(into: pcm)
            let samples = Array(UnsafeBufferPointer(start: pcm.floatChannelData![0], count: Int(pcm.frameLength)))
            let work = SpeechJob()
            job = work
            phase = .processing
            task = Task {
                do {
                    let text = try await speech.recognize(samples, p, work)
                    if !cancelInference {
                        guard text.lowercased().contains("country") else { throw Failure("Shutdown test transcription failed.") }
                        print("Speech model loaded for normal application Quit (\(p.useMetal ? "Metal" : "CPU")).")
                        fflush(stdout)
                        scheduleCheckQuit()
                    }
                } catch {
                    if !closing { fputs("\(error.localizedDescription)\n", stderr); exit(1) }
                }
            }
            if cancelInference {
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) {
                    print("Quit requested during speech processing.")
                    fflush(stdout)
                    scheduleCheckQuit()
                }
            }
        } catch { fputs("\(error.localizedDescription)\n", stderr); exit(1) }
    }
    func quit(completion: @escaping @MainActor @Sendable () -> Void) {
        guard !closing else { return }
        closing = true
        Diagnostics.event("exit.requested")
        refreshMenu = nil
        cancel()
        phase = .processing
        status = "Closing and releasing models…"
        keys.stop()
        for tone in tones { tone?.stop() }
        if let observer { NSWorkspace.shared.notificationCenter.removeObserver(observer); self.observer = nil }
        editor.close()
        speech.close {
            // AppKit runs a special termination loop after terminateLater. Reply
            // through that run loop, without depending on MainActor task scheduling.
            RunLoop.main.perform(inModes: [.common, .modalPanel]) {
                MainActor.assumeIsolated { ClipboardLease.finishBeforeQuit(completion) }
            }
            CFRunLoopWakeUp(CFRunLoopGetMain())
        }
    }
}
