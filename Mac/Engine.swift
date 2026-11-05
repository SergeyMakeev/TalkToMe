import Foundation

final class SpeechJob {
    let pointer = ttm_new_job()!
    func cancel() { ttm_cancel(pointer) }
    deinit { ttm_free_job(pointer) }
}

final class SpeechEngine {
    private let queue = DispatchQueue(label: "TalkToMe.whisper", qos: .userInitiated)
    private var model: UnsafeMutableRawPointer?
    private var path = ""
    private var metal = false
    deinit { if let model { ttm_close(model) } }

    func recognize(_ pcm: [Float], _ p: Preferences, _ job: SpeechJob) async throws -> String {
        try await withCheckedThrowingContinuation { continuation in
            queue.async {
                continuation.resume(with: Result { try self.transcribe(pcm, p, job) })
            }
        }
    }
    // Called on the model queue, or from the standalone transcription check.
    func transcribe(_ pcm: [Float], _ p: Preferences, _ job: SpeechJob) throws -> String {
        guard pcm.count >= 1600, pcm.contains(where: { abs($0) > 0.0001 }) else { return "" }
        guard FileManager.default.isReadableFile(atPath: p.whisper),
              p.vad.isEmpty || FileManager.default.isReadableFile(atPath: p.vad) else {
            throw Failure("Open Settings → Speech to download or select the speech models.")
        }
        if model == nil || path != p.whisper || metal != p.useMetal {
            if let model { ttm_close(model) }
            model = ttm_open(p.whisper, p.useMetal)
            path = p.whisper
            metal = p.useMetal
        }
        guard let model else { throw Failure("Whisper could not load the model. Check its format or turn off Metal in Settings.") }
        let result = pcm.withUnsafeBufferPointer {
            ttm_transcribe(model, job.pointer, $0.baseAddress, Int32($0.count), p.language, p.glossary.joined(separator: ", "), p.vad, Int32(p.threads))
        }
        guard let result else { throw Failure("Whisper could not finish transcription.") }
        defer { free(result) }
        return String(cString: result).trimmingCharacters(in: .whitespacesAndNewlines)
    }
}

private final class NoRedirect: NSObject, URLSessionTaskDelegate {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) { completionHandler(nil) }
}

@MainActor final class EnglishEditor {
    private var owned: Process?
    private let session = URLSession(configuration: .ephemeral, delegate: NoRedirect(), delegateQueue: nil)
    static var defaultPrompt: String {
        get throws {
            guard let file = Bundle.main.url(forResource: "EnglishPrompt", withExtension: "txt") else { throw Failure("The app is missing its editing prompt.") }
            return try String(contentsOf: file, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines)
        }
    }
    func stop() {
        if let owned, owned.isRunning { owned.terminate() }
        owned = nil
    }
    private func prepare(_ p: Preferences, _ base: URL, key: String) async throws {
        guard p.manageServer else { return }
        try p.validate()
        let health = URL(string: "/health", relativeTo: base)!.absoluteURL
        let deadline = Date().addingTimeInterval(180)
        while Date() < deadline {
            try Task.checkCancellation()
            do {
                let (_, response) = try await session.data(for: URLRequest(url: health, timeoutInterval: 3))
                switch (response as? HTTPURLResponse)?.statusCode {
                case 200: return
                case 503: break
                default: throw Failure("Unexpected llama-server health response.")
                }
            } catch let error as URLError where error.code == .cannotConnectToHost {
                if let owned {
                    if !owned.isRunning { self.owned = nil; throw Failure("Local llama-server exited. Check the GGUF, port and available memory.") }
                } else {
                    let process = Process()
                    process.executableURL = URL(fileURLWithPath: p.executable)
                    process.arguments = ["-m", p.gguf, "--alias", p.model, "--host", base.host!.trimmingCharacters(in: CharacterSet(charactersIn: "[]")),
                        "--port", String(base.port ?? 80), "--ctx-size", String(p.contextSize), "--parallel", "1", "-ngl", p.gpuLayers,
                        "--reasoning", "off", "--no-mmproj", "--log-disable"]
                    var env = ProcessInfo.processInfo.environment
                    if !key.isEmpty { env["LLAMA_API_KEY"] = key }
                    process.environment = env
                    process.standardOutput = FileHandle.nullDevice
                    process.standardError = FileHandle.nullDevice
                    try process.run()
                    owned = process
                }
            }
            try await Task.sleep(nanoseconds: 300_000_000)
        }
        throw Failure("Local llama-server is still loading. Retry when it is ready.")
    }
    func edit(_ transcript: String, _ p: Preferences) async throws -> String {
        let endpoint = try p.endpoint()
        let key = try Secrets.read(p.keyAccount)
        try await prepare(p, endpoint, key: key)
        try Task.checkCancellation()
        var request = URLRequest(url: endpoint.appendingPathComponent("chat/completions"), timeoutInterval: TimeInterval(p.timeout))
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        if !key.isEmpty { request.setValue("Bearer " + key, forHTTPHeaderField: "Authorization") }
        let user = try JSONSerialization.data(withJSONObject: ["transcript": transcript, "glossary": p.glossary])
        let instruction = try p.prompt.isEmpty ? Self.defaultPrompt : p.prompt
        request.httpBody = try JSONSerialization.data(withJSONObject: ["model": p.model, "temperature": p.temperature, "stream": false,
            "chat_template_kwargs": ["enable_thinking": false], "max_tokens": min(4096, max(128, transcript.utf16.count + 64)),
            "messages": [["role": "system", "content": instruction], ["role": "user", "content": String(decoding: user, as: UTF8.self)]]])
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
            throw Failure("llama-server returned HTTP \((response as? HTTPURLResponse)?.statusCode ?? 0). Check the server URL, model and API key.")
        }
        return try Self.completeText(data)
    }
    static func completeText(_ data: Data) throws -> String {
        let json = try JSONSerialization.jsonObject(with: data) as? [String: Any]
        guard let choices = json?["choices"] as? [[String: Any]], choices.count == 1, choices[0]["finish_reason"] as? String == "stop",
              let message = choices[0]["message"] as? [String: Any], let raw = message["content"] as? String else {
            throw Failure("Incomplete llama-server response. Retry or copy the transcript.")
        }
        let text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        let forbidden = CharacterSet.controlCharacters.subtracting(CharacterSet(charactersIn: "\t\r\n"))
        guard !text.isEmpty, text.rangeOfCharacter(from: forbidden) == nil,
              text.range(of: #"</?(think|thinking|reasoning)(\s[^>]*)?>|<\|(im_|channel|message|endof)"#, options: [.regularExpression,.caseInsensitive]) == nil,
              ["reasoning", "reasoning_content", "tool_calls"].allSatisfy({ k in
                  message[k] == nil || message[k] is NSNull || (message[k] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines) == ""
              }) else { throw Failure("llama-server returned empty text or reasoning. Check the Qwen template and reasoning settings.") }
        return text
    }
}
