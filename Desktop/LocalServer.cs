using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace TalkToMe;

// Only the optional loopback deployment owns a server process. A remote endpoint is always a client connection.
internal sealed class LocalServer(Settings settings) : IDisposable
{
    private readonly SemaphoreSlim startup = new(1);
    private readonly HttpClient probe = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        { Timeout = TimeSpan.FromSeconds(3) };
    private Process? owned;
    private bool verified;
    private NativeEngines.GpuDevice? gpuDevice;
    private bool cpuFallback;
    private volatile int gpuLayersOffloaded;
    internal bool RunningGpu { get; private set; }
    internal string Backend { get; private set; } = settings.LocalTranslation ? "Not loaded" : "custom server";
    internal string FallbackReason { get; private set; } = "";
    internal int? ProcessId => owned is { HasExited: false } ? owned.Id : null;
    private Uri? privateEndpoint;
    private readonly string privateKey = Guid.NewGuid().ToString("N");
    private readonly string privateAlias = Guid.NewGuid().ToString("N");
    public string Model => settings.LocalTranslation ? privateAlias : settings.Model;
    public Uri Endpoint => settings.LocalTranslation
        ? privateEndpoint ??= AvailableEndpoint() : new Uri(settings.ServerUrl);
    public string? Key => settings.LocalTranslation ? privateKey : settings.ReadApiKey();
    internal static string BundledExecutable => NativeEngines.PathFor("llama-server.exe");

    private static Uri AvailableEndpoint()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1");
    }

    public async Task PrepareAsync(CancellationToken token)
    {
        await startup.WaitAsync(token);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try { await PrepareLockedAsync(token); return; }
                catch (Exception ex) when (attempt == 0 && settings.LocalTranslation && RunningGpu && !token.IsCancellationRequested &&
                    ex is IOException or System.ComponentModel.Win32Exception or TimeoutException or HttpRequestException)
                { UseCpuLocked("The CUDA translation engine could not load; automatically switched to CPU."); }
            }
        }
        catch { if (settings.LocalTranslation) StopOwned(); throw; }
        finally { startup.Release(); }
    }

    internal async Task<bool> FallBackAsync(CancellationToken token)
    {
        await startup.WaitAsync(token);
        try
        {
            if (!settings.LocalTranslation || !RunningGpu) return false;
            UseCpuLocked("The CUDA translation engine failed during processing; automatically retried on CPU.");
            return true;
        }
        finally { startup.Release(); }
    }
    private void UseCpuLocked(string reason)
    {
        StopOwned(); cpuFallback = true; RunningGpu = false; FallbackReason = reason;
        Backend = NativeEngines.Description("llama-server.exe");
    }
    private async Task PrepareLockedAsync(CancellationToken token)
    {
        if (settings.Translation == "transcript") return;
        var local = settings.LocalServer;
        if (settings.LocalTranslation)
        {
            var model = ModelDownload.Get(settings.Translation);
            if (!model.Installed(settings.ModelDirectory)) throw new IOException("Open Settings → Models to download your translation model.");
            if (!File.Exists(BundledExecutable)) throw new IOException("The translation engine is missing. Extract the complete TalkToMe download and reopen the app.");
            gpuDevice ??= await NativeEngines.GpuAsync(settings.UseGpu, token);
            RunningGpu = !cpuFallback && gpuDevice.Id != null;
            if (!RunningGpu && !cpuFallback) FallbackReason = gpuDevice.Reason;
            local = new(RunningGpu ? NativeEngines.CudaPath("llama-server.exe") : BundledExecutable,
                model.PathIn(settings.ModelDirectory), 8192, RunningGpu ? "all" : "0");
        }
        var uri = Endpoint;
        if (local == null || !uri.IsLoopback || uri.Scheme != "http") return;
        var health = new Uri(uri, "/health");
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < TimeSpan.FromSeconds(RunningGpu ? 60 : 180))
        {
            token.ThrowIfCancellationRequested();
            if (owned is { HasExited: true })
            {
                owned.Dispose(); owned = null; privateEndpoint = null;
                throw new IOException("Local translation stopped. Check available memory and retry.");
            }
            HttpStatusCode? status = null;
            if (!settings.LocalTranslation || owned != null)
            {
                try
                {
                    using var response = await probe.GetAsync(health, token);
                    status = response.StatusCode;
                }
                catch (HttpRequestException e) when (e.HttpRequestError == HttpRequestError.ConnectionError) { }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            }
            if (status == HttpStatusCode.OK)
            {
                if (settings.LocalTranslation && !verified) await VerifyModelAsync(uri, token);
                if (RunningGpu && gpuLayersOffloaded <= 0) throw new IOException("The translator did not offload layers to the selected GPU.");
                if (settings.LocalTranslation) Backend = RunningGpu ? $"CUDA · {gpuDevice!.Name} ({gpuLayersOffloaded} layers)" : NativeEngines.Description("llama-server.exe");
                return;
            }
            if (status != null && status != HttpStatusCode.ServiceUnavailable)
                throw new IOException($"Local llama-server health check returned HTTP {(int)status}.");
            if (status == null && owned == null)
            {
                var exe = string.IsNullOrWhiteSpace(local.Executable) ? BundledExecutable : settings.Resolve(local.Executable);
                var model = settings.Resolve(local.ModelFile);
                if (!File.Exists(exe) || !File.Exists(model))
                    throw new IOException("Choose the llama-server executable and GGUF in Settings → Server.");
                var info = settings.LocalTranslation ? NativeEngines.ProcessInfo(exe) : new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in new[] { "-m", model, "--alias", Model, "--host", uri.DnsSafeHost,
                    "--port", uri.Port.ToString(), "--ctx-size", local.ContextSize.ToString(), "--parallel", "1", "-ngl", local.GpuLayers,
                    "--reasoning", "off", "--no-mmproj" }) info.ArgumentList.Add(arg);
                if (settings.LocalTranslation)
                    foreach (var arg in new[] { "--offline", "--no-webui", "--device", RunningGpu ? gpuDevice!.Id! : "none", "--fit", "off" }) info.ArgumentList.Add(arg);
                if (settings.Translation == "translategemma")
                    foreach (var arg in new[] { "--no-jinja", "--chat-template", "gemma" }) info.ArgumentList.Add(arg);
                if (!RunningGpu) info.ArgumentList.Add("--log-disable");
            else foreach (var arg in new[] { "--log-verbosity", "4", "--log-colors", "off" }) info.ArgumentList.Add(arg);
                if (Key is { Length: > 0 } key)
                    info.Environment["LLAMA_API_KEY"] = key;
                verified = false;
                gpuLayersOffloaded = 0;
                owned = new Process { StartInfo = info };
                if (settings.LocalTranslation)
                {
                    owned.OutputDataReceived += (_, _) => { };
                    owned.ErrorDataReceived += (_, e) => {
                        if (e.Data == null) return;
                        var match = System.Text.RegularExpressions.Regex.Match(e.Data, @"offloaded (\d+)/\d+ layers to GPU");
                        if (match.Success && int.TryParse(match.Groups[1].Value, out int count)) gpuLayersOffloaded = count;
                    };
                }
                owned.Start();
                if (settings.LocalTranslation) { owned.BeginOutputReadLine(); owned.BeginErrorReadLine(); }
            }
            await Task.Delay(300, token);
        }
        throw new TimeoutException("Local llama-server is still loading. Retry processing when it is ready.");
    }

    private async Task VerifyModelAsync(Uri endpoint, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, "/v1/models"));
        request.Headers.Authorization = new("Bearer", privateKey);
        using var response = await probe.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        verified = json.RootElement.GetProperty("data").EnumerateArray().Any(m => m.GetProperty("id").GetString() == privateAlias);
        if (!verified) throw new IOException("Could not connect to this app’s translation model. Retry processing.");
    }

    private void StopOwned()
    {
        NativeEngines.Stop(owned); owned?.Dispose(); owned = null;
        verified = false; privateEndpoint = null; gpuLayersOffloaded = 0;
    }
    public void Dispose() { probe.Dispose(); StopOwned(); }
}
