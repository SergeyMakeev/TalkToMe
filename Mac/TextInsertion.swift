import Cocoa
import Carbon.HIToolbox

enum InsertionProblem: Error, Equatable {
    case accessibility, eventPermission, noApplication, ownWindow, terminal, secureInput
    case focusUnavailable(Int32), unsupportedField, disabledField, focusChanged, shortcutHeld
    case rejected(Int32), multiline, eventCreation, interrupted

    var message: String {
        switch self {
        case .accessibility: return "Enable TalkToMe in Privacy & Security → Accessibility to insert text."
        case .eventPermission: return "macOS is blocking simulated typing. Re-enable TalkToMe in Accessibility and restart it."
        case .noApplication, .ownWindow: return "Click a text field in another app before starting dictation."
        case .terminal: return "Automatic insertion is disabled in terminal fields. Use Copy result."
        case .secureInput: return "Secure keyboard input is active. Leave the password field or close the app holding secure input, then try again."
        case .focusUnavailable(let code): return "Could not read the focused text field (macOS error \(code)). Check Accessibility access and focus a text field."
        case .unsupportedField: return "The focused item did not expose an editable text field. Click inside the editor before dictating."
        case .disabledField: return "The target text field is disabled."
        case .focusChanged: return "The target app or text field changed during dictation. Keep it focused until insertion finishes."
        case .shortcutHeld: return "Release the shortcut keys after recording so TalkToMe can insert text."
        case .rejected(let code): return "The editor did not confirm insertion (macOS error \(code)). Check it before retrying to avoid duplicate text."
        case .multiline: return "This editor needs pasted text for line breaks or tabs. Use Copy result."
        case .eventCreation: return "macOS could not create the text-input events. Use Copy result."
        case .interrupted: return "Typing stopped after partial insertion. Check the editor; the full result is still available here."
        }
    }
}

enum InsertionOutcome: Equatable {
    case inserted, sent, blocked(InsertionProblem)

    var message: String {
        switch self {
        case .inserted: return "Text inserted into the original field."
        case .sent: return "Text sent to the original field. If the editor ignored it, use Copy result."
        case .blocked(let problem): return "Ready to copy. \(problem.message)"
        }
    }
}

// Keep delivery policy separate from AX/Quartz so failed writes, lost focus and
// cancellation can be checked without granting a CI process control of the desktop.
@MainActor struct TextInsertion {
    var modifiersReleased: () -> Bool
    var focused: () -> Bool
    var secureInput: () -> Bool
    var selectionWritable: () -> Bool
    var replaceSelection: (String) -> AXError
    var canPostEvents: () -> Bool
    var sendUnicode: ([UniChar]) -> Bool
    var prepareText: (String) -> String = { $0 }
    var pause: (UInt64) async throws -> Void = { try await Task.sleep(nanoseconds: $0) }

    func insert(_ text: String, valid: () -> Bool) async throws -> InsertionOutcome {
        for _ in 0..<200 {
            try Task.checkCancellation()
            guard !secureInput() else { return .blocked(.secureInput) }
            guard valid(), focused() else { return .blocked(.focusChanged) }
            if modifiersReleased() { break }
            try await pause(25_000_000)
        }
        try Task.checkCancellation()
        guard modifiersReleased() else { return .blocked(.shortcutHeld) }
        guard valid(), focused() else { return .blocked(.focusChanged) }
        guard !secureInput() else { return .blocked(.secureInput) }
        let text = prepareText(text)
        if selectionWritable() {
            let result = replaceSelection(text)
            if result == .success { return .inserted }
            if result == .apiDisabled { return .blocked(.accessibility) }
            // A timeout may mean the write was applied. Do not type a duplicate.
            guard result == .attributeUnsupported || result == .notImplemented else {
                return .blocked(.rejected(result.rawValue))
            }
        }
        // Typing does not require the editor to allow replacement of its entire
        // AXValue. Many browser/Electron editors expose that value as read-only.
        guard !text.unicodeScalars.contains(where: CharacterSet.controlCharacters.contains) else { return .blocked(.multiline) }
        guard canPostEvents() else { return .blocked(.eventPermission) }
        var chunks = [[UniChar]]()
        var chunk = [UniChar]()
        for scalar in text.unicodeScalars {
            if chunk.count + scalar.utf16.count > 20 { chunks.append(chunk); chunk.removeAll(keepingCapacity: true) }
            chunk.append(contentsOf: scalar.utf16)
        }
        if !chunk.isEmpty { chunks.append(chunk) }
        for (index, chunk) in chunks.enumerated() {
            try Task.checkCancellation()
            guard !secureInput() else { return .blocked(index == 0 ? .secureInput : .interrupted) }
            guard modifiersReleased() else { return .blocked(index == 0 ? .shortcutHeld : .interrupted) }
            guard valid(), focused() else {
                return .blocked(index == 0 ? .focusChanged : .interrupted)
            }
            guard canPostEvents() else { return .blocked(index == 0 ? .eventPermission : .interrupted) }
            guard sendUnicode(chunk) else { return .blocked(index == 0 ? .eventCreation : .interrupted) }
            if index + 1 < chunks.count { try await pause(10_000_000) }
        }
        return .sent
    }
}
