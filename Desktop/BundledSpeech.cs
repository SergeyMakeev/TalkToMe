using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TalkToMe;

// Native inference is isolated so a CUDA abort, device reset or missing DLL cannot crash the UI.
internal sealed class BundledSpeech : IDisposable
{
    private Process? worker;
    private NativeEngines.GpuDevice? device;
    private bool runningGpu, cpuFallback;
    private string fallbackReason = "";
    internal string Backend { get; private set; } = "Not loaded";
    internal int? ProcessId => worker is { HasExited: false } ? worker.Id : null;

    public async Task<string> RunAsync(byte[]? pcm, string language, Settings settings, CancellationToken token, ProcessingMetrics? metrics = null)
    {
        token.ThrowIfCancellationRequested();
        await EngineInstall.EnsureAsync(settings, token);
        var speech = ModelDownload.Get(settings.SpeechModel);
        var vad = ModelDownload.Get("activity");
        string host = settings.SpeechModel == "parakeet" ? "TalkToMeParakeetHost.exe" : "TalkToMeSpeechHost.exe";
        if (!speech.Installed(settings.ModelDirectory) || (settings.SpeechModel != "parakeet" && !vad.Installed(settings.ModelDirectory)))
            throw new IOException("Open Settings → Models to download the speech models.");
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (worker == null)
                    {
                        using var load = metrics?.Measure("speech_load");
                        device ??= await NativeEngines.GpuAsync(settings.UseGpu, token);
                        runningGpu = !cpuFallback && device.Id != null;
                        if (!runningGpu && !cpuFallback) fallbackReason = device.Reason;
                        string executable = runningGpu ? NativeEngines.CudaPath(host) : NativeEngines.PathFor(host);
                        if (!File.Exists(executable)) throw new IOException("The speech engine is missing. Extract the complete TalkToMe download and reopen the app.");
                        var info = NativeEngines.ProcessInfo(executable);
                        info.ArgumentList.Add("--worker");
                        worker = Process.Start(info) ?? throw new IOException("Could not start the speech engine.");
                        worker.ErrorDataReceived += (_, _) => { };
                        worker.BeginErrorReadLine();
                        await SendAsync(new { model = speech.PathIn(settings.ModelDirectory), gpu = runningGpu }, token);
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                        deadline.CancelAfter(TimeSpan.FromSeconds(runningGpu ? 60 : 180));
                        JsonElement ready;
                        try { ready = await ReplyAsync(deadline.Token); }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("Speech engine startup timed out."); }
                        if (!ready.GetProperty("ready").GetBoolean() || (runningGpu && ready.GetProperty("backend").GetString() != "CUDA"))
                            throw new IOException("The speech engine did not initialize its selected backend.");
                        Backend = runningGpu ? $"CUDA · {device.Name}" : NativeEngines.Description(host);
                    }
                    if (worker.HasExited) throw new IOException("The speech engine stopped.");
                    if (pcm == null || IsSilent(pcm)) return "";
                    using var decode = metrics?.Measure("speech_decode");
                    using var inferenceDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    inferenceDeadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(runningGpu ? 60 : 300, pcm.Length / 32000d * 4)));
                    try
                    {
                        await SendAsync(new { pcm, language, glossary = string.Join(", ", settings.Glossary),
                            vad = vad.PathIn(settings.ModelDirectory), threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8) }, inferenceDeadline.Token);
                        var reply = await ReplyAsync(inferenceDeadline.Token);
                        return reply.GetProperty("text").GetString()?.Trim() ?? throw new IOException("The speech engine returned no result.");
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("Speech inference timed out."); }
                }
                catch (Exception ex) when (attempt == 0 && runningGpu && !token.IsCancellationRequested &&
                    ex is IOException or System.ComponentModel.Win32Exception or JsonException)
                {
                    StopWorker();
                    cpuFallback = true;
                    fallbackReason = "The CUDA speech engine failed; this recording was retried on CPU. GPU remains disabled for speech until its settings change or the app restarts.";
                }
            }
        }
        catch { StopWorker(); throw; }
        finally
        {
            if (metrics != null) { metrics.SpeechBackend = Backend; metrics.SpeechFallbackReason = fallbackReason; }
        }
    }

    private static bool IsSilent(byte[] pcm)
    {
        if (pcm.Length < 3200) return true;
        foreach (short sample in MemoryMarshal.Cast<byte, short>(pcm)) if (Math.Abs((int)sample) > 3) return false;
        return true;
    }
    private async Task SendAsync(object request, CancellationToken token)
    {
        await worker!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), token);
        await worker.StandardInput.FlushAsync(token);
    }
    private async Task<JsonElement> ReplyAsync(CancellationToken token)
    {
        string line = await worker!.StandardOutput.ReadLineAsync(token) ?? throw new IOException("The speech engine stopped.");
        using var json = JsonDocument.Parse(line);
        if (json.RootElement.TryGetProperty("error", out _)) throw new IOException("The speech engine could not complete the request.");
        return json.RootElement.Clone();
    }
    private void StopWorker() { NativeEngines.Stop(worker); worker?.Dispose(); worker = null; }
    public void Dispose() { StopWorker(); device = null; cpuFallback = false; runningGpu = false; Backend = "Not loaded"; fallbackReason = ""; }
}
