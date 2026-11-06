import Foundation

enum TranslateGemma {
    static func needsTranslation(_ text: String) -> Bool {
        text.range(of: "[А-Яа-яЁё]", options: .regularExpression) != nil
    }
    static func request(_ text: String, model: String) throws -> [String: Any] {
        guard let file = Bundle.main.url(forResource: "TranslateGemmaPrompt", withExtension: "txt") else {
            throw Failure("The app is missing its translation prompt.")
        }
        let prompt = try String(contentsOf: file, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines)
        return ["model": model, "messages": [["role": "user", "content": prompt + "\n\n\n" + text.trimmingCharacters(in: .whitespacesAndNewlines)]],
                "temperature": 0, "max_tokens": 2048, "stream": false]
    }
    static func chunks(_ text: String) throws -> [(text: String, separator: String)] {
        var result: [(String, String)] = []
        var start = text.startIndex
        while start < text.endIndex {
            var end = start, split: String.Index?, bytes = 0
            while end < text.endIndex {
                let next = text.index(after: end)
                let size = text[end..<next].utf8.count
                if bytes + size > 1500 { break }
                bytes += size
                if text[end].isWhitespace { split = next }
                end = next
            }
            if end < text.endIndex {
                guard let boundary = split, boundary > start else {
                    throw Failure("A word is too long for TranslateGemma. Use Qwen or copy the transcript.")
                }
                end = boundary
            }
            let chunk = String(text[start..<end])
            let trailing = String(chunk.reversed().prefix(while: { $0.isWhitespace }).reversed())
            result.append((chunk.trimmingCharacters(in: .whitespacesAndNewlines), trailing))
            start = end
        }
        return result
    }
}
