using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace TalkToMe;

internal sealed class Recognizer(Settings settings) : IDisposable
{
    private Process? worker;
    private readonly SemaphoreSlim gate = new(1);

    // The caller waits for outstanding reads before applying a settings draft.
    public bool Reconfigure(Settings next)
    {
        bool reload = settings.Python != next.Python || settings.WhisperDirectory != next.WhisperDirectory ||
            settings.Device != next.Device || settings.ComputeType != next.ComputeType ||
            settings.CudaLibraryDirectory != next.CudaLibraryDirectory;
        bool transcribeAgain = reload || !settings.Glossary.SequenceEqual(next.Glossary);
        if (reload) StopWorker();
        settings = next;
        return transcribeAgain;
    }

    private async Task<JsonElement> ReplyAsync(CancellationToken token)
    {
        var line = await worker!.StandardOutput.ReadLineAsync(token)
            ?? throw new IOException("Whisper exited. Check the Python environment, model directory and CUDA libraries.");
        using var reply = JsonDocument.Parse(line);
        if (reply.RootElement.TryGetProperty("error", out var error))
            throw new InvalidDataException("Whisper: " + error.GetString());
        return reply.RootElement.Clone();
    }

    private async Task EnsureWorkerAsync(CancellationToken token)
    {
        if (worker is { HasExited: false }) return;
        StopWorker();
        var exe = settings.Resolve(settings.Python);
        if (!File.Exists(exe)) throw new FileNotFoundException("Whisper environment missing. Run setup.ps1 or choose a Python interpreter in Settings → Speech.");
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var arg in new[] { "-u", settings.Resolve("asr/worker.py"), settings.Resolve(settings.WhisperDirectory),
                                    settings.Device, settings.ComputeType }) info.ArgumentList.Add(arg);
        if (!string.IsNullOrWhiteSpace(settings.CudaLibraryDirectory))
            info.Environment["PATH"] = settings.Resolve(settings.CudaLibraryDirectory) + ";" + info.Environment["PATH"];
        worker = Process.Start(info) ?? throw new IOException("Could not start the Whisper worker.");
        var ready = await ReplyAsync(token);
        if (!ready.GetProperty("ready").GetBoolean()) throw new IOException("Whisper failed to initialize.");
    }

    // A cancelled read must discard the entire pipe, including any late reply.
    public async Task<string> RunAsync(byte[]? pcm, string language, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            await EnsureWorkerAsync(token);
            if (pcm is null) return ""; // Warm up the resident model.
            string request = JsonSerializer.Serialize(new { pcm, language = language == "auto" ? null : language, glossary = settings.Glossary });
            await worker!.StandardInput.WriteLineAsync(request.AsMemory(), token);
            var reply = await ReplyAsync(token);
            return reply.GetProperty("text").GetString()!.Trim();
        }
        catch { StopWorker(); throw; }
        finally { gate.Release(); }
    }

    private void StopWorker()
    {
        if (worker is null) return;
        try { if (!worker.HasExited) worker.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        finally { worker.Dispose(); worker = null; }
    }

    public void Dispose() => StopWorker();
}
