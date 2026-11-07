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
        if (args is ["--crash-child", var crashDirectory]) { DiagnosticsCheck.Child(crashDirectory); return 1; }
        if (args is ["--crash-check"])
        {
            try { DiagnosticsCheck.Run().GetAwaiter().GetResult(); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        if (args is ["--clipboard"])
        {
            try { CheckClipboard(); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args is ["--capture-check"])
        {
            try { CaptureLifecycleCheck.Run(); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        if (args is ["--translation-check", var modelDir, var modelId])
        {
            try { EditingCheck.RunModels(modelDir, modelId).GetAwaiter().GetResult(); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        if (args is ["--translation-check", var cpuModelDir, var cpuModelId, "cpu"])
        {
            try { EditingCheck.RunModels(cpuModelDir, cpuModelId, gpu: false).GetAwaiter().GetResult(); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        if (args.Length is 3 or 4 && args[0] == "--benchmark")
        {
            if (args.Length == 4 && args[3] is not ("portable" or "cpu" or "auto")) throw new ArgumentException("Use --benchmark model-directory sample.wav [auto|cpu|portable].");
            try { PerformanceCheck.Benchmark(args[1], args[2], args.Length == 4 ? args[3] : "auto").GetAwaiter().GetResult(); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        if (args.Length is >= 3 and <= 5 && args[0] == "--local-models")
        {
            // Exercise first install, cached install and inference without a microphone or user settings.
            try { CheckLocalModels(args[1], args[2], args.Length >= 4 ? args[3] : "qwen2", args.Length == 5 ? args[4] : "base").GetAwaiter().GetResult(); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        if (args is ["--gpu-check", var modelDirectory, var audioSample])
        {
            try { GpuCheck.Run(modelDirectory, audioSample).GetAwaiter().GetResult(); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        if (args is ["--gpu-check", var newModels, var newSample, var speechModel, var translator])
        {
            try { GpuCheck.Run(newModels, newSample, speechModel, translator).GetAwaiter().GetResult(); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        if (args is ["--field"]) { RunField(); return 0; }
        Assert(Shortcut.Parse("Ctrl+Alt+F9") == new Shortcut(3, Keys.F9), "shortcut");
        Assert(Shortcut.Parse(new Settings().Shortcut) == new Shortcut(2, Keys.NumPad0), "default talking shortcut");
        for (int digit = 0; digit < 10; digit++)
        {
            var keypad = new Shortcut(2, Keys.NumPad0 + digit);
            Assert(Shortcut.Parse($"Ctrl + Numpad {digit}") == keypad && Shortcut.Parse($"Ctrl+NumPad{digit}") == keypad, "keypad shortcut");
            Assert(Shortcut.Parse($"Ctrl+{digit}") == new Shortcut(2, Keys.D0 + digit), "top-row shortcut");
        }
        CheckDownload().GetAwaiter().GetResult();
        EngineInstallCheck.Run().GetAwaiter().GetResult();
        CheckSettings();
        PerformanceCheck.Check();
        EditingCheck.Check();
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

    private static void CheckSettings()
    {
        var root = Directory.CreateTempSubdirectory("TalkToMe-check-");
        try
        {
            var fresh = Settings.Load(root.FullName);
            Assert(fresh.BundledSpeech && fresh.Translation == "qwen2", "fresh install uses bundled models");
            File.Copy(Path.Combine(Environment.CurrentDirectory, "settings.example.json"), Path.Combine(root.FullName, "settings.example.json"));
            var packaged = Settings.Load(root.FullName);
            Assert(packaged.BundledSpeech && packaged.Translation == "qwen2", "packaged first-run settings use Qwen 2B");
            fresh.Translation = "transcript";
            using (var editor = new EnglishEditor(fresh))
                Assert(editor.EditAsync("Привет", default).GetAwaiter().GetResult() == "Привет", "transcript only");
            string path = Path.Combine(root.FullName, "settings.json");
            File.WriteAllText(path, """{"shortcut":"Ctrl+Alt+F9","localServer":{"executable":"llama-server.exe","modelFile":"model.gguf"}}""");
            var old = Settings.Load(root.FullName);
            Assert(!old.BundledSpeech && old.Translation == "server", "legacy engine choice");
            Assert(old.LocalServer is { ContextSize: 8192, GpuLayers: "all" } && old.Sounds && old.RecordingMinutes == 5, "old preference defaults");
            old.Prompt = "Custom editing instructions."; old.Glossary = ["CFrame", "Vulkan"];
            old.SetApiKey("example-dummy-key");
            using var form = new SettingsDialog(old, _ => false);
            var draft = form.ReadForm();
            draft.Save();
            var saved = Settings.Load(root.FullName);
            Assert(saved.Shortcut == "Ctrl+Alt+F9", "saved recording shortcut survives settings dialog");
            Assert(saved.Prompt == old.Prompt && saved.Glossary.SequenceEqual(old.Glossary) && saved.LocalServer == old.LocalServer,
                "settings dialog round trip");
            Assert(!File.ReadAllText(path).Contains("example-dummy-key") && saved.ReadApiKey() == "example-dummy-key", "protected server key");
            saved.Shortcut = Settings.DefaultRecordShortcut;
            saved.CancelShortcut = "Ctrl+NumPad0";
            try { saved.Validate(); } catch (InvalidDataException) { return; }
            throw new Exception("Duplicate shortcuts accepted.");
        }
        finally { root.Delete(recursive: true); }
    }
    private sealed class DownloadResponse(byte[] bytes, bool transientFailure = false) : System.Net.Http.HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            if (transientFailure && Requests == 1)
                return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new System.Net.Http.ByteArrayContent(bytes) });
        }
    }
    private static async Task CheckDownload()
    {
        var dir = Directory.CreateTempSubdirectory("TalkToMe-download-");
        try {
            byte[] good = [1, 2, 3], bad = [4, 5, 6];
            var model = new ModelDownload("test.bin", "test/model", "test", good.Length,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(good)));
            var progress = new Progress<string>();
            var handler = new DownloadResponse(good);
            using var valid = new System.Net.Http.HttpClient(handler);
            using var corrupt = new System.Net.Http.HttpClient(new DownloadResponse(bad));
            var path = model.PathIn(dir.FullName);
            await File.WriteAllBytesAsync(path, bad).ConfigureAwait(false);
            try {
                await model.InstallAsync(dir.FullName, progress, default, corrupt).ConfigureAwait(false);
                throw new Exception("Corrupt download accepted.");
            } catch (InvalidDataException) { }
            Assert(File.ReadAllBytes(path).SequenceEqual(bad) && dir.GetFiles().Length == 1, "failed install preserved the previous file");
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            try {
                await model.InstallAsync(dir.FullName, progress, cancel.Token, valid).ConfigureAwait(false);
                throw new Exception("Cancelled install succeeded.");
            } catch (OperationCanceledException) { }
            await model.InstallAsync(dir.FullName, progress, default, valid).ConfigureAwait(false);
            await model.InstallAsync(dir.FullName, progress, default, valid).ConfigureAwait(false);
            Assert(handler.Requests == 1 && File.ReadAllBytes(path).SequenceEqual(good), "retry repaired the model and offline reuse succeeded");
            await File.WriteAllBytesAsync(path, bad).ConfigureAwait(false);
            var interrupted = new DownloadResponse(good, transientFailure: true);
            using var retry = new System.Net.Http.HttpClient(interrupted);
            await model.InstallAsync(dir.FullName, progress, default, retry).ConfigureAwait(false);
            Assert(interrupted.Requests == 2 && File.ReadAllBytes(path).SequenceEqual(good) && dir.GetFiles().Length == 1,
                "transient download failure retries and removes partial files");
        }
        finally { dir.Delete(recursive: true); }
    }
    private static async Task CheckLocalModels(string directory, string sample, string translation, string speechModel)
    {
        var settings = Settings.Defaults(Environment.CurrentDirectory);
        settings.Translation = translation;
        settings.SpeechModel = speechModel;
        // This checks the portable instruction set, not editing quality. The full
        // engineering prompt is tested separately with --translation-check; scalar
        // prefill of hundreds of tokens can exceed five minutes on hosted runners.
        if (Environment.GetEnvironmentVariable("TALKTOME_CPU") == "portable" && translation.StartsWith("qwen"))
            settings.Prompt = "Translate the Russian transcript into English. Return only the translation.";
        settings.Validate();
        settings.ModelDirectory = Path.GetFullPath(directory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        var token = timeout.Token;
        foreach (string id in settings.RequiredModels())
        {
            var asset = ModelDownload.Get(id);
            Console.WriteLine($"Installing or verifying {asset.Name}");
            await asset.InstallAsync(settings.ModelDirectory, new Progress<string>(), token).ConfigureAwait(false);
        }
        using var wav = new WaveFileReader(sample);
        using var pcm = new MemoryStream();
        wav.CopyTo(pcm);
        using var recognizer = new Recognizer(settings);
        var metrics = new ProcessingMetrics(settings, pcm.Length / 32000d, "auto");
        string text = await recognizer.RunAsync(pcm.ToArray(), "auto", token, metrics).ConfigureAwait(false);
        Console.WriteLine(text);
        Assert(text.Contains("country", StringComparison.OrdinalIgnoreCase), "bundled speech JFK sample");
        using var translator = new EnglishEditor(settings);
        string result = await translator.EditAsync("У меня два кота.", token, metrics).ConfigureAwait(false);
        Console.WriteLine(result);
        Assert(result.Contains("cat", StringComparison.OrdinalIgnoreCase), "bundled Russian to English translation");
        metrics.Finish("model check");
        Console.WriteLine(metrics.Json());
        if (Environment.GetEnvironmentVariable("TALKTOME_EXPECT_BACKEND") is { Length: > 0 } expected)
            Assert(metrics.SpeechBackend.StartsWith(expected) && metrics.TranslationBackend.StartsWith(expected), "expected inference backends");
    }
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
