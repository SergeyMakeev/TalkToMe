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
        QuestionRetryCheck.Run().GetAwaiter().GetResult();
        Assert(DictationStyle.MissingQuestion("Why did it fail?", "It failed.") &&
            !DictationStyle.MissingQuestion("Why did it fail?", "Why did it fail?") &&
            !DictationStyle.MissingQuestion("Use `a ? b : c`.", "Use `a ? b : c`."), "lost questions exclude protected code");
        foreach (var item in Cases("VocabularyCases").EnumerateArray())
        {
            string input = DictationStyle.Protect(item.GetProperty("transcript").GetString()!).Text;
            string[] personal = item.GetProperty("glossary").EnumerateArray().Select(x => x.GetString()!).ToArray();
            var hints = JsonSerializer.SerializeToElement(EngineeringVocabulary.Hints(input, personal));
            Assert(hints.EnumerateArray().Select(h => h.GetProperty("term").GetString()).SequenceEqual(
                item.GetProperty("terms").EnumerateArray().Select(t => t.GetString())), item.GetProperty("name").GetString()!);
        }
        var bypass = Settings.Defaults(Environment.CurrentDirectory); bypass.Processing = "none"; bypass.BundledSpeech = false;
        Assert(!bypass.RequiredModels().Any() && !EngineInstall.Required(bypass), "none needs no editing download");
        using (var editor = new EnglishEditor(bypass))
        {
            const string raw = "  Ну, три, um, three jay ess.  \n";
            editor.PrepareAsync(default).GetAwaiter().GetResult();
            Assert(editor.ProcessId == null && editor.EditAsync(raw, default).GetAwaiter().GetResult() == raw, "none is exact and never starts a server");
        }
        bypass.Processing = "invalid";
        try { bypass.Validate(); throw new Exception("Invalid mode accepted"); } catch (InvalidDataException) { }
        const string codeSample = "`x—y`\n```text\n- keep this.\n```\n🙂";
        Assert(DictationStyle.Apply(codeSample, codeSample) == codeSample, "code whitespace, punctuation and emoji preserved");
        const string emojiSample = "Hi 👩🏽‍💻 🇺🇸 1️⃣ 🙂 `literal🙂` __TTM_EMOJI_0__";
        var protectedEmoji = DictationStyle.Protect(emojiSample);
        Assert(DictationStyle.Restore(emojiSample, protectedEmoji.Text) == emojiSample && protectedEmoji.Map.Count == 5, "emoji clusters, code and placeholder collisions");
        Assert(DictationStyle.Restore("🙂", "__TTM_EMOJI_0__") == "🙂", "bare emoji placeholder");
        try { DictationStyle.Restore("🙂", "__TTM_EMOJI_0__ __TTM_EMOJI_0__"); throw new Exception("Duplicate emoji accepted"); }
        catch (InvalidDataException) { }
        Assert(DictationStyle.Apply("", "It’s fine—really.\n1. Fix it.\n- Test it.") == "It's fine-really.\n1. Fix it\n- Test it", "ASCII prose and list punctuation");
        try { DictationStyle.Apply("`foo()`", "`bar()`"); throw new Exception("Changed code accepted"); }
        catch (InvalidDataException) { }
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
            Assert(body.GetProperty("messages").GetArrayLength() == (model == "qwen08" ? 2 : 10), "examples " + model);
            Assert(body.GetProperty("response_format").GetProperty("type").GetString() == "json_schema", "constrained format " + model);
            settings.Processing = "heavy";
            body = JsonSerializer.SerializeToElement(EnglishEditor.RequestBody("three jay ess", settings, model));
            Assert(body.GetProperty("messages")[0].GetProperty("content").GetString()!.Contains("Processing level: HEAVY"), "heavy prompt " + model);
            Assert(body.GetProperty("messages").GetArrayLength() == (model == "qwen08" ? 2 : 10), "heavy examples " + model);
            Assert(body.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!.Contains("three.js"), "engineering hints " + model);
            settings.EngineeringTerms = false;
            body = JsonSerializer.SerializeToElement(EnglishEditor.RequestBody("three jay ess", settings, model));
            Assert(!body.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!.Contains("engineering_terms"), "dictionary can be disabled");
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
    internal static async Task RunModels(string directory, string model, bool gpu = true, bool engineering = false)
    {
        var settings = Settings.Defaults(Environment.CurrentDirectory);
        settings.ModelDirectory = Path.GetFullPath(directory); settings.Translation = model;
        settings.UseGpu = gpu;
        settings.Validate();
        if (!settings.LocalTranslation) throw new ArgumentException("Choose a bundled translator.");
        await ModelDownload.Get(model).InstallAsync(settings.ModelDirectory, new Progress<string>(), default);
        using var editor = new EnglishEditor(settings);
        var failures = new List<string>();
        var cases = Cases(model == "translategemma" ? "TranslateGemmaCases" : "TranslationCases").EnumerateArray().ToList();
        // 2B remains supported for Light editing. The advanced quality suite targets 4B;
        // --engineering-check also runs it on smaller models to measure their limitations.
        if (model == "qwen4" || engineering) cases.AddRange(Cases("EngineeringCases").EnumerateArray());
        foreach (var item in cases)
        {
            settings.Glossary = item.GetProperty("glossary").EnumerateArray().Select(x => x.GetString()!).ToArray();
            settings.Processing = item.TryGetProperty("processing", out var mode) ? mode.GetString()! : "light";
            string text = await editor.EditAsync(item.GetProperty("transcript").GetString()!, default);
            Console.WriteLine(model + ": " + item.GetProperty("name").GetString() + ": " + text);
            bool passed = item.GetProperty("contains").EnumerateArray().All(x => text.Contains(x.GetString()!, StringComparison.OrdinalIgnoreCase)) &&
                item.GetProperty("excludes").EnumerateArray().All(x => !text.Contains(x.GetString()!, StringComparison.OrdinalIgnoreCase)) &&
                (!item.TryGetProperty("matches", out var patterns) || patterns.EnumerateArray().All(x => Regex.IsMatch(text, x.GetString()!, RegexOptions.IgnoreCase)));
            if (!passed) failures.Add(item.GetProperty("name").GetString()!);
        }
        Assert(failures.Count == 0, model + ": " + string.Join(", ", failures));
    }
}
