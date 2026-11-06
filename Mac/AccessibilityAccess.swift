import Cocoa
import Security

struct AccessibilityAccess: Equatable {
    let trusted: Bool
    let canPostEvents: Bool

    static func current() -> Self {
        Self(trusted: AXIsProcessTrusted(), canPostEvents: CGPreflightPostEventAccess())
    }
    var message: String {
        if trusted && canPostEvents { return "Editor access and simulated typing are enabled." }
        if trusted { return "Editor access is enabled, but macOS has not enabled simulated typing. Restart TalkToMe after changing permission." }
        if canPostEvents { return "Typing is enabled, but macOS has not confirmed editor access. Restart TalkToMe after changing permission." }
        return "macOS is not granting Accessibility access to this running copy of TalkToMe."
    }
    static var version: String { Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "development" }
    // Explicit user action only. Never reset other apps or other permission types.
    static func resetApproval() async throws {
        let identifier = "com.sergeymakeev.TalkToMe"
        guard Bundle.main.bundleIdentifier == identifier else { throw Failure("Run the installed TalkToMe app to reset its approval.") }
        let process = Process()
        let output = Pipe()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/tccutil")
        process.arguments = ["reset", "Accessibility", identifier]
        process.standardOutput = output
        process.standardError = output
        try process.run()
        defer { if process.isRunning { process.terminate() } }
        for _ in 0..<100 where process.isRunning { try await Task.sleep(nanoseconds: 100_000_000) }
        guard !process.isRunning else { throw Failure("macOS did not finish resetting the approval. Quit TalkToMe and try again.") }
        let detail = String(decoding: output.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
        guard process.terminationStatus == 0 else { throw Failure("macOS could not reset TalkToMe’s Accessibility approval: \(detail)") }
    }
    var diagnostics: String {
        var code: SecCode?
        var staticCode: SecStaticCode?
        var information: CFDictionary?
        if SecCodeCopySelf([], &code) == errSecSuccess, let code,
           SecCodeCopyStaticCode(code, [], &staticCode) == errSecSuccess, let staticCode {
            _ = SecCodeCopySigningInformation(staticCode, SecCSFlags(rawValue: kSecCSSigningInformation), &information)
        }
        let signing = information as? [String: Any] ?? [:]
        let digest = (signing[kSecCodeInfoUnique as String] as? Data)?.map { String(format: "%02x", $0) }.joined() ?? "unavailable"
        return """
        TalkToMe \(Self.version)
        macOS: \(ProcessInfo.processInfo.operatingSystemVersionString)
        Running app: \(Bundle.main.bundleURL.path)
        Bundle ID: \(Bundle.main.bundleIdentifier ?? "unavailable")
        Accessibility (AXIsProcessTrusted): \(trusted)
        Typing (CGPreflightPostEventAccess): \(canPostEvents)
        Signing team: \(signing[kSecCodeInfoTeamIdentifier as String] as? String ?? "none")
        Code hash: \(digest)
        """
    }
}
