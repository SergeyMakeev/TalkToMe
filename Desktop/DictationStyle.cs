using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;

namespace TalkToMe;

internal static class DictationStyle
{
    internal static bool MissingQuestion(string source, string result) =>
        Protect(source).Text.Contains('?') && !Protect(result).Text.Contains('?');
    private static readonly Regex Code = new(@"(?<!`)(`+)(?!`)[\s\S]*?(?<!`)\1(?!`)");
    private static bool IsEmoji(string element) => element.EnumerateRunes().Any(r => r.Value is >= 0x1F000 and <= 0x1FAFF or >= 0x2600 and <= 0x27BF or 0xFE0F or 0x20E3);
    private static string Prefix(string source)
    {
        string prefix = "__TTM_";
        while (source.Contains(prefix)) prefix = "_" + prefix;
        return prefix;
    }
    internal static (string Text, List<(string Token, string Value)> Map) Protect(string source)
    {
        string prefix = Prefix(source);
        var map = new List<(string, string)>();
        var text = new StringBuilder();
        void Preserve(string value, string kind)
        {
            string token = "`" + prefix + kind + "_" + map.Count + "__`";
            map.Add((token, value)); text.Append(token);
        }
        void Prose(string value)
        {
            var elements = StringInfo.GetTextElementEnumerator(value);
            while (elements.MoveNext())
            {
                string element = elements.GetTextElement();
                if (IsEmoji(element)) Preserve(element, "EMOJI"); else text.Append(element);
            }
        }
        int offset = 0;
        foreach (Match code in Code.Matches(source))
        {
            Prose(source[offset..code.Index]); Preserve(code.Value, "CODE");
            offset = code.Index + code.Length;
        }
        Prose(source[offset..]);
        return (text.ToString(), map);
    }
    internal static string Restore(string source, string result)
    {
        foreach (var (token, value) in Protect(source).Map)
        {
            string bare = token.Trim('`');
            if (result.Split(bare).Length != 2) throw new InvalidDataException("The editor changed protected code or an emoji. Retry processing or copy the source transcript.");
            result = result.Replace(result.Contains(token) ? token : bare, value);
        }
        if (result.Contains(Prefix(source))) throw new InvalidDataException("The editor added an unknown placeholder. Retry processing or copy the source transcript.");
        return result;
    }
    internal static string Apply(string source, string result)
    {
        var originalCode = Code.Matches(source).Select(m => m.Value);
        if (!originalCode.SequenceEqual(Code.Matches(result).Select(m => m.Value)))
            throw new InvalidDataException("The editor changed a code snippet. Retry processing or copy the source transcript.");
        var output = new StringBuilder();
        var protectedCode = new List<(string Token, string Text)>();
        string prefix = "TTM_CODE_" + Guid.NewGuid().ToString("N") + "_";
        int offset = 0;
        foreach (Match code in Code.Matches(result))
        {
            output.Append(Punctuation(result[offset..code.Index]));
            string placeholder = prefix + protectedCode.Count;
            protectedCode.Add((placeholder, code.Value));
            output.Append(placeholder);
            offset = code.Index + code.Length;
        }
        output.Append(Punctuation(result[offset..]));
        // Only remove a prose period after a list item, never a character inside code.
        var text = output.ToString();
        text = Regex.Replace(text, @"(?m)^([ \t]*(?:[-*+] |\d+[.)] )[^\r\n]*)\.(?=\r?$)", "$1");
        foreach (var code in protectedCode) text = text.Replace(code.Token, code.Text);
        return text;
    }
    private static string Punctuation(string text) => text.Replace('—', '-').Replace('–', '-')
        .Replace('“', '"').Replace('”', '"').Replace('‘', '\'').Replace('’', '\'')
        .Replace("…", "...").Replace(' ', ' ').Replace("• ", "- ");
}
