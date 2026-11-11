import Foundation

enum EngineeringVocabulary {
    private struct Entry: Decodable { let term: String; let aliases: [String] }
    private static let catalog: Result<[(Entry, NSRegularExpression)], Error> = Result {
        guard let url = Bundle.main.url(forResource: "EngineeringTerms", withExtension: "json") else {
            throw Failure("The app is missing its engineering dictionary.")
        }
        return try JSONDecoder().decode([Entry].self, from: Data(contentsOf: url)).map { entry in
            let aliases = entry.aliases.sorted { $0.utf16.count > $1.utf16.count }.map(NSRegularExpression.escapedPattern(for:)).joined(separator: "|")
            return (entry, try NSRegularExpression(pattern: #"(?<![\p{L}\p{N}_])(?:"# + aliases + #")(?![\p{L}\p{N}_])"#, options: .caseInsensitive))
        }
    }
    static func hints(_ transcript: String, _ glossary: [String]) throws -> [[String: String]] {
        let input = transcript as NSString
        let matches = try catalog.get().enumerated().compactMap { index, item -> (Int, Int, Int, String, String)? in
            let (entry, pattern) = item
            guard let match = pattern.firstMatch(in: transcript, range: NSRange(location: 0, length: input.length)) else { return nil }
            let term = glossary.first { $0.caseInsensitiveCompare(entry.term) == .orderedSame } ?? entry.term
            return (match.range.location, match.range.length, index, term, input.substring(with: match.range))
        }.sorted { a, b in
            if a.0 != b.0 { return a.0 < b.0 }
            if a.1 != b.1 { return a.1 > b.1 }
            return a.2 < b.2
        }
        var hints: [[String: String]] = []
        var end = 0
        for item in matches where item.0 >= end {
            hints.append(["term": item.3, "heard": item.4]); end = item.0 + item.1
            if hints.count == 24 { break }
        }
        return hints
    }
}
