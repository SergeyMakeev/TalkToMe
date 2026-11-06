using System.Text.Json;
using NAudio.Wave;
using TalkToMe;

internal static class PerformanceCheck
{
    public static void Check()
    {
        var root = Directory.CreateTempSubdirectory("TalkToMe-cpu-");
        try
        {
            var fast = Path.Combine(root.FullName, "engines", "avx2", "engine.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(fast)!);
            File.WriteAllText(fast, "test");
            var portable = Path.Combine(root.FullName, "engines", "engine.dll");
            if (NativeEngines.Select(root.FullName, "engine.dll", true, false) != fast ||
                NativeEngines.Select(root.FullName, "engine.dll", false, false) != portable ||
                NativeEngines.Select(root.FullName, "engine.dll", true, true) != portable ||
                NativeEngines.Select(root.FullName, "missing.dll", true, false).Contains("avx2"))
                throw new Exception("CPU engine compatibility selection failed.");
        }
        finally { root.Delete(true); }

        var settings = Settings.Defaults(".");
        if (!settings.UseGpu || !new Settings().UseGpu) throw new Exception("GPU acceleration must default on for new and existing bundled setups.");
        settings.Prompt = "secret instruction";
        settings.Glossary = ["private glossary"];
        settings.ServerUrl = "http://private-host:8080/v1";
        var metrics = new ProcessingMetrics(settings, 2, "ru");
        using (var response = JsonDocument.Parse("""{"timings":{"prompt_ms":123,"predicted_ms":456,"predicted_n":9,"secret":"private content"}}"""))
            metrics.ReadTranslationDetails(response.RootElement);
        using (var response = JsonDocument.Parse("""{"choices":[],"timings":{"prompt_n":"invalid","cache_n":-1}}"""))
            metrics.ReadTranslationDetails(response.RootElement);
        metrics.Finish("ready to copy");
        double end = metrics.TotalMs;
        metrics.Finish("failed");
        string report = metrics.Report();
        if (metrics.TotalMs != end || metrics.Outcome != "ready to copy" || metrics.TranslationDetails.Count != 3 ||
            metrics.TranslationDetails["prompt_ms"] != 123 || report.Contains("secret instruction") ||
            report.Contains("private glossary") || report.Contains("private-host") || report.Contains("private content"))
            throw new Exception("Performance report privacy/timing check failed.");
        Console.WriteLine("Performance report and CPU compatibility checks passed.");
    }

    public static async Task Benchmark(string directory, string sample, string backend)
    {
        if (backend == "portable") Environment.SetEnvironmentVariable("TALKTOME_CPU", "portable");
        var settings = Settings.Defaults(Environment.CurrentDirectory);
        settings.UseGpu = backend == "auto";
        settings.ModelDirectory = Path.GetFullPath(directory);
        foreach (var id in settings.RequiredModels())
            if (!ModelDownload.Get(id).Installed(settings.ModelDirectory)) throw new IOException($"Missing model {id}; run --local-models first.");
        using var wav = new WaveFileReader(sample);
        if (wav.WaveFormat.SampleRate != 16000 || wav.WaveFormat.Channels != 1 || wav.WaveFormat.BitsPerSample != 16)
            throw new InvalidDataException("Benchmark needs a 16 kHz mono PCM16 WAV.");
        using var pcm = new MemoryStream();
        wav.CopyTo(pcm);
        byte[] audio = pcm.ToArray();
        using var recognizer = new Recognizer(settings);
        using var editor = new EnglishEditor(settings);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        // A changing request avoids measuring exact-request cache hits. Same audio for comparable ASR work.
        string[] phrases = ["У меня два кота.", "У меня три кота.", "У меня четыре кота.", "У меня пять котов."];
        for (int i = 0; i < phrases.Length; i++)
        {
            var metrics = new ProcessingMetrics(settings, audio.Length / 32000d, "auto");
            var text = await recognizer.RunAsync(audio, "auto", timeout.Token, metrics).ConfigureAwait(false);
            var translated = await editor.EditAsync(phrases[i], timeout.Token, metrics).ConfigureAwait(false);
            metrics.Finish(i == 0 ? "cold benchmark" : "warm benchmark");
            if (string.IsNullOrWhiteSpace(text) || !translated.Contains("cat", StringComparison.OrdinalIgnoreCase))
                throw new Exception("Benchmark inference produced an unexpected result.");
            Console.WriteLine(metrics.Json());
        }
    }
}
