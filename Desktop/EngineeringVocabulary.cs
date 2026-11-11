using System.Text.Json;
using System.Text.RegularExpressions;

namespace TalkToMe;

// Supply bounded, contextual hints to the editor, never blindly replace transcript words.
internal static class EngineeringVocabulary
{
    private sealed record Entry(string Term, string[] Aliases);
    private static readonly (Entry Entry, Regex Pattern)[] Catalog = Load();
    private static (Entry, Regex)[] Load()
    {
        using var file = typeof(EngineeringVocabulary).Assembly.GetManifestResourceStream("TalkToMe.EngineeringTerms.json")!;
        var entries = JsonSerializer.Deserialize<Entry[]>(file, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        return entries.Select(e => (e, new Regex(@"(?<![\p{L}\p{N}_])(?:" +
            string.Join("|", e.Aliases.OrderByDescending(a => a.Length).Select(Regex.Escape)) + @")(?![\p{L}\p{N}_])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))).ToArray();
    }
    internal static object[] Hints(string transcript, string[] glossary)
    {
        var hints = new List<object>();
        int end = 0;
        foreach (var item in Catalog.Select(e => (e.Entry, Match: e.Pattern.Match(transcript)))
            .Where(e => e.Match.Success).OrderBy(e => e.Match.Index).ThenByDescending(e => e.Match.Length))
        {
            if (item.Match.Index < end) continue; // Prefer WebGPU over the GPU within its spoken name.
            hints.Add(new {
                term = glossary.FirstOrDefault(g => g.Equals(item.Entry.Term, StringComparison.OrdinalIgnoreCase)) ?? item.Entry.Term,
                heard = item.Match.Value
            });
            end = item.Match.Index + item.Match.Length;
            if (hints.Count == 24) break;
        }
        return hints.ToArray();
    }
}
