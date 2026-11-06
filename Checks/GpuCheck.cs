using System.Diagnostics;
using NAudio.Wave;
using TalkToMe;

internal static class GpuCheck
{
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void KillOwned(int? id)
    {
        Require(id != null, "Missing test helper process");
        using var child = Process.GetProcessById(id!.Value);
        child.Kill(); child.WaitForExit();
    }
    public static async Task Run(string directory, string sample)
    {
        var settings = Settings.Defaults(Environment.CurrentDirectory);
        settings.ModelDirectory = Path.GetFullPath(directory);
        using var wav = new WaveFileReader(sample);
        using var buffer = new MemoryStream();
        wav.CopyTo(buffer);
        byte[] pcm = buffer.ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var token = deadline.Token;
        var gpu = await NativeEngines.GpuAsync(true, token);
        Require(gpu.Id != null, "This check requires a working NVIDIA GPU and bundled CUDA engines: " + gpu.Reason);
        Console.WriteLine("GPU detected: " + gpu.Name);
        using (var speech = new BundledSpeech())
        {
            var metrics = new ProcessingMetrics(settings, pcm.Length / 32000d, "en");
            string text = await speech.RunAsync(pcm, "en", settings, token, metrics);
            Require(text.Contains("country", StringComparison.OrdinalIgnoreCase) && metrics.SpeechBackend.StartsWith("CUDA"), "Speech did not run correctly on CUDA");
            Console.WriteLine(metrics.SpeechBackend);
            KillOwned(speech.ProcessId);
            var recovery = new ProcessingMetrics(settings, pcm.Length / 32000d, "en");
            text = await speech.RunAsync(pcm, "en", settings, token, recovery);
            Require(text.Contains("country", StringComparison.OrdinalIgnoreCase) && recovery.SpeechBackend.StartsWith("CPU") && recovery.SpeechFallbackReason.Length > 0, "Speech did not recover on CPU");
            int? resident = speech.ProcessId;
            await speech.RunAsync(null, "en", settings, token);
            Require(speech.ProcessId == resident, "Speech retried the failed GPU on the next request");
            Console.WriteLine("Speech worker termination recovered automatically on CPU.");
        }
        using (var editor = new EnglishEditor(settings))
        {
            var metrics = new ProcessingMetrics(settings, 0, "ru");
            string result = await editor.EditAsync("У меня два кота.", token, metrics);
            Console.WriteLine(metrics.TranslationBackend + " " + metrics.TranslationFallbackReason);
            Require(result.Contains("cat", StringComparison.OrdinalIgnoreCase) && metrics.TranslationBackend.StartsWith("CUDA"), "Translation did not run correctly on CUDA");
            Console.WriteLine(metrics.TranslationBackend);
            // Terminate only this check's owned process, including a request in flight.
            var recovery = new ProcessingMetrics(settings, 0, "ru");
            var pending = editor.EditAsync("У меня три кота.", token, recovery);
            KillOwned(editor.ProcessId);
            result = await pending;
            Require(result.Contains("cat", StringComparison.OrdinalIgnoreCase) && recovery.TranslationBackend.StartsWith("CPU") && recovery.TranslationFallbackReason.Length > 0,
                "Translation did not recover on CPU");
            Console.WriteLine("Translation worker termination recovered automatically on CPU.");
        }
        // Detection has succeeded, but the next child cannot see a GPU. Exercise startup fallback.
        string? visible = Environment.GetEnvironmentVariable("CUDA_VISIBLE_DEVICES");
        try
        {
            Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", "-1");
            using var speech = new BundledSpeech();
            using var editor = new EnglishEditor(settings);
            var metrics = new ProcessingMetrics(settings, pcm.Length / 32000d, "en");
            string text = await speech.RunAsync(pcm, "en", settings, token, metrics);
            string result = await editor.EditAsync("У меня два кота.", token, metrics);
            Require(text.Contains("country", StringComparison.OrdinalIgnoreCase) && result.Contains("cat", StringComparison.OrdinalIgnoreCase) &&
                metrics.SpeechBackend.StartsWith("CPU") && metrics.TranslationBackend.StartsWith("CPU") &&
                metrics.SpeechFallbackReason.Length > 0 && metrics.TranslationFallbackReason.Length > 0, "GPU startup failure did not recover on CPU");
            Console.WriteLine("Both GPU startup failures recovered automatically on CPU.");
        }
        finally { Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", visible); }
        using (var speech = new BundledSpeech())
        {
            await speech.RunAsync(null, "en", settings, token);
            using var cancelled = new CancellationTokenSource();
            cancelled.CancelAfter(1);
            try { await speech.RunAsync(pcm, "en", settings, cancelled.Token); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { }
            var metrics = new ProcessingMetrics(settings, pcm.Length / 32000d, "en");
            await speech.RunAsync(pcm, "en", settings, token, metrics);
            Require(metrics.SpeechBackend.StartsWith("CUDA") && metrics.SpeechFallbackReason.Length == 0, "Cancellation disabled the GPU");
            Console.WriteLine("Cancellation preserved GPU eligibility.");
        }
    }
}
