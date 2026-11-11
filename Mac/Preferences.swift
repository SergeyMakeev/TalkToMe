import AppKit
import CryptoKit
import Metal
import Security

struct Failure: LocalizedError {
    let errorDescription: String?
    init(_ message: String) { errorDescription = message }
}

enum Translation: String, Codable, CaseIterable {
    case qwen08, qwen2, qwen4, translategemma, transcript, server
    var bundled: Bool { self == .qwen08 || self == .qwen2 || self == .qwen4 || self == .translategemma }
    var label: String {
        switch self {
        case .qwen08: return "Local Qwen 3.5 · 0.8B · smallest · 563 MB"
        case .qwen2: return "Local Qwen 3.5 · 2B · 1.28 GB"
        case .qwen4: return "Local Qwen 3 · 4B Instruct · 2.50 GB"
        case .translategemma: return "TranslateGemma 4B · translation only · 2.49 GB"
        case .transcript: return "Transcript only · keep the spoken language"
        case .server: return "Use my own server"
        }
    }
}

enum TextProcessing: String, Codable, CaseIterable {
    case none, light, heavy
    var label: String {
        switch self {
        case .none: return "None · original transcript"
        case .light: return "Light · clean up wording"
        case .heavy: return "Heavy · organize ideas"
        }
    }
}

struct Preferences: Codable, Equatable {
    static let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("TalkToMe", isDirectory: true)
    static let settingsFile = support.appendingPathComponent("settings.json")
    static let models = support.appendingPathComponent("Models", isDirectory: true)
    var serverURL = "http://127.0.0.1:8080/v1"
    var model = "dictation"
    var translation = Translation.qwen2
    var processing = TextProcessing.light
    var engineeringTerms = true
    var processText: Bool { processing != .none && translation != .transcript }
    var manageServer = false
    var executable = ""
    var serverExecutable: String {
        executable.isEmpty ? Bundle.main.bundleURL.appendingPathComponent("Contents/Helpers/llama-server").path : executable
    }
    var gguf = ""
    var contextSize = 8192
    var gpuLayers = "all"
    var temperature = 0.1
    var timeout = 60
    var prompt = ""
    var whisper = ModelDownload.catalog["base"]?.path ?? ""
    var parakeet: Bool { whisper == ModelDownload.catalog["parakeet"]?.path }
    var vad = ModelDownload.catalog["activity"]?.path ?? ""
    #if arch(arm64)
    // Virtual Macs can advertise a Metal device that cannot run the inference kernels correctly.
    var useMetal = MTLCreateSystemDefaultDevice().map {
        $0.supportsFamily(.apple7) && !$0.name.localizedCaseInsensitiveContains("paravirtual")
    } ?? false
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
    static let defaultRecordingShortcut = "Option+Space"
    static let defaultCancelShortcut = "Control+Option+Escape"
    var shortcut = Self.defaultRecordingShortcut
    var cancelShortcut = Self.defaultCancelShortcut
    var shortcutDefaultsVersion = 1
    var glossary = ["SLIM", "RCC", "EditableMesh", "CFrame", "RenderDoc", "Vulkan", "llama.cpp", "draw calls", "latency"]

    static func load(from file: URL = settingsFile) throws -> Self {
        guard FileManager.default.fileExists(atPath: file.path) else { return Self() }
        let saved = try JSONSerialization.jsonObject(with: Data(contentsOf: file)) as? [String: Any]
        guard let saved else { throw Failure("Invalid preferences. Save new settings to repair them.") }
        var defaults = try JSONSerialization.jsonObject(with: JSONEncoder().encode(Self())) as! [String: Any]
        defaults["translation"] = Translation.server.rawValue // Saved preferences from before bundled translation.
        var result = try JSONDecoder().decode(Self.self, from: JSONSerialization.data(withJSONObject: defaults.merging(saved) { _, new in new }))
        // Migrate once; users can still explicitly choose the old presets later.
        // Avoid colliding with a customized shortcut for the other action.
        if (saved["shortcutDefaultsVersion"] as? Int ?? 0) < 1 {
            if (try? KeyChord(result.shortcut)) == (try? KeyChord("Ctrl+Numpad0")),
               (try? KeyChord(result.cancelShortcut)) != (try? KeyChord(Self.defaultRecordingShortcut)) {
                result.shortcut = Self.defaultRecordingShortcut
            }
            if (try? KeyChord(result.cancelShortcut)) == (try? KeyChord("Control+Option+F10")),
               (try? KeyChord(result.shortcut)) != (try? KeyChord(Self.defaultCancelShortcut)) {
                result.cancelShortcut = Self.defaultCancelShortcut
            }
            result.shortcutDefaultsVersion = 1
        }
        return result
    }
    func requiredModels() throws -> [ModelDownload] {
        let activity = try ModelDownload.get("activity")
        var assets = [ModelDownload]()
        for id in ["base", "speech", "parakeet"] {
            let model = try ModelDownload.get(id)
            if whisper == model.path { assets.append(model) }
        }
        if !parakeet && vad == activity.path { assets.append(activity) }
        if processText && translation.bundled { assets.append(try ModelDownload.get(translation.rawValue)) }
        return assets
    }

    func endpoint() throws -> URL {
        guard let u = URL(string: serverURL), ["http", "https"].contains(u.scheme?.lowercased() ?? ""),
              let host = u.host, !host.isEmpty, u.user == nil, u.password == nil, u.query == nil, u.fragment == nil else {
            throw Failure("Enter an HTTP or HTTPS llama-server URL including /v1.")
        }
        return u
    }
    func validate() throws {
        if translation == .server {
            _ = try endpoint()
            guard !model.trimmingCharacters(in: .whitespaces).isEmpty else { throw Failure("Enter the server’s model alias.") }
        }
        guard (1...600).contains(timeout), (0...2).contains(temperature),
              (1...60).contains(minutes), (1...128).contains(threads), ["auto","ru","en"].contains(language),
              !whisper.isEmpty else { throw Failure("Check the model, language, recording limit, threads, temperature and timeout.") }
        if translation == .server && manageServer {
            let url = try endpoint()
            guard url.scheme == "http", ["localhost","127.0.0.1","::1","[::1]"].contains(url.host?.lowercased() ?? ""),
                  FileManager.default.isExecutableFile(atPath: serverExecutable), FileManager.default.fileExists(atPath: gguf),
                  contextSize >= 512, gpuLayers == "all" || (Int(gpuLayers) ?? -1) >= 0 else {
                throw Failure("Choose an HTTP loopback URL and a GGUF. Check the server executable and memory settings.")
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

private final class DownloadProgress: NSObject, URLSessionDownloadDelegate {
    let size: Int64
    let report: @Sendable (Int) -> Void
    init(size: Int64, report: @escaping @Sendable (Int) -> Void) {
        self.size = size
        self.report = report
    }
    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {
        // The async download API returns the temporary file to install().
    }
    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didWriteData bytesWritten: Int64,
                    totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64) {
        let percent = totalBytesWritten * 100 / size
        if totalBytesWritten > size { downloadTask.cancel(); return }
        if percent != (totalBytesWritten - bytesWritten) * 100 / size { report(Int(min(100, percent))) }
    }
}

struct ModelDownload: Decodable {
    let name: String
    let repository: String
    let revision: String
    let digest: String
    let size: Int64
    static let catalog: [String: Self] = {
        guard let url = Bundle.main.url(forResource: "Models", withExtension: "json"),
              let data = try? Data(contentsOf: url),
              let catalog = try? JSONDecoder().decode([String: Self].self, from: data) else { return [:] }
        return catalog
    }()
    static func get(_ id: String) throws -> Self {
        guard let asset = catalog[id] else { throw Failure("The app is missing its model catalog. Reinstall TalkToMe.") }
        return asset
    }
    var path: String { Preferences.models.appendingPathComponent(name).path }
    var installed: Bool {
        let attrs = try? FileManager.default.attributesOfItem(atPath: path)
        return (attrs?[.size] as? NSNumber)?.int64Value == size
    }

    func install(progress: @escaping @Sendable (Int) -> Void = { _ in }) async throws -> String {
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
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 300
        config.timeoutIntervalForResource = 7200
        let session = URLSession(configuration: config)
        defer { session.invalidateAndCancel() }
        var downloaded: (URL, URLResponse)?
        for attempt in 0..<3 {
            do {
                let result = try await session.download(from: source, delegate: DownloadProgress(size: size, report: progress))
                if let http = result.1 as? HTTPURLResponse, [408, 429, 500, 502, 503, 504].contains(http.statusCode), attempt < 2 {
                    try? FileManager.default.removeItem(at: result.0)
                } else { downloaded = result; break }
            } catch let error as URLError where [.timedOut, .networkConnectionLost, .cannotConnectToHost, .notConnectedToInternet].contains(error.code) && attempt < 2 {
                try Task.checkCancellation()
            }
            try await Task.sleep(nanoseconds: UInt64(attempt + 1) * 2_000_000_000)
        }
        guard let (temp, response) = downloaded else { throw Failure("Model download failed. Check your connection and retry.") }
        defer { try? FileManager.default.removeItem(at: temp) }
        guard (response as? HTTPURLResponse)?.statusCode == 200, try verified(temp) else {
            throw Failure("Model download failed verification. Try again.")
        }
        try Task.checkCancellation()
        if FileManager.default.fileExists(atPath: destination.path) {
            _ = try FileManager.default.replaceItemAt(destination, withItemAt: temp)
        } else { try FileManager.default.moveItem(at: temp, to: destination) }
        return destination.path
    }
}
