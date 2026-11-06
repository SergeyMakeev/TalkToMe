using System.Text;
using System.Text.RegularExpressions;

namespace TalkToMe;

// TranslateGemma is a translator, not an editing assistant. Use its published prompt
// inside the standard Gemma turn template; llama-server's generic content adapter
// does not preserve source_lang_code/target_lang_code from Google's typed template.
internal static class TranslateGemma
{
    internal static bool NeedsTranslation(string text) => Regex.IsMatch(text, "[А-Яа-яЁё]");
    internal static Dictionary<string, object> Request(string text, string model)
    {
        using var stream = typeof(EnglishEditor).Assembly.GetManifestResourceStream("TalkToMe.TranslateGemmaPrompt.txt")!;
        using var reader = new StreamReader(stream);
        return new() { ["model"] = model,
            ["messages"] = new[] { new { role = "user", content = reader.ReadToEnd().Trim() + "\n\n\n" + text.Trim() } },
            ["temperature"] = 0, ["max_tokens"] = 2048, ["stream"] = false };
    }

    // A conservative byte budget stays below the model's 2K input-token training
    // length, including its prompt. Split at whitespace, never in a Unicode scalar.
    internal static IEnumerable<(string Text, string Separator)> Chunks(string text)
    {
        var chunks = new List<(string, string)>();
        int offset = 0;
        while (offset < text.Length)
        {
            int end = offset, bytes = 0, split = -1;
            foreach (var rune in text.AsSpan(offset).EnumerateRunes())
            {
                if (bytes + rune.Utf8SequenceLength > 1500) break;
                bytes += rune.Utf8SequenceLength;
                end += rune.Utf16SequenceLength;
                if (Rune.IsWhiteSpace(rune)) split = end;
            }
            if (end < text.Length)
            {
                if (split <= offset) throw new InvalidDataException("A word is too long for TranslateGemma. Use Qwen or copy the transcript.");
                end = split;
            }
            string chunk = text[offset..end];
            string separator = chunk[chunk.TrimEnd().Length..];
            chunks.Add((chunk.Trim(), separator));
            offset = end;
        }
        return chunks;
    }
}
