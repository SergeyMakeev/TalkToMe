using System.Runtime.InteropServices;
using TalkToMe;

internal static class CaptureLifecycleCheck
{
    private sealed class Capture : IAudioCapture
    {
        private readonly TaskCompletionSource<byte[]> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed;
        internal bool Started;
        public Task<byte[]> Completion => completion.Task;
        public void Start() { Started = true; }
        public void Stop() => throw new IOException("Simulated microphone removal at hotkey release");
        public void Dispose() { Disposed = true; }
    }
    [DllImport("user32")] private static extern nint SendMessage(nint window, uint message, nint wp, nint lp);
    internal static void Run()
    {
        CheckStartReentry();
        CheckStartReentry(cancelDuringOpen: true);
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

    private static void CheckStartReentry(bool cancelDuringOpen = false)
    {
        var settings = Settings.Defaults(Environment.CurrentDirectory);
        settings.CopyOnly = true; settings.Sounds = false;
        settings.Shortcut = "Ctrl+Alt+F24"; settings.CancelShortcut = "Ctrl+Shift+F24";
        var captures = new List<Capture>();
        DictationWindow? owner = null;
        using var window = new DictationWindow(settings, _ => {
            var capture = new Capture(); captures.Add(capture);
            // UI Automation/native device calls can pump the STA message loop.
            if (captures.Count == 1) SendMessage(owner!.Handle, 0x0312, cancelDuringOpen ? 2 : 1, 0);
            return capture;
        });
        owner = window;
        SendMessage(window.Handle, 0x0312, 1, 0);
        SendMessage(window.Handle, 0x0312, 2, 0);
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (captures.Any(c => !c.Disposed) && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
        if (captures.Count != 1) throw new Exception("Reentrant hotkey started overlapping microphone sessions");
        if (!captures[0].Disposed) throw new Exception("Reentrant capture did not release its microphone");
        if (cancelDuringOpen && captures[0].Started) throw new Exception("Cancelled device startup still began recording");
        Console.WriteLine(cancelDuringOpen ? "Cancellation during device startup prevented recording and disposed the device." : "Reentrant hotkey during microphone startup did not start a second session.");
    }
}
