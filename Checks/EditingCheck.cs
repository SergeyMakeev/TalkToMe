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
            Assert(!body.GetProperty("messages")[0].GetProperty("content").GetString()!.Contains("English"), "custom editing language preserved");
            using (var user = JsonDocument.Parse(body.GetProperty("messages")[1].GetProperty("content").GetString()!))
                Assert(user.RootElement.GetProperty("transcript").GetString() == "Test", "custom editing receives dictation data");
            settings.Translation = "server";
            body = JsonSerializer.SerializeToElement(EnglishEditor.RequestBody("Test", settings, model));
            Assert(!body.TryGetProperty("response_format", out _) && body.GetProperty("messages")[0].GetProperty("content").GetString() == settings.Prompt,
                "external server compatibility");
        }
        Console.WriteLine("Structured editing response and request checks passed.");
        var gemma = Settings.Defaults(Environment.CurrentDirectory);
        gemma.Translation = "translategemma"; gemma.SpeechModel = "parakeet"; gemma.Validate();
        Assert(gemma.RequiredModels().SequenceEqual(new[] { "parakeet", "translategemma" }), "Parakeet needs no Whisper VAD");
        var gemmaRequest = JsonSerializer.SerializeToElement(EnglishEditor.RequestBody("Привет", gemma, "test"));
        Assert(!gemmaRequest.TryGetProperty("response_format", out _) && gemmaRequest.GetProperty("messages").GetArrayLength() == 1, "dedicated translation format");
        string longInput = string.Concat(Enumerable.Repeat("Привет, мир! 🙂\n", 300));
        var chunks = TranslateGemma.Chunks(longInput).ToArray();
        Assert(chunks.Length > 1 && chunks.All(c => System.Text.Encoding.UTF8.GetByteCount(c.Text) <= 1500) &&
            string.Concat(chunks.Select(c => c.Text + c.Separator)) == longInput, "lossless Unicode translation chunks");
        using var gemmaEditor = new EnglishEditor(gemma);
        Assert(gemmaEditor.EditAsync("Keep this English text.", default).GetAwaiter().GetResult() == "Keep this English text.", "English needs no translator download");
    }
    internal static async Task RunModels(string directory, string model, bool gpu = true)
    {
        var settings = Settings.Defaults(Environment.CurrentDirectory);
        settings.ModelDirectory = Path.GetFullPath(directory); settings.Translation = model;
        settings.UseGpu = gpu;
        settings.Validate();
        if (!settings.LocalTranslation) throw new ArgumentException("Choose a bundled translator.");
        await ModelDownload.Get(model).InstallAsync(settings.ModelDirectory, new Progress<string>(), default);
        using var editor = new EnglishEditor(settings);
        foreach (var item in Cases(model == "translategemma" ? "TranslateGemmaCases" : "TranslationCases").EnumerateArray())
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
