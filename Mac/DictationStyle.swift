import Foundation

enum DictationStyle {
    private static func prefix(_ source: String) -> String {
        var prefix = "__TTM_"
        while source.contains(prefix) { prefix = "_" + prefix }
        return prefix
    }
    static func protect(_ source: String) -> (text: String, map: [(String, String)]) {
        let prefix = Self.prefix(source)
        var map: [(String, String)] = []
        var text = ""
        let code = try! NSRegularExpression(pattern: #"(?<!`)(`+)(?!`)[\s\S]*?(?<!`)\1(?!`)"#)
        let spans = code.matches(in: source, range: NSRange(source.startIndex..., in: source))
        func preserve(_ value: String, _ kind: String) {
            let token = "`" + prefix + kind + "_" + String(map.count) + "__`"
            map.append((token, value)); text += token
        }
        func prose(_ value: String) {
            for character in value {
                let emoji = character.unicodeScalars.contains { (0x1F000...0x1FAFF).contains($0.value) || (0x2600...0x27BF).contains($0.value) || $0.value == 0xFE0F || $0.value == 0x20E3 }
                if emoji { preserve(String(character), "EMOJI") } else { text.append(character) }
            }
        }
        let input = source as NSString
        var offset = 0
        for span in spans {
            prose(input.substring(with: NSRange(location: offset, length: span.range.location - offset)))
            preserve(input.substring(with: span.range), "CODE")
            offset = NSMaxRange(span.range)
        }
        prose(input.substring(from: offset))
        return (text, map)
    }
    static func restore(_ source: String, _ result: String) throws -> String {
        var text = result
        for (token, emoji) in protect(source).map {
            let bare = token.trimmingCharacters(in: CharacterSet(charactersIn: "`"))
            guard text.components(separatedBy: bare).count == 2 else {
                throw Failure("The editor changed protected code or an emoji. Retry processing or copy the source transcript.")
            }
            text = text.replacingOccurrences(of: text.contains(token) ? token : bare, with: emoji)
        }
        guard !text.contains(prefix(source)) else {
            throw Failure("The editor added an unknown placeholder. Retry processing or copy the source transcript.")
        }
        return text
    }
    static func apply(_ source: String, _ result: String) throws -> String {
        let code = try NSRegularExpression(pattern: #"(?<!`)(`+)(?!`)[\s\S]*?(?<!`)\1(?!`)"#)
        func snippets(_ text: String) -> [String] {
            code.matches(in: text, range: NSRange(text.startIndex..., in: text)).map { (text as NSString).substring(with: $0.range) }
        }
        guard snippets(source) == snippets(result) else {
            throw Failure("The editor changed a code snippet. Retry processing or copy the source transcript.")
        }
        var text = result
        let prefix = "TTM_CODE_" + UUID().uuidString + "_"
        let spans = code.matches(in: result, range: NSRange(result.startIndex..., in: result))
        var protected: [(String, String)] = []
        for (index, span) in spans.enumerated().reversed() {
            let token = prefix + String(index)
            protected.append((token, (result as NSString).substring(with: span.range)))
            text = (text as NSString).replacingCharacters(in: span.range, with: token)
        }
        for (from, to) in [("—", "-"), ("–", "-"), ("“", "\""), ("”", "\""), ("‘", "'"), ("’", "'"), ("…", "..."), ("\u{00a0}", " "), ("• ", "- ")] {
            text = text.replacingOccurrences(of: from, with: to)
        }
        let lists = try NSRegularExpression(pattern: #"(?m)^([ \t]*(?:[-*+] |\d+[.)] )[^\r\n]*)\.(?=\r?$)"#)
        text = lists.stringByReplacingMatches(in: text, range: NSRange(text.startIndex..., in: text), withTemplate: "$1")
        for (token, value) in protected { text = text.replacingOccurrences(of: token, with: value) }
        return text
    }
}
