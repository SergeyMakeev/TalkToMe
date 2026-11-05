namespace TalkToMe;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        using var instance = new Mutex(true, @"Local\TalkToMe.Dictation", out bool first);
        if (!first) { MessageBox.Show("TalkToMe is running. Open it from the notification area.", "TalkToMe"); return; }
        try
        {
            string root = args.Length == 1 ? args[0] : FindInstallation();
            using var window = new DictationWindow(Settings.Load(root));
            Application.Run(window);
        }
        catch (Exception error) { MessageBox.Show(error.Message, "TalkToMe", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private static string FindInstallation()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "settings.example.json"))) return dir.FullName;
        return Environment.CurrentDirectory;
    }
}
