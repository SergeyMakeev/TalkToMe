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
    private Uri? privateEndpoint;
    private readonly string privateKey = Guid.NewGuid().ToString("N");
    private readonly string privateAlias = Guid.NewGuid().ToString("N");
    public string Model => settings.LocalTranslation ? privateAlias : settings.Model;
    public Uri Endpoint => settings.LocalTranslation
        ? privateEndpoint ??= AvailableEndpoint() : new Uri(settings.ServerUrl);
    public string? Key => settings.LocalTranslation ? privateKey : settings.ReadApiKey();
    internal static string BundledExecutable => Path.Combine(AppContext.BaseDirectory, "engines", "llama-server.exe");

    private static Uri AvailableEndpoint()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1");
    }

    public async Task PrepareAsync(CancellationToken token)
    {
        if (settings.Translation == "transcript") return;
        var local = settings.LocalServer;
        if (settings.LocalTranslation)
        {
            var model = ModelDownload.Get(settings.Translation);
            if (!model.Installed(settings.ModelDirectory)) throw new IOException("Open Settings → Models to download your translation model.");
            if (!File.Exists(BundledExecutable)) throw new IOException("The translation engine is missing. Extract the complete TalkToMe download and reopen the app.");
            local = new(BundledExecutable, model.PathIn(settings.ModelDirectory), 8192, "0");
        }
        var uri = Endpoint;
        if (local == null || !uri.IsLoopback || uri.Scheme != "http") return;
        var health = new Uri(uri, "/health");
        await startup.WaitAsync(token);
        try
        {
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromMinutes(3))
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
                }
                if (status == HttpStatusCode.OK)
                {
                    if (settings.LocalTranslation && !verified) await VerifyModelAsync(uri, token);
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
                    var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
                    foreach (var arg in new[] { "-m", model, "--alias", Model, "--host", uri.DnsSafeHost,
                        "--port", uri.Port.ToString(), "--ctx-size", local.ContextSize.ToString(), "--parallel", "1", "-ngl", local.GpuLayers,
                        "--reasoning", "off", "--no-mmproj", "--log-disable" }) info.ArgumentList.Add(arg);
                    if (settings.LocalTranslation)
                        foreach (var arg in new[] { "--offline", "--no-webui" }) info.ArgumentList.Add(arg);
                    if (Key is { Length: > 0 } key)
                        info.Environment["LLAMA_API_KEY"] = key;
                    verified = false;
                    owned = Process.Start(info) ?? throw new IOException("Could not start local llama-server.");
                }
                await Task.Delay(300, token);
            }
            throw new TimeoutException("Local llama-server is still loading. Retry processing when it is ready.");
        }
        finally { startup.Release(); }
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

    public void Dispose()
    {
        probe.Dispose();
        if (owned == null) return;
        try { if (!owned.HasExited) owned.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        finally { owned.Dispose(); }
    }
}
