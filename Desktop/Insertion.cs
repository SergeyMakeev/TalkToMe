using System.Diagnostics;

namespace TalkToMe;

internal static class Insertion
{
    // Null means the input was dispatched. A reason means the completed result needs manual review.
    public static async Task<string?> TryAsync(nint owner, Destination? target, uint clipboardSequence, Keys shortcut,
        string text, CancellationToken token, Action dispatched)
    {
        if (target == null || string.IsNullOrWhiteSpace(text)) return "No eligible text field.";
        var wait = Stopwatch.StartNew();
        while (WindowsInput.BusyKeys(shortcut))
        {
            if (wait.ElapsedMilliseconds >= 1200) return "Shortcut or modifier still held.";
            await Task.Delay(25, token);
        }
        token.ThrowIfCancellationRequested();
        if (!target.StillFocused()) return "The focused field changed.";
        text = target.SpaceAtCaret(text);
        using var clipboard = ClipboardLease.TryPlace(owner, clipboardSequence, text);
        token.ThrowIfCancellationRequested();
        if (!target.StillFocused() || WindowsInput.BusyKeys(shortcut)) return "Focus or keyboard state changed.";
        token.ThrowIfCancellationRequested();
        if (clipboard == null)
        {
            bool ok = WindowsInput.IsNativeEdit(target.Focus)
                ? WindowsInput.ReplaceSelection(target.Focus, text)
                : WindowsInput.TypePrintable(text);
            if (!ok) return "Clipboard unavailable or Windows blocked keyboard input.";
            dispatched();
            return null;
        }
        if (!clipboard.StillCurrent) return "Clipboard changed before paste.";
        if (!WindowsInput.Paste()) return "Windows blocked the paste shortcut.";
        dispatched();
        // GUI paste is asynchronous. Keep data available while the application handles Ctrl+V.
        // After dispatch, finish restoration even if the session is cancelled.
        await Task.Delay(650);
        return null;
    }
}
