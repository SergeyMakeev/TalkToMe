using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TalkToMe;

internal static class EngineInstall
{
    internal sealed record Package(string Name, long Size, string Digest, string Url, Dictionary<string, long> Files);
    internal sealed record Manifest(string Version, Dictionary<string, Package> Packages);
    internal static readonly Manifest Catalog = Load();
    private static readonly SemaphoreSlim Gate = new(1);
    private static bool cudaFailed;
    internal static string CudaFailure { get; private set; } = "";
    internal static string CacheRoot => Path.Combine(Settings.UserDirectory, "engines", Catalog.Version);
    private static string? Override => Environment.GetEnvironmentVariable("TALKTOME_ENGINE_DIRECTORY");
    internal static string CpuDirectory => string.IsNullOrEmpty(Override) ? Path.Combine(CacheRoot, "cpu") : Path.GetFullPath(Override);
    internal static string CudaDirectory => string.IsNullOrEmpty(Override) ? Path.Combine(CacheRoot, "cuda") : Path.Combine(Path.GetFullPath(Override), "cuda");
    private static Manifest Load()
    {
        using var file = typeof(EngineInstall).Assembly.GetManifestResourceStream("TalkToMe.WindowsRuntime.json")!;
        return JsonSerializer.Deserialize<Manifest>(file, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }
    internal static bool Required(Settings s) => s.BundledSpeech || s.LocalTranslation ||
        s.LocalServer is { Executable: "" };
    internal static async Task EnsureAsync(Settings settings, CancellationToken token, IProgress<string>? progress = null)
    {
        if (!Required(settings) || !string.IsNullOrEmpty(Override)) return;
        progress ??= new Progress<string>();
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(CacheRoot);
            // Also serialize installations made by a different app version/process.
            using var installLock = await LockAsync(Path.Combine(CacheRoot, ".install.lock"), token).ConfigureAwait(false);
            await InstallAsync(Catalog.Packages["cpu"], CpuDirectory, progress, token).ConfigureAwait(false);
            if (!settings.UseGpu || !NativeEngines.SupportsAvx2 || Environment.GetEnvironmentVariable("TALKTOME_CPU") == "portable" || cudaFailed) return;
            if (!HasNvidiaAdapter()) { CudaFailure = "No NVIDIA display adapter detected; CPU selected."; return; }
            try { await InstallAsync(Catalog.Packages["cuda"], CudaDirectory, progress, token).ConfigureAwait(false); }
            catch (Exception error) when (!token.IsCancellationRequested && error is IOException or HttpRequestException or TaskCanceledException)
            {
                cudaFailed = true;
                CudaFailure = "CUDA download was interrupted or could not be verified. Using CPU; restart the app to retry.";
                Diagnostics.Error("runtime.cuda download", error);
                progress.Report(CudaFailure);
            }
        }
        finally { Gate.Release(); }
    }
    private static async Task<FileStream> LockAsync(string file, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33)
            { await Task.Delay(200, token).ConfigureAwait(false); }
        }
    }
    internal static bool Installed(Package package, string destination)
    {
        try
        {
            return File.ReadAllText(Path.Combine(destination, ".verified")) == package.Digest &&
                package.Files.All(file => new FileInfo(Path.Combine(destination, file.Key)) is { Exists: true } found && found.Length == file.Value);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    internal static async Task InstallAsync(Package package, string destination, IProgress<string> progress, CancellationToken token, HttpClient? client = null)
    {
        if (Installed(package, destination)) return;
        string parent = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, ".install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var asset = new ModelDownload(package.Name, "", "", package.Size, package.Digest, package.Url);
            await asset.InstallAsync(staging, progress, token, client).ConfigureAwait(false);
            progress.Report("Installing verified dictation engines…");
            string unpacked = Path.Combine(staging, "unpacked");
            Directory.CreateDirectory(unpacked);
            using (var archive = ZipFile.OpenRead(asset.PathIn(staging)))
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in archive.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    string name = entry.FullName;
                    if (!package.Files.TryGetValue(name, out long expected) || !seen.Add(name) || entry.Length != expected ||
                        name.Contains('\\') || name.Contains(':') || name.StartsWith('/') || name.Split('/').Any(p => p is "" or "." or "..") ||
                        ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                        throw new InvalidDataException("Unexpected file in the dictation engine archive.");
                    string target = Path.GetFullPath(Path.Combine(unpacked, name));
                    if (!target.StartsWith(unpacked + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Unsafe engine archive path.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var input = entry.Open();
                    using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await input.CopyToAsync(output, token).ConfigureAwait(false);
                    if (output.Length != expected) throw new InvalidDataException("Incomplete engine archive.");
                }
                if (seen.Count != package.Files.Count) throw new InvalidDataException("The engine archive is missing files.");
            }
            token.ThrowIfCancellationRequested();
            await File.WriteAllTextAsync(Path.Combine(unpacked, ".verified"), package.Digest, token).ConfigureAwait(false);
            // destination is a single package directory under its resolved parent.
            if (Path.GetFullPath(destination) == parent || !Path.GetFullPath(destination).StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid runtime destination.");
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            Directory.Move(unpacked, destination);
            progress.Report("Dictation engines ready.");
        }
        finally { Directory.Delete(staging, recursive: true); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        internal int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Description;
        internal int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Key;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice display, uint flags);
    internal static bool HasNvidiaAdapter()
    {
        for (uint i = 0; i < 64; i++)
        {
            var display = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, i, ref display, 0)) break;
            if (display.Id.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase) || display.Description.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
