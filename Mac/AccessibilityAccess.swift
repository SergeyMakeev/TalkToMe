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
