using System.Text.Json;
using System.Text.RegularExpressions;
using TalkToMe;

internal static class EditingCheck
{
    private static JsonElement Cases(string name)
    {
        using var file = typeof(EditingCheck).Assembly.GetManifestResourceStream("TalkToMe.Checks." + name + ".json")!;
        using var json = JsonDocument.Parse(file);
        return json.RootElement.Clone();
    }
    private static void Assert(bool value, string label)
    { if (!value) throw new Exception("Editing check failed: " + label); }
    private static string? Parse(object message, string finish = "stop", string transcript = "Раз, два, три, четыре, пять.")
    {
        var response = JsonSerializer.SerializeToElement(new { choices = new[] { new { finish_reason = finish, message } } });
        try { return EnglishEditor.CompleteText(response, structured: true, transcript); }
        catch (InvalidDataException) { return null; }
    }
    internal static void Check()
    {
        foreach (var item in Cases("EditingResponses").EnumerateArray())
        {
            var message = new Dictionary<string, object> { ["content"] = item.GetProperty("content") };
            foreach (string key in new[] { "reasoning_content", "tool_calls", "refusal" })
                if (item.TryGetProperty(key, out var value)) message[key] = value;
            string finish = item.TryGetProperty("finish_reason", out var reason) ? reason.GetString()! : "stop";
            string? expected = item.TryGetProperty("expected", out var text) ? text.GetString() : null;
            Assert(Parse(message, finish) == expected, item.GetProperty("name").GetString()!);
        }
        string longText = string.Concat(Enumerable.Repeat("The speaker is counting. ", 20)).Trim();
        var longMessage = new { content = JsonSerializer.Serialize(new { text = longText }) };
        Assert(Parse(longMessage) == null && Parse(longMessage, transcript: longText) == longText, "unexpected expansion vs legitimate long dictation");
        foreach (string model in new[] { "qwen08", "qwen2", "qwen4" })
        {
            var settings = Settings.Defaults(Environment.CurrentDirectory); settings.Translation = model;
            settings.Validate();
            Assert(settings.RequiredModels().Contains(model) && ModelDownload.Get(model).Size > 0, "selectable model " + model);
            var body = JsonSerializer.SerializeToElement(EnglishEditor.RequestBody("Проверка", settings, model));
            Assert(body.GetProperty("messages").GetArrayLength() == (model == "qwen08" ? 2 : 6), "examples " + model);
            Assert(body.GetProperty("response_format").GetProperty("type").GetString() == "json_schema", "constrained format " + model);
            settings.Prompt = "Custom instructions.";
            body = JsonSerializer.SerializeToElement(EnglishEditor.RequestBody("Test", settings, model));
            Assert(body.GetProperty("messages").GetArrayLength() == 2, "custom prompt excludes examples");
            settings.Translation = "server";
            body = JsonSerializer.SerializeToElement(EnglishEditor.RequestBody("Test", settings, model));
            Assert(!body.TryGetProperty("response_format", out _) && body.GetProperty("messages")[0].GetProperty("content").GetString() == settings.Prompt,
                "external server compatibility");
        }
        Console.WriteLine("Structured editing response and request checks passed.");
    }
    internal static async Task RunModels(string directory, string model)
    {
        var settings = Settings.Defaults(Environment.CurrentDirectory);
        settings.ModelDirectory = Path.GetFullPath(directory); settings.Translation = model;
        settings.Validate();
        if (!settings.LocalTranslation) throw new ArgumentException("Choose a bundled translator.");
        await ModelDownload.Get(model).InstallAsync(settings.ModelDirectory, new Progress<string>(), default);
        using var editor = new EnglishEditor(settings);
        foreach (var item in Cases("TranslationCases").EnumerateArray())
        {
            settings.Glossary = item.GetProperty("glossary").EnumerateArray().Select(x => x.GetString()!).ToArray();
            string text = await editor.EditAsync(item.GetProperty("transcript").GetString()!, default);
            Console.WriteLine(model + ": " + item.GetProperty("name").GetString() + ": " + text);
            Assert(item.GetProperty("contains").EnumerateArray().All(x => text.Contains(x.GetString()!, StringComparison.OrdinalIgnoreCase)) &&
                item.GetProperty("excludes").EnumerateArray().All(x => !text.Contains(x.GetString()!, StringComparison.OrdinalIgnoreCase)) &&
                (!item.TryGetProperty("matches", out var patterns) || patterns.EnumerateArray().All(x => Regex.IsMatch(text, x.GetString()!, RegexOptions.IgnoreCase))),
                model + ": " + item.GetProperty("name").GetString());
        }
    }
}
