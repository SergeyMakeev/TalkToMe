using System.Runtime.InteropServices;
using System.Text;

namespace TalkToMe;

internal sealed record Shortcut(uint Modifiers, Keys Key)
{
    public static Shortcut Parse(string text)
    {
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        uint modifiers = 0;
        foreach (var part in parts[..^1])
            modifiers |= part.ToLowerInvariant() switch {
                "alt" => 1u, "ctrl" => 2u, "shift" => 4u, "win" => 8u,
                _ => throw new InvalidDataException("Unknown shortcut modifier: " + part)
            };
        var name = parts[^1];
        if (name.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase))
            name = "NumPad" + name["NumPad".Length..].TrimStart();
        if (name.Length == 1 && char.IsAsciiDigit(name[0])) name = "D" + name;
        if (!Enum.TryParse<Keys>(name, true, out var key) || !Enum.IsDefined(key) ||
            key < Keys.Space || key > Keys.OemClear || key is Keys.LWin or Keys.RWin)
            throw new InvalidDataException($"Set shortcut to a key combination such as {Settings.DefaultRecordShortcut}.");
        return new(modifiers, key);
    }

    public bool Held => WindowsInput.Down(Key) &&
        ((Modifiers & 1) == 0 || WindowsInput.Down(Keys.Menu)) &&
        ((Modifiers & 2) == 0 || WindowsInput.Down(Keys.ControlKey)) &&
        ((Modifiers & 4) == 0 || WindowsInput.Down(Keys.ShiftKey)) &&
        ((Modifiers & 8) == 0 || WindowsInput.Down(Keys.LWin) || WindowsInput.Down(Keys.RWin));
}

internal sealed record Destination(nint Window, nint Focus, int[] ElementId, bool NeedsSpace = false)
{
    public static Destination? Capture() => FocusProbe.Shared.Capture();

    public bool StillFocused()
    {
        var now = Capture();
        return now != null && Window == now.Window && Focus == now.Focus && ElementId.SequenceEqual(now.ElementId);
    }

    public string SpaceAtCaret(string text)
    {
        if (text.Length == 0 || !char.IsLetterOrDigit(text[0])) return text;
        var now = Capture();
        return now != null && Window == now.Window && Focus == now.Focus && ElementId.SequenceEqual(now.ElementId) && now.NeedsSpace ? " " + text : text;
    }
}

internal static class WindowsInput
{
    [DllImport("user32")] internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32")] internal static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32")] internal static extern nint GetForegroundWindow();
    [DllImport("user32")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32")] internal static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder text, int count);
    [DllImport("user32")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern nint SendMessageTimeout(nint hwnd,
        uint message, nuint wparam, string text, uint flags, uint timeout, out nuint result);
    [StructLayout(LayoutKind.Sequential)] internal struct GuiThreadInfo
    {
        public int Size, Flags;
        public nint Active, Focus, Capture, Menu, Move, Caret;
        public int Left, Top, Right, Bottom;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Keyboard
    { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Mouse
    { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData
    { [FieldOffset(0)] public Keyboard Keyboard; [FieldOffset(0)] public Mouse Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct Input
    { public uint Type; public InputData Data; }

    public static bool Down(Keys key) => GetAsyncKeyState((int)key) < 0;
    public static bool BusyKeys(Keys key) => Down(key) || Down(Keys.ControlKey) || Down(Keys.ShiftKey) ||
        Down(Keys.Menu) || Down(Keys.LWin) || Down(Keys.RWin) || Down(Keys.LButton) || Down(Keys.RButton);

    internal static string ClassName(nint window)
    {
        var name = new StringBuilder(256);
        GetClassName(window, name, name.Capacity);
        return name.ToString();
    }

    internal static bool IsTerminal(string name) =>
        new[] { "terminal", "consolewindowclass", "virtualconsole", "cascadia_hosting", "mintty", "putty", "conemu", "xterm" }
            .Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase));

    private static Input Key(ushort key, ushort scan, uint flags) =>
        new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Scan = scan, Flags = flags } } };

    public static bool Paste()
    {
        Input[] keys = [Key(0x11, 0, 0), Key(0x56, 0, 0), Key(0x56, 0, 2), Key(0x11, 0, 2)];
        uint sent = SendInput((uint)keys.Length, keys, Marshal.SizeOf<Input>());
        if (sent is 1 or 2 or 3)
        {
            Input[] release = [Key(0x56, 0, 2), Key(0x11, 0, 2)];
            SendInput(2, release, Marshal.SizeOf<Input>());
        }
        return sent == keys.Length;
    }

    // The standard edit control accepts a complete, undoable replacement at the selection.
    public static bool IsNativeEdit(nint hwnd)
    {
        var name = ClassName(hwnd);
        return name.Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("WindowsForms10.EDIT.", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ReplaceSelection(nint hwnd, string text) =>
        SendMessageTimeout(hwnd, 0xC2, 1, text.ReplaceLineEndings(), 2, 500, out _) != 0;

    // When another app owns a rich clipboard, a single line can still be typed without disturbing it.
    public static bool TypePrintable(string text)
    {
        if (text.Length > 8192 || text.Any(char.IsControl)) return false;
        var keys = new List<Input>(text.Length * 2);
        foreach (char ch in text) { keys.Add(Key(0, ch, 4)); keys.Add(Key(0, ch, 6)); }
        return SendInput((uint)keys.Count, keys.ToArray(), Marshal.SizeOf<Input>()) == keys.Count;
    }
}
