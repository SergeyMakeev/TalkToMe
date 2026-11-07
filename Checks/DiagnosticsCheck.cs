using System.Diagnostics;
using System.Runtime.InteropServices;
using TalkToMe;

internal static class DiagnosticsCheck
{
    internal static void CheckEvents()
    {
        const string xml = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <System><EventID>1000</EventID><TimeCreated SystemTime="2026-11-07T05:12:35Z" /></System>
              <EventData><Data Name="AppName">TalkToMe.exe</Data><Data Name="ModuleName">uiautomationcore.dll</Data>
                <Data Name="ExceptionCode">c0000005</Data><Data Name="AppPath">C:\PRIVATE\TalkToMe.exe</Data>
                <Data Name="ModulePath">C:\PRIVATE\module.dll</Data><Data Name="Message">PRIVATE</Data></EventData>
            </Event>
            """;
        string text = WindowsCrashEvents.Summarize(xml) ?? throw new Exception("App crash event omitted");
        if (!text.Contains("uiautomationcore.dll") || !text.Contains("c0000005") || text.Contains("PRIVATE")) throw new Exception("Crash event fields missing or private fields included");
        if (WindowsCrashEvents.Summarize(xml.Replace("TalkToMe.exe", "AnotherApp.exe")) != null) throw new Exception("Unrelated app event included");
        Console.WriteLine("Windows crash-event filtering and privacy checks passed.");
    }

    internal static void Child(string directory, bool terminate = false)
    {
        Diagnostics.Initialize(directory);
        if (terminate)
        {
            File.WriteAllText(Path.Combine(directory, "copied-report.txt"), Diagnostics.Report(Settings.Defaults(Environment.CurrentDirectory)));
            Environment.Exit(37);
        }
        // Deliberately fault only the disposable check process, never the app.
        ThreadPool.QueueUserWorkItem(_ => throw new InvalidOperationException("PRIVATE-DICTATION-MUST-NOT-APPEAR"));
        Thread.Sleep(Timeout.Infinite);
    }
    internal static async Task Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "TalkToMe-crash-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string executable = Path.ChangeExtension(typeof(DiagnosticsCheck).Assembly.Location, ".exe");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            start.ArgumentList.Add("--crash-child"); start.ArgumentList.Add(directory);
            using var child = Process.Start(start)!;
            var errors = child.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await child.WaitForExitAsync(timeout.Token); }
            finally { if (!child.HasExited) child.Kill(); }
            await errors;
            var dumps = Directory.GetFiles(directory, "*.dmp");
            if (child.ExitCode == 0 || dumps.Length != 1) throw new Exception("Crash dump was not captured: " + string.Join("\n", Directory.GetFiles(directory, "*.txt").Select(File.ReadAllText)));
            using var dump = File.OpenRead(dumps[0]);
            byte[] signature = new byte[4]; dump.ReadExactly(signature);
            if (System.Text.Encoding.ASCII.GetString(signature) != "MDMP") throw new Exception("Invalid minidump");
            string events = File.ReadAllText(Path.Combine(directory, "events.log"));
            if (!events.Contains(nameof(Child)) || events.Contains("PRIVATE-DICTATION-MUST-NOT-APPEAR")) throw new Exception("Managed stack missing or private exception message leaked");
            string native = File.ReadAllText(Path.ChangeExtension(dumps[0], ".txt"));
            if (!native.Contains("Native callstack") || native.Contains("Dump failed")) throw new Exception("Native stack or dump missing");
            start.ArgumentList[0] = "--exit-child";
            using var exit = Process.Start(start)!;
            var exitErrors = exit.StandardError.ReadToEndAsync();
            await exit.WaitForExitAsync(timeout.Token);
            await exitErrors;
            if (exit.ExitCode != 37) throw new Exception("Unexpected termination-check exit code");
            string copied = File.ReadAllText(Path.Combine(directory, "copied-report.txt"));
            if (!copied.Contains("Attached. App will survive") || copied.Contains("Log temporarily unavailable") || copied.Contains("PRIVATE-DICTATION-MUST-NOT-APPEAR"))
                throw new Exception("Could not copy the live crash-monitor report safely");
            var monitors = Directory.GetFiles(directory, "monitor-*.txt");
            if (monitors.Length != 2) throw new Exception("Restart overwrote monitor history");
            string history = string.Join("\n", monitors.Select(File.ReadAllText));
            if (!history.Contains("Process exited: 0x25; crash captured: no") || !history.Contains("Second-chance exception:"))
                throw new Exception("Abrupt exit or crash exception missing from monitor history: " + history);
            Console.WriteLine("Disposable crash captured: managed stack, native stack and valid minidump; exception message excluded.");
            Console.WriteLine("Exit without a crash event recorded; previous monitor history retained.");
        }
        finally { Directory.Delete(directory, true); }
    }
}
