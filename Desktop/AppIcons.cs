using System.Drawing.Text;

namespace TalkToMe;

internal enum CommandIcon { Copy, Retry, Cancel, Quit, ShowResult }

internal sealed class AppIcons : IDisposable
{
    public Icon Application { get; } = new(typeof(AppIcons), "Icons.TalkToMe.ico");
    public Icon Idle { get; } = LoadTrayIcon("TalkToMe");
    public Icon Recording { get; } = LoadTrayIcon("Recording");
    public Icon Processing { get; } = LoadTrayIcon("Processing");
    public ImageList Commands { get; } = new() { ColorDepth = ColorDepth.Depth32Bit };
    private Bitmap? commandStrip;

    private static Icon LoadTrayIcon(string name)
    {
        using var original = new Icon(typeof(AppIcons), $"Icons.{name}.ico");
        return new Icon(original, SystemInformation.SmallIconSize);
    }

    public void RenderCommands(int size)
    {
        // Segoe MDL2 Assets glyphs in CommandIcon order. Keep the monochrome ink
        // tied to the Windows text color, including high-contrast themes.
        const string glyphs = "\uE8C8\uE72C\uE711\uF3B1\uE8BC";
        Commands.Images.Clear();
        Commands.ImageSize = new Size(size, size);
        commandStrip?.Dispose();
        commandStrip = new Bitmap(glyphs.Length * size, size);
        using var graphics = Graphics.FromImage(commandStrip);
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Segoe MDL2 Assets", size, FontStyle.Regular, GraphicsUnit.Pixel);
        using var format = new StringFormat(StringFormat.GenericTypographic)
            { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        for (int i = 0; i < glyphs.Length; i++)
            graphics.DrawString(glyphs[i].ToString(), font, SystemBrushes.ControlText,
                new RectangleF(i * size, 0, size, size), format);
        Commands.Images.AddStrip(commandStrip);
    }

    public void Dispose()
    {
        Commands.Dispose();
        commandStrip?.Dispose();
        Idle.Dispose(); Recording.Dispose(); Processing.Dispose(); Application.Dispose();
    }
}
