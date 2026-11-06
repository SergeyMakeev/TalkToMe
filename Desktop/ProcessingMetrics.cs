using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace TalkToMe;

// Contains durations and configuration only. Never store audio, dictation, prompts, keys or URLs here.
internal sealed class ProcessingMetrics(Settings settings, double audioSeconds, string language, bool retry = false)
{
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    public string Version { get; } = typeof(Settings).Assembly.GetName().Version?.ToString() ?? "unknown";
    public string OS { get; } = RuntimeInformation.OSDescription;
    public string CPU { get; } = ReadCpu();
    public int LogicalProcessors { get; } = Environment.ProcessorCount;
    public int? SpeechThreads { get; } = !settings.BundledSpeech ? null : settings.SpeechModel == "parakeet"
        ? Math.Clamp(Environment.ProcessorCount, 1, 8) : Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
    public double AudioSeconds { get; } = audioSeconds;
    public string Language { get; } = language;
    public bool Retry { get; } = retry;
    public bool ReusedTranscript { get; set; }
    public string SpeechModel { get; } = settings.BundledSpeech ? settings.SpeechModel : "custom";
    public bool GpuRequested { get; } = settings.UseGpu;
    public string SpeechBackend { get; set; } = settings.BundledSpeech ? "Not run" : $"faster-whisper ({settings.Device})";
    public string SpeechFallbackReason { get; set; } = "";
    public string TranslationModel { get; } = settings.Translation;
    public string TranslationBackend { get; set; } = settings.LocalTranslation ? "Not run" : settings.Translation == "transcript" ? "disabled" : "custom server";
    public string TranslationFallbackReason { get; set; } = "";
    public string Outcome { get; private set; } = "running";
    public double TotalMs { get; private set; }
    public Dictionary<string, double> StagesMs { get; } = [];
    public Dictionary<string, double> TranslationDetails { get; } = [];

    private static string ReadCpu()
    {
        try { using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"); return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "unknown"; }
        catch (System.Security.SecurityException) { return "unknown"; }
    }

    public IDisposable Measure(string stage) => new Measurement(this, stage);
    private sealed class Measurement(ProcessingMetrics owner, string stage) : IDisposable
    {
        private readonly Stopwatch timer = Stopwatch.StartNew();
        public void Dispose() { timer.Stop(); owner.StagesMs[stage] = owner.StagesMs.GetValueOrDefault(stage) + timer.Elapsed.TotalMilliseconds; }
    }

    internal void ReadTranslationDetails(JsonElement response)
    {
        if (!response.TryGetProperty("timings", out var timings) || timings.ValueKind != JsonValueKind.Object) return;
        foreach (string name in new[] { "prompt_n", "prompt_ms", "prompt_per_second", "predicted_n", "predicted_ms", "predicted_per_second", "cache_n" })
            if (timings.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double n) && double.IsFinite(n) && n >= 0)
                TranslationDetails[name] = n;
    }

    public void Finish(string outcome)
    {
        if (Outcome != "running") return;
        elapsed.Stop();
        TotalMs = elapsed.Elapsed.TotalMilliseconds + StagesMs.GetValueOrDefault("audio_finalize");
        Outcome = outcome;
    }

    public string Summary => $"Speech {Seconds("speech_queue", "speech_load", "speech_decode"):F2}s · Translation {Seconds("translation_ready", "translation_request"):F2}s · Total {TotalMs / 1000:F2}s";
    private double Seconds(params string[] stages) => stages.Sum(s => StagesMs.GetValueOrDefault(s)) / 1000;
    public string Json() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
    public string Report()
    {
        var text = new StringBuilder($"{Summary}\r\n{SpeechBackend} / {TranslationBackend}\r\n\r\n");
        text.AppendLine("Total starts when recording stops (or Retry is clicked) and ends at input dispatch or result readiness. Recording time is excluded. Translation prompt/decode timings are inside translation_request, not additional stages.");
        text.AppendLine("audio_finalize = microphone flush; speech_queue = wait for previous work/loading; speech_load = model startup; speech_decode = speech detection, language detection and transcription; translation_ready = server wait/load; translation_request = prompt processing and generation; insertion = wait until text dispatch. Unlisted stages did not run.\r\n");
        text.AppendLine("No audio, transcript, prompt, API key or server address is included.\r\n");
        return text.Append(Json()).ToString();
    }
}
