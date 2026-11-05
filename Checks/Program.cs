using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using NAudio.Wave;
using TalkToMe;
using Shortcut = TalkToMe.Shortcut;

internal static class Program
{
    [DllImport("user32")] private static extern bool AllowSetForegroundWindow(int process);
    [STAThread]
    private static int Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args is ["--clipboard"])
        {
            try { CheckClipboard(); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args is ["--field"]) { RunField(); return 0; }
        Assert(Shortcut.Parse("Ctrl+Alt+F9") == new Shortcut(3, Keys.F9), "shortcut");
        foreach (string bad in new[] { "length", "tool_calls", "" })
            Reject(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = bad, message = new { content = "Partial" } } } }));
        Reject("""{"choices":[{"finish_reason":"stop","message":{"reasoning_content":"thought","content":"Final"}}]}""");
        Reject("""{"choices":[{"finish_reason":"stop","message":{"content":"<think>thought</think>Final"}}]}""");
        Assert(Parse("""{"choices":[{"finish_reason":"stop","message":{"content":"Why?"}}]}""") == "Why?", "question");
        foreach (string cue in new[] { "start", "stop" })
        {
            using var stream = typeof(RecordingSounds).Assembly.GetManifestResourceStream($"TalkToMe.Sounds.{cue}.wav")
                ?? throw new InvalidDataException($"Missing embedded {cue} sound.");
            using var wav = new WaveFileReader(stream);
            Assert(wav.WaveFormat.Encoding == WaveFormatEncoding.Pcm && wav.Length > 0, cue + " sound");
        }
        using (var icons = new AppIcons())
        {
            foreach (var icon in new[] { icons.Application, icons.Idle, icons.Recording, icons.Processing })
                Assert(icon.Handle != IntPtr.Zero, "embedded Windows icon");
            foreach (int size in new[] { 16, 32 })
            {
                icons.RenderCommands(size);
                Assert(icons.Commands.Images.Count == Enum.GetValues<CommandIcon>().Length, "command icons");
            }
        }
        Console.WriteLine("Response, shortcut, sound and icon checks passed.");
        if (args.Length == 0) return 0;
        if (args[0] != "--desktop" && args is not ["--browser" or "--session", _])
            throw new ArgumentException("Use --desktop [WAV files ...], --session clip.wav or --browser path-to-chromium.exe.");
        int exit = 0;
        using var owner = new Control();
        owner.CreateControl();
        EventHandler? start = null;
        start = async (_, _) =>
        {
            Application.Idle -= start;
            try
            {
                if (args[0] == "--browser") await BrowserCheck.RunAsync(owner.Handle, args[1]);
                else await CheckDesktop(owner.Handle, args[1..], args[0] == "--session");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
            finally { Application.ExitThread(); }
        };
        Application.Idle += start;
        Application.Run();
        return exit;
    }

    private static void Assert(bool condition, string message)
    { if (!condition) throw new Exception("Check failed: " + message); }
    private static string Parse(string json)
    { using var doc = JsonDocument.Parse(json); return EnglishEditor.CompleteText(doc.RootElement); }
    private static void Reject(string json)
    {
        try { Parse(json); }
        catch (InvalidDataException) { return; }
        throw new Exception("Unsafe completion was accepted.");
    }

    private static void CheckClipboard()
    {
        using var owner = new Control();
        uint originalSequence = ClipboardLease.GetClipboardSequenceNumber();
        string original = Clipboard.GetText();
        using (var lease = ClipboardLease.TryPlace(owner.Handle, originalSequence, "First line.\nПривет!"))
        {
            if (lease == null) throw new Exception("Clipboard busy, recently changed, or contains an unsupported format. Left it untouched.");
            Assert(lease.StillCurrent && Clipboard.GetText() == "First line.\r\nПривет!", "multiline Unicode clipboard");
            using (var newer = ClipboardLease.TryPlace(owner.Handle, ClipboardLease.GetClipboardSequenceNumber(), "newer copy"))
                Assert(newer != null && newer.StillCurrent && !lease.StillCurrent, "newer clipboard ownership");
            Assert(lease.StillCurrent, "restored clipboard ownership");
        }
        Assert(Clipboard.GetText() == original, "original clipboard text restored");
        Assert(ClipboardLease.TryPlace(owner.Handle, originalSequence, "stale") == null, "newer clipboard change preserved");
        Console.WriteLine("Multiline Unicode, clipboard restoration and stale clipboard sequence checks passed.");
    }

    // A separate process makes the focus, clipboard and paste checks cross a real application boundary.
    private static void RunField()
    {
        using var window = new Form { Text = "TalkToMe insertion check", Width = 520, Height = 250, TopMost = true };
        var first = new TextBox { Multiline = true, Dock = DockStyle.Fill };
        var second = new TextBox { Dock = DockStyle.Bottom };
        window.Controls.Add(first); window.Controls.Add(second);
        window.Shown += async (_, _) =>
        {
            window.Activate(); first.Focus();
            Console.WriteLine(window.Handle.ToInt64());
            using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
            while (await reader.ReadLineAsync() is {} cmd)
            {
                switch (cmd)
                {
                    case "first": window.Activate(); first.Clear(); first.Focus(); break;
                    case "second": second.Clear(); second.Focus(); break;
                    case "read": Console.WriteLine(JsonSerializer.Serialize(first.Text)); continue;
                    case "readSecond": Console.WriteLine(JsonSerializer.Serialize(second.Text)); continue;
                    case "exit": window.Close(); return;
                }
                Console.WriteLine("ok");
            }
            window.Close();
        };
        Application.Run(window);
    }

    private static async Task CheckDesktop(nint owner, string[] files, bool checkSession)
    {
        using var session = checkSession ? new SessionCheck(files.Single()) : null;
        using var field = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--field")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
        })!;
        AllowSetForegroundWindow(field.Id);
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var token = limit.Token;
        var window = new IntPtr(long.Parse((await field.StandardOutput.ReadLineAsync(token))!));
        async Task<string> Command(string command)
        {
            await field.StandardInput.WriteLineAsync(command);
            return (await field.StandardOutput.ReadLineAsync(token))!;
        }
        Destination Target()
        {
            var captured = Destination.Capture();
            var foreground = WindowsInput.GetForegroundWindow();
            WindowsInput.GetWindowThreadProcessId(foreground, out var foregroundPid);
            Assert(captured?.Window == window, $"test field must have keyboard focus (fixture PID {field.Id}, foreground PID {foregroundPid}, same HWND {foreground == window}, eligible {captured != null})");
            return captured!;
        }
        try
        {
            await Command("first");
            if (Destination.Capture()?.Window != window)
            {
                Console.WriteLine("Focus the TalkToMe insertion check window to continue.");
                for (int i = 0; i < 100 && Destination.Capture()?.Window != window; i++)
                    await Task.Delay(100, token);
            }
            var first = Target();
            await Command("second");
            Assert(!first.StillFocused(), "field change in one window");
            string? stale = await Insertion.TryAsync(owner, first, ClipboardLease.GetClipboardSequenceNumber(), Keys.F9, "stale", token, () => {});
            Assert(stale != null, "stale target held for review");
            await Command("first");
            const string example = "Café · Привет — 50 ms?";
            string? held = await Insertion.TryAsync(owner, Target(), ClipboardLease.GetClipboardSequenceNumber(), Keys.F9, example, token, () => {});
            string? actual = JsonSerializer.Deserialize<string>(await Command("read"));
            Assert(held == null && actual == example, $"Unicode insertion exactly once (held={held}, received length={actual?.Length})");
            Console.WriteLine("Cross-process focus and Unicode insertion checks passed.");
            if (session != null) { await session.RunAsync(Command, token); return; }
            if (files.Length == 0) return;

            var settings = Settings.Load(Environment.CurrentDirectory);
            if (Environment.GetEnvironmentVariable("TALKTOME_CHECK_SERVER") is { Length: > 0 } url) settings.ServerUrl = url;
            using var asr = new Recognizer(settings);
            using var editor = new EnglishEditor(settings);
            await asr.RunAsync(null, "auto", token);
            Assert(await asr.RunAsync(new byte[32000], "auto", token) == "", "silence");
            var noise = new byte[32000];
            for (int i = 0; i < noise.Length; i += 2) noise[i] = (byte)(i % 5);
            Assert(await asr.RunAsync(noise, "auto", token) == "", "quiet non-speech");
            var times = new List<double>();
            foreach (var file in files)
            {
                using var wav = new WaveFileReader(file);
                Assert(wav.WaveFormat.SampleRate == 16000 && wav.WaveFormat.Channels == 1 && wav.WaveFormat.BitsPerSample == 16, "PCM format");
                using var buffer = new MemoryStream();
                wav.CopyTo(buffer);
                await Command("first");
                var target = Target();
                uint sequence = ClipboardLease.GetClipboardSequenceNumber();
                var watch = Stopwatch.StartNew();
                var transcript = await asr.RunAsync(buffer.ToArray(), "auto", token);
                string english = await editor.EditAsync(transcript, token);
                var paste = Insertion.TryAsync(owner, target, sequence, Keys.F9, english, token, () => {});
                for (int i = 0; i < 100; i++)
                {
                    if (JsonSerializer.Deserialize<string>(await Command("read")) == english.ReplaceLineEndings()) { watch.Stop(); break; }
                    await Task.Delay(15, token);
                }
                Assert(await paste == null && !watch.IsRunning, "English received by edit control");
                Console.WriteLine($"{Path.GetFileName(file)} | {transcript} => {english} | {watch.Elapsed.TotalSeconds:F2}s");
                times.Add(watch.Elapsed.TotalSeconds);
            }
            var warm = times.Skip(1).Order().ToArray();
            if (warm.Length > 0)
                Console.WriteLine($"Warm audio-ready to observed insertion (first sample excluded): n={warm.Length}, median={(warm[(warm.Length-1)/2]+warm[warm.Length/2])/2:F2}s, p95={warm[(int)Math.Ceiling(warm.Length*.95)-1]:F2}s");
        }
        finally
        {
            field.StandardInput.Close();
            if (!field.WaitForExit(1500)) field.Kill();
        }
    }
}
