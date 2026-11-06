using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace TalkToMe;

internal sealed record ModelDownload(string Name, string Repository, string Revision, long Size, string Digest)
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromHours(2) };
    private static readonly Dictionary<string, ModelDownload> Catalog = LoadCatalog();
    internal static ModelDownload Get(string id) => Catalog[id];
    internal string PathIn(string directory) => Path.Combine(directory, Name);
    internal bool Installed(string directory) => new FileInfo(PathIn(directory)) is { Exists: true } file && file.Length == Size;

    private static Dictionary<string, ModelDownload> LoadCatalog()
    {
        using var file = typeof(ModelDownload).Assembly.GetManifestResourceStream("TalkToMe.Models.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, ModelDownload>>(file, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    internal async Task InstallAsync(string directory, IProgress<string> progress, CancellationToken token, HttpClient? client = null)
    {
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { await InstallAttemptAsync(directory, progress, token, client).ConfigureAwait(false); return; }
            catch (Exception error) when (attempt < 2 && !token.IsCancellationRequested &&
                (error is HttpIOException or TaskCanceledException || error is HttpRequestException http &&
                (http.StatusCode == null || (int)http.StatusCode is 408 or 429 or 500 or 502 or 503 or 504)))
            {
                progress.Report($"Download interrupted; retrying {Name} ({attempt + 1}/2)…");
                await Task.Delay(TimeSpan.FromSeconds((attempt + 1) * 2), token).ConfigureAwait(false);
            }
        }
    }

    private async Task InstallAttemptAsync(string directory, IProgress<string> progress, CancellationToken token, HttpClient? client)
    {
        Directory.CreateDirectory(directory);
        string path = PathIn(directory);
        bool Matches(byte[] hash) => Convert.ToHexString(hash).Equals(Digest, StringComparison.OrdinalIgnoreCase);
        if (File.Exists(path) && new FileInfo(path).Length == Size)
        {
            progress.Report($"Checking {Name}…");
            using var existing = File.OpenRead(path);
            if (Matches(await SHA256.HashDataAsync(existing, token).ConfigureAwait(false))) return;
        }
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            progress.Report($"Downloading {Name}…");
            var url = $"https://huggingface.co/{Repository}/resolve/{Revision}/{Name}";
            using var response = await (client ?? Client).GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                byte[] buffer = new byte[131072];
                long total = 0, lastPercent = -1;
                int count;
                while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    total += count;
                    if (total > Size) throw new InvalidDataException($"Unexpected size for {Name}.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    long percent = 100 * total / Size;
                    if (percent != lastPercent) progress.Report($"Downloading {Name} · {percent}%");
                    lastPercent = percent;
                }
                if (total != Size || !Matches(hash.GetHashAndReset()))
                    throw new InvalidDataException($"Could not verify {Name}. Download it again.");
            }
            token.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: true);
        }
        finally { File.Delete(temp); }
    }
}
