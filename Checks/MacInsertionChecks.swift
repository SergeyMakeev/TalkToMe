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
    var field = FieldText(value: "", selection: NSRange(location: 0, length: 0))
    var appliesWrite = true

    func apply(_ text: String) {
        guard let range = field.selection else { return }
        field = FieldText(value: (field.value as NSString).replacingCharacters(in: range, with: text),
            selection: NSRange(location: range.location + text.utf16.count, length: 0))
    }

    func delivery() -> TextInsertion {
        TextInsertion(
            modifiersReleased: { self.released }, focused: { self.focused }, secureInput: { self.secure },
            selectionWritable: { self.writable },
            replaceSelection: {
                self.writes.append($0)
                if self.appliesWrite && self.writeResult == .success { self.apply($0) }
                return self.writeResult
            },
            canPostEvents: { self.permission },
            sendUnicode: {
                guard self.eventCreation else { return false }
                self.chunks.append($0)
                self.apply(String(decoding: $0, as: UTF16.self))
                if self.loseFocusAfterSend { self.focused = false }
                return true
            },
            readText: { self.field },
            pause: { _ in self.waits += 1 })
    }
}

@MainActor func checkTextInsertion() async throws {
    func require(_ condition: Bool, _ message: String) throws {
        guard condition else { throw Failure("Insertion check failed: \(message)") }
    }
    // Reproduce the reported false success: AX returns success without changing
    // the document. Never label that as inserted or follow it with another write.
    do {
        let probe = InsertionProbe()
        probe.writable = true; probe.appliesWrite = false
        var logs = [String]()
        var delivery = probe.delivery()
        delivery.report = { logs.append($0) }
        let outcome = try await delivery.insert("Private dictated words", valid: { true })
        try require(outcome == .sent && probe.writes.count == 1 && probe.chunks.isEmpty, "accepted but ignored AX write is not successful insertion")
        try require(!logs.joined().contains("Private dictated words"), "diagnostics contain no dictated text")
    }
    do {
        let probe = InsertionProbe()
        probe.writable = true; probe.appliesWrite = false
        var delivery = probe.delivery()
        var pastes = 0
        delivery.preferPaste = true
        delivery.paste = { text in pastes += 1; probe.apply(text); return .sent }
        let outcome = try await delivery.insert("First line.\nSecond line 🙂", valid: { true })
        try require(outcome == .inserted && pastes == 1 && probe.writes.isEmpty && probe.chunks.isEmpty, "Electron uses one verified multiline paste instead of an AX write")
    }
    do {
        let probe = InsertionProbe()
        probe.writable = true; probe.appliesWrite = false
        var delivery = probe.delivery()
        delivery.pause = { _ in probe.apply("Delayed result"); probe.appliesWrite = true }
        let result = try await delivery.insert("Delayed result", valid: { true })
        try require(result == .inserted && probe.writes.count == 1, "delayed accessibility updates are verified without retrying insertion")
    }
    do {
        let probe = InsertionProbe()
        var delivery = probe.delivery()
        var pastes = 0
        delivery.preferPaste = true
        delivery.paste = { _ in pastes += 1; return .sent }
        delivery.readText = { nil }
        let result = try await delivery.insert("Unavailable readback", valid: { true })
        try require(result == .sent && pastes == 1, "unreadable editor is explicitly unverified")
        probe.permission = false
        let blocked = try await delivery.insert("Blocked", valid: { true })
        try require(blocked == .blocked(.eventPermission) && pastes == 1, "paste requires event permission")
    }
    do {
        let before = FieldText(value: "a🙂b", selection: NSRange(location: 1, length: 2))
        try require(before.confirms("世界", after: FieldText(value: "a世界b", selection: NSRange(location: 3, length: 0))), "verification uses UTF-16 selection offsets")
        try require(!before.confirms("世界", after: before), "unchanged value cannot confirm insertion")
        try require(!FieldText(value: "x", selection: NSRange(location: Int.max, length: 1)).confirms("a", after: before), "invalid ranges cannot overflow verification")
    }
    // Use an isolated pasteboard, never the user's clipboard, for preservation checks.
    do {
        let board = NSPasteboard.withUniqueName()
        defer { board.releaseGlobally() }
        board.setString("Keep this clipboard", forType: .string)
        let generation = board.changeCount
        let denied = ClipboardLease.place("Do not write", on: board, readItems: { _ in nil })
        try require(denied == nil && board.changeCount == generation && board.string(forType: .string) == "Keep this clipboard",
                    "denied clipboard reads must never be treated as empty")
    }
    do {
        let board = NSPasteboard.withUniqueName()
        defer { board.releaseGlobally() }
        let first = NSPasteboardItem(), second = NSPasteboardItem()
        let rich = NSPasteboard.PasteboardType("public.rtf")
        let richData = Data(#"{\rtf1\ansi Original clipboard}"#.utf8)
        let binary = NSPasteboard.PasteboardType("com.sergeymakeev.TalkToMe.test.binary")
        first.setString("Original clipboard", forType: .string)
        // AppKit advertises conversions from RTF to UTF-16. The fixture must be
        // valid RTF or those promised formats cannot be materialized for backup.
        first.setData(richData, forType: rich)
        second.setData(Data([5, 6, 7]), forType: binary)
        board.writeObjects([first, second])
        guard let lease = ClipboardLease.place("Dictated text", on: board, report: { print($0) }) else { throw Failure("Could not create private pasteboard lease.") }
        try require(lease.isCurrent && board.string(forType: .string) == "Dictated text", "temporary paste text is available")
        try require(ClipboardLease.place("Overlapping paste", on: board) == nil, "overlapping paste cannot capture temporary text as a backup")
        lease.restore()
        let items = board.pasteboardItems ?? []
        try require(items.count == 2 && items[0].string(forType: .string) == "Original clipboard" &&
                    items[0].data(forType: rich) == richData && items[1].data(forType: binary) == Data([5, 6, 7]), "all clipboard items and formats are restored")
        guard let changed = ClipboardLease.place("Second dictation", on: board) else { throw Failure("Second pasteboard lease failed.") }
        board.clearContents(); board.setString("User copied this later", forType: .string)
        changed.restore()
        try require(board.string(forType: .string) == "User copied this later", "restoration never replaces a newer user copy")
        board.clearContents()
        guard let empty = ClipboardLease.place("Temporary", on: board) else { throw Failure("Empty pasteboard lease failed.") }
        empty.restore()
        try require(board.pasteboardItems?.isEmpty != false, "empty clipboard is restored to empty")
    }
    // A renderer may expose no focused element until AXManualAccessibility has
    // been requested and its accessibility tree reaches the browser process.
    do {
        var requests = 0, reads = 0, waits = 0
        let recovery = FocusRecovery<String>(current: { true }, read: {
            reads += 1
            return reads == 1 ? .failure(.focusUnavailable(AXError.noValue.rawValue)) : .success("original Slack composer")
        }, enable: { requests += 1; return true }, pause: { _ in waits += 1 })
        let result = await recovery.resolve(.failure(.focusUnavailable(AXError.noValue.rawValue)))
        try require(result == .success("original Slack composer") && requests == 1 && waits == 2, "delayed Electron focus becomes available")
    }
    do {
        var requests = 0
        let recovery = FocusRecovery<String>(current: { true }, read: { .success("unexpected") },
            enable: { requests += 1; return true }, pause: { _ in })
        let result = await recovery.resolve(.success("TextEdit field"))
        try require(result == .success("TextEdit field") && requests == 0, "working native focus needs no compatibility request")
        for problem in [InsertionProblem.accessibility, .secureInput, .terminal, .disabledField, .focusChanged,
                        .focusUnavailable(AXError.failure.rawValue)] {
            let result = await recovery.resolve(.failure(problem))
            try require(result == .failure(problem) && requests == 0, "focus recovery preserves blocked target: \(problem)")
        }
    }
    do {
        var waits = 0
        let recovery = FocusRecovery<String>(current: { true }, read: { .success("unexpected") },
            enable: { false }, pause: { _ in waits += 1 })
        let original: Result<String, InsertionProblem> = .failure(.focusUnavailable(AXError.noValue.rawValue))
        let result = await recovery.resolve(original)
        try require(result == original && waits == 0, "unsupported compatibility attribute is not an access grant")
    }
    do {
        var current = true, reads = 0
        let recovery = FocusRecovery<String>(current: { current }, read: { reads += 1; return .success("wrong target") },
            enable: { true }, pause: { _ in current = false })
        let result = await recovery.resolve(.failure(.unsupportedField))
        try require(result == .failure(.focusChanged) && reads == 0, "app or window switch prevents retargeting during recovery")
    }
    do {
        var reads = 0
        let missing: Result<String, InsertionProblem> = .failure(.focusUnavailable(AXError.noValue.rawValue))
        let recovery = FocusRecovery<String>(current: { true }, read: { reads += 1; return missing },
            enable: { true }, pause: { _ in })
        let result = await recovery.resolve(missing)
        try require(result == missing && reads == 6, "missing focus has a bounded retry instead of blind typing")
        try require(!InsertionProblem.focusUnavailable(AXError.noValue.rawValue).message.contains("Accessibility"), "missing field is not reported as missing permission")
    }
    do {
        var current = true
        let recovery = FocusRecovery<String>(current: { current }, read: { current = false; return .success("new window") },
            enable: { true }, pause: { _ in })
        let result = await recovery.resolve(.failure(.unsupportedField))
        try require(result == .failure(.focusChanged), "switching windows during an AX read rejects its result")
    }
    do {
        var reads = 0, requests = 0
        let recovery = FocusRecovery<String>(current: { true }, read: { reads += 1; return .success("unexpected") },
            enable: { requests += 1; return true }, pause: { _ in throw CancellationError() })
        let result = await recovery.resolve(.failure(.unsupportedField))
        try require(result == .failure(.focusChanged) && reads == 0, "cancellation while waiting stops focus reads")
        let pending = Task { @MainActor in await recovery.resolve(.failure(.unsupportedField)) }
        pending.cancel()
        let cancelled = await pending.value
        try require(cancelled == .failure(.focusChanged) && requests == 1 && reads == 0, "cancelled capture cannot enable or retarget an app")
    }
    do {
        var reads = 0
        let recovery = FocusRecovery<String>(current: { true }, read: { reads += 1; return .failure(.secureInput) },
            enable: { true }, pause: { _ in })
        let result = await recovery.resolve(.failure(.unsupportedField))
        try require(result == .failure(.secureInput) && reads == 1, "newly exposed secure field stays blocked")
    }
    // Editors can expose a read-only AXValue and still accept keyboard input.
    // Delivery must not depend on permission to overwrite the whole document.
    do {
        let probe = InsertionProbe()
        let text = "1234567890123456789🙂 Привет! Café 世界."
        let outcome = try await probe.delivery().insert(text, valid: { true })
        try require(outcome == .inserted && probe.writes.isEmpty && probe.chunks.count > 1, "Unicode fallback without a writable selection")
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
            try require(outcome == .inserted && probe.chunks.count == 1, "unsupported AX write falls back")
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
        try require(released == .inserted && probe.chunks.count == 1, "input resumes after shortcut release")
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
    print("Mac insertion policy checks passed: Electron focus recovery, Unicode fallback, selection writes, permissions, held shortcuts, focus changes and cancellation.")
}
