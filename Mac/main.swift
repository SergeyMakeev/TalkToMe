import Cocoa
import SwiftUI
import AVFoundation

@MainActor final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private var controller: Dictation!
    private var window: NSWindow!
    private var item: NSStatusItem!
    private let menu = NSMenu()
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
        if !FileManager.default.fileExists(atPath: Preferences.settingsFile.path) { controller.configure() }
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
    func applicationWillTerminate(_ notification: Notification) { controller.quit() }
}

let args = CommandLine.arguments
if args.contains("--self-test") || args.contains("--check-asr") {
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
            guard try KeyChord("Ctrl+Option+F9") == KeyChord("Control+Alt+F9"), !(try EnglishEditor.defaultPrompt).isEmpty else { throw Failure("Defaults failed.") }
            let ok = Data(#"{"choices":[{"finish_reason":"stop","message":{"content":" Hello. "}}]}"#.utf8)
            guard try EnglishEditor.completeText(ok) == "Hello." else { throw Failure("Completion parsing failed.") }
            for bad in ["length", "stop"] {
                let data = Data("{\"choices\":[{\"finish_reason\":\"\(bad)\",\"message\":{\"content\":\"<think>bad</think>\"}}]}".utf8)
                guard (try? EnglishEditor.completeText(data)) == nil else { throw Failure("Unsafe completion accepted.") }
            }
            print("Mac preference, shortcut, resource and completion checks passed.")
        }
    } catch { fputs("\(error.localizedDescription)\n", stderr); exit(1) }
} else {
    let app = NSApplication.shared
    let delegate = AppDelegate()
    app.setActivationPolicy(.regular)
    app.delegate = delegate
    app.run()
}
