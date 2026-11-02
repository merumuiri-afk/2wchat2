using TwoWChat.Core;
using TwoWChat.Images;

namespace TwoWChat.UI;

public sealed class ImageViewerForm : Form
{
    private readonly PictureBox _picture = new();
    private readonly Panel _viewport = new();
    private readonly TextBox _caption = new();
    public string Caption => _caption.Text;
    public ImageViewerForm(ImagePayload image, bool sending = false)
    {
        Text = sending ? "2WChat — отправить изображение" : "2WChat — " + image.Info.FileName;
        Font = new Font("Tahoma", 9); AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(760, 540); MinimumSize = new Size(500, 400);
        _viewport.Dock = DockStyle.Fill; _viewport.AutoScroll = true; _viewport.BackColor = Color.FromArgb(70, 70, 70);
        _picture.Dock = DockStyle.Fill; _picture.SizeMode = PictureBoxSizeMode.Zoom; _picture.Image = ImagePreparation.Decode(image);
        _picture.AccessibleName = image.Info.FileName;
        _viewport.Controls.Add(_picture); Controls.Add(_viewport);
        var description = new Label { Text = " " + ImagePreparation.Description(image.Info), Dock = DockStyle.Top, Height = 28, TextAlign = ContentAlignment.MiddleLeft };
        Controls.Add(description);
        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = sending ? 76 : 42, ColumnCount = 1, Padding = new Padding(6) };
        if (sending)
        {
            _caption.Dock = DockStyle.Fill; _caption.MaxLength = Protocol.MaxTextLength; _caption.PlaceholderText = "Подпись к изображению (необязательно)";
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); bottom.Controls.Add(_caption, 0, 0);
        }
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var close = new Button { Text = sending ? "Отмена" : "Закрыть", DialogResult = DialogResult.Cancel, AutoSize = true };
        actions.Controls.Add(close); CancelButton = close;
        if (sending)
        {
            var send = new Button { Text = "Отправить", DialogResult = DialogResult.OK, AutoSize = true };
            actions.Controls.Add(send); AcceptButton = send;
        }
        else
        {
            var save = new Button { Text = "Сохранить как…", AutoSize = true }; save.Click += (_, _) => SaveImage(this, image);
            var actual = new CheckBox { Text = "Масштаб 100%", AutoSize = true, Padding = new Padding(8, 3, 0, 0) };
            actual.CheckedChanged += (_, _) =>
            {
                _picture.Dock = actual.Checked ? DockStyle.None : DockStyle.Fill;
                _picture.SizeMode = actual.Checked ? PictureBoxSizeMode.AutoSize : PictureBoxSizeMode.Zoom;
            };
            actions.Controls.Add(save); actions.Controls.Add(actual); AcceptButton = close;
        }
        bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); bottom.Controls.Add(actions, 0, sending ? 1 : 0); Controls.Add(bottom);
        FormClosed += (_, _) => { var bitmap = _picture.Image; _picture.Image = null; bitmap?.Dispose(); };
    }
    public static void SaveImage(IWin32Window owner, ImagePayload image)
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Сохранить изображение", FileName = image.Info.FileName,
            Filter = image.Info.ContentType == "image/png" ? "PNG (*.png)|*.png" : "JPEG (*.jpg)|*.jpg", AddExtension = true
        };
        if (dialog.ShowDialog(owner) != DialogResult.OK) return;
        try { File.WriteAllBytes(dialog.FileName, image.Data); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { MessageBox.Show(owner, e.Message, "2WChat", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    }
}
