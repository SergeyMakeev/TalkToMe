using System.Runtime.InteropServices;
using System.Text;

namespace TalkToMe;

// Compare and replace under the clipboard lock. Only duplicate formats with known HGLOBAL semantics.
// Delayed-rendered/private objects stay with their owner; callers may type printable text instead.
internal sealed class ClipboardLease : IDisposable
{
    [DllImport("user32")] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32")] private static extern bool CloseClipboard();
    [DllImport("user32")] private static extern bool EmptyClipboard();
    [DllImport("user32")] private static extern nint GetClipboardOwner();
    [DllImport("user32")] private static extern uint EnumClipboardFormats(uint previous);
    [DllImport("user32")] private static extern nint GetClipboardData(uint format);
    [DllImport("user32")] private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("user32")] internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int GetClipboardFormatName(uint format, StringBuilder text, int size);
    [DllImport("kernel32")] private static extern nuint GlobalSize(nint h);
    [DllImport("kernel32")] private static extern nint GlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32")] private static extern nint GlobalLock(nint h);
    [DllImport("kernel32")] private static extern bool GlobalUnlock(nint h);
    [DllImport("kernel32")] private static extern nint GlobalFree(nint h);
    [DllImport("ole32")] private static extern nint OleDuplicateData(nint source, ushort format, uint flags);

    private readonly nint owner;
    private readonly List<(uint Format, nint Handle)> backup = [];
    private readonly byte[] tag = Guid.NewGuid().ToByteArray();
    private readonly uint markerFormat = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private ClipboardLease(nint owner) => this.owner = owner;

    private static bool OpenFor(nint owner, int attempts = 12)
    {
        for (int i = 0; i < attempts; i++)
        {
            if (OpenClipboard(owner)) return true;
            Thread.Sleep(8);
        }
        return false;
    }

    private static bool CanDuplicate(uint format)
    {
        if (format is 1 or 7 or 8 or 13 or 15 or 16 or 17) return true;
        var name = new StringBuilder(128);
        GetClipboardFormatName(format, name, name.Capacity);
        return name.ToString() is "HTML Format" or "Rich Text Format" or "CSV" or "PNG" or "FAR_Text_Length" or
            "ExcludeClipboardContentFromMonitorProcessing" or "CanIncludeInClipboardHistory" or "CanUploadToCloudClipboard";
    }

    private static nint Allocate(byte[] data)
    {
        var handle = GlobalAlloc(2, (nuint)data.Length);
        var pointer = GlobalLock(handle);
        if (pointer == 0) { GlobalFree(handle); throw new OutOfMemoryException(); }
        try { Marshal.Copy(data, 0, pointer, data.Length); }
        finally { GlobalUnlock(handle); }
        return handle;
    }

    public static ClipboardLease? TryPlace(nint owner, uint expected, string text)
    {
        if (!OpenFor(owner)) return null;
        var lease = new ClipboardLease(owner);
        nint unicode = 0, marker = 0;
        bool keep = false;
        try
        {
            if (GetClipboardSequenceNumber() != expected) return null;
            for (uint f = EnumClipboardFormats(0); f != 0; f = EnumClipboardFormats(f))
            {
                if (!CanDuplicate(f)) return null;
                nint source = GetClipboardData(f);
                if (source == 0 || GlobalSize(source) is 0 or > 16777216) return null;
                nint saved = OleDuplicateData(source, (ushort)f, 2);
                if (saved == 0) return null;
                lease.backup.Add((f, saved));
            }
            var normalized = text.ReplaceLineEndings("\r\n");
            unicode = Allocate(Encoding.Unicode.GetBytes(normalized + '\0'));
            marker = Allocate(lease.tag);
            if (lease.markerFormat == 0 || !EmptyClipboard()) return null;
            if (SetClipboardData(lease.markerFormat, marker) != 0) marker = 0;
            if (marker != 0 || SetClipboardData(13, unicode) == 0)
            {
                lease.RestoreLocked();
                return null;
            }
            unicode = 0; // The system owns published handles.
            keep = true;
            return lease;
        }
        finally
        {
            GlobalFree(unicode); GlobalFree(marker);
            if (!keep) lease.FreeBackup();
            CloseClipboard();
        }
    }

    private void RestoreLocked()
    {
        if (!EmptyClipboard()) return;
        for (int i = 0; i < backup.Count; ++i)
        {
            var item = backup[i];
            if (SetClipboardData(item.Format, item.Handle) != 0) backup[i] = (item.Format, 0);
        }
    }

    // Windows can advance the sequence when CloseClipboard publishes our formats. Identify the
    // actual paste by its owner and unique marker, including when another TalkToMe operation copies.
    private bool OwnsLocked()
    {
        if (GetClipboardOwner() != owner) return false;
        nint handle = GetClipboardData(markerFormat);
        if (handle == 0 || GlobalSize(handle) < (nuint)tag.Length) return false;
        nint pointer = GlobalLock(handle);
        if (pointer == 0) return false;
        try
        {
            var value = new byte[tag.Length];
            Marshal.Copy(pointer, value, 0, value.Length);
            return value.AsSpan().SequenceEqual(tag);
        }
        finally { GlobalUnlock(handle); }
    }

    public bool StillCurrent
    {
        get
        {
            if (!OpenFor(owner)) return false;
            try { return OwnsLocked(); }
            finally { CloseClipboard(); }
        }
    }

    private void FreeBackup()
    {
        foreach (var item in backup) GlobalFree(item.Handle);
        backup.Clear();
    }

    public void Dispose()
    {
        // Clipboard readers can briefly hold the lock. Never replace a more recent copy.
        if (OpenFor(owner, 60))
        {
            try { if (OwnsLocked()) RestoreLocked(); }
            finally { CloseClipboard(); }
        }
        FreeBackup();
    }
}
