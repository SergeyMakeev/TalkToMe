import Cocoa

@MainActor private final class InsertionProbe {
    var released = true
    var focused = true
    var secure = false
    var writable = false
    var permission = true
    var writeResult = AXError.success
    var eventCreation = true
    var writes = [String]()
    var chunks = [[UniChar]]()
    var loseFocusAfterSend = false
    var waits = 0

    func delivery() -> TextInsertion {
        TextInsertion(
            modifiersReleased: { self.released }, focused: { self.focused }, secureInput: { self.secure },
            selectionWritable: { self.writable },
            replaceSelection: { self.writes.append($0); return self.writeResult },
            canPostEvents: { self.permission },
            sendUnicode: {
                guard self.eventCreation else { return false }
                self.chunks.append($0)
                if self.loseFocusAfterSend { self.focused = false }
                return true
            },
            pause: { _ in self.waits += 1 })
    }
}

@MainActor func checkTextInsertion() async throws {
    func require(_ condition: Bool, _ message: String) throws {
        guard condition else { throw Failure("Insertion check failed: \(message)") }
    }
    // Editors can expose a read-only AXValue and still accept keyboard input.
    // Delivery must not depend on permission to overwrite the whole document.
    do {
        let probe = InsertionProbe()
        let text = "1234567890123456789🙂 Привет! Café 世界."
        let outcome = try await probe.delivery().insert(text, valid: { true })
        try require(outcome == .sent && probe.writes.isEmpty && probe.chunks.count > 1, "Unicode fallback without a writable selection")
        try require(String(decoding: probe.chunks.flatMap { $0 }, as: UTF16.self) == text, "Unicode preserved")
        for chunk in probe.chunks {
            try require(chunk.count <= 20 && Array(String(decoding: chunk, as: UTF16.self).utf16) == chunk, "surrogate pairs stay in one event")
        }
    }
    do {
        let probe = InsertionProbe()
        probe.writable = true; probe.permission = false
        let text = "First line.\nSecond line."
        let outcome = try await probe.delivery().insert(text, valid: { true })
        try require(outcome == .inserted && probe.writes == [text] && probe.chunks.isEmpty, "AX selection replacement supports multiline without keyboard permission")
    }
    for result in [AXError.attributeUnsupported, .notImplemented, .cannotComplete, .apiDisabled] {
        let probe = InsertionProbe()
        probe.writable = true; probe.writeResult = result
        let outcome = try await probe.delivery().insert("Test", valid: { true })
        if result == .attributeUnsupported || result == .notImplemented {
            try require(outcome == .sent && probe.chunks.count == 1, "unsupported AX write falls back")
        } else {
            let expected: InsertionOutcome = result == .apiDisabled ? .blocked(.accessibility) : .blocked(.rejected(result.rawValue))
            try require(outcome == expected && probe.chunks.isEmpty, "ambiguous write must not type a duplicate")
        }
    }
    do {
        let probe = InsertionProbe()
        probe.released = false
        let outcome = try await probe.delivery().insert("Test", valid: { true })
        try require(outcome == .blocked(.shortcutHeld) && probe.chunks.isEmpty && probe.writes.isEmpty, "held modifiers prevent input")
        var delivery = probe.delivery()
        delivery.pause = { _ in probe.released = true }
        let released = try await delivery.insert("Test", valid: { true })
        try require(released == .sent && probe.chunks.count == 1, "input resumes after shortcut release")
    }
    for problem in [InsertionProblem.focusChanged, .secureInput, .eventPermission, .eventCreation, .multiline] {
        let probe = InsertionProbe()
        if problem == .focusChanged { probe.focused = false }
        if problem == .secureInput { probe.secure = true }
        if problem == .eventPermission { probe.permission = false }
        if problem == .eventCreation { probe.eventCreation = false }
        let text = problem == .multiline ? "First\nSecond" : "Test"
        let outcome = try await probe.delivery().insert(text, valid: { true })
        try require(outcome == .blocked(problem) && probe.writes.isEmpty && probe.chunks.isEmpty, "blocked input: \(problem)")
        try require(outcome.message.contains(problem.message) && !outcome.message.contains("review"), "blocked input explains the reason")
    }
    do {
        let probe = InsertionProbe()
        probe.writable = true
        let outcome = try await probe.delivery().insert("Stale session", valid: { false })
        try require(outcome == .blocked(.focusChanged) && probe.writes.isEmpty, "invalid session cannot replace a selection")
    }
    do {
        let probe = InsertionProbe()
        probe.loseFocusAfterSend = true
        let outcome = try await probe.delivery().insert(String(repeating: "a", count: 60), valid: { true })
        try require(outcome == .blocked(.interrupted) && probe.chunks.count == 1, "focus change stops subsequent chunks")
    }
    do {
        let probe = InsertionProbe()
        probe.released = false
        let pending = Task { @MainActor in try await probe.delivery().insert("Cancelled", valid: { true }) }
        pending.cancel()
        do {
            _ = try await pending.value
            throw Failure("Cancelled insertion was allowed.")
        } catch is CancellationError { }
        try require(probe.chunks.isEmpty && probe.writes.isEmpty, "cancelled session sends nothing")
    }
    print("Mac insertion policy checks passed: Unicode fallback, selection writes, permissions, held shortcuts, focus changes and cancellation.")
}
