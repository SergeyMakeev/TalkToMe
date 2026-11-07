using System.Diagnostics;
using System.Windows.Automation;
using TalkToMe;

internal static class BrowserCheck
{
    // A fresh profile and local HTML keep this test independent of the user's tabs and browser data.
    public static async Task RunAsync(nint owner, string executable)
    {
        var profileDirectory = Directory.CreateTempSubdirectory("TalkToMe-check-");
        string folder = profileDirectory.FullName;
        string page = Path.Combine(folder, "check.html");
        File.WriteAllText(page, """
            <!doctype html><meta charset="utf-8"><title>TalkToMe browser input check</title>
            <input id="first" aria-label="First test field" autofocus>
            <textarea id="second" aria-label="Second test field"></textarea>
            <script>document.addEventListener('keydown', e => {if(e.key==='Enter') document.title='Unexpected Enter';});</script>
            """);
        var info = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (string arg in new[] { "--user-data-dir=" + Path.Combine(folder, "profile"), "--no-first-run",
            "--no-default-browser-check", "--disable-background-networking", "--disable-sync", "--disable-extensions",
            "--app=" + new Uri(page).AbsoluteUri }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var token = deadline.Token;
            nint window;
            do
            {
                await Task.Delay(100, token);
                window = WindowsInput.GetForegroundWindow();
                WindowsInput.GetWindowThreadProcessId(window, out var pid);
                if (pid == process.Id && AutomationElement.FromHandle(window).Current.Name.Contains("TalkToMe browser input check")) break;
            } while (true);
            var root = AutomationElement.FromHandle(window);
            async Task<AutomationElement> Find(string name)
            {
                while (true)
                {
                    var el = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name));
                    if (el != null) return el;
                    await Task.Delay(100, token);
                }
            }
            var first = await Find("First test field");
            var second = await Find("Second test field");
            async Task<Destination> Focus(AutomationElement element)
            {
                element.SetFocus();
                await Task.Delay(100, token);
                var target = Destination.Capture();
                if (target?.Window != window || !AutomationElement.FocusedElement.GetRuntimeId().SequenceEqual(element.GetRuntimeId()))
                    throw new Exception("Browser field could not be identified.");
                return target;
            }
            var old = await Focus(first);
            var current = await Focus(second);
            if (old.StillFocused()) throw new Exception("Stale browser field accepted.");
            string text = "Why wait 50 ms?\nПривет!";
            var held = await Insertion.TryAsync(owner, current, ClipboardLease.GetClipboardSequenceNumber(), Keys.F9, text, token, () => {});
            if (held == "Clipboard unavailable or Windows blocked keyboard input.")
            {
                // Do not erase the user's rich clipboard just to make a test pass.
                // Exercise the existing printable-key fallback when it cannot be leased.
                Console.WriteLine("Multiline paste held because the clipboard could not be preserved; checking printable Unicode fallback.");
                text = "Why wait 50 ms? Привет!";
                held = await Insertion.TryAsync(owner, current, ClipboardLease.GetClipboardSequenceNumber(), Keys.F9, text, token, () => {});
            }
            if (held != null) throw new Exception(held);
            var value = (ValuePattern)second.GetCurrentPattern(ValuePattern.Pattern);
            while (value.Current.Value.ReplaceLineEndings("\n") != text) await Task.Delay(25, token);
            if (root.Current.Name.Contains("Unexpected Enter")) throw new Exception("Unexpected Enter.");
            Console.WriteLine("Chromium textarea received Unicode. Same-tab field change held the result. No Enter.");
        }
        finally
        {
            FocusProbe.Shared.Dispose();
            if (!process.HasExited) { process.CloseMainWindow(); if (!process.WaitForExit(2000)) process.Kill(true); }
            if (string.Equals(profileDirectory.Parent?.FullName, Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            {
                try { profileDirectory.Delete(recursive: true); } catch (IOException) { }
            }
        }
    }
}
