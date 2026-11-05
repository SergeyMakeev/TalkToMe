using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.Wave;
using TalkToMe;

internal sealed class SessionCheck : IDisposable
{
    private sealed class RecordedClip(byte[] pcm) : IAudioCapture
    {
        private readonly TaskCompletionSource<byte[]> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<byte[]> Completion => completion.Task;
        public void Start() { }
        public void Stop() => completion.TrySetResult(pcm);
        public void Dispose() { }
    }

    [DllImport("user32")] private static extern nint SendMessage(nint hwnd, uint message, nint wparam, nint lparam);
    private readonly DictationWindow app;
    private readonly List<bool> recordingSounds = [];
    public SessionCheck(string audio)
    {
        using var wav = new WaveFileReader(audio);
        if (wav.WaveFormat.SampleRate != 16000 || wav.WaveFormat.BitsPerSample != 16 || wav.WaveFormat.Channels != 1)
            throw new InvalidDataException("Session check needs 16 kHz mono PCM.");
        using var data = new MemoryStream();
        wav.CopyTo(data);
        var pcm = data.ToArray();
        var settings = Settings.Load(Environment.CurrentDirectory);
        if (Environment.GetEnvironmentVariable("TALKTOME_CHECK_SERVER") is { Length: > 0 } url) settings.ServerUrl = url;
        settings.Shortcut = "Ctrl+Alt+F24"; settings.Toggle = true; settings.CopyOnly = false;
        app = new DictationWindow(settings, _ => new RecordedClip(pcm), recordingSounds.Add);
        app.Show();
    }

    private void Shortcut(int id = 1) => SendMessage(app.Handle, 0x0312, id, 0);

    public async Task RunAsync(Func<string, Task<string>> command, CancellationToken token)
    {
        var layout = app.Controls.OfType<TableLayoutPanel>().Single();
        var retry = layout.Controls.OfType<FlowLayoutPanel>().SelectMany(p => p.Controls.OfType<Button>())
            .Single(b => b.Text == "Retry processing");
        var english = layout.Controls.OfType<TextBox>().Single(t => !t.ReadOnly);
        async Task Ready()
        {
            while (!retry.Enabled) await Task.Delay(25, token);
            if (english.TextLength == 0) throw new Exception("Session failed: " + string.Join(" ", layout.Controls.OfType<Label>().Select(l => l.Text)));
        }
        await command("first");
        var field = Destination.Capture() ?? throw new Exception("Test field lost focus.");
        Shortcut();
        if (!field.StillFocused()) throw new Exception("Recording indicator stole focus.");
        Shortcut(2); // Cancel a recording.
        Shortcut(); Shortcut(); Shortcut(2); // Cancel after Stop before the worker can commit its result.
        Shortcut(); Shortcut();
        await Ready();
        string expected = english.Text;
        if (JsonSerializer.Deserialize<string>(await command("read")) != expected) throw new Exception("Session failed to insert exactly once.");
        app.Show();
        retry.PerformClick();
        if (retry.Enabled) throw new Exception("Retry did not start.");
        await Ready(); app.Hide();
        if (JsonSerializer.Deserialize<string>(await command("read")) != expected) throw new Exception("Retry inserted a duplicate.");
        await command("first");
        Shortcut(); Shortcut();
        await command("second");
        await Ready();
        if (JsonSerializer.Deserialize<string>(await command("read")) != "" ||
            JsonSerializer.Deserialize<string>(await command("readSecond")) != "")
            throw new Exception("A focus change inserted text into an unintended field.");
        if (!recordingSounds.SequenceEqual(new[] { true, false, true, false, true, false, true, false }))
            throw new Exception("Expected one start and one stop sound per recording, including cancellation.");
        Console.WriteLine("Desktop session passed: record/stop, nonactivating overlay, cancellation, late results, retry, focus change, recovery.");
    }

    public void Dispose() => app.Dispose();
}
