import Cocoa
import Carbon.HIToolbox

enum InsertionProblem: Error, Equatable {
    case accessibility, eventPermission, noApplication, ownWindow, terminal, secureInput
    case focusUnavailable(Int32), unsupportedField, disabledField, focusChanged, shortcutHeld
    case rejected(Int32), multiline, eventCreation, interrupted, clipboardUnavailable, clipboardChanged

    var message: String {
        switch self {
        case .accessibility: return "Enable TalkToMe in Privacy & Security → Accessibility to insert text."
        case .eventPermission: return "macOS is blocking simulated typing. Re-enable TalkToMe in Accessibility and restart it."
        case .noApplication, .ownWindow: return "Click a text field in another app before starting dictation."
        case .terminal: return "Automatic insertion is disabled in terminal fields. Use Copy result."
        case .secureInput: return "Secure keyboard input is active. Leave the password field or close the app holding secure input, then try again."
        case .focusUnavailable(let code) where code == AXError.noValue.rawValue:
            return "The target app did not expose its focused text field. Click inside the editor and try again."
        case .focusUnavailable(let code): return "Could not read the target app's focused text field (macOS error \(code)). Click inside the editor and try again."
        case .unsupportedField: return "The focused item did not expose an editable text field. Click inside the editor before dictating."
        case .disabledField: return "The target text field is disabled."
        case .focusChanged: return "The target app or text field changed during dictation. Keep it focused until insertion finishes."
        case .shortcutHeld: return "Release the shortcut keys after recording so TalkToMe can insert text."
        case .rejected(let code): return "The editor did not confirm insertion (macOS error \(code)). Check it before retrying to avoid duplicate text."
        case .multiline: return "This editor needs pasted text for line breaks or tabs. Use Copy result."
        case .eventCreation: return "macOS could not create the text-input events. Use Copy result."
        case .interrupted: return "Typing stopped after partial insertion. Check the editor; the full result is still available here."
        case .clipboardUnavailable: return "Could not preserve the clipboard for pasting. Use Copy result."
        case .clipboardChanged: return "The clipboard changed before paste. Use Copy result."
        }
    }
}

// Electron apps may withhold their web accessibility tree until an assistive
// app requests it. Wait briefly for that tree, without blocking audio capture.
@MainActor struct FocusRecovery<Field> {
    var current: () -> Bool
    var read: () -> Result<Field, InsertionProblem>
    var enable: () -> Bool
    var pause: (UInt64) async throws -> Void = { try await Task.sleep(nanoseconds: $0) }

    private func retryable(_ result: Result<Field, InsertionProblem>) -> Bool {
        guard case .failure(let problem) = result else { return false }
        switch problem {
        case .unsupportedField: return true
        case .focusUnavailable(let code):
            return [AXError.noValue.rawValue, AXError.attributeUnsupported.rawValue, AXError.cannotComplete.rawValue].contains(code)
        default: return false
        }
    }
    func resolve(_ initial: Result<Field, InsertionProblem>) async -> Result<Field, InsertionProblem> {
        guard !Task.isCancelled, current() else { return .failure(.focusChanged) }
        guard retryable(initial), enable() else { return initial }
        var result = initial
        for _ in 0..<6 {
            do { try await pause(100_000_000) }
            catch { return .failure(.focusChanged) }
            guard !Task.isCancelled, current() else { return .failure(.focusChanged) }
            result = read()
            guard !Task.isCancelled, current() else { return .failure(.focusChanged) }
            if !retryable(result) { return result }
        }
        return result
    }
}

enum InsertionOutcome: Equatable {
    case inserted, sent, blocked(InsertionProblem)

    var message: String {
        switch self {
        case .inserted: return "Text inserted into the original field."
        case .sent: return "Insertion requested, but the editor did not confirm the text. Check it before using Copy result."
        case .blocked(let problem): return "Ready to copy. \(problem.message)"
        }
    }
}

struct FieldText: Equatable {
    let value: String
    let selection: NSRange?
    var summary: String { "length=\(value.utf16.count), selection=\(selection.map { "\($0.location):\($0.length)" } ?? "unavailable")" }
    func confirms(_ text: String, after: FieldText) -> Bool {
        let count = value.utf16.count
        guard let range = selection, range.location >= 0, range.location <= count,
              range.length >= 0, range.length <= count - range.location else { return false }
        let expected = (value as NSString).replacingCharacters(in: range, with: text)
        guard after.value == expected else { return false }
        // Replacing a selection with identical text needs a caret change as proof.
        return expected != value || (after.selection != nil && after.selection != selection)
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
    var preferPaste = false
    var paste: ((String) async throws -> InsertionOutcome)?
    var readText: () -> FieldText? = { nil }
    var report: (String) -> Void = { _ in }
    var prepareText: (String) -> String = { $0 }
    var pause: (UInt64) async throws -> Void = { try await Task.sleep(nanoseconds: $0) }

    private func confirm(_ text: String, before: FieldText?, valid: () -> Bool) async throws -> InsertionOutcome {
        try Task.checkCancellation()
        guard let before, before.selection != nil else { report("Verification unavailable: no readable initial text/selection."); return .sent }
        for attempt in 0..<11 {
            try Task.checkCancellation()
            guard valid(), focused(), !secureInput() else { report("Verification stopped: target changed."); return .sent }
            if let after = readText() {
                if before.confirms(text, after: after) { report("Verified text change: \(after.summary)"); return .inserted }
                if attempt == 10 { report("Text change not confirmed: \(after.summary)") }
            } else { report("Verification unavailable: target text cannot be read."); return .sent }
            if attempt < 10 { try await pause(100_000_000) }
        }
        return .sent
    }

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
        let before = readText()
        report("Before insertion: \(before?.summary ?? "unavailable"); input length=\(text.utf16.count)")
        if preferPaste, let paste {
            report("Delivery: clipboard + Command+V")
            guard canPostEvents() else { return .blocked(.eventPermission) }
            let outcome = try await paste(text)
            if case .blocked = outcome { return outcome }
            return try await confirm(text, before: before, valid: valid)
        }
        if selectionWritable() {
            report("Delivery: AXSelectedText")
            let result = replaceSelection(text)
            report("AX write result: \(result.rawValue)")
            if result == .success { return try await confirm(text, before: before, valid: valid) }
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
        report("Delivery: Unicode keyboard events")
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
        return try await confirm(text, before: before, valid: valid)
    }
}
