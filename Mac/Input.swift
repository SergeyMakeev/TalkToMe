import Cocoa
import Carbon.HIToolbox
import AVFoundation

struct KeyChord: Equatable {
    let code: UInt32
    let modifiers: UInt32
    static let keys: [String: UInt32] = [
        "A":0,"S":1,"D":2,"F":3,"H":4,"G":5,"Z":6,"X":7,"C":8,"V":9,"B":11,"Q":12,"W":13,
        "E":14,"R":15,"Y":16,"T":17,"1":18,"2":19,"3":20,"4":21,"6":22,"5":23,"9":25,"7":26,
        "8":28,"0":29,"O":31,"U":32,"I":34,"P":35,"L":37,"J":38,"K":40,"N":45,"M":46,
        "SPACE":49,"ESCAPE":53,"F1":122,"F2":120,"F3":99,"F4":118,"F5":96,"F6":97,"F7":98,
        "F8":100,"F9":101,"F10":109,"F11":103,"F12":111,"F13":105,"F14":107,"F15":113,
        "F16":106,"F17":64,"F18":79,"F19":80,"F20":90,
        "NUMPAD0":UInt32(kVK_ANSI_Keypad0), "NUMPAD1":UInt32(kVK_ANSI_Keypad1),
        "NUMPAD2":UInt32(kVK_ANSI_Keypad2), "NUMPAD3":UInt32(kVK_ANSI_Keypad3),
        "NUMPAD4":UInt32(kVK_ANSI_Keypad4), "NUMPAD5":UInt32(kVK_ANSI_Keypad5),
        "NUMPAD6":UInt32(kVK_ANSI_Keypad6), "NUMPAD7":UInt32(kVK_ANSI_Keypad7),
        "NUMPAD8":UInt32(kVK_ANSI_Keypad8), "NUMPAD9":UInt32(kVK_ANSI_Keypad9)]
    init(_ description: String) throws {
        let parts = description.uppercased().components(separatedBy: "+").map { $0.trimmingCharacters(in: .whitespaces) }
        var name = parts.last ?? ""
        if name.hasPrefix("NUMPAD") {
            name = "NUMPAD" + name.dropFirst("NUMPAD".count).trimmingCharacters(in: .whitespaces)
        }
        guard let key = Self.keys[name] else { throw Failure("Use a shortcut such as \(Preferences.defaultRecordingShortcut).") }
        var mods = 0
        for part in parts.dropLast() {
            switch part {
            case "CTRL", "CONTROL": mods |= controlKey
            case "ALT", "OPTION": mods |= optionKey
            case "CMD", "COMMAND": mods |= cmdKey
            case "SHIFT": mods |= shiftKey
            default: throw Failure("Unknown shortcut modifier \(part).")
            }
        }
        guard mods != 0 || (name.hasPrefix("F") && name.count > 1) else { throw Failure("Add a modifier to the shortcut.") }
        code = key; modifiers = UInt32(mods)
    }
}

final class Hotkeys {
    private var handler: EventHandlerRef?
    private var record: EventHotKeyRef?
    private var cancel: EventHotKeyRef?
    var action: ((UInt32, Bool) -> Void)?
    init() {
        var events = [EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed)),
                      EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyReleased))]
        InstallEventHandler(GetApplicationEventTarget(), { _, event, context in
            guard let event, let context else { return OSStatus(eventNotHandledErr) }
            var id = EventHotKeyID()
            guard GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                                    nil, MemoryLayout<EventHotKeyID>.size, nil, &id) == noErr,
                  id.signature == 0x54544D45 else { return OSStatus(eventNotHandledErr) }
            let keys = Unmanaged<Hotkeys>.fromOpaque(context).takeUnretainedValue()
            keys.action?(id.id, GetEventKind(event) == UInt32(kEventHotKeyPressed))
            return noErr
        }, 2, &events, Unmanaged.passUnretained(self).toOpaque(), &handler)
    }
    private func register(_ chord: KeyChord, _ id: UInt32) throws -> EventHotKeyRef {
        var ref: EventHotKeyRef?
        guard handler != nil, RegisterEventHotKey(chord.code, chord.modifiers, EventHotKeyID(signature: 0x54544D45, id: id),
            GetApplicationEventTarget(), 0, &ref) == noErr, let ref else {
            throw Failure("That shortcut is in use. Choose another in Settings.")
        }
        return ref
    }
    func setRecord(_ description: String) throws {
        let chord = try KeyChord(description)
        if let record { UnregisterEventHotKey(record); self.record = nil }
        record = try register(chord, 1)
    }
    func enableCancel(_ description: String) throws { disableCancel(); cancel = try register(KeyChord(description), 2) }
    func disableCancel() { if let cancel { UnregisterEventHotKey(cancel); self.cancel = nil } }
    func stop() {
        action = nil
        if let record { UnregisterEventHotKey(record); self.record = nil }
        disableCancel()
        if let handler { RemoveEventHandler(handler); self.handler = nil }
    }
    deinit { stop() }
}

final class Capture: NSObject, AVCaptureAudioDataOutputSampleBufferDelegate {
    private let session = AVCaptureSession()
    private let queue = DispatchQueue(label: "TalkToMe.capture")
    private var samples: [Float] = []
    private var invalidFormat = false
    private var observer: NSObjectProtocol?
    private var limit = 0
    static var devices: [AVCaptureDevice] {
        guard AVCaptureDevice.authorizationStatus(for: .audio) == .authorized else { return [] }
        return AVCaptureDevice.devices(for: .audio)
    }
    func start(deviceID: String, minutes: Int, interrupted: @escaping () -> Void) throws {
        guard AVCaptureDevice.authorizationStatus(for: .audio) == .authorized else {
            throw Failure("Allow microphone access in TalkToMe Settings, then try again.")
        }
        let device = deviceID.isEmpty ? AVCaptureDevice.default(for: .audio) : Self.devices.first { $0.uniqueID == deviceID }
        guard let device else { throw Failure("Selected microphone is unavailable. Choose an input in Settings.") }
        let input = try AVCaptureDeviceInput(device: device)
        let output = AVCaptureAudioDataOutput()
        output.audioSettings = [AVFormatIDKey: kAudioFormatLinearPCM, AVSampleRateKey: 16000,
            AVNumberOfChannelsKey: 1, AVLinearPCMBitDepthKey: 32, AVLinearPCMIsFloatKey: true,
            AVLinearPCMIsBigEndianKey: false, AVLinearPCMIsNonInterleaved: false]
        output.setSampleBufferDelegate(self, queue: queue)
        guard session.canAddInput(input), session.canAddOutput(output) else { throw Failure("Cannot open this microphone.") }
        session.addInput(input); session.addOutput(output)
        limit = minutes * 60 * 16000
        observer = NotificationCenter.default.addObserver(forName: .AVCaptureSessionRuntimeError, object: session, queue: .main) { _ in interrupted() }
        session.startRunning()
        guard session.isRunning else { throw Failure("Microphone capture could not start.") }
    }
    func captureOutput(_ output: AVCaptureOutput, didOutput buffer: CMSampleBuffer, from connection: AVCaptureConnection) {
        guard let format = CMSampleBufferGetFormatDescription(buffer), let p = CMAudioFormatDescriptionGetStreamBasicDescription(format),
              p.pointee.mSampleRate == 16000, p.pointee.mChannelsPerFrame == 1, p.pointee.mBitsPerChannel == 32,
              p.pointee.mFormatFlags & kAudioFormatFlagIsFloat != 0, let block = CMSampleBufferGetDataBuffer(buffer)
        else { invalidFormat = true; return }
        let n = min(CMBlockBufferGetDataLength(block) / 4, limit - samples.count)
        guard n > 0 else { return }
        var chunk = [Float](repeating: 0, count: n)
        let result = chunk.withUnsafeMutableBytes {
            CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: $0.count, destination: $0.baseAddress!)
        }
        if result == noErr { samples.append(contentsOf: chunk) } else { invalidFormat = true }
    }
    func stop() throws -> [Float] {
        if let observer { NotificationCenter.default.removeObserver(observer); self.observer = nil }
        session.stopRunning()
        return try queue.sync {
            if invalidFormat { throw Failure("The microphone could not deliver 16 kHz mono audio. Try another input.") }
            return samples
        }
    }
    deinit { if let observer { NotificationCenter.default.removeObserver(observer) } }
}

struct Destination {
    let pid: pid_t
    let field: AXUIElement
    let bundleIdentifier: String
    let preferPaste: Bool
    private static func attribute(_ el: AXUIElement, _ key: String) -> CFTypeRef? {
        var value: CFTypeRef?
        return AXUIElementCopyAttributeValue(el, key as CFString, &value) == .success ? value : nil
    }
    static func capture() -> Result<Self, InsertionProblem> {
        // The trust preflight can remain stale after a permission change. Let the
        // actual AX request report access; macOS still enforces the permission.
        let trusted = AXIsProcessTrusted()
        guard let app = NSWorkspace.shared.frontmostApplication else { return .failure(.noApplication) }
        guard app.processIdentifier != getpid() else { return .failure(.ownWindow) }
        let bundle = (app.bundleIdentifier ?? "").lowercased()
        guard !["terminal","iterm","wezterm","alacritty","kitty","ghostty","warp-terminal"].contains(where: bundle.contains) else { return .failure(.terminal) }
        guard !IsSecureEventInputEnabled() else { return .failure(.secureInput) }
        let application = AXUIElementCreateApplication(app.processIdentifier)
        // Some editors expose focus through the system-wide object, others through
        // their application object. Never accept an element belonging to another app.
        var focused: AXUIElement?
        var error = AXError.noValue
        for root in [AXUIElementCreateSystemWide(), application] {
            AXUIElementSetMessagingTimeout(root, 0.25)
            var item: CFTypeRef?
            error = AXUIElementCopyAttributeValue(root, kAXFocusedUIElementAttribute as CFString, &item)
            if error == .success, let item, CFGetTypeID(item) == AXUIElementGetTypeID() {
                let element = item as! AXUIElement
                var owner: pid_t = 0
                if AXUIElementGetPid(element, &owner) == .success, owner == app.processIdentifier {
                    focused = element; break
                }
                error = .invalidUIElement
            } else if error == .success { error = .noValue }
        }
        guard let el = focused else {
            return .failure(!trusted || error == .apiDisabled ? .accessibility : .focusUnavailable(error.rawValue))
        }
        AXUIElementSetMessagingTimeout(el, 0.25)
        let role = attribute(el, kAXRoleAttribute) as? String ?? ""
        guard attribute(el, kAXSubroleAttribute) as? String != "AXSecureTextField" else { return .failure(.secureInput) }
        guard ["AXTextField","AXTextArea","AXComboBox","AXSearchField"].contains(role) ||
              attribute(el, "AXEditable") as? Bool == true else { return .failure(.unsupportedField) }
        guard attribute(el, kAXEnabledAttribute) as? Bool != false else { return .failure(.disabledField) }
        var ancestor: AXUIElement? = el
        for depth in 0..<12 {
            guard let node = ancestor else { break }
            let role = attribute(node, kAXRoleAttribute) as? String
            if role == "AXWindow" || role == "AXApplication" { break }
            var labels = ["AXIdentifier", kAXRoleDescriptionAttribute]
            if depth == 0 { labels += [kAXTitleAttribute, kAXDescriptionAttribute] }
            if labels.contains(where: { (attribute(node, $0) as? String)?.localizedCaseInsensitiveContains("terminal") == true }) { return .failure(.terminal) }
            guard let parent = attribute(node, kAXParentAttribute), CFGetTypeID(parent) == AXUIElementGetTypeID() else { break }
            ancestor = (parent as! AXUIElement)
        }
        let electron = app.bundleURL.map {
            FileManager.default.fileExists(atPath: $0.appendingPathComponent("Contents/Frameworks/Electron Framework.framework").path)
        } ?? false
        return .success(Self(pid: app.processIdentifier, field: el, bundleIdentifier: bundle, preferPaste: electron))
    }
    // Construct this while the original app is still focused. Do not retarget a
    // different app/window while its renderer builds the accessibility tree.
    @MainActor static func recovery(for pid: pid_t) -> FocusRecovery<Self> {
        let application = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(application, 0.25)
        let window = attribute(application, kAXFocusedWindowAttribute)
        return FocusRecovery(
            current: {
                guard NSWorkspace.shared.frontmostApplication?.processIdentifier == pid else { return false }
                if let window {
                    guard let now = attribute(application, kAXFocusedWindowAttribute), CFEqual(window, now) else { return false }
                }
                return true
            },
            read: { capture() },
            enable: {
                // Electron's documented assistive-technology API. Unsupported
                // applications simply reject it; never change VoiceOver settings.
                // https://www.electronjs.org/docs/latest/tutorial/accessibility#macos
                AXUIElementSetAttributeValue(application, "AXManualAccessibility" as CFString, kCFBooleanTrue) == .success
            })
    }
    func stillFocused() -> Bool {
        guard case .success(let current) = Self.capture() else { return false }
        return current.pid == pid && CFEqual(current.field, field)
    }

    private func atCaret(_ text: String) -> String {
        guard text.first?.isLetter == true || text.first?.isNumber == true,
              let raw = Self.attribute(field, kAXSelectedTextRangeAttribute), CFGetTypeID(raw) == AXValueGetTypeID() else { return text }
        var selection = CFRange()
        guard AXValueGetValue(raw as! AXValue, .cfRange, &selection), selection.length == 0, selection.location > 0 else { return text }
        func character(_ offset: CFIndex) -> String {
            var range = CFRange(location: offset, length: 1)
            guard let param = AXValueCreate(.cfRange, &range) else { return "" }
            var value: CFTypeRef?
            AXUIElementCopyParameterizedAttributeValue(field, kAXStringForRangeParameterizedAttribute as CFString, param, &value)
            return value as? String ?? ""
        }
        let left = character(selection.location - 1).last
        let right = character(selection.location).first
        return (left?.isLetter == true || left?.isNumber == true || ".!?".contains(left ?? " ")) &&
            (right == nil || right!.isWhitespace) ? " " + text : text
    }

    private func readText() -> FieldText? {
        if let count = Self.attribute(field, kAXNumberOfCharactersAttribute) as? Int, count > 1_000_000 { return nil }
        guard let value = Self.attribute(field, kAXValueAttribute) as? String, value.utf16.count <= 1_000_000 else { return nil }
        var selection: NSRange?
        if let raw = Self.attribute(field, kAXSelectedTextRangeAttribute), CFGetTypeID(raw) == AXValueGetTypeID() {
            var range = CFRange()
            if AXValueGetValue(raw as! AXValue, .cfRange, &range), range.location >= 0, range.length >= 0 {
                selection = NSRange(location: range.location, length: range.length)
            }
        }
        return FieldText(value: value, selection: selection)
    }

    @MainActor func insert(_ english: String, valid: @escaping () -> Bool, report: @escaping (String) -> Void = { _ in }) async throws -> InsertionOutcome {
        report("Target: \(bundleIdentifier); Electron: \(preferPaste); typing permission: \(CGPreflightPostEventAccess())")
        let delivery = TextInsertion(
            modifiersReleased: { CGEventSource.flagsState(.combinedSessionState).intersection([.maskCommand,.maskAlternate,.maskControl,.maskShift]).isEmpty },
            focused: { stillFocused() },
            secureInput: { IsSecureEventInputEnabled() },
            selectionWritable: {
                var settable = DarwinBoolean(false)
                return AXUIElementIsAttributeSettable(field, kAXSelectedTextAttribute as CFString, &settable) == .success && settable.boolValue
            },
            replaceSelection: { AXUIElementSetAttributeValue(field, kAXSelectedTextAttribute as CFString, $0 as CFString) },
            canPostEvents: { CGPreflightPostEventAccess() },
            sendUnicode: { chunk in
                guard let down = CGEvent(keyboardEventSource: nil, virtualKey: 0, keyDown: true),
                      let up = CGEvent(keyboardEventSource: nil, virtualKey: 0, keyDown: false) else { return false }
                for event in [down, up] {
                    event.flags = []
                    event.keyboardSetUnicodeString(stringLength: chunk.count, unicodeString: chunk)
                    event.postToPid(pid)
                }
                return true
            },
            preferPaste: preferPaste,
            paste: { text in
                await ClipboardPaste.send(text, pid: pid) {
                    if Task.isCancelled || !valid() || !stillFocused() { return .focusChanged }
                    if IsSecureEventInputEnabled() { return .secureInput }
                    if !CGEventSource.flagsState(.combinedSessionState).intersection([.maskCommand,.maskAlternate,.maskControl,.maskShift]).isEmpty { return .shortcutHeld }
                    if !CGPreflightPostEventAccess() { return .eventPermission }
                    return nil
                }
            },
            readText: { readText() },
            report: report,
            prepareText: { atCaret($0) })
        return try await delivery.insert(english, valid: valid)
    }
}
