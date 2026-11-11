import AppKit
import AVFoundation
import Metal
import Darwin

// Local, bounded diagnostics. Never log transcripts, prompts, HTTP bodies or keys.
enum Diagnostics {
    private static let lock = NSLock()
    private static var enabled = false
    private(set) static var directory = Preferences.support.appendingPathComponent("Diagnostics", isDirectory: true)
    private static var crashDirectory = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Logs/DiagnosticReports", isDirectory: true)

    static func initialize(at location: URL? = nil, crashReports: URL? = nil) {
        if let location { directory = location }
        if let crashReports { crashDirectory = crashReports }
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            enabled = true
            let state = directory.appendingPathComponent("session.json")
            if let data = try? Data(contentsOf: state),
               let previous = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
               let pid = previous["pid"] as? Int32, kill(pid, 0) != 0, errno == ESRCH {
                event("previous-session", "No clean shutdown recorded for PID \(pid). This can also follow Force Quit or a power loss.")
            }
            let data = try JSONSerialization.data(withJSONObject: ["pid": ProcessInfo.processInfo.processIdentifier])
            try data.write(to: state, options: .atomic)
            event("startup", environment)
            NSSetUncaughtExceptionHandler { exception in
                // Exception reasons/userInfo can contain dictated text.
                Diagnostics.event("uncaught-exception", exception.name.rawValue + "\n" + exception.callStackSymbols.joined(separator: "\n"))
            }
        } catch { /* Diagnostics must not stop the app from opening. */ }
    }

    static func event(_ stage: String, _ detail: String = "") {
        guard enabled else { return }
        lock.lock(); defer { lock.unlock() }
        do {
            let file = directory.appendingPathComponent("events.log")
            let fm = FileManager.default
            if let size = try? file.resourceValues(forKeys: [.fileSizeKey]).fileSize, size > 2_000_000 {
                let previous = directory.appendingPathComponent("events.log.previous")
                if fm.fileExists(atPath: previous.path) { try fm.removeItem(at: previous) }
                try fm.moveItem(at: file, to: previous)
            }
            if !fm.fileExists(atPath: file.path) { fm.createFile(atPath: file.path, contents: nil) }
            let handle = try FileHandle(forWritingTo: file)
            defer { try? handle.close() }
            try handle.seekToEnd()
            let line = "\(ISO8601DateFormatter().string(from: Date())) [\(ProcessInfo.processInfo.processIdentifier)] \(stage): \(detail)\n"
            try handle.write(contentsOf: Data(line.utf8))
        } catch { }
    }

    static func error(_ stage: String, _ error: Error) {
        let ns = error as NSError
        event(stage, "\(String(reflecting: type(of: error))); domain=\(ns.domain); code=\(ns.code)\n" + Thread.callStackSymbols.joined(separator: "\n"))
    }

    static func cleanExit() {
        event("exit", "Normal exit")
        let state = directory.appendingPathComponent("session.json")
        if let data = try? Data(contentsOf: state),
           let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
           json["pid"] as? Int32 == ProcessInfo.processInfo.processIdentifier { try? FileManager.default.removeItem(at: state) }
    }

    static func timing(_ stage: String, since start: TimeInterval) {
        event("timing." + stage, String(format: "%.1f ms", (ProcessInfo.processInfo.systemUptime - start) * 1000))
    }

    static var environment: String {
        #if arch(arm64)
        let architecture = "arm64"
        #else
        let architecture = "x86_64"
        #endif
        let info = ProcessInfo.processInfo
        return "TalkToMe \(AccessibilityAccess.version)\nOS: \(info.operatingSystemVersionString); architecture: \(architecture)\nLogical CPUs: \(info.activeProcessorCount); memory: \(info.physicalMemory / 1_048_576) MiB\nMetal device: \(MTLCreateSystemDefaultDevice()?.name ?? "none")"
    }

    static func model(_ p: Preferences) -> String {
        ["base", "speech", "parakeet"].first { ModelDownload.catalog[$0]?.path == p.whisper } ?? "custom"
    }

    @MainActor static func report(_ p: Preferences) -> String {
        let access = AccessibilityAccess.current()
        var report = environment + "\nUTC: \(ISO8601DateFormatter().string(from: Date()))\n"
        report += "Speech: \(model(p)); editor: \(p.translation.rawValue); language: \(p.language); Metal requested: \(p.useMetal); threads: \(p.threads)\n"
        report += "Microphone: \(p.microphone.isEmpty ? "system default" : "custom selection"); authorization: \(AVCaptureDevice.authorizationStatus(for: .audio).rawValue); Accessibility trusted: \(access.trusted); can post events: \(access.canPostEvents)\n"
        report += "Review: \(p.review); toggle: \(p.toggle); sounds: \(p.sounds); custom prompt: \(!p.prompt.isEmpty); glossary entries: \(p.glossary.count)\n"
        report += "Text processing: \(p.processing.rawValue); engineering dictionary: \(p.engineeringTerms)\n"
        report += "No transcripts, audio, prompts, glossary contents, server URLs or keys are included. Crash excerpts omit exception messages, memory/register contents and file paths.\n"
        for name in ["events.log.previous", "events.log"] {
            if let text = try? String(contentsOf: directory.appendingPathComponent(name), encoding: .utf8) {
                report += "\n--- \(name) ---\n" + text.split(separator: "\n", omittingEmptySubsequences: false).suffix(500).joined(separator: "\n")
            }
        }
        let files = (try? FileManager.default.contentsOfDirectory(at: crashDirectory,
            includingPropertiesForKeys: [.contentModificationDateKey, .fileSizeKey], options: [.skipsHiddenFiles])) ?? []
        let candidates = files.filter {
            $0.lastPathComponent.hasPrefix("TalkToMe") && ["ips", "crash"].contains($0.pathExtension)
        }.sorted {
            ((try? $0.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast) >
            ((try? $1.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast)
        }
        var count = 0
        for file in candidates.prefix(20) {
            guard let size = try? file.resourceValues(forKeys: [.fileSizeKey]).fileSize, size <= 10_000_000,
                  let text = try? String(contentsOf: file, encoding: .utf8),
                  let summary = CrashSummary.render(text, format: file.pathExtension) else { continue }
            report += "\n--- macOS crash report ---\n" + summary
            count += 1
            if count == 3 { break }
        }
        if count == 0 { report += "\nNo readable TalkToMe crash reports found. macOS may create a report shortly after the app exits; forced termination may not produce one.\n" }
        return report
    }

    @MainActor static func openFolder() {
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        NSWorkspace.shared.open(directory)
    }
    @MainActor static func openCrashReports() { NSWorkspace.shared.open(crashDirectory) }
}

enum CrashSummary {
    // Only allowlist technical crash fields. Never copy the full system report.
    static func render(_ text: String, format ext: String) -> String? {
        if ext == "crash" { return legacy(text) }
        guard let newline = text.firstIndex(of: "\n"),
              let metadata = try? JSONSerialization.jsonObject(with: Data(text[..<newline].utf8)) as? [String: Any],
              String(describing: metadata["bug_type"] ?? "") == "309",
              let body = try? JSONSerialization.jsonObject(with: Data(text[text.index(after: newline)...].utf8)) as? [String: Any],
              body["procName"] as? String == "TalkToMe" else { return nil }
        var lines = [String]()
        func fields(_ object: [String: Any], _ keys: [String], prefix: String = "") {
            for key in keys { if let value = object[key] as? String { lines.append("\(prefix)\(key): \(value)") }
                else if let value = object[key] as? NSNumber { lines.append("\(prefix)\(key): \(value)") } }
        }
        fields(body, ["procName", "pid", "captureTime", "cpuType", "translated", "faultingThread"])
        fields(body["bundleInfo"] as? [String: Any] ?? [:], ["CFBundleShortVersionString", "CFBundleVersion"])
        fields(body["exception"] as? [String: Any] ?? [:], ["type", "signal", "codes"], prefix: "exception.")
        fields(body["termination"] as? [String: Any] ?? [:], ["namespace", "code"], prefix: "termination.")
        let images = body["usedImages"] as? [[String: Any]] ?? []
        func frames(_ frames: [[String: Any]]) {
            for (i, frame) in frames.prefix(64).enumerated() {
                let index = frame["imageIndex"] as? Int ?? -1
                let image = images.indices.contains(index) ? images[index] : [:]
                let name = URL(fileURLWithPath: image["name"] as? String ?? "unknown").lastPathComponent
                let offset = (frame["imageOffset"] as? NSNumber)?.uint64Value ?? 0
                // Symbols, UUIDs and image offsets permit offline symbolication.
                lines.append("  \(i) \(name)+0x\(String(offset, radix: 16)) \(frame["symbol"] as? String ?? "") [\(image["uuid"] as? String ?? "")]")
            }
        }
        if let threads = body["threads"] as? [[String: Any]], let fault = body["faultingThread"] as? Int, threads.indices.contains(fault) {
            lines.append("Crashed thread:")
            frames(threads[fault]["frames"] as? [[String: Any]] ?? [])
        }
        if let trace = body["lastExceptionBacktrace"] as? [[String: Any]] { lines.append("Last exception backtrace:"); frames(trace) }
        return lines.joined(separator: "\n") + "\n"
    }

    private static func legacy(_ text: String) -> String? {
        let lines = text.components(separatedBy: .newlines)
        guard lines.contains(where: { $0.hasPrefix("Process:") && $0.dropFirst(8).trimmingCharacters(in: .whitespaces).hasPrefix("TalkToMe [") }) else { return nil }
        var result = [String](), inStack = false, count = 0
        for line in lines {
            if ["Process:", "Version:", "Code Type:", "Date/Time:", "OS Version:", "Exception Type:", "Exception Codes:", "Crashed Thread:"].contains(where: line.hasPrefix) { result.append(line) }
            if line.range(of: #"^Thread \d+ Crashed:"#, options: .regularExpression) != nil { inStack = true; result.append(line); continue }
            if inStack {
                if line.isEmpty { inStack = false }
                else if count < 64, line.range(of: #"^\d+\s+\S+\s+0x[0-9a-fA-F]+"#, options: .regularExpression) != nil { result.append(line); count += 1 }
                else { inStack = false }
            }
        }
        return result.joined(separator: "\n") + "\n"
    }
}
