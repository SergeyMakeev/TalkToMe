using System.Runtime.Intrinsics.X86;
using System.Diagnostics;
using System.Text.Json;

namespace TalkToMe;

internal static class NativeEngines
{
    internal sealed record GpuDevice(string? Id, string? Name, string Reason);
    private static readonly Lazy<Task<GpuDevice>> gpu = new(ProbeGpuAsync);
    internal static string CudaPath(string file) => Path.Combine(AppContext.BaseDirectory, "engines", "cuda", file);
    internal static async Task<GpuDevice> GpuAsync(bool enabled, CancellationToken token)
    {
        if (!enabled) return new(null, null, "GPU disabled in Settings.");
        if (Environment.GetEnvironmentVariable("TALKTOME_CPU") == "portable") return new(null, null, "Portable CPU override enabled.");
        if (!SupportsAvx2) return new(null, null, "The CUDA runtime requires an AVX2/FMA-capable CPU.");
        return await gpu.Value.WaitAsync(token);
    }
    private static async Task<GpuDevice> ProbeGpuAsync()
    {
        foreach (var name in new[] { "TalkToMeSpeechHost.exe", "llama-server.exe", "cudart64_13.dll", "cublas64_13.dll", "cublasLt64_13.dll" })
            if (!File.Exists(CudaPath(name))) return new(null, null, "Bundled CUDA runtime files are missing.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var process = new Process { StartInfo = ProcessInfo(CudaPath("TalkToMeSpeechHost.exe")) };
        process.StartInfo.ArgumentList.Add("--probe");
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            string json = await output;
            await errors;
            if (process.ExitCode != 0) return new(null, null, "CUDA could not initialize. CPU selected; check the NVIDIA driver if GPU use was expected.");
            using var doc = JsonDocument.Parse(json);
            foreach (var device in doc.RootElement.GetProperty("devices").EnumerateArray())
            {
                string? id = device.GetProperty("id").GetString(), name = device.GetProperty("name").GetString();
                if (id == "CUDA0" && !string.IsNullOrWhiteSpace(name)) return new(id, name, "");
            }
            return new(null, null, "No compatible NVIDIA CUDA device is available.");
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or OperationCanceledException or JsonException)
        { return new(null, null, ex is OperationCanceledException ? "GPU detection timed out; CPU selected." : "CUDA runtime or driver is unavailable; CPU selected."); }
        finally { Stop(process); }
    }
    internal static ProcessStartInfo ProcessInfo(string executable) => new(executable) {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
        RedirectStandardOutput = true, RedirectStandardError = true,
        StandardInputEncoding = new System.Text.UTF8Encoding(false), StandardOutputEncoding = System.Text.Encoding.UTF8,
        WorkingDirectory = Path.GetDirectoryName(executable)!
    };
    internal static void Stop(Process? process)
    {
        if (process == null) return;
        try { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(3000); } }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
    internal static bool SupportsAvx2 => Avx2.IsSupported && Fma.IsSupported && Bmi2.IsSupported &&
        (X86Base.CpuId(1, 0).Ecx & (1 << 29)) != 0; // F16C
    internal static string PathFor(string file) => Select(AppContext.BaseDirectory, file, SupportsAvx2,
        Environment.GetEnvironmentVariable("TALKTOME_CPU") == "portable");
    internal static string Select(string root, string file, bool avx2, bool portable)
    {
        var fast = Path.Combine(root, "engines", "avx2", file);
        return avx2 && !portable && File.Exists(fast) ? fast : Path.Combine(root, "engines", file);
    }
    internal static string Description(string file) => Path.GetFileName(Path.GetDirectoryName(PathFor(file))) == "avx2" ? "CPU (AVX2/FMA)" : "CPU (portable)";
}
