using System.Diagnostics;
using System.Globalization;

namespace TalkToMe;

// No UI Automation COM objects or SafeHandles enter the UI process. A broken
// provider/finalizer can only stop this private, restartable native worker.
internal sealed class FocusProbe : IDisposable
{
    internal static readonly FocusProbe Shared = new();
    private readonly object gate = new();
    private readonly Func<ProcessStartInfo>? launch;
    private readonly TimeSpan timeout;
    private Process? process;
    internal FocusProbe(Func<ProcessStartInfo>? launch = null, TimeSpan? timeout = null)
    { this.launch = launch; this.timeout = timeout ?? TimeSpan.FromSeconds(2); }
    internal int? ProcessId => process is { HasExited: false } ? process.Id : null;

    internal Destination? Capture()
    {
        lock (gate)
        {
            try { return ReadAsync().GetAwaiter().GetResult(); }
            catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException or
                FormatException or OverflowException or System.ComponentModel.Win32Exception)
            {
                string exit = process is { HasExited: true } ? $"; exit=0x{process.ExitCode:X8}" : "";
                Diagnostics.Event("focus.worker.failed", error.GetType().Name + exit + "; insertion held for review");
                Stop();
                return null;
            }
        }
    }

    private async Task<Destination?> ReadAsync()
    {
        using var deadline = new CancellationTokenSource(timeout);
        if (process == null || process.HasExited)
        {
            Stop();
            var info = launch?.Invoke() ?? NativeEngines.ProcessInfo(Path.Combine(AppContext.BaseDirectory, "TalkToMe.FocusHost.exe"));
            if (launch == null) { info.ArgumentList.Add("--owner"); info.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture)); }
            process = Process.Start(info) ?? throw new IOException("Focus worker did not start");
            _ = process.StandardError.ReadToEndAsync();
            Diagnostics.TrackChild(process);
            if (await process.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false) != "ready") throw new IOException("Focus worker did not become ready");
            Diagnostics.Event("focus.worker.ready", $"PID={process.Id}; native COM MTA");
        }
        await process.StandardInput.WriteLineAsync("capture".AsMemory(), deadline.Token).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
        string? line = await process.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false);
        return Parse(line);
    }

    internal static Destination? Parse(string? line)
    {
        if (line == "none") return null;
        if (line == null || line.Length > 4096) throw new IOException("Focus worker returned no valid response");
        var parts = line.Split(' ');
        if (parts.Length < 5) throw new FormatException("Invalid focus response");
        long window = long.Parse(parts[0], CultureInfo.InvariantCulture), focus = long.Parse(parts[1], CultureInfo.InvariantCulture);
        int count = int.Parse(parts[3], CultureInfo.InvariantCulture);
        if (window == 0 || focus == 0 || parts[2] is not ("0" or "1") || count < 1 || count > 128 || parts.Length != count + 4)
            throw new FormatException("Invalid focus response");
        return new((nint)window, (nint)focus, parts.Skip(4).Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray(), parts[2] == "1");
    }

    private void Stop()
    {
        if (process == null) return;
        try
        {
            process.StandardInput.Close();
            if (!process.WaitForExit(100)) { process.Kill(); process.WaitForExit(500); }
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or System.ComponentModel.Win32Exception) { }
        process.Dispose(); process = null;
    }
    public void Dispose() { lock (gate) Stop(); }
}
