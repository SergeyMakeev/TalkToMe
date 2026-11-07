using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using TalkToMe;

internal static class EngineInstallCheck
{
    private sealed class Handler(byte[] bytes) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }); }
    }
    internal static async Task Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "TalkToMe-runtime-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            async Task Check(string name, bool reject, bool missing = false)
            {
                using var data = new MemoryStream();
                using (var zip = new ZipArchive(data, ZipArchiveMode.Create, true))
                {
                    using var stream = zip.CreateEntry(name).Open();
                    stream.Write(new byte[] { 1, 2, 3, 4 });
                }
                byte[] bytes = data.ToArray();
                var files = new Dictionary<string, long> { [name] = 4 };
                if (missing) files.Add("missing.dll", 1);
                var package = new EngineInstall.Package("runtime.zip", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), "https://example.test/runtime.zip", files);
                var handler = new Handler(bytes);
                using var client = new HttpClient(handler);
                string destination = Path.Combine(root, Guid.NewGuid().ToString("N"));
                try
                {
                    await EngineInstall.InstallAsync(package, destination, new Progress<string>(), default, client);
                    if (reject) throw new Exception("Unsafe or incomplete runtime accepted");
                }
                catch (InvalidDataException) when (reject)
                { if (Directory.Exists(destination)) throw new Exception("Partial runtime published"); return; }
                if (!EngineInstall.Installed(package, destination)) throw new Exception("Installed runtime missing");
                await EngineInstall.InstallAsync(package, destination, new Progress<string>(), default, client);
                if (handler.Calls != 1) throw new Exception("Runtime downloaded again instead of reused offline");
                File.WriteAllBytes(Path.Combine(destination, name), new byte[] { 1 });
                if (EngineInstall.Installed(package, destination)) throw new Exception("Truncated runtime accepted");
                await EngineInstall.InstallAsync(package, destination, new Progress<string>(), default, client);
                if (!EngineInstall.Installed(package, destination) || handler.Calls != 2) throw new Exception("Runtime repair failed");
                var corrupt = package with { Digest = new string('0', 64) };
                string badDestination = Path.Combine(root, "bad");
                try { await EngineInstall.InstallAsync(corrupt, badDestination, new Progress<string>(), default, client); throw new Exception("Bad runtime digest accepted"); }
                catch (InvalidDataException) { }
                using var cancel = new CancellationTokenSource(); cancel.Cancel();
                try { await EngineInstall.InstallAsync(package, badDestination, new Progress<string>(), cancel.Token, client); throw new Exception("Cancelled installation completed"); }
                catch (OperationCanceledException) { }
                if (Directory.Exists(badDestination)) throw new Exception("Failed installation published files");
            }
            await Check("engine.exe", false);
            await Check("../escape.exe", true);
            await Check("C:/escape.exe", true);
            await Check("engine.exe", true, missing: true);
            if (Directory.EnumerateDirectories(root, ".install-*").Any()) throw new Exception("Temporary downloads were not removed");
            Console.WriteLine("Engine download, integrity, extraction, repair, cancellation and offline reuse checks passed.");
            Console.WriteLine("NVIDIA adapter detected for automatic CUDA download: " + EngineInstall.HasNvidiaAdapter());
        }
        finally { Directory.Delete(root, true); }
    }
}
