using NAudio.Wave;

namespace TalkToMe;

internal sealed class SettingsDialog : Form
{
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly List<Action<Settings>> collect = [];
    private TableLayoutPanel grid = null!;
    private readonly Settings original;
    private readonly Func<Settings, bool> apply;
    private readonly Label feedback = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new(590, 0) };
    private readonly Button save = new() { Text = "Save", AutoSize = true };
    private CancellationTokenSource? install;

    public SettingsDialog(Settings settings, Func<Settings, bool> apply)
    {
        original = settings;
        this.apply = apply;
        Text = "TalkToMe Settings";
        Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new(705, 564);
        MinimumSize = new(610, 500);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new(12) };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(tabs, 0, 0); layout.Controls.Add(feedback, 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.AddRange([save, cancel]);
        layout.Controls.Add(buttons, 0, 2); Controls.Add(layout);
        AcceptButton = save; CancelButton = cancel;
        save.Click += async (_, _) => await SaveAsync();
        FormClosing += (_, _) => install?.Cancel();

        Page("Models");
        var speech = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        speech.Items.AddRange(["Whisper Base · smaller and faster · 61 MB", "Whisper Large v3 Turbo · 575 MB", "Parakeet TDT v3 · 714 MB", "Custom Python / faster-whisper"]);
        speech.SelectedIndex = !settings.BundledSpeech ? 3 : settings.SpeechModel == "base" ? 0 : settings.SpeechModel == "parakeet" ? 2 : 1;
        Row("Listen with", speech);
        var translation = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        string[] ids = ["qwen08", "qwen2", "qwen4", "translategemma", "transcript", "server"];
        translation.Items.AddRange(["Local Qwen 3.5 · 0.8B · smallest · 563 MB", "Local Qwen 3.5 · 2B · 1.28 GB", "Local Qwen 3 · 4B Instruct · 2.50 GB",
            "TranslateGemma 4B · translation only · 2.49 GB", "Transcript only · keep the spoken language", "Use my own server"]);
        translation.SelectedIndex = Array.IndexOf(ids, settings.Translation);
        Row("Translate and edit with", translation);
        Row("", new Label { AutoSize = true, MaximumSize = new(420, 0), Text = "For accuracy, try Whisper Large v3 Turbo and Qwen 2B or 4B. Qwen 0.8B can miss words and instructions." });
        void SetModels(Settings s) {
            s.BundledSpeech = speech.SelectedIndex != 3;
            if (s.BundledSpeech) s.SpeechModel = speech.SelectedIndex == 0 ? "base" : speech.SelectedIndex == 2 ? "parakeet" : "speech";
            s.Translation = ids[translation.SelectedIndex];
        }
        collect.Add(SetModels);
        Check("Use NVIDIA GPU when available (automatic CPU fallback)", settings.UseGpu, (s,v) => s.UseGpu = v);
        var summary = new Label { AutoSize = true, MaximumSize = new(420, 0) };
        Row("", summary);
        var modelNotice = new Label { AutoSize = true, MaximumSize = new(420, 0), Text = "TranslateGemma: Russian → English only. English stays unchanged. Editing instructions and glossary are not applied." };
        Row("", modelNotice);
        var modelLicense = new LinkLabel { AutoSize = true, Text = "TranslateGemma license (Gemma terms)" };
        modelLicense.LinkClicked += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://ai.google.dev/gemma/terms") { UseShellExecute = true });
        Row("", modelLicense);
        var parakeetLicense = new LinkLabel { AutoSize = true, Text = "Parakeet by NVIDIA · CC BY 4.0" };
        parakeetLicense.LinkClicked += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3") { UseShellExecute = true });
        Row("", parakeetLicense);
        var verify = new Button { Text = "Verify downloaded models", AutoSize = true };
        Row("", verify);
        verify.Click += async (_, _) => await SaveAsync(verifyOnly: true);

        Page("Dictation");
        var microphone = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        microphone.Items.Add(new InputDevice(-1, "System default"));
        try {
            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
                microphone.Items.Add(new InputDevice(i, WaveInEvent.GetCapabilities(i).ProductName));
        } catch (NAudio.MmException) { /* The default and previously selected input are still available. */ }
        var selected = microphone.Items.Cast<InputDevice>().FirstOrDefault(x => x.Id == settings.Microphone);
        if (selected == null) { selected = new(settings.Microphone, $"Unavailable input ({settings.Microphone})"); microphone.Items.Add(selected); }
        microphone.SelectedItem = selected;
        Row("Microphone", microphone);
        collect.Add(s => s.Microphone = ((InputDevice)microphone.SelectedItem!).Id);
        Choice("Recording shortcut", settings.Shortcut, [Settings.DefaultRecordShortcut, "Ctrl+Alt+F9"],
            (s,v) => s.Shortcut = v, editable: true);
        TextValue("Cancel shortcut", settings.CancelShortcut, (s,v) => s.CancelShortcut = v);
        Number("Recording limit (minutes)", settings.RecordingMinutes, 1, 60, (s,v) => s.RecordingMinutes = (int)v);
        Choice("Spoken language", settings.Language, ["auto","ru","en"], (s,v) => s.Language = v);
        Check("Press to toggle recording", settings.Toggle, (s,v) => s.Toggle = v);
        Check("Review before pasting", settings.CopyOnly, (s,v) => s.CopyOnly = v);
        Check("Play start and end tones", settings.Sounds, (s,v) => s.Sounds = v);

        Page("Speech");
        int firstSpeechField = collect.Count;
        PathValue("Whisper model folder", settings.WhisperDirectory, true, (s,v) => s.WhisperDirectory = v);
        PathValue("Python interpreter", settings.Python, false, (s,v) => s.Python = v);
        Choice("Recognition device", settings.Device, ["cpu","cuda","auto"], (s,v) => s.Device = v);
        Choice("Compute precision", settings.ComputeType, ["auto","default","int8","int8_float16","int8_float32","float16","float32","bfloat16"], (s,v) => s.ComputeType = v, editable: true);
        PathValue("CUDA libraries folder (optional)", settings.CudaLibraryDirectory, true, (s,v) => s.CudaLibraryDirectory = v);
        CollectWhen(firstSpeechField, s => !s.BundledSpeech);

        Page("Server");
        int firstServerField = collect.Count;
        var url = TextValue("Server URL (including /v1)", settings.ServerUrl, (s,v) => s.ServerUrl = v);
        TextValue("Served model or alias", settings.Model, (s,v) => s.Model = v);
        var password = new TextBox { UseSystemPasswordChar = true, PlaceholderText = "Leave blank to keep the current key" };
        Row("New API key", password);
        var env = Check("Use TALKTOME_API_KEY when no key is saved", settings.UseEnvironmentKey, (s,v) => s.UseEnvironmentKey = v);
        var clear = new CheckBox { Text = "Remove saved key", AutoSize = true };
        Row("", clear);
        clear.CheckedChanged += (_, _) => { password.Enabled = !clear.Checked; };
        url.TextChanged += (_, _) => { password.Clear(); clear.Checked = false; env.Checked = false; };
        collect.Add(s => {
            if (!Uri.TryCreate(s.ServerUrl, UriKind.Absolute, out var next)) throw new InvalidDataException("Enter a valid server URL.");
            if (clear.Checked || password.Text.Length > 0 || !new Uri(original.ServerUrl).GetLeftPart(UriPartial.Authority).Equals(
                next.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)) s.SetApiKey(clear.Checked ? "" : password.Text);
            s.UseEnvironmentKey = env.Checked;
        });
        Number("Request timeout (seconds)", settings.TimeoutSeconds, 1, 600, (s,v) => s.TimeoutSeconds = (int)v);
        var local = settings.LocalServer;
        var manage = new CheckBox { Text = "Start a local llama-server when needed", AutoSize = true,
            Checked = local != null && new Uri(settings.ServerUrl).IsLoopback };
        Row("", manage);
        var exe = PathValue("Custom llama-server (optional)", local?.Executable ?? "", false);
        exe.PlaceholderText = "Use the included llama-server";
        var model = PathValue("Translation model (GGUF)", local?.ModelFile ?? "", false);
        var context = Number("Context size", local?.ContextSize ?? 8192, 512, int.MaxValue);
        var layers = TextValue("GPU layers (all or a number)", local?.GpuLayers ?? "all");
        void EnableLocal() { foreach (var control in new Control[] { exe.Parent!, model.Parent!, context, layers }) control.Enabled = manage.Checked; }
        manage.CheckedChanged += (_, _) => EnableLocal();
        EnableLocal();
        collect.Add(s => {
            if (manage.Checked && (!new Uri(s.ServerUrl).IsLoopback || new Uri(s.ServerUrl).Scheme != "http"))
                throw new InvalidDataException("Use an HTTP loopback URL to start a local server.");
            s.LocalServer = manage.Checked ? new(exe.Text.Trim(), model.Text.Trim(), (int)context.Value, layers.Text.Trim()) : null;
        });
        CollectWhen(firstServerField, s => s.Translation == "server");

        Page("Editing");
        var prompt = Multiline("Editing instructions", string.IsNullOrWhiteSpace(settings.Prompt) ? EnglishEditor.Instruction : settings.Prompt, 170);
        var restore = new Button { Text = "Restore default instructions", AutoSize = true };
        Row("", restore); restore.Click += (_, _) => prompt.Text = EnglishEditor.Instruction.ReplaceLineEndings();
        var glossary = Multiline("Glossary (one term per line)", string.Join(Environment.NewLine, settings.Glossary), 110);
        collect.Add(s => { s.Prompt = prompt.Text.Trim().ReplaceLineEndings("\n") == EnglishEditor.Instruction.ReplaceLineEndings("\n") ? "" : prompt.Text.Trim();
            s.Glossary = glossary.Lines.Select(x => x.Trim()).Where(x => x.Length > 0).ToArray(); });
        Number("Temperature", (decimal)settings.Temperature, 0, 2, (s,v) => s.Temperature = (double)v, decimals: 2);
        var speechPage = tabs.TabPages[2];
        var serverPage = tabs.TabPages[3];
        void ShowChoices()
        {
            void ShowPage(TabPage page, bool visible)
            {
                if (visible && !tabs.TabPages.Contains(page)) tabs.TabPages.Insert(tabs.TabPages.Count - 1, page);
                if (!visible) tabs.TabPages.Remove(page);
            }
            ShowPage(speechPage, speech.SelectedIndex == 3);
            ShowPage(serverPage, ids[translation.SelectedIndex] == "server");
            var selection = settings.Draft();
            SetModels(selection);
            modelNotice.Visible = modelLicense.Visible = selection.Translation == "translategemma";
            parakeetLicense.Visible = selection.BundledSpeech && selection.SpeechModel == "parakeet";
            var models = selection.RequiredModels().Select(ModelDownload.Get).ToArray();
            long bytes = models.Where(m => !m.Installed(settings.ModelDirectory)).Sum(m => m.Size);
            summary.Text = bytes > 0 ? $"Download {Math.Ceiling(bytes / 1e6):N0} MB once to use these models offline." :
                models.Length > 0 ? "Selected models are installed and ready for offline use." : "";
            save.Text = bytes > 0 ? "Download and save" : "Save";
            verify.Visible = bytes == 0 && models.Length > 0;
        }
        speech.SelectedIndexChanged += (_, _) => ShowChoices();
        translation.SelectedIndexChanged += (_, _) => ShowChoices();
        ShowChoices();
        if (settings.RequiredModels().All(id => ModelDownload.Get(id).Installed(settings.ModelDirectory)))
            tabs.SelectedIndex = 1;
    }

    private sealed record InputDevice(int Id, string Label) { public override string ToString() => Label; }
    private void CollectWhen(int start, Func<Settings, bool> enabled)
    {
        var fields = collect.GetRange(start, collect.Count - start);
        collect.RemoveRange(start, fields.Count);
        collect.Add(s => { if (enabled(s)) foreach (var field in fields) field(s); });
    }
    private void Page(string name)
    {
        var page = new TabPage(name) { AutoScroll = true };
        grid = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new(8) };
        grid.ColumnStyles.Add(new(SizeType.AutoSize)); grid.ColumnStyles.Add(new(SizeType.Percent, 100));
        page.Controls.Add(grid); tabs.TabPages.Add(page);
    }
    private void Row(string title, Control input)
    {
        int row = grid.RowCount++;
        grid.RowStyles.Add(new(SizeType.AutoSize));
        var label = new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left };
        grid.Controls.Add(label, 0, row);
        input.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        grid.Controls.Add(input, 1, row);
    }
    private TextBox TextValue(string label, string value, Action<Settings,string>? set = null)
    {
        var box = new TextBox { Text = value }; Row(label, box);
        if (set != null) collect.Add(s => set(s, box.Text.Trim()));
        return box;
    }
    private TextBox Multiline(string label, string value, int height)
    {
        var box = new TextBox { Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Height = height, Text = value.ReplaceLineEndings() };
        Row(label, box); return box;
    }
    private TextBox PathValue(string label, string value, bool folder, Action<Settings,string>? set = null)
    {
        var line = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, Margin = Padding.Empty };
        line.ColumnStyles.Add(new(SizeType.Percent, 100)); line.ColumnStyles.Add(new(SizeType.AutoSize));
        var box = new TextBox { Text = value, Dock = DockStyle.Fill };
        var browse = new Button { Text = "Browse…", AutoSize = true };
        browse.Click += (_, _) => {
            if (folder) { using var dialog = new FolderBrowserDialog(); if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.SelectedPath; }
            else { using var dialog = new OpenFileDialog(); if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.FileName; }
        };
        line.Controls.Add(box, 0, 0); line.Controls.Add(browse, 1, 0); Row(label, line);
        if (set != null) collect.Add(s => set(s, box.Text.Trim()));
        return box;
    }
    private NumericUpDown Number(string label, decimal value, decimal min, decimal max, Action<Settings,decimal>? set = null, int decimals = 0)
    {
        var box = new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = decimals == 0 ? 1 : .01M, Value = value };
        Row(label, box); if (set != null) collect.Add(s => set(s, box.Value)); return box;
    }
    private CheckBox Check(string label, bool value, Action<Settings,bool> set)
    {
        var box = new CheckBox { Text = label, Checked = value, AutoSize = true }; Row("", box);
        collect.Add(s => set(s, box.Checked)); return box;
    }
    private void Choice(string label, string value, string[] values, Action<Settings,string> set, bool editable = false)
    {
        var box = new ComboBox { DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList };
        box.Items.AddRange(values);
        if (!box.Items.Contains(value)) box.Items.Add(value);
        box.SelectedItem = value; Row(label, box); collect.Add(s => set(s, box.Text.Trim()));
    }
    internal Settings ReadForm()
    {
        var draft = original.Draft();
        foreach (var field in collect) field(draft);
        draft.Validate();
        return draft;
    }
    private async Task SaveAsync(bool verifyOnly = false)
    {
        if (install != null) return;
        try {
            var draft = ReadForm();
            using var cancel = new CancellationTokenSource();
            install = cancel;
            tabs.Enabled = save.Enabled = false;
            var progress = new Progress<string>(s => { if (!IsDisposed && install == cancel) feedback.Text = s; });
            foreach (var model in draft.RequiredModels().Select(ModelDownload.Get))
                if (verifyOnly || !model.Installed(draft.ModelDirectory))
                    await model.InstallAsync(draft.ModelDirectory, progress, cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            if (verifyOnly) feedback.Text = "Selected models verified.";
            else if (apply(draft)) DialogResult = DialogResult.OK;
        } catch (OperationCanceledException) { }
        catch (Exception e) { if (!IsDisposed) feedback.Text = e.Message; }
        finally { install = null; if (!IsDisposed) tabs.Enabled = save.Enabled = true; }
    }
}
