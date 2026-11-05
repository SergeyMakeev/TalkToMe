namespace TalkToMe;

internal sealed class Overlay : Form
{
    private readonly Label label = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
    public Overlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(31, 41, 55);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10);
        Controls.Add(label);
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.ExStyle |= 0x08000000 | 0x80 | 0x20; // WS_EX_NOACTIVATE | TOOLWINDOW | TRANSPARENT
            return p;
        }
    }

    public void Indicate(string message)
    {
        label.Text = message;
        var area = Screen.FromHandle(WindowsInput.GetForegroundWindow()).WorkingArea;
        var size = TextRenderer.MeasureText(message, Font);
        SetBounds(area.Left + (area.Width - size.Width - 36) / 2, area.Bottom - size.Height - 46, size.Width + 36, size.Height + 22);
        if (!Visible) Show();
    }
}
