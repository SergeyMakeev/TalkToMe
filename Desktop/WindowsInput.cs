using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Automation.Text;

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
        if (!Enum.TryParse<Keys>(parts[^1], true, out var key) || !Enum.IsDefined(key) ||
            key < Keys.Space || key > Keys.OemClear || key is Keys.LWin or Keys.RWin)
            throw new InvalidDataException("Set shortcut to a key combination such as Ctrl+Alt+F9.");
        return new(modifiers, key);
    }

    public bool Held => WindowsInput.Down(Key) &&
        ((Modifiers & 1) == 0 || WindowsInput.Down(Keys.Menu)) &&
        ((Modifiers & 2) == 0 || WindowsInput.Down(Keys.ControlKey)) &&
        ((Modifiers & 4) == 0 || WindowsInput.Down(Keys.ShiftKey)) &&
        ((Modifiers & 8) == 0 || WindowsInput.Down(Keys.LWin) || WindowsInput.Down(Keys.RWin));
}

internal sealed record Destination(nint Window, nint Focus, int[] ElementId)
{
    public static Destination? Capture()
    {
        nint window = WindowsInput.GetForegroundWindow();
        uint thread = WindowsInput.GetWindowThreadProcessId(window, out uint process);
        if (window == 0 || thread == 0 || process == Environment.ProcessId) return null;
        var gui = new WindowsInput.GuiThreadInfo { Size = Marshal.SizeOf<WindowsInput.GuiThreadInfo>() };
        if (!WindowsInput.GetGUIThreadInfo(thread, ref gui) || gui.Focus == 0) return null;
        try
        {
            var element = AutomationElement.FocusedElement;
            if (element is null || element.Current.IsPassword || !element.Current.IsEnabled) return null;
            var type = element.Current.ControlType;
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value))
            {
                if (((ValuePattern)value).Current.IsReadOnly) return null;
            }
            else if (element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern))
            {
                var readOnly = ((TextPattern)pattern).DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute);
                if (readOnly is true || (readOnly is not false && type != ControlType.Edit)) return null;
            }
            else if (type != ControlType.Edit) return null;

            if (WindowsInput.IsTerminal(WindowsInput.ClassName(window))) return null;
            // Embedded terminals can share a top-level HWND with a normal editor.
            for (AutomationElement? el = element; el != null; el = TreeWalker.RawViewWalker.GetParent(el))
            {
                if (WindowsInput.IsTerminal(el.Current.ClassName) || WindowsInput.IsTerminal(el.Current.AutomationId) ||
                    (el == element && WindowsInput.IsTerminal(el.Current.Name))) return null;
                if (el.Current.NativeWindowHandle == window) break;
            }
            var id = element.GetRuntimeId();
            if (id.Length == 0 || WindowsInput.GetForegroundWindow() != window) return null;
            return new(window, gui.Focus, id);
        }
        catch (Exception e) when (Unavailable(e))
        { return null; }
    }

    public bool StillFocused()
    {
        var now = Capture();
        return now != null && Window == now.Window && Focus == now.Focus && ElementId.SequenceEqual(now.ElementId);
    }

    private static bool Unavailable(Exception e) => e is ElementNotAvailableException or COMException or
        InvalidOperationException or UnauthorizedAccessException or NotSupportedException or TimeoutException or ArgumentException;

    public string SpaceAtCaret(string text)
    {
        if (!char.IsLetterOrDigit(text[0])) return text;
        try
        {
            var el = AutomationElement.FocusedElement;
            if (el == null || !ElementId.SequenceEqual(el.GetRuntimeId()) ||
                !el.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) return text;
            var selection = ((TextPattern)pattern).GetSelection();
            if (selection.Length != 1) return text;
            var range = selection[0];
            if (range.CompareEndpoints(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End) != 0) return text;
            var right = range.Clone();
            if (right.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, 1) == 1 &&
                !string.IsNullOrWhiteSpace(right.GetText(2))) return text;
            if (range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -1) != -1) return text;
            string left = range.GetText(2);
            return left.Length > 0 && (char.IsLetterOrDigit(left[^1]) || ".!?".Contains(left[^1])) ? " " + text : text;
        }
        catch (Exception e) when (Unavailable(e)) { return text; }
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
