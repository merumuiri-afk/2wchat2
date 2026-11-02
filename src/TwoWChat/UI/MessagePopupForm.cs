using TwoWChat.Core;
using TwoWChat.Images;

namespace TwoWChat.UI;

internal sealed class MessagePopupForm : Form
{
    private readonly System.Windows.Forms.Timer _expiry = new() { Interval = 5000 };
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000000; return p; } }
    public MessagePopupForm(ChatEntry chat, byte[]? avatar, Action openChat)
    {
        Text = "2WChat"; ShowInTaskbar = false; TopMost = true; FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ClientSize = new Size(330, 100); Font = new Font("Tahoma", 9); BackColor = Color.FromArgb(212, 208, 200);
        StartPosition = FormStartPosition.Manual;
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 768);
        Location = new Point(area.Right - Width - 12, area.Bottom - Height - 12);
        var picture = new PictureBox { Image = AvatarPreparation.Render(avatar, chat.Name, 64), Location = new Point(8, 12), Size = new Size(64, 64) };
        var name = new Label { Text = Protocol.CleanName(chat.Name, 40), Location = new Point(82, 8), Size = new Size(238, 22), AutoEllipsis = true };
        var body = new Label { Text = chat.Text.Length > 180 ? chat.Text[..180] + "…" : chat.Text, Location = new Point(82, 32), Size = new Size(238, 60), AutoEllipsis = true };
        Controls.AddRange([picture, name, body]);
        foreach (Control control in Controls) { control.Cursor = Cursors.Hand; control.Click += (_, _) => { openChat(); Close(); }; }
        _expiry.Tick += (_, _) => Close(); Shown += (_, _) => _expiry.Start();
        FormClosed += (_, _) => { _expiry.Dispose(); picture.Image?.Dispose(); picture.Image = null; };
    }
}
