namespace TalkToMe;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Diagnostics.Initialize();
        ApplicationConfiguration.Initialize();
        using var instance = new Mutex(true, @"Local\TalkToMe.Dictation", out bool first);
        if (!first) { MessageBox.Show("TalkToMe is running. Open it from the notification area.", "TalkToMe"); return; }
        try
        {
            string root = args.Length == 1 ? args[0] : FindInstallation();
            Settings settings;
            try { settings = Settings.Load(root, useUserDirectory: true); }
            catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException or ArgumentException)
            {
                MessageBox.Show($"Could not load your preferences. Review the defaults in Settings and save to replace the invalid file.\n\n{error.Message}", "TalkToMe Settings");
                settings = Settings.Defaults(root, useUserDirectory: true);
                using var repair = new SettingsDialog(settings, draft => { draft.Save(); settings = draft; return true; });
                if (repair.ShowDialog() != DialogResult.OK) return;
            }
            using var window = new DictationWindow(settings);
            Application.Run(window);
        }
        catch (Exception error) { Diagnostics.Error("startup failed", error); MessageBox.Show(error.Message, "TalkToMe", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { FocusProbe.Shared.Dispose(); Diagnostics.Event("exit", "Normal exit"); }
    }

    private static string FindInstallation()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "settings.example.json"))) return dir.FullName;
        return AppContext.BaseDirectory;
    }
}
