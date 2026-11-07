using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace TalkToMe;

internal static class Diagnostics
{
    private static string? testDirectory;
    internal static string DirectoryPath => testDirectory ?? Path.Combine(Settings.UserDirectory, "Diagnostics");
    private static readonly object Gate = new();
    private static bool enabled;
    internal static void Initialize(string? directory = null)
    {
        try
        {
            testDirectory = directory;
            Directory.CreateDirectory(DirectoryPath);
            enabled = true;
            Prune();
            Event("startup", EnvironmentText());
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Error("unhandled", e.ExceptionObject as Exception);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => {
                Error("UI unhandled", e.Exception);
                Environment.FailFast("Unhandled TalkToMe UI exception. See the local Diagnostics folder.", e.Exception);
            };
            TaskScheduler.UnobservedTaskException += (_, e) => Error("unobserved task", e.Exception);
            if (!Debugger.IsAttached) StartMonitor(Environment.ProcessId);
        }
        catch { /* Diagnostics must never prevent the app from opening. */ }
    }
    internal static void TrackChild(Process process)
    {
        if (!enabled || Debugger.IsAttached) return;
        _ = Task.Run(() => {
            try { if (!process.HasExited) StartMonitor(process.Id); }
            catch (Exception error) { Error("child monitor", error); }
        });
    }
    private static void StartMonitor(int processId)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "TalkToMe.CrashMonitor.exe");
        if (!File.Exists(executable)) { Event("monitor", "Crash monitor is missing."); return; }
        string eventName = @"Local\TalkToMe.CrashReady." + processId + "." + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(processId.ToString());
        info.ArgumentList.Add(DirectoryPath); info.ArgumentList.Add(eventName);
        using var monitor = Process.Start(info);
        Event("monitor", ready.WaitOne(5000) ? "Crash monitor started; per-session status and exit codes are in monitor-*.txt." : "Crash monitor startup timed out.");
    }
    internal static void Event(string stage, string detail = "")
    {
        if (!enabled) return;
        try
        {
            lock (Gate)
            {
                string file = Path.Combine(DirectoryPath, "events.log");
                if (File.Exists(file) && new FileInfo(file).Length > 2_000_000)
                    File.Move(file, file + ".previous", true);
                File.AppendAllText(file, $"{DateTime.UtcNow:O} [{Environment.ProcessId}] {stage}: {detail}\n");
            }
        }
        catch { }
    }
    internal static void Error(string stage, Exception? error)
    {
        // Messages can contain dictation, request bodies or server credentials.
        if (error == null) { Event(stage, "Unknown exception"); return; }
        Event(stage, $"{error.GetType().FullName} (0x{error.HResult:X8})\n{error.StackTrace}");
        if (error.InnerException != null) Error(stage + " / inner", error.InnerException);
    }
    private static string EnvironmentText() =>
        $"TalkToMe {typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}\n" +
        $"OS: {Environment.OSVersion}; .NET: {Environment.Version}; process: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}\n" +
        $"CPU: {Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")}; logical CPUs: {Environment.ProcessorCount}; AVX2: {NativeEngines.SupportsAvx2}";

    internal static string Report(Settings settings)
    {
        var report = new StringBuilder(EnvironmentText());
        report.AppendLine($"\nUTC: {DateTime.UtcNow:O}\nSpeech: {settings.SpeechModel}; translation: {settings.Translation}; GPU requested: {settings.UseGpu}; language: {settings.Language}");
        report.AppendLine($"Microphone device: {settings.Microphone}; sounds: {settings.Sounds}; toggle: {settings.Toggle}; review: {settings.CopyOnly}");
        report.AppendLine($"Custom editing prompt: {!string.IsNullOrWhiteSpace(settings.Prompt)}; glossary entries: {settings.Glossary.Length}");
        report.AppendLine($"Engine runtime: {EngineInstall.Catalog.Version}; CPU installed: {EngineInstall.Installed(EngineInstall.Catalog.Packages["cpu"], EngineInstall.CpuDirectory)}; CUDA installed: {EngineInstall.Installed(EngineInstall.Catalog.Packages["cuda"], EngineInstall.CudaDirectory)}; NVIDIA adapter: {EngineInstall.HasNvidiaAdapter()}");
        report.AppendLine("No transcripts, audio, glossary contents, editing prompts or API keys are included. Memory dumps are separate files and may contain private data.");
        foreach (string name in new[] { "events.log.previous", "events.log", "monitor.txt" })
        {
            string path = Path.Combine(DirectoryPath, name);
            if (File.Exists(path)) { report.AppendLine("\n--- " + name + " ---"); report.AppendLine(string.Join('\n', ReadLiveLog(path).Split('\n').TakeLast(500))); }
        }
        if (Directory.Exists(DirectoryPath))
        {
            foreach (var file in new DirectoryInfo(DirectoryPath).GetFiles("monitor-*.txt").OrderByDescending(f => f.LastWriteTimeUtc).Take(8))
            { report.AppendLine("\n--- " + file.Name + " ---"); report.AppendLine(ReadLiveLog(file.FullName)); }
            foreach (var file in new DirectoryInfo(DirectoryPath).GetFiles("crash-*.txt").OrderByDescending(f => f.LastWriteTimeUtc).Take(3))
            { report.AppendLine("\n--- " + file.Name + " ---"); report.AppendLine(ReadLiveLog(file.FullName)); }
        }
        report.AppendLine("\n--- Windows crash records (last seven days) ---");
        report.AppendLine(WindowsCrashEvents.Report());
        return report.ToString();
    }
    private static string ReadLiveLog(string path)
    {
        try
        {
            // The native monitor keeps its current session file open for writing.
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file);
            return reader.ReadToEnd();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return $"Log temporarily unavailable ({error.GetType().Name}; 0x{error.HResult:X8})."; }
    }
    internal static void OpenFolder()
    {
        Directory.CreateDirectory(DirectoryPath);
        Process.Start(new ProcessStartInfo(DirectoryPath) { UseShellExecute = true });
    }
    private static void Prune()
    {
        foreach (var old in new DirectoryInfo(DirectoryPath).GetFiles("monitor-*.txt").OrderByDescending(f => f.LastWriteTimeUtc).Skip(8))
            old.Delete();
        var dumps = new DirectoryInfo(DirectoryPath).GetFiles("crash-*.dmp").OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
        long bytes = 0;
        for (int i = 0; i < dumps.Length; i++)
        {
            bytes += dumps[i].Length;
            if (i >= 3 || bytes > 512L * 1024 * 1024)
            { File.Delete(dumps[i].FullName); File.Delete(Path.ChangeExtension(dumps[i].FullName, ".txt")); }
        }
    }
}
