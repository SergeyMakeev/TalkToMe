import AppKit
import CryptoKit
import Security

struct Failure: LocalizedError {
    let errorDescription: String?
    init(_ message: String) { errorDescription = message }
}

struct Preferences: Codable, Equatable {
    static let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("TalkToMe", isDirectory: true)
    static let settingsFile = support.appendingPathComponent("settings.json")
    static let models = support.appendingPathComponent("Models", isDirectory: true)
    var serverURL = "http://127.0.0.1:8080/v1"
    var model = "dictation"
    var manageServer = false
    var executable = ""
    var gguf = ""
    var contextSize = 8192
    var gpuLayers = "all"
    var temperature = 0.1
    var timeout = 60
    var prompt = ""
    var whisper = models.appendingPathComponent(ModelDownload.speech.name).path
    var vad = models.appendingPathComponent(ModelDownload.activity.name).path
    #if arch(arm64)
    var useMetal = true
    #else
    var useMetal = false
    #endif
    var threads = min(8, max(1, ProcessInfo.processInfo.activeProcessorCount / 2))
    var language = "auto"
    var toggle = false
    var review = false
    var sounds = true
    var minutes = 5
    var microphone = "" // AVCaptureDevice.uniqueID; empty follows the system default.
    var shortcut = "Control+Option+F9"
    var cancelShortcut = "Control+Option+F10"
    var glossary = ["SLIM", "RCC", "EditableMesh", "CFrame", "RenderDoc", "Vulkan", "llama.cpp", "draw calls", "latency"]

    static func load(from file: URL = settingsFile) throws -> Self {
        guard FileManager.default.fileExists(atPath: file.path) else { return Self() }
        let saved = try JSONSerialization.jsonObject(with: Data(contentsOf: file)) as? [String: Any]
        guard let saved else { throw Failure("Invalid preferences. Save new settings to repair them.") }
        let defaults = try JSONSerialization.jsonObject(with: JSONEncoder().encode(Self())) as! [String: Any]
        return try JSONDecoder().decode(Self.self, from: JSONSerialization.data(withJSONObject: defaults.merging(saved) { _, new in new }))
    }

    func endpoint() throws -> URL {
        guard let u = URL(string: serverURL), ["http", "https"].contains(u.scheme?.lowercased() ?? ""),
              let host = u.host, !host.isEmpty, u.user == nil, u.password == nil, u.query == nil, u.fragment == nil else {
            throw Failure("Enter an HTTP or HTTPS llama-server URL including /v1.")
        }
        return u
    }
    func validate() throws {
        let url = try endpoint()
        guard !model.trimmingCharacters(in: .whitespaces).isEmpty, (1...600).contains(timeout), (0...2).contains(temperature),
              (1...60).contains(minutes), (1...128).contains(threads), ["auto","ru","en"].contains(language),
              !whisper.isEmpty else { throw Failure("Check the model, language, recording limit, threads, temperature and timeout.") }
        if manageServer {
            guard url.scheme == "http", ["localhost","127.0.0.1","::1","[::1]"].contains(url.host?.lowercased() ?? ""),
                  FileManager.default.isExecutableFile(atPath: executable), FileManager.default.fileExists(atPath: gguf),
                  contextSize >= 512, gpuLayers == "all" || (Int(gpuLayers) ?? -1) >= 0 else {
                throw Failure("A managed server needs an HTTP loopback URL, a llama-server executable and a GGUF model.")
            }
        }
        if try KeyChord(shortcut) == KeyChord(cancelShortcut) { throw Failure("Choose different record and cancel shortcuts.") }
    }
    func save(to file: URL = Self.settingsFile) throws {
        try FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
        try JSONEncoder().encode(self).write(to: file, options: .atomic)
    }
    var keyAccount: String {
        guard let u = try? endpoint() else { return "" }
        return "\(u.scheme!.lowercased())://\(u.host!.lowercased()):\(u.port ?? (u.scheme == "https" ? 443 : 80))"
    }
}

enum Secrets {
    private static func query(_ account: String) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "TalkToMe.llama-server", kSecAttrAccount as String: account]
    }
    static func read(_ account: String) throws -> String {
        var q = query(account)
        q[kSecReturnData as String] = true
        var result: CFTypeRef?
        let code = SecItemCopyMatching(q as CFDictionary, &result)
        if code == errSecItemNotFound { return "" }
        guard code == errSecSuccess, let bytes = result as? Data else { throw Failure("Keychain could not read the server key (\(code)).") }
        return String(decoding: bytes, as: UTF8.self)
    }
    static func write(_ value: String, account: String) throws {
        let q = query(account)
        var code: OSStatus
        if value.isEmpty {
            code = SecItemDelete(q as CFDictionary)
            if code == errSecItemNotFound { return }
        } else {
            let data = Data(value.utf8)
            code = SecItemUpdate(q as CFDictionary, [kSecValueData as String: data] as CFDictionary)
            if code == errSecItemNotFound {
                var entry = q; entry[kSecValueData as String] = data
                code = SecItemAdd(entry as CFDictionary, nil)
            }
        }
        if code != errSecSuccess { throw Failure("Keychain could not save the server key (\(code)).") }
    }
}

struct ModelDownload {
    let name: String
    let repository: String
    let revision: String
    let digest: String
    static let speech = Self(name: "ggml-large-v3-turbo-q5_0.bin", repository: "ggerganov/whisper.cpp",
        revision: "5359861c739e955e79d9a303bcbc70fb988958b1", digest: "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2")
    static let activity = Self(name: "ggml-silero-v5.1.2.bin", repository: "ggml-org/whisper-vad",
        revision: "9ffd54a1e1ee413ddf265af9913beaf518d1639b", digest: "29940d98d42b91fbd05ce489f3ecf7c72f0a42f027e4875919a28fb4c04ea2cf")

    func install() async throws -> String {
        let dir = Preferences.models
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let destination = dir.appendingPathComponent(name)
        func verified(_ file: URL) throws -> Bool {
            let handle = try FileHandle(forReadingFrom: file)
            defer { try? handle.close() }
            var hash = SHA256()
            while let block = try handle.read(upToCount: 1024 * 1024), !block.isEmpty {
                try Task.checkCancellation()
                hash.update(data: block)
            }
            return hash.finalize().map { String(format: "%02x", $0) }.joined() == digest
        }
        if FileManager.default.fileExists(atPath: destination.path), try verified(destination) { return destination.path }
        let source = URL(string: "https://huggingface.co/\(repository)/resolve/\(revision)/\(name)")!
        let (temp, response) = try await URLSession.shared.download(from: source)
        guard (response as? HTTPURLResponse)?.statusCode == 200, try verified(temp) else {
            throw Failure("Model download failed verification. Try again.")
        }
        if FileManager.default.fileExists(atPath: destination.path) {
            _ = try FileManager.default.replaceItemAt(destination, withItemAt: temp)
        } else { try FileManager.default.moveItem(at: temp, to: destination) }
        return destination.path
    }
}
