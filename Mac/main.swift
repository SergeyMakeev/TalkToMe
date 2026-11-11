import Cocoa
import Carbon.HIToolbox
import SwiftUI
import AVFoundation

// AppKit's terminateLater path must be entered from a run-loop event, as with
// Cmd+Q. Calling terminate inside a main-queue block can starve the async cleanup
// that needs that same queue; a speech-check Task must also return before Quit.
@MainActor func scheduleCheckQuit() {
    let deadline = Date().addingTimeInterval(3)
    let timer = Timer(timeInterval: 0.05, repeats: true) { timer in
        MainActor.assumeIsolated {
            // AppKit ignores terminate while SwiftUI's Settings sheet is open.
            // Let its normal dismissal finish before testing the Quit action.
            guard NSApp.windows.allSatisfy({ $0.attachedSheet == nil }) else {
                if Date() > deadline { fputs("Settings did not close before Quit.\n", stderr); exit(1) }
                return
            }
            timer.invalidate()
            NSApp.terminate(nil)
        }
    }
    RunLoop.main.add(timer, forMode: .common)
}

@MainActor final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private var controller: Dictation!
    private var window: NSWindow!
    private var item: NSStatusItem!
    private let menu = NSMenu()
    private var terminating = false
    private var readyToTerminate = false
    private var quitPasteboardCheck: NSPasteboard?
    func applicationDidFinishLaunching(_ notification: Notification) {
        controller = Dictation()
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 690, height: 480),
            styleMask: [.titled,.closable,.miniaturizable,.resizable], backing: .buffered, defer: false)
        window.title = "TalkToMe"
        window.contentView = NSHostingView(rootView: Panel(app: controller))
        window.isReleasedWhenClosed = false
        window.center()
        let main = NSMenu()
        let application = NSMenu()
        main.addItem(withTitle: "TalkToMe", action: nil, keyEquivalent: "").submenu = application
        application.addItem(withTitle: "Settings…", action: #selector(settings), keyEquivalent: ",").target = self
        application.addItem(withTitle: "Quit TalkToMe", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        let edit = NSMenu()
        main.addItem(withTitle: "Edit", action: nil, keyEquivalent: "").submenu = edit
        for (name, action, key) in [("Undo","undo:","z"),("Cut","cut:","x"),("Copy","copy:","c"),("Paste","paste:","v"),("Select All","selectAll:","a")] {
            edit.addItem(withTitle: name, action: NSSelectorFromString(action), keyEquivalent: key)
        }
        NSApp.mainMenu = main
        item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        item.menu = menu
        menu.delegate = self
        controller.refreshMenu = { [weak self] in self?.icon() }
        icon()
        show()
        let checkWindow = CommandLine.arguments.contains("--check-window")
        let missingModels = (try? controller.prefs.requiredModels().contains { !$0.installed }) ?? true
        if checkWindow || missingModels || !FileManager.default.fileExists(atPath: Preferences.settingsFile.path) { controller.configure() }
        if checkWindow {
            DispatchQueue.main.asyncAfter(deadline: .now() + 2) {
                guard self.window.isVisible, self.window.attachedSheet?.isVisible == true, self.item.button?.image != nil else {
                    fputs("The result window, Settings sheet or menu-bar icon failed to appear.\n", stderr)
                    exit(1)
                }
                print("Result window, Settings sheet and menu-bar icon appeared.")
                fflush(stdout)
                let board = NSPasteboard.withUniqueName()
                board.setString("Clipboard before Quit", forType: .string)
                guard let lease = ClipboardLease.place("Pending paste", on: board) else {
                    fputs("Could not prepare the clipboard shutdown check.\n", stderr); exit(1)
                }
                self.quitPasteboardCheck = board
                lease.didDispatch()
                self.controller.settingsOpen = false
                scheduleCheckQuit()
            }
        }
        if CommandLine.arguments.contains("--check-shutdown") {
            controller.checkShutdown(Array(CommandLine.arguments.dropFirst(2)).filter { $0 != "--cancel-inference" },
                cancelInference: CommandLine.arguments.contains("--cancel-inference"))
        }
    }
    func icon() {
        let symbol: String
        switch controller.phase { case .idle: symbol = "waveform"; case .recording: symbol = "record.circle.fill"; case .processing: symbol = "ellipsis.circle" }
        item.button?.image = NSImage(systemSymbolName: symbol, accessibilityDescription: "TalkToMe \(controller.phase)")
        item.button?.toolTip = "TalkToMe · \(controller.phase)"
    }
    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        menu.addItem(withTitle: controller.status, action: nil, keyEquivalent: "")
        menu.addItem(.separator())
        menu.addItem(withTitle: "Show last result", action: #selector(show), keyEquivalent: "").target = self
        if controller.phase == .idle { menu.addItem(withTitle: "Settings…", action: #selector(settings), keyEquivalent: "").target = self }
        else { menu.addItem(withTitle: "Cancel dictation", action: #selector(cancel), keyEquivalent: "").target = self }
        menu.addItem(withTitle: "Quit TalkToMe", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "")
    }
    @objc func show() { window.makeKeyAndOrderFront(nil); NSApp.activate(ignoringOtherApps: true) }
    @objc func settings() { show(); controller.configure() }
    @objc func cancel() { controller.cancel() }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool { show(); return true }
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        if readyToTerminate { return .terminateNow }
        if !terminating {
            terminating = true
            if CommandLine.arguments.contains("--check-window") || CommandLine.arguments.contains("--check-shutdown") {
                fputs("Beginning normal app shutdown.\n", stderr)
            }
            controller.quit { [self] in
                self.readyToTerminate = true
                sender.reply(toApplicationShouldTerminate: true)
            }
        }
        return .terminateLater
    }
    func applicationWillTerminate(_ notification: Notification) {
        Diagnostics.cleanExit()
        if let board = quitPasteboardCheck {
            guard board.string(forType: .string) == "Clipboard before Quit" else {
                fputs("Quit did not restore its pending pasteboard.\n", stderr); exit(1)
            }
            board.releaseGlobally()
            print("Pending pasteboard restored before Quit.")
        }
        if CommandLine.arguments.contains("--check-window") || CommandLine.arguments.contains("--check-shutdown") {
            print("Application shutdown completed after releasing its models.")
        }
    }
}

let args = CommandLine.arguments
if args.count == 2 && args[1] == "--check-diagnostics" {
    MainActor.assumeIsolated {
        do { try checkDiagnostics(); print("Mac diagnostic persistence, crash parsing and privacy checks passed.") }
        catch { fputs("\(error)\n", stderr); exit(1) }
    }
} else if args.count == 2 && args[1] == "--check-access-reset" {
    Task { @MainActor in
        do {
            try await AccessibilityAccess.resetApproval()
            print("TalkToMe Accessibility approval reset.")
            exit(0)
        } catch { fputs("\(error.localizedDescription)\n", stderr); exit(1) }
    }
    dispatchMain()
} else if args.count == 2 && args[1] == "--check-insertion" {
    Task { @MainActor in
        do { try await checkTextInsertion(); exit(0) }
        catch { fputs("\(error.localizedDescription)\n", stderr); exit(1) }
    }
    dispatchMain()
} else if (2...3).contains(args.count) && args[1] == "--check-local-translation" {
    Task { @MainActor in
        let editor = EnglishEditor()
        do {
            var p = Preferences()
            if args.count == 3 {
                guard let model = Translation(rawValue: args[2]), model.bundled else { throw Failure("Choose a bundled translator.") }
                p.translation = model
            }
            print("Testing bundled translation with \(p.useMetal ? "Metal" : "CPU").")
            _ = try await ModelDownload.get(p.translation.rawValue).install()
            let text = try await editor.edit("У меня два кота.", p)
            guard text.lowercased().contains("cat") else { throw Failure("Unexpected local translation: \(text)") }
            print(text)
            if p.translation != .qwen08 { try await checkTranslationCases(editor, p) }
            editor.stop()
            exit(0)
        } catch { editor.stop(); fputs("\(error.localizedDescription)\n", stderr); exit(1) }
    }
    dispatchMain()
} else if args.count == 3 && args[1] == "--check-parakeet" {
    Task {
        do {
            var p = Preferences()
            p.whisper = try await ModelDownload.get("parakeet").install()
            let file = try AVAudioFile(forReading: URL(fileURLWithPath: args[2]))
            guard let pcm = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(file.length)),
                  file.processingFormat.sampleRate == 16000, file.processingFormat.channelCount == 1 else { throw Failure("Expected 16 kHz mono WAV.") }
            try file.read(into: pcm)
            let samples = Array(UnsafeBufferPointer(start: pcm.floatChannelData![0], count: Int(pcm.frameLength)))
            let engine = SpeechEngine()
            for metal in [false, true] {
                p.useMetal = metal
                let text = try await engine.recognize(samples, p, SpeechJob())
                guard text.lowercased().contains("country") else { throw Failure("Unexpected Parakeet transcript: \(text)") }
                print("Parakeet \(metal ? "Metal or CPU fallback" : "CPU"): \(text)")
            }
            let job = SpeechJob()
            DispatchQueue.global().asyncAfter(deadline: .now() + 0.02) { job.cancel() }
            do { _ = try await engine.recognize(Array(repeating: samples, count: 20).flatMap { $0 }, p, job); throw Failure("Cancelled speech returned a result.") }
            catch is CancellationError { print("Parakeet cancellation passed.") }
            let resumed = try await engine.recognize(samples, p, SpeechJob())
            guard resumed.lowercased().contains("country") else { throw Failure("Parakeet did not recover after cancellation.") }
            await withCheckedContinuation { continuation in engine.close { continuation.resume() } }
            print("Parakeet recovery and shutdown passed.")
            exit(0)
        } catch { fputs("\(error.localizedDescription)\n", stderr); exit(1) }
    }
    dispatchMain()
} else if args.count == 3 && args[1] == "--check-server" {
    Task { @MainActor in
        do {
            var p = Preferences()
            p.translation = .server; p.serverURL = args[2]; p.model = "ci-model"; p.prompt = "CI instructions"; p.glossary = ["Metal"]
            let editor = EnglishEditor()
            let result = try await editor.edit("Проверка Metal", p)
            guard result == "Check Metal." else { throw Failure("Unexpected editor response.") }
            p.prompt = ""; p.glossary = []; p.processing = .heavy
            guard try await editor.edit("Why did it fail?", p) == "Why did it fail?" else { throw Failure("Question retry did not repair the output.") }
            do {
                _ = try await editor.edit("Why did it fail?", p)
                throw Failure("Lost question accepted after retry.")
            } catch let error as Failure where error.localizedDescription.contains("removed a question") { }
            p.processing = .none
            guard try await editor.edit("  Why did it fail?  ", p) == "  Why did it fail?  " else { throw Failure("None did not bypass editing.") }
            editor.close()
            exit(0)
        } catch { fputs("\(error.localizedDescription)\n", stderr); exit(1) }
    }
    dispatchMain()
} else if args.contains("--self-test") || args.contains("--check-asr") {
    do {
        if args.count == 4 || args.count == 5, args[1] == "--check-asr" {
            var p = Preferences()
            p.whisper = args[2]; p.vad = args.count == 5 ? args[4] : ""; p.useMetal = false; p.language = "en"; p.glossary = []
            let file = try AVAudioFile(forReading: URL(fileURLWithPath: args[3]))
            guard let pcm = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(file.length)),
                  file.processingFormat.sampleRate == 16000, file.processingFormat.channelCount == 1 else { throw Failure("Expected 16 kHz mono WAV.") }
            try file.read(into: pcm)
            let samples = Array(UnsafeBufferPointer(start: pcm.floatChannelData![0], count: Int(pcm.frameLength)))
            let text = try SpeechEngine().transcribe(samples, p, SpeechJob())
            guard text.lowercased().contains("country") else { throw Failure("Unexpected JFK transcript: \(text)") }
            print(text)
        } else {
            try Preferences().validate()
            let temp = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
            defer { try? FileManager.default.removeItem(at: temp) }
            let file = temp.appendingPathComponent("settings.json")
            var draft = Preferences()
            guard draft.translation == .qwen2, try draft.requiredModels().count == 3 else { throw Failure("Default local models missing.") }
            try FileManager.default.createDirectory(at: temp, withIntermediateDirectories: true)
            try Data("{}".utf8).write(to: file)
            guard try Preferences.load(from: file).translation == .server else { throw Failure("Legacy server selection lost.") }
            let record = try KeyChord(draft.shortcut)
            guard record.code == UInt32(kVK_Space), record.modifiers == UInt32(optionKey),
                  try KeyChord(draft.cancelShortcut) == KeyChord("Ctrl+Alt+Escape") else { throw Failure("Default talking shortcut failed.") }
            try Data(#"{"shortcut":"Ctrl + Numpad 0","cancelShortcut":"Control+Option+F10","review":false}"#.utf8).write(to: file)
            let migrated = try Preferences.load(from: file)
            guard migrated.processing == .light, migrated.engineeringTerms else { throw Failure("Legacy processing defaults changed.") }
            guard migrated.shortcut == draft.shortcut, migrated.cancelShortcut == draft.cancelShortcut, !migrated.review else {
                throw Failure("Legacy Mac shortcuts did not migrate or review mode changed.")
            }
            for digit in 0...9 {
                guard try KeyChord("Ctrl + Numpad \(digit)") != KeyChord("Ctrl+\(digit)") else { throw Failure("Keypad and number row must be distinct.") }
            }
            draft.shortcut = "Control+Option+F9"
            draft.cancelShortcut = "Control+Shift+Escape"
            draft.threads = 1; draft.review = true; draft.glossary = ["Metal"]; draft.prompt = "Edit lightly."
            draft.processing = .heavy; draft.engineeringTerms = false
            try draft.save(to: file)
            guard try Preferences.load(from: file) == draft else { throw Failure("Preferences did not round trip.") }
            var keypad = draft
            keypad.shortcut = "Ctrl+Numpad0"; keypad.cancelShortcut = "Control+Option+F10"
            try keypad.save(to: file)
            guard try Preferences.load(from: file) == keypad else { throw Failure("Explicit legacy shortcut choice was overwritten.") }
            try Data(#"{"shortcut":"Ctrl+Numpad0","cancelShortcut":"Option+Space"}"#.utf8).write(to: file)
            let conflict = try Preferences.load(from: file)
            try conflict.validate()
            guard conflict.shortcut == "Ctrl+Numpad0", conflict.cancelShortcut == "Option+Space" else { throw Failure("Shortcut migration conflicts with a custom shortcut.") }
            guard try KeyChord("Ctrl+Option+F9") == KeyChord("Control+Alt+F9"), !(try EnglishEditor.defaultPrompt).isEmpty else { throw Failure("Defaults failed.") }
            let ok = Data(#"{"choices":[{"finish_reason":"stop","message":{"content":" Hello. "}}]}"#.utf8)
            guard try EnglishEditor.completeText(ok) == "Hello." else { throw Failure("Completion parsing failed.") }
            for bad in ["length", "stop"] {
                let data = Data("{\"choices\":[{\"finish_reason\":\"\(bad)\",\"message\":{\"content\":\"<think>bad</think>\"}}]}".utf8)
                guard (try? EnglishEditor.completeText(data)) == nil else { throw Failure("Unsafe completion accepted.") }
            }
            try checkEditingResponses()
            print("Mac preference, shortcut, resource and completion checks passed.")
        }
    } catch { fputs("\(error.localizedDescription)\n", stderr); exit(1) }
} else {
    MainActor.assumeIsolated {
        Diagnostics.initialize()
        let app = NSApplication.shared
        let delegate = AppDelegate()
        app.setActivationPolicy(.regular)
        app.delegate = delegate
        withExtendedLifetime(delegate) { app.run() }
    }
}
