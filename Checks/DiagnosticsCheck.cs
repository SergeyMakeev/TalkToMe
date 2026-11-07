using System.Diagnostics;
using System.Runtime.InteropServices;
using TalkToMe;

internal static class DiagnosticsCheck
{
    internal static void Child(string directory)
    {
        Diagnostics.Initialize(directory);
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
            Console.WriteLine("Disposable crash captured: managed stack, native stack and valid minidump; exception message excluded.");
        }
        finally { Directory.Delete(directory, true); }
    }
}
