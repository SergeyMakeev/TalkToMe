import Foundation

// The native cancellation flag is atomic; Swift state and the process callback
// are protected by lock when accessed from the UI and inference queues.
final class SpeechJob: @unchecked Sendable {
    let pointer = ttm_new_job()!
    private let lock = NSLock()
    private var cancelled = false
    private var cancellation: (() -> Void)?
    func cancel() {
        lock.lock(); defer { lock.unlock() }
        cancelled = true; ttm_cancel(pointer); cancellation?()
    }
    func onCancel(_ action: (() -> Void)?) {
        lock.lock(); defer { lock.unlock() }
        cancellation = action
        if cancelled { action?() }
    }
    func checkCancellation() throws {
        lock.lock(); defer { lock.unlock() }
        if cancelled { throw CancellationError() }
    }
    deinit { ttm_free_job(pointer) }
}

final class SpeechEngine {
    private let queue = DispatchQueue(label: "TalkToMe.whisper", qos: .userInitiated)
    private var model: UnsafeMutableRawPointer?
    private var path = ""
    private var metal = false
    private var closed = false
    private let parakeet = ParakeetWorker()
    deinit { if let model { ttm_close(model) } }

    // NSApplication.terminate calls exit(), which does not release Swift owners.
    // Drain inference and free its buffers before ggml's global Metal destructor.
    func close(completion: @escaping @Sendable () -> Void) {
        queue.async {
            self.closed = true
            self.parakeet.stop()
            if let model = self.model { ttm_close(model); self.model = nil }
            self.path = ""
            completion()
        }
    }

    func recognize(_ pcm: [Float], _ p: Preferences, _ job: SpeechJob) async throws -> String {
        try await withCheckedThrowingContinuation { continuation in
            queue.async {
                continuation.resume(with: Result { try self.transcribe(pcm, p, job) })
            }
        }
    }
    // Called on the model queue, or from the standalone transcription check.
    func transcribe(_ pcm: [Float], _ p: Preferences, _ job: SpeechJob) throws -> String {
        guard !closed else { throw CancellationError() }
        try job.checkCancellation()
        guard pcm.count >= 1600, pcm.contains(where: { abs($0) > 0.0001 }) else { return "" }
        if p.parakeet {
            guard FileManager.default.isReadableFile(atPath: p.whisper) else { throw Failure("Open Settings → Models to download Parakeet.") }
            if let model { ttm_close(model); self.model = nil }
            return try parakeet.transcribe(pcm, p, job)
        }
        parakeet.stop()
        guard FileManager.default.isReadableFile(atPath: p.whisper),
              p.vad.isEmpty || FileManager.default.isReadableFile(atPath: p.vad) else {
            throw Failure("Open Settings → Models to download speech models, or choose your files in Speech.")
        }
        if model == nil || path != p.whisper || metal != p.useMetal {
            Diagnostics.event("speech.load", "Whisper; Metal requested=\(p.useMetal)")
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
        defer { ttm_free_text(result) }
        return String(cString: result).trimmingCharacters(in: .whitespacesAndNewlines)
    }
}

private final class NoRedirect: NSObject, URLSessionTaskDelegate {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) { completionHandler(nil) }
}

@MainActor final class EnglishEditor {
    private var owned: Process?
    private var port = Int.random(in: 49152...65535)
    private let privateKey = UUID().uuidString
    private let privateAlias = UUID().uuidString
    private var verified = false
    private var closing = false
    private let session = URLSession(configuration: .ephemeral, delegate: NoRedirect(), delegateQueue: nil)
    private let loopback: URLSession = {
        let config = URLSessionConfiguration.ephemeral
        config.connectionProxyDictionary = [:]
        return URLSession(configuration: config, delegate: NoRedirect(), delegateQueue: nil)
    }()
    nonisolated static var defaultPrompt: String {
        get throws {
            guard let file = Bundle.main.url(forResource: "EnglishPrompt", withExtension: "txt") else { throw Failure("The app is missing its editing prompt.") }
            return try String(contentsOf: file, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines)
        }
    }
    nonisolated static func requestBody(_ transcript: String, _ p: Preferences, preserveQuestions: Bool = false) throws -> [String: Any] {
        if p.translation == .translategemma { return try TranslateGemma.request(transcript, model: p.model) }
        let defaults = p.prompt.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        var instruction = try defaults ? defaultPrompt : p.prompt
        var format: [String: Any] = [:]
        if defaults || p.translation.bundled {
            guard let file = Bundle.main.url(forResource: "LocalEditing", withExtension: "json"),
                  let loaded = try JSONSerialization.jsonObject(with: Data(contentsOf: file)) as? [String: Any],
                  let suffix = loaded[defaults ? "instruction" : "custom_instruction"] as? String else { throw Failure("The app is missing its editing format.") }
            format = loaded
            if defaults {
                guard let mode = loaded[p.processing.rawValue + "_instruction"] as? String else { throw Failure("The app is missing its processing instructions.") }
                instruction += "\n" + mode
            }
            if p.translation.bundled { instruction += "\n" + suffix }
        }
        var messages: [[String: String]] = [["role": "system", "content": instruction]]
        // The tiny model tends to copy examples; larger models benefit from them.
        if p.translation.bundled && p.translation != .qwen08 && defaults,
           let examples = format[p.processing == .heavy ? "heavy_examples" : "examples"] as? [[String: String]] { messages += examples }
        let protectedInput = defaults ? DictationStyle.protect(transcript).text : transcript
        var input: [String: Any] = ["transcript": protectedInput, "glossary": p.glossary]
        if defaults && p.engineeringTerms { input["engineering_terms"] = try EngineeringVocabulary.hints(protectedInput, p.glossary) }
        let user = try JSONSerialization.data(withJSONObject: input, options: [.sortedKeys])
        let prefix = defaults && p.translation.bundled ? (format[p.processing.rawValue + "_prefix"] as? String ?? "") : ""
        let reminder = preserveQuestions ? "Preserve the speaker's questions as questions ending in '?'. Do not turn them into statements or answer them.\n" : ""
        messages.append(["role": "user", "content": reminder + prefix + String(decoding: user, as: UTF8.self)])
        var body: [String: Any] = ["model": p.model, "temperature": p.temperature, "stream": false,
            "chat_template_kwargs": ["enable_thinking": false], "max_tokens": min(4096, max(160, protectedInput.utf16.count + 96)),
            "messages": messages]
        if p.translation.bundled { body["response_format"] = format["response_format"] }
        return body
    }
    func stop() {
        if let owned, owned.isRunning { owned.terminate() }
        owned = nil
        verified = false
        port = Int.random(in: 49152...65535)
    }
    func close() {
        closing = true
        session.invalidateAndCancel()
        loopback.invalidateAndCancel()
        stop()
    }
    private func launch(_ p: Preferences, _ base: URL, key: String) throws {
        try Task.checkCancellation()
        guard !closing else { throw CancellationError() }
        let process = Process()
        process.executableURL = URL(fileURLWithPath: p.serverExecutable)
        var arguments = ["-m", p.gguf, "--alias", p.model, "--host", base.host!.trimmingCharacters(in: CharacterSet(charactersIn: "[]")),
            "--port", String(base.port ?? 80), "--ctx-size", String(p.contextSize), "--parallel", "1", "-ngl", p.gpuLayers,
            "--reasoning", "off", "--no-mmproj", "--log-disable"]
        if p.translation.bundled {
            arguments += ["--offline", "--no-webui"]
            if !p.useMetal { arguments += ["--device", "none"] }
        }
        if p.translation == .translategemma { arguments += ["--no-jinja", "--chat-template", "gemma"] }
        process.arguments = arguments
        var env = ProcessInfo.processInfo.environment
        if !key.isEmpty { env["LLAMA_API_KEY"] = key }
        process.environment = env
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        try process.run()
        Diagnostics.event("editor.started", "PID=\(process.processIdentifier); bundled=\(p.translation.bundled); Metal requested=\(p.useMetal)")
        owned = process
    }
    private func prepare(_ p: Preferences, _ base: URL, key: String) async throws {
        guard p.manageServer else { return }
        try p.validate()
        let health = URL(string: "/health", relativeTo: base)!.absoluteURL
        let client = p.translation.bundled ? loopback : session
        let deadline = Date().addingTimeInterval(180)
        while Date() < deadline {
            try Task.checkCancellation()
            if let owned, !owned.isRunning {
                Diagnostics.event("editor.exited", "PID=\(owned.processIdentifier); reason=\(owned.terminationReason.rawValue); status=\(owned.terminationStatus)")
                stop()
                throw Failure("Local translation stopped. Check available memory and retry.")
            }
            if p.translation.bundled && owned == nil { try launch(p, base, key: key) }
            do {
                let (_, response) = try await client.data(for: URLRequest(url: health, timeoutInterval: 3))
                switch (response as? HTTPURLResponse)?.statusCode {
                case 200:
                    if p.translation.bundled && !verified {
                        var request = URLRequest(url: base.appendingPathComponent("models"), timeoutInterval: 3)
                        request.setValue("Bearer " + key, forHTTPHeaderField: "Authorization")
                        let (data, _) = try await client.data(for: request)
                        let json = try JSONSerialization.jsonObject(with: data) as? [String: Any]
                        guard let models = json?["data"] as? [[String: Any]], models.contains(where: { $0["id"] as? String == privateAlias }) else {
                            throw Failure("Could not connect to this app’s translation model. Retry processing.")
                        }
                        verified = true
                    }
                    return
                case 503: break
                default: throw Failure("Unexpected llama-server health response.")
                }
            } catch let error as URLError where error.code == .cannotConnectToHost {
                if owned == nil { try launch(p, base, key: key) }
            }
            try await Task.sleep(nanoseconds: 300_000_000)
        }
        throw Failure("Local llama-server is still loading. Retry when it is ready.")
    }
    func edit(_ transcript: String, _ preferences: Preferences) async throws -> String {
        if !preferences.processText { return transcript }
        if preferences.translation != .translategemma {
            var text = try await editChunk(transcript, preferences)
            if preferences.prompt.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && DictationStyle.missingQuestion(transcript, text) {
                Diagnostics.event("editing.retry", "Question preservation")
                text = try await editChunk(transcript, preferences, preserveQuestions: true)
                if DictationStyle.missingQuestion(transcript, text) {
                    throw Failure("The editor removed a question. Retry processing or copy the source transcript.")
                }
            }
            return text
        }
        if !TranslateGemma.needsTranslation(transcript) { return transcript }
        var result = ""
        for chunk in try TranslateGemma.chunks(transcript) {
            try Task.checkCancellation()
            result += TranslateGemma.needsTranslation(chunk.text) ? try await editChunk(chunk.text, preferences) : chunk.text
            result += chunk.separator
        }
        return result.trimmingCharacters(in: .whitespacesAndNewlines)
    }
    private func editChunk(_ transcript: String, _ preferences: Preferences, preserveQuestions: Bool = false) async throws -> String {
        var p = preferences
        if !p.processText { return transcript }
        if p.translation.bundled {
            let asset = try ModelDownload.get(p.translation.rawValue)
            guard asset.installed else { throw Failure("Open Settings → Models to download your translation model.") }
            p.serverURL = "http://127.0.0.1:\(port)/v1"
            p.manageServer = true
            p.executable = ""
            p.gguf = asset.path
            p.model = privateAlias
            p.contextSize = 8192
            p.timeout = max(300, p.timeout)
            p.gpuLayers = p.useMetal ? "all" : "0"
        }
        let endpoint = try p.endpoint()
        let key = try p.translation.bundled ? privateKey : Secrets.read(p.keyAccount)
        let preparation = ProcessInfo.processInfo.systemUptime
        try await prepare(p, endpoint, key: key)
        Diagnostics.timing("editor_ready", since: preparation)
        try Task.checkCancellation()
        var request = URLRequest(url: endpoint.appendingPathComponent("chat/completions"), timeoutInterval: TimeInterval(p.timeout))
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        if !key.isEmpty { request.setValue("Bearer " + key, forHTTPHeaderField: "Authorization") }
        request.httpBody = try JSONSerialization.data(withJSONObject: Self.requestBody(transcript, p, preserveQuestions: preserveQuestions))
        let client = p.translation.bundled ? loopback : session
        let requested = ProcessInfo.processInfo.systemUptime
        let (data, response) = try await client.data(for: request)
        Diagnostics.timing("editor_request", since: requested)
        guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
            throw Failure("llama-server returned HTTP \((response as? HTTPURLResponse)?.statusCode ?? 0). Check the server URL, model and API key.")
        }
        let guardInput = p.prompt.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && p.translation != .translategemma ? DictationStyle.protect(transcript).text : transcript
        let text = try Self.completeText(data, structured: p.translation.bundled && p.translation != .translategemma, transcript: guardInput)
        if p.translation == .translategemma && text.utf16.count > max(256, transcript.utf16.count * 4) {
            throw Failure("TranslateGemma expanded the dictation unexpectedly. Retry or copy the transcript.")
        }
        if p.translation != .translategemma && p.prompt.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return try DictationStyle.apply(transcript, DictationStyle.restore(transcript, text))
        }
        return text
    }
    nonisolated static func completeText(_ data: Data, structured: Bool = false, transcript: String? = nil) throws -> String {
        let json = try JSONSerialization.jsonObject(with: data) as? [String: Any]
        guard let choices = json?["choices"] as? [[String: Any]], choices.count == 1,
              let message = choices[0]["message"] as? [String: Any], let raw = message["content"] as? String else {
            throw Failure("Incomplete llama-server response. Retry or copy the transcript.")
        }
        if choices[0]["finish_reason"] as? String == "length" {
            throw Failure("The translation reached its output limit. Retry or copy the transcript.")
        }
        guard choices[0]["finish_reason"] as? String == "stop" else {
            throw Failure("The translator stopped before completing the text. Retry or copy the transcript.")
        }
        guard ["reasoning", "reasoning_content", "tool_calls", "refusal"].allSatisfy({ k in
            message[k] == nil || message[k] is NSNull || (message[k] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines) == ""
        }) else { throw Failure("The translator returned reasoning or an invalid result. Retry or copy the transcript.") }
        var text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        if structured {
            guard let object = try? JSONSerialization.jsonObject(with: Data(text.utf8)),
                  let result = object as? [String: Any], result.count == 1, let final = result["text"] as? String else {
                throw Failure("The translator returned commentary or an invalid result. Try Qwen 2B or 4B, or copy the transcript.")
            }
            text = final.trimmingCharacters(in: .whitespacesAndNewlines)
            if let transcript, text.utf16.count > max(256, transcript.utf16.count * 4) {
                throw Failure("The translator expanded the dictation unexpectedly. Try a larger model or copy the transcript.")
            }
        }
        let forbidden = CharacterSet.controlCharacters.subtracting(CharacterSet(charactersIn: "\t\r\n"))
        guard !text.isEmpty, text.rangeOfCharacter(from: forbidden) == nil,
              text.range(of: #"</?(think|thinking|reasoning)(\s[^>]*)?>|<\|(im_|channel|message|endof)"#, options: [.regularExpression,.caseInsensitive]) == nil else {
            throw Failure("llama-server returned empty text or reasoning. Check the Qwen template and reasoning settings.")
        }
        return text
    }
}
