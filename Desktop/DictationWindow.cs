using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TalkToMe;

internal sealed class DictationWindow : Form
{
    private sealed class Session(string language, Destination? target)
    {
        public readonly CancellationTokenSource Cancel = new();
        public string Language = language;
        public Destination? Target = target;
        public readonly uint ClipboardSequence = ClipboardLease.GetClipboardSequenceNumber();
        public readonly Stopwatch Recording = Stopwatch.StartNew();
        public readonly Stopwatch Latency = new();
        public byte[]? Audio;
        public string Transcript = "";
    }

    private Settings settings;
    private readonly Func<int, IAudioCapture> openMicrophone;
    private readonly Action<bool> playRecordingSound;
    private Shortcut shortcut, cancelShortcut;
    private Recognizer asr;
    private EnglishEditor editor;
    private readonly Label instructions;
    private readonly ComboBox language, mode;
    private readonly Button preferences = new() { Text = "Settings…", AutoSize = true };
    private readonly HashSet<Task> processing = [];
    private CancellationTokenSource? warmupCancel;
    private Task warming = Task.CompletedTask;
    private bool configuring;
    private readonly Overlay overlay = new();
    private readonly AppIcons icons = new();
    private readonly NotifyIcon tray = new() { Text = "TalkToMe" };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 30 };
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<double> timings = [];
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(640, 0) };
    private readonly Label timingLabel = new() { AutoSize = true };
    private readonly TextBox original = TextArea(true), english = TextArea(false);
    private readonly Button retry = new() { Text = "Retry processing", AutoSize = true, Enabled = false };
    private readonly Button cancel = new() { Text = "Cancel", AutoSize = true, Enabled = false };
    private readonly CheckBox copyOnly = new() { Text = "Review before pasting", AutoSize = true };
    private Session? current;
    private IAudioCapture? microphone;
    private bool busy, dispatched, exiting, cancelRegistered;
    private long hideAt;

    public DictationWindow(Settings settings, Func<int, IAudioCapture>? openMicrophone = null,
        Action<bool>? playRecordingSound = null)
    {
        this.settings = settings;
        this.openMicrophone = openMicrophone ?? (device => new Microphone(device));
        this.playRecordingSound = playRecordingSound ?? RecordingSounds.Play;
        shortcut = Shortcut.Parse(settings.Shortcut);
        cancelShortcut = Shortcut.Parse(settings.CancelShortcut);
        asr = new(settings);
        editor = new(settings);
        Text = "TalkToMe";
        Icon = icons.Application;
        tray.Icon = icons.Idle;
        Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(550, 520);
        Size = new Size(720, 610);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 10, Padding = new Padding(16) };
        Controls.Add(layout);
        for (int i = 0; i < layout.RowCount; i++)
            layout.RowStyles.Add(i is 4 or 6 ? new RowStyle(SizeType.Percent, i == 4 ? 38 : 62) : new RowStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        instructions = new Label { AutoSize = true, Font = new Font(Font, FontStyle.Bold),
            Text = $"Dictate at the cursor   ·   {settings.Shortcut}" };
        layout.Controls.Add(instructions, 0, 0);
        var options = Row();
        language = Choice(["Auto", "Russian", "English"], settings.Language switch { "ru" => 1, "en" => 2, _ => 0 });
        language.SelectedIndexChanged += (_, _) => { if (!configuring) { this.settings.Language = new[] { "auto", "ru", "en" }[language.SelectedIndex]; SaveSettings(); } };
        mode = Choice(["Hold to talk", "Press to toggle"], settings.Toggle ? 1 : 0);
        mode.SelectedIndexChanged += (_, _) => { if (!configuring) { this.settings.Toggle = mode.SelectedIndex == 1; SaveSettings(); } };
        copyOnly.Checked = settings.CopyOnly;
        copyOnly.CheckedChanged += (_, _) => { if (!configuring) { this.settings.CopyOnly = copyOnly.Checked; SaveSettings(); } };
        options.Controls.AddRange([new Label { Text = "Input", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, language, mode, copyOnly]);
        layout.Controls.Add(options, 0, 1);
        layout.Controls.Add(status, 0, 2);
        layout.Controls.Add(Caption("Source transcript"), 0, 3);
        layout.Controls.Add(original, 0, 4);
        layout.Controls.Add(Caption("Written English"), 0, 5);
        layout.Controls.Add(english, 0, 6);
        var actions = Row();
        var copyEnglish = new Button { Text = "Copy English", AutoSize = true };
        var copySource = new Button { Text = "Copy transcript", AutoSize = true };
        var quit = new Button { Text = "Quit", AutoSize = true };
        SetCommandIcon(copyEnglish, CommandIcon.Copy);
        SetCommandIcon(copySource, CommandIcon.Copy);
        SetCommandIcon(retry, CommandIcon.Retry);
        SetCommandIcon(cancel, CommandIcon.Cancel);
        SetCommandIcon(quit, CommandIcon.Quit);
        quit.Click += (_, _) => { exiting = true; Close(); };
        copyEnglish.Click += (_, _) => Copy(english.Text);
        copySource.Click += (_, _) => Copy(original.Text);
        retry.Click += async (_, _) => { if (!busy && current is { Audio: not null } s) await ProcessAsync(s, false); };
        cancel.Click += (_, _) => CancelSession();
        preferences.Click += async (_, _) => await ConfigureAsync();
        actions.Controls.AddRange([copyEnglish, copySource, retry, cancel, preferences, quit]);
        layout.Controls.Add(actions, 0, 7);
        layout.Controls.Add(timingLabel, 0, 8);
        layout.Controls.Add(new Label { AutoSize = true, Text = "Close to keep dictating. Open or quit TalkToMe from the notification area." }, 0, 9);

        tray.ContextMenuStrip = new ContextMenuStrip { ImageList = icons.Commands };
        tray.ContextMenuStrip.Items.Add("Show last result", null, (_, _) => OpenPanel()).ImageIndex = (int)CommandIcon.ShowResult;
        tray.ContextMenuStrip.Items.Add("Settings…", null, async (_, _) => await ConfigureAsync());
        tray.ContextMenuStrip.Items.Add("Cancel dictation", null, (_, _) => CancelSession()).ImageIndex = (int)CommandIcon.Cancel;
        tray.ContextMenuStrip.Items.Add("Quit", null, (_, _) => { exiting = true; Close(); }).ImageIndex = (int)CommandIcon.Quit;
        RefreshCommandIcons();
        DpiChanged += (_, e) => RefreshCommandIcons(e.DeviceDpiNew);
        SystemColorsChanged += (_, _) => RefreshCommandIcons();
        tray.DoubleClick += (_, _) => OpenPanel();
        timer.Tick += (_, _) => Tick();
    }

    private void SetCommandIcon(Button button, CommandIcon icon)
    {
        button.ImageList = icons.Commands;
        button.ImageIndex = (int)icon;
        button.TextImageRelation = TextImageRelation.ImageBeforeText;
    }

    private void RefreshCommandIcons(int? dpi = null)
    {
        int size = (int)Math.Round(16 * (dpi ?? DeviceDpi) / 96d);
        icons.RenderCommands(size);
        tray.ContextMenuStrip!.ImageScalingSize = icons.Commands.ImageSize;
    }

    private static TextBox TextArea(bool readOnly) => new() {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = readOnly, ScrollBars = ScrollBars.Vertical,
        BackColor = SystemColors.Window, Font = new Font("Segoe UI", 11) };
    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(0, 8, 0, 1) };
    private static FlowLayoutPanel Row() => new() { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
    private static ComboBox Choice(string[] items, int selected)
    {
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 135 };
        box.Items.AddRange(items); box.SelectedIndex = selected; return box;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        tray.Visible = true; timer.Start();
        if (!RegisterRecord(shortcut))
        {
            SetStatus($"Windows could not register {settings.Shortcut}. Choose a different shortcut in Settings.", null);
            return;
        }
        WarmUp();
    }

    private bool RegisterRecord(Shortcut chord) => WindowsInput.RegisterHotKey(Handle, 1, chord.Modifiers | 0x4000, (uint)chord.Key);
    private void WarmUp()
    {
        warmupCancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        warming = WarmAsync(warmupCancel.Token);
    }
    private async Task WarmAsync(CancellationToken token)
    {
        SetStatus("Loading dictation models. You can start dictating.", null);
        try
        {
            await Task.WhenAll(asr.RunAsync(null, "auto", token), editor.PrepareAsync(token));
            if (current == null && !busy && !exiting && !token.IsCancellationRequested) SetStatus("Ready. Focus a text field and use the dictation shortcut.", null);
        }
        catch (Exception ex) { if (!exiting && !token.IsCancellationRequested && !busy) SetStatus(ex.Message, null); }
    }

    private async Task ConfigureAsync()
    {
        if (busy || configuring || exiting) return;
        configuring = true;
        try
        {
            OpenPanel();
            using var dialog = new SettingsDialog(settings, draft => {
                var record = Shortcut.Parse(draft.Shortcut);
                var cancelKey = Shortcut.Parse(draft.CancelShortcut);
                WindowsInput.UnregisterHotKey(Handle, 1);
                try {
                    if (!RegisterRecord(record)) throw new InvalidDataException("The recording shortcut is in use.");
                    if (!WindowsInput.RegisterHotKey(Handle, 2, cancelKey.Modifiers | 0x4000, (uint)cancelKey.Key))
                        throw new InvalidDataException("The cancel shortcut is in use.");
                    WindowsInput.UnregisterHotKey(Handle, 2);
                    draft.Save();
                } catch {
                    WindowsInput.UnregisterHotKey(Handle, 1);
                    RegisterRecord(shortcut);
                    throw;
                }
                settings = draft; shortcut = record; cancelShortcut = cancelKey;
                return true;
            });
            if (dialog.ShowDialog(this) != DialogResult.OK || exiting) return;
            Enabled = false;
            try {
                warmupCancel?.Cancel();
                await Task.WhenAll(processing.Append(warming).ToArray());
                if (exiting) return;
                warmupCancel?.Dispose();
                editor.Dispose(); asr.Dispose();
                editor = new(settings); asr = new(settings);
                instructions.Text = $"Dictate at the cursor   ·   {settings.Shortcut}";
                language.SelectedIndex = settings.Language switch { "ru" => 1, "en" => 2, _ => 0 };
                mode.SelectedIndex = settings.Toggle ? 1 : 0;
                copyOnly.Checked = settings.CopyOnly;
                WarmUp();
            } finally { if (!exiting) Enabled = true; }
        }
        finally { configuring = false; }
    }

    private void SaveSettings()
    {
        try { settings.Save(); }
        catch (Exception ex) { SetStatus("Could not save preferences: " + ex.Message, null); }
    }

    private void OpenPanel()
    {
        if (current != null) current.Target = null;
        Show(); WindowState = FormWindowState.Normal; Activate();
    }

    private void Copy(string text)
    {
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); SetStatus("Copied.", null); }
        catch (ExternalException) { SetStatus("Clipboard busy. Select the text and copy it again.", null); }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312)
        {
            if (m.WParam == 2) CancelSession();
            if (m.WParam == 1 && !exiting && !configuring)
            {
                if (microphone != null && settings.Toggle) StopCapture();
                else if (!busy) StartCapture();
            }
            return;
        }
        base.WndProc(ref m);
    }

    private void SetStatus(string detail, string? badge, bool transient = false)
    {
        status.Text = detail;
        if (badge == null) return;
        overlay.Indicate(badge);
        hideAt = transient ? Environment.TickCount64 + 3200 : 0;
    }

    private void SetBusy(bool value)
    {
        busy = value; cancel.Enabled = value && !dispatched;
        preferences.Enabled = !value;
        UpdateTrayIcon();
        retry.Enabled = !value && current?.Audio != null;
        if (value && !cancelRegistered)
            cancelRegistered = WindowsInput.RegisterHotKey(Handle, 2, cancelShortcut.Modifiers | 0x4000, (uint)cancelShortcut.Key);
        if (!value && cancelRegistered) { WindowsInput.UnregisterHotKey(Handle, 2); cancelRegistered = false; }
    }

    private void UpdateTrayIcon()
    {
        var (icon, title) = microphone != null ? (icons.Recording, "TalkToMe · Recording")
            : busy ? (icons.Processing, "TalkToMe · Processing") : (icons.Idle, "TalkToMe");
        if (tray.Icon == icon) return;
        tray.Icon = icon;
        tray.Text = title;
    }

    private void Tick()
    {
        if (hideAt != 0 && Environment.TickCount64 >= hideAt) { hideAt = 0; overlay.Hide(); }
        if (busy && current?.Target is {} target && WindowsInput.GetForegroundWindow() != target.Window) current.Target = null;
        if (microphone != null && ((!settings.Toggle && !shortcut.Held) || current!.Recording.Elapsed.TotalMinutes >= settings.RecordingMinutes))
            StopCapture();
    }

    private async void StartCapture()
    {
        var s = new Session(settings.Language, settings.CopyOnly ? null : Destination.Capture());
        IAudioCapture? capture = null;
        try
        {
            capture = openMicrophone(settings.Microphone);
            capture.Start();
            current = s; dispatched = false;
            original.Clear(); english.Clear(); microphone = capture;
            if (settings.Sounds) playRecordingSound(true);
            SetBusy(true);
            var hint = settings.Toggle ? "Press shortcut to finish" : "Release to finish";
            SetStatus("Recording. " + hint + (cancelRegistered ? $". {settings.CancelShortcut} cancels." : "."),
                "Recording  ·  " + hint);
            byte[] pcm;
            try { pcm = await capture.Completion; }
            finally { RecordingEnded(capture); }
            capture.Dispose(); capture = null;
            if (current != s || exiting) return;
            s.Audio = pcm;
            if (!s.Latency.IsRunning) s.Latency.Start();
            await ProcessAsync(s, true);
        }
        catch (Exception ex)
        {
            if (!exiting && (current == s || !busy))
            {
                microphone = null; SetBusy(false);
                SetStatus("Microphone: " + ex.Message + " Check the recording device and Windows microphone access.", "Microphone unavailable", true);
            }
        }
        finally { capture?.Dispose(); }
    }

    private void StopCapture()
    {
        var capture = microphone;
        if (capture == null) return;
        current!.Latency.Start();
        capture.Stop();
        RecordingEnded(capture);
    }

    private void RecordingEnded(IAudioCapture capture)
    {
        // A stop and its later device callback mark the same transition. A cancelled
        // capture can also finish after the next recording has already started.
        if (microphone != capture) return;
        microphone = null;
        UpdateTrayIcon();
        if (settings.Sounds) playRecordingSound(false);
    }

    private void CancelSession()
    {
        if (!busy || dispatched || current == null) return;
        var s = current;
        StopCapture();
        current = null;
        s.Cancel.Cancel();
        original.Clear(); english.Clear();
        SetBusy(false);
        SetStatus("Cancelled.", "Cancelled", true);
    }

    private async Task ProcessAsync(Session s, bool insert)
    {
        var work = ProcessSessionAsync(s, insert);
        processing.Add(work);
        try { await work; } finally { processing.Remove(work); }
    }

    private async Task ProcessSessionAsync(Session s, bool insert)
    {
        var token = s.Cancel.Token;
        dispatched = false;
        SetBusy(true);
        try
        {
            string language = insert ? s.Language : settings.Language;
            if (s.Transcript.Length == 0 || language != s.Language)
            {
                SetStatus("Transcribing speech…", "Transcribing…");
                s.Transcript = await asr.RunAsync(s.Audio!, language, token);
                if (current != s || exiting) return;
                s.Language = language;
                original.Text = s.Transcript;
                english.Clear();
            }
            if (s.Transcript.Length == 0)
            {
                s.Audio = null;
                SetStatus("No speech detected.", "No speech detected", true);
                return;
            }
            SetStatus("Writing English with llama-server…", "Writing English…");
            var result = await editor.EditAsync(s.Transcript, token);
            if (current != s || exiting) return;
            english.Text = result.ReplaceLineEndings();
            string? held = !insert || settings.CopyOnly ? "Review requested." : await Insertion.TryAsync(Handle, s.Target, s.ClipboardSequence, shortcut.Key, result, token, () =>
            {
                dispatched = true; cancel.Enabled = false;
                s.Latency.Stop(); timings.Add(s.Latency.Elapsed.TotalMilliseconds);
                var ordered = timings.Order().ToArray();
                double median = (ordered[(ordered.Length - 1) / 2] + ordered[ordered.Length / 2]) / 2;
                timingLabel.Text = $"Release to input dispatch · last {s.Latency.Elapsed.TotalSeconds:F2}s · median {median / 1000:F2}s · p95 {ordered[(int)Math.Ceiling(ordered.Length * .95) - 1] / 1000:F2}s · n={ordered.Length}";
            });
            if (current != s || exiting) return;
            SetStatus(held == null ? "Text sent to the focused field. The result is also available here." :
                held + " Review and copy the English here.", held == null ? "Text sent" : "Ready to copy  ·  Open TalkToMe", true);
        }
        catch (Exception ex)
        {
            if (current == s && !exiting)
                SetStatus(ex is TaskCanceledException ? "Processing timed out. Check llama-server and retry." : ex.Message,
                    "Processing failed  ·  Open TalkToMe to retry", true);
        }
        finally { if (current == s && !exiting) SetBusy(false); }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
        // Allow an already-dispatched paste to restore its clipboard before destroying the owner window.
        if (dispatched && busy)
        {
            e.Cancel = true; exiting = false;
            _ = CloseAfterPasteAsync(); return;
        }
        exiting = true; CancelSession(); lifetime.Cancel();
        WindowsInput.UnregisterHotKey(Handle, 1);
        tray.Visible = false; timer.Stop();
        base.OnFormClosing(e);
    }

    private async Task CloseAfterPasteAsync()
    {
        while (busy) await Task.Delay(50);
        exiting = true; Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose(); tray.Dispose(); overlay.Dispose(); editor.Dispose(); asr.Dispose(); warmupCancel?.Dispose(); lifetime.Dispose();
        }
        base.Dispose(disposing);
        if (disposing) icons.Dispose();
    }
}
