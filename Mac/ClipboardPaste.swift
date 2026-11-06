import Cocoa
import Carbon.HIToolbox

// Materialize every existing format before touching the pasteboard. Restore only
// our own generation, so a copy made by the user while pasting is never replaced.
@MainActor final class ClipboardLease {
    private static var active: ClipboardLease?
    private static let marker = NSPasteboard.PasteboardType("com.sergeymakeev.TalkToMe.paste")
    private let board: NSPasteboard
    private let backup: [NSPasteboardItem]
    private let token = UUID().uuidString
    private var generation = 0
    private var keepUntil: Date?

    private init(board: NSPasteboard, backup: [NSPasteboardItem]) {
        self.board = board; self.backup = backup
    }
    static func place(_ text: String, on board: NSPasteboard = .general, report: (String) -> Void = { _ in }) -> ClipboardLease? {
        func rejected(_ reason: String) -> ClipboardLease? { report("Clipboard: " + reason); return nil }
        guard active == nil else { return rejected("another paste is still pending") }
        let original = board.changeCount
        var backup = [NSPasteboardItem]()
        var bytes = 0
        for item in board.pasteboardItems ?? [] {
            let copy = NSPasteboardItem()
            for type in item.types {
                guard let data = item.data(forType: type) else { return rejected("cannot preserve format \(type.rawValue)") }
                bytes += data.count
                guard bytes <= 16 * 1024 * 1024, copy.setData(data, forType: type) else { return rejected("backup exceeds limit or format cannot be copied") }
            }
            backup.append(copy)
        }
        guard board.changeCount == original else { return rejected("changed while preparing backup") }
        let lease = ClipboardLease(board: board, backup: backup)
        let item = NSPasteboardItem()
        guard item.setString(text, forType: .string), item.setString(lease.token, forType: marker) else { return rejected("cannot prepare temporary text") }
        // Tell cooperating clipboard managers this is temporary input data.
        item.setData(Data(), forType: NSPasteboard.PasteboardType("org.nspasteboard.TransientType"))
        let cleared = board.clearContents()
        guard board.writeObjects([item]) else {
            if board.changeCount == cleared, !backup.isEmpty { _ = board.writeObjects(backup) }
            return rejected("write failed")
        }
        lease.generation = board.changeCount
        guard lease.isCurrent else { return rejected("temporary clipboard ownership not confirmed") }
        active = lease
        return lease
    }
    var isCurrent: Bool {
        board.changeCount == generation && board.string(forType: Self.marker) == token
    }
    func restore() {
        defer { if Self.active === self { Self.active = nil } }
        guard isCurrent else { return }
        board.clearContents()
        if !backup.isEmpty { _ = board.writeObjects(backup) }
    }
    func didDispatch() { keepUntil = Date().addingTimeInterval(0.75) }
    static func finishBeforeQuit(_ completion: @escaping @MainActor @Sendable () -> Void) {
        guard let deadline = active?.keepUntil, deadline > Date() else { active?.restore(); completion(); return }
        let timer = Timer(fire: deadline, interval: 0, repeats: false) { _ in
            MainActor.assumeIsolated { active?.restore(); completion() }
        }
        RunLoop.main.add(timer, forMode: .common)
        RunLoop.main.add(timer, forMode: .modalPanel)
    }
}

@MainActor enum ClipboardPaste {
    static func send(_ text: String, pid: pid_t, report: (String) -> Void, allowed: () -> InsertionProblem?) async -> InsertionOutcome {
        if let problem = allowed() { return .blocked(problem) }
        guard let lease = ClipboardLease.place(text, report: report) else { return .blocked(.clipboardUnavailable) }
        defer { lease.restore() }
        if let problem = allowed() { return .blocked(problem) }
        guard lease.isCurrent else { return .blocked(.clipboardChanged) }
        guard let down = CGEvent(keyboardEventSource: nil, virtualKey: CGKeyCode(kVK_ANSI_V), keyDown: true),
              let up = CGEvent(keyboardEventSource: nil, virtualKey: CGKeyCode(kVK_ANSI_V), keyDown: false) else {
            return .blocked(.eventCreation)
        }
        down.flags = .maskCommand; up.flags = .maskCommand
        down.postToPid(pid); up.postToPid(pid)
        lease.didDispatch()
        // Once dispatched, allow the app to consume the pasteboard even when the
        // dictation Task is cancelled. Quit also restores the active lease.
        await Task.detached { try? await Task.sleep(nanoseconds: 750_000_000) }.value
        return .sent
    }
}
