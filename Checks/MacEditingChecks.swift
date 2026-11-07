import Foundation

private func editingFixture(_ name: String) throws -> [[String: Any]] {
    guard let file = Bundle.main.url(forResource: name, withExtension: "json"),
          let cases = try JSONSerialization.jsonObject(with: Data(contentsOf: file)) as? [[String: Any]] else {
        throw Failure("Missing editing fixture: \(name)")
    }
    return cases
}

func checkEditingResponses() throws {
    let codeSample = "`x—y`\n```text\n- keep this.\n```\n🙂"
    let emojiSample = "Hi 👩🏽‍💻 🇺🇸 1️⃣ 🙂 `literal🙂` __EMOJI_0__"
    let protectedEmoji = DictationStyle.protect(emojiSample)
    guard try DictationStyle.restore(emojiSample, protectedEmoji.text) == emojiSample,
          protectedEmoji.map.count == 4,
          try DictationStyle.restore("🙂", "__EMOJI_0__") == "🙂",
          (try? DictationStyle.restore("🙂", "__EMOJI_0__ __EMOJI_0__")) == nil else {
        throw Failure("Emoji clusters or placeholders were not preserved.")
    }
    guard try DictationStyle.apply(codeSample, codeSample) == codeSample,
          try DictationStyle.apply("", "It’s fine—really.\n1. Fix it.\n- Test it.") == "It's fine-really.\n1. Fix it\n- Test it",
          (try? DictationStyle.apply("`foo()`", "`bar()`")) == nil else {
        throw Failure("Engineering style or code preservation check failed.")
    }
    for item in try editingFixture("EditingResponses") {
        var message: [String: Any] = ["content": item["content"]!]
        for key in ["reasoning_content", "tool_calls", "refusal"] { message[key] = item[key] }
        let data = try JSONSerialization.data(withJSONObject: ["choices": [["finish_reason": item["finish_reason"] ?? "stop", "message": message]]])
        let actual = try? EnglishEditor.completeText(data, structured: true, transcript: "Раз, два, три, четыре, пять.")
        guard actual == item["expected"] as? String else { throw Failure("Editing response check failed: \(item["name"]!)") }
    }
    let long = String(repeating: "The speaker is counting. ", count: 20)
    let content = try JSONSerialization.data(withJSONObject: ["text": long])
    let data = try JSONSerialization.data(withJSONObject: ["choices": [["finish_reason": "stop", "message": ["content": String(decoding: content, as: UTF8.self)]]]])
    guard (try? EnglishEditor.completeText(data, structured: true, transcript: "Раз, два.")) == nil,
          try EnglishEditor.completeText(data, structured: true, transcript: long) == long.trimmingCharacters(in: .whitespacesAndNewlines) else {
        throw Failure("Unexpected expansion check failed.")
    }
    for model in Translation.allCases where model.bundled && model != .translategemma {
        var p = Preferences(); p.translation = model
        let request = try EnglishEditor.requestBody("Проверка", p)
        let asset = try ModelDownload.get(model.rawValue)
        guard request["response_format"] != nil, try p.requiredModels().contains(where: { $0.name == asset.name }) else {
            throw Failure("Local editing format or model missing.")
        }
        let messages = request["messages"] as! [[String: String]]
        guard messages.count == (model == .qwen08 ? 2 : 8) else { throw Failure("Editing examples missing.") }
        p.prompt = "Custom instructions."
        guard (try EnglishEditor.requestBody("Test", p)["messages"] as! [[String: String]]).count == 2 else {
            throw Failure("Examples override a custom prompt.")
        }
    }
    print("Structured editing response and request checks passed.")
    var p = Preferences(); p.translation = .translategemma
    let request = try EnglishEditor.requestBody("Привет", p)
    guard request["response_format"] == nil, (request["messages"] as? [[String: String]])?.count == 1 else {
        throw Failure("TranslateGemma must use its dedicated translation format.")
    }
    let input = String(repeating: "Привет, мир! 🙂\n", count: 300)
    let chunks = try TranslateGemma.chunks(input)
    guard chunks.count > 1, chunks.allSatisfy({ $0.text.utf8.count <= 1500 }), chunks.map({ $0.text + $0.separator }).joined() == input else {
        throw Failure("Translation chunking lost Unicode text.")
    }
}

@MainActor func checkTranslationCases(_ editor: EnglishEditor, _ preferences: Preferences) async throws {
    for item in try editingFixture(preferences.translation == .translategemma ? "TranslateGemmaCases" : "TranslationCases") {
        var p = preferences; p.glossary = item["glossary"] as! [String]
        let text = try await editor.edit(item["transcript"] as! String, p)
        let lower = text.lowercased()
        guard (item["contains"] as! [String]).allSatisfy({ lower.contains($0.lowercased()) }),
              (item["excludes"] as! [String]).allSatisfy({ !lower.contains($0.lowercased()) }),
              (item["matches"] as? [String] ?? []).allSatisfy({ lower.range(of: $0, options: .regularExpression) != nil }) else {
            throw Failure("Translation regression (\(p.translation)): \(item["name"]!): \(text)")
        }
        print("\(p.translation): \(item["name"]!): \(text)")
    }
}
