using TwoWChat.Images;

namespace TwoWChat.UI;

internal sealed class AvatarViewerForm : Form
{
    public AvatarViewerForm(byte[]? avatar, string name)
    {
        Text = "Аватарка — " + name;
        Font = new Font("Tahoma", 9); BackColor = Color.FromArgb(212, 208, 200);
        StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(360, 410); MinimumSize = new Size(240, 280);
        var picture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Image = AvatarPreparation.Render(avatar, name, 96), Padding = new Padding(12) };
        var caption = new Label { Text = name + " — исходная аватарка 96×96", Dock = DockStyle.Top, Height = 30, TextAlign = ContentAlignment.MiddleCenter };
        var close = new Button { Text = "Закрыть", Dock = DockStyle.Bottom, Height = 32, DialogResult = DialogResult.Cancel };
        Controls.Add(picture); Controls.Add(caption); Controls.Add(close); CancelButton = close;
        FormClosed += (_, _) => { picture.Image?.Dispose(); picture.Image = null; };
    }
}
