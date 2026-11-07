using System.Diagnostics;
using System.Runtime.InteropServices;
using TalkToMe;

internal static class FocusProbeCheck
{
    [DllImport("kernel32")] private static extern void RaiseException(uint code, uint flags, uint count, nint arguments);
    internal static void Child(string mode)
    {
        Console.WriteLine("ready"); Console.Out.Flush();
        Console.ReadLine();
        if (mode == "crash") RaiseException(0xC0000005, 1, 0, 0);
        else if (mode == "hang") Thread.Sleep(Timeout.Infinite);
        else { Console.WriteLine("100 200 0 2 42 43"); Console.Out.Flush(); Console.ReadLine(); }
    }
    internal static void Run()
    {
        foreach (string mode in new[] { "crash", "hang" })
        {
            int launches = 0;
            using var probe = new FocusProbe(() => {
                var info = NativeEngines.ProcessInfo(Path.ChangeExtension(typeof(FocusProbeCheck).Assembly.Location, ".exe"));
                info.ArgumentList.Add("--focus-child"); info.ArgumentList.Add(launches++ == 0 ? mode : "ok");
                return info;
            }, TimeSpan.FromSeconds(2));
            var started = Stopwatch.StartNew();
            if (probe.Capture() != null || started.Elapsed > TimeSpan.FromSeconds(5)) throw new Exception("Failed focus helper did not return promptly without a target");
            var recovered = probe.Capture();
            if (launches != 2 || recovered?.Window != 100 || recovered.Focus != 200 || !recovered.ElementId.SequenceEqual(new[] { 42, 43 }))
                throw new Exception("Focus helper did not recover after " + mode);
        }
        if (FocusProbe.Parse("none") != null) throw new Exception("No target was not preserved");
        foreach (string bad in new[] { "", "0 2 0 1 3", "1 2 1 2 3", "1 2 5 1 3", "1 2 1 0", "1 2 1 129 3" })
        {
            try { FocusProbe.Parse(bad); throw new Exception("Invalid focus response accepted"); }
            catch (FormatException) { }
        }
        if (typeof(Destination).Assembly.GetReferencedAssemblies().Any(a => a.Name == "UIAutomationClient"))
            throw new Exception("App still references the legacy managed UI Automation client");
        using var native = new FocusProbe();
        _ = native.Capture(); // A locked/headless desktop can legitimately have no field.
        if (native.ProcessId == null) throw new Exception("Bundled native focus helper could not start");
        GC.Collect(); GC.WaitForPendingFinalizers();
        Console.WriteLine("Native focus helper startup, protocol validation, crash/hang isolation and restart checks passed. No legacy UIAutomationClient reference in the app.");
    }
}
