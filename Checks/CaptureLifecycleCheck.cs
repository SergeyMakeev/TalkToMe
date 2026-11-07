using System.Runtime.InteropServices;
using TalkToMe;

internal static class CaptureLifecycleCheck
{
    private sealed class Capture : IAudioCapture
    {
        private readonly TaskCompletionSource<byte[]> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed;
        public Task<byte[]> Completion => completion.Task;
        public void Start() { }
        public void Stop() => throw new IOException("Simulated microphone removal at hotkey release");
        public void Dispose() { Disposed = true; }
    }
    [DllImport("user32")] private static extern nint SendMessage(nint window, uint message, nint wp, nint lp);
    internal static void Run()
    {
        var settings = Settings.Defaults(Environment.CurrentDirectory);
        settings.Toggle = true; settings.CopyOnly = true;
        settings.Shortcut = "Ctrl+Alt+F24"; settings.CancelShortcut = "Ctrl+Shift+F24";
        var captures = new List<Capture>();
        using var window = new DictationWindow(settings, _ => { var capture = new Capture(); captures.Add(capture); return capture; },
            _ => throw new IOException("Simulated sound device removal"));
        void Hotkey(int id) => SendMessage(window.Handle, 0x0312, id, 0);
        void WaitDisposed(Capture capture)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!capture.Disposed && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
            if (!capture.Disposed) throw new Exception("Failed capture did not release its resources");
        }
        Hotkey(1); Hotkey(1);
        if (captures.Count != 1) throw new Exception("Capture did not start");
        WaitDisposed(captures[0]);
        var labels = window.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<Label>();
        if (!labels.Any(l => l.Text.Contains("could not stop recording"))) throw new Exception("Stop failure was not reported");
        Hotkey(1);
        if (captures.Count != 2) throw new Exception("Cannot record again after stop failure");
        Hotkey(2);
        WaitDisposed(captures[1]);
        Console.WriteLine("Hotkey stop, microphone removal, sound failure, cancellation and recording recovery checks passed.");
    }
}
