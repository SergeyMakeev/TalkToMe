import Foundation
import Darwin

// A resident native worker isolates GPU faults and owns its model buffers. All
// access is on SpeechEngine's serial queue; cancellation can kill a blocked worker.
final class ParakeetWorker {
    private var process: Process?
    private var input: FileHandle?
    private var output: FileHandle?
    private var pending = Data()
    private var modelPath = ""
    private var requestedMetal = false
    private var cpuFallback = false
    private(set) var backend = "Not loaded"

    deinit { stop() }
    func stop() {
        if let process, process.isRunning { kill(process.processIdentifier, SIGKILL); process.waitUntilExit() }
        try? input?.close(); try? output?.close()
        process = nil; input = nil; output = nil; pending.removeAll()
    }
    private func reply(_ job: SpeechJob, timeout: TimeInterval) throws -> [String: Any] {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            try job.checkCancellation()
            if let newline = pending.firstIndex(of: 10) {
                let line = pending.prefix(upTo: newline)
                pending.removeSubrange(...newline)
                guard let object = try JSONSerialization.jsonObject(with: line) as? [String: Any], object["error"] == nil else {
                    throw Failure("Parakeet could not complete the request.")
                }
                return object
            }
            guard let process, process.isRunning, let output else { throw Failure("Parakeet stopped unexpectedly.") }
            var descriptor = pollfd(fd: output.fileDescriptor, events: Int16(POLLIN), revents: 0)
            let status = poll(&descriptor, 1, 50)
            if status < 0 && errno != EINTR { throw Failure("Could not read Parakeet’s response.") }
            if status > 0 {
                // One POSIX read returns currently available bytes; never wait for
                // a full Foundation read buffer and lose the deadline/cancel poll.
                var buffer = [UInt8](repeating: 0, count: 65536)
                let count = Darwin.read(output.fileDescriptor, &buffer, buffer.count)
                if count < 0 && errno == EINTR { continue }
                guard count > 0 else { throw Failure("Parakeet stopped unexpectedly.") }
                pending.append(contentsOf: buffer.prefix(count))
                guard pending.count <= 8 * 1024 * 1024 else { throw Failure("Parakeet returned an oversized response.") }
            }
        }
        throw Failure("Parakeet processing timed out. Retry or choose a different speech model.")
    }
    private func send(_ object: [String: Any]) throws {
        guard let input else { throw Failure("Parakeet is not running.") }
        var data = try JSONSerialization.data(withJSONObject: object)
        data.append(10)
        try input.write(contentsOf: data)
    }
    func transcribe(_ pcm: [Float], _ p: Preferences, _ job: SpeechJob) throws -> String {
        if modelPath != p.whisper || requestedMetal != p.useMetal {
            stop(); cpuFallback = false; modelPath = p.whisper; requestedMetal = p.useMetal
        }
        // Capture the process, not self: cancellation runs outside the model queue.
        defer { job.onCancel(nil) }
        for attempt in 0...1 {
            let gpu = p.useMetal && !cpuFallback
            do {
                try job.checkCancellation()
                if process == nil {
                    let worker = Process()
                    worker.executableURL = Bundle.main.bundleURL.appendingPathComponent("Contents/Helpers/TalkToMeParakeetHost")
                    worker.arguments = ["--worker"]
                    let stdin = Pipe(), stdout = Pipe()
                    worker.standardInput = stdin; worker.standardOutput = stdout
                    worker.standardError = FileHandle.nullDevice
                    try worker.run()
                    process = worker; input = stdin.fileHandleForWriting; output = stdout.fileHandleForReading
                    // A cancelled/crashed child must make writes fail, not deliver
                    // SIGPIPE to the UI process.
                    guard fcntl(stdin.fileHandleForWriting.fileDescriptor, F_SETNOSIGPIPE, 1) != -1 else {
                        throw Failure("Could not configure the Parakeet connection.")
                    }
                    job.onCancel { if worker.isRunning { kill(worker.processIdentifier, SIGKILL) } }
                    try send(["model": p.whisper, "gpu": gpu])
                    let ready = try reply(job, timeout: 180)
                    guard ready["ready"] as? Bool == true, ready["backend"] as? String == (gpu ? "Metal" : "CPU") else {
                        throw Failure("Parakeet could not start its selected backend.")
                    }
                    backend = gpu ? "Metal" : cpuFallback ? "CPU (Metal fallback)" : "CPU"
                } else if let worker = process {
                    job.onCancel { if worker.isRunning { kill(worker.processIdentifier, SIGKILL) } }
                }
                let samples = pcm.map { Int16(max(-32768, min(32767, $0 * 32768))).littleEndian }
                let bytes = samples.withUnsafeBytes { Data($0) }
                try send(["pcm": bytes.base64EncodedString(), "language": p.language])
                let result = try reply(job, timeout: max(300, Double(pcm.count) / 16000 * 8))
                try job.checkCancellation()
                guard let text = result["text"] as? String else { throw Failure("Parakeet returned an invalid result.") }
                return text.trimmingCharacters(in: .whitespacesAndNewlines)
            } catch {
                job.onCancel(nil); stop()
                try job.checkCancellation()
                if attempt == 0 && gpu { cpuFallback = true; continue }
                throw error
            }
        }
        throw Failure("Parakeet could not finish transcription.")
    }
}
