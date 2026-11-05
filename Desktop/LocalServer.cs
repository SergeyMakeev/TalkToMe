using System.Diagnostics;
using System.Net;
using System.Net.Http;

namespace TalkToMe;

// Only the optional loopback deployment owns a server process. A remote endpoint is always a client connection.
internal sealed class LocalServer(Settings settings) : IDisposable
{
    private readonly SemaphoreSlim startup = new(1);
    private readonly HttpClient probe = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        { Timeout = TimeSpan.FromSeconds(3) };
    private Process? owned;

    public async Task PrepareAsync(CancellationToken token)
    {
        var uri = new Uri(settings.ServerUrl);
        if (settings.LocalServer is not {} local || !uri.IsLoopback || uri.Scheme != "http") return;
        var health = new Uri(uri, "/health");
        await startup.WaitAsync(token);
        try
        {
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromMinutes(3))
            {
                HttpStatusCode? status = null;
                try
                {
                    using var response = await probe.GetAsync(health, token);
                    status = response.StatusCode;
                }
                catch (HttpRequestException e) when (e.HttpRequestError == HttpRequestError.ConnectionError) { }
                if (status == HttpStatusCode.OK) return;
                if (status != null && status != HttpStatusCode.ServiceUnavailable)
                    throw new IOException($"Local llama-server health check returned HTTP {(int)status}.");
                if (owned is { HasExited: true })
                {
                    owned.Dispose(); owned = null;
                    throw new IOException("Local llama-server exited. Check its executable, GGUF, port and available GPU memory.");
                }
                if (status == null && owned == null)
                {
                    var exe = settings.Resolve(local.Executable);
                    var model = settings.Resolve(local.ModelFile);
                    if (!File.Exists(exe) || !File.Exists(model))
                        throw new IOException("Check localServer.executable and localServer.modelFile in settings.json.");
                    var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
                    foreach (var arg in new[] { "-m", model, "--alias", settings.Model, "--host", uri.DnsSafeHost,
                        "--port", uri.Port.ToString(), "--ctx-size", "8192", "--parallel", "1", "-ngl", "all",
                        "--reasoning", "off", "--no-mmproj", "--log-disable" }) info.ArgumentList.Add(arg);
                    if (Environment.GetEnvironmentVariable("TALKTOME_API_KEY") is { Length: > 0 } key)
                        info.Environment["LLAMA_API_KEY"] = key;
                    owned = Process.Start(info) ?? throw new IOException("Could not start local llama-server.");
                }
                await Task.Delay(300, token);
            }
            throw new TimeoutException("Local llama-server is still loading. Retry processing when it is ready.");
        }
        finally { startup.Release(); }
    }

    public void Dispose()
    {
        probe.Dispose();
        if (owned == null) return;
        try { if (!owned.HasExited) owned.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        finally { owned.Dispose(); }
    }
}
