using TwoWChat.Images;

namespace TwoWChat.UI;

internal sealed class AvatarForm : Form
{
    public byte[]? Avatar { get; private set; }
    private readonly PictureBox _preview = new() { Size = new Size(96, 96), BorderStyle = BorderStyle.FixedSingle };
    private readonly string _name;
    public AvatarForm(byte[]? avatar, string name)
    {
        Avatar = avatar; _name = name;
        Text = "Моя аватарка"; Font = new Font("Tahoma", 9); BackColor = Color.FromArgb(212, 208, 200);
        ClientSize = new Size(350, 235); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
        _preview.Location = new Point(18, 18); Controls.Add(_preview);
        var choose = new Button { Text = "Выбрать изображение…", Location = new Point(130, 18), Size = new Size(200, 32) };
        var clear = new Button { Text = "Убрать аватарку", Location = new Point(130, 60), Size = new Size(200, 32) };
        choose.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog { Filter = "Изображения|*.png;*.jpg;*.jpeg;*.gif;*.bmp" };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            try { Avatar = AvatarPreparation.FromFile(picker.FileName); RefreshPreview(); }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException or UnauthorizedAccessException)
            { MessageBox.Show(this, "Не удалось открыть изображение: " + e.Message, Text); }
        };
        clear.Click += (_, _) => { Avatar = null; RefreshPreview(); };
        Controls.AddRange([choose, clear, new Label { Text = "Квадрат 96×96, обрезка по центру.\nДругие увидят аватарку в комнате и чате.", Location = new Point(18, 130), Size = new Size(315, 40) }]);
        var apply = new Button { Text = "Применить", DialogResult = DialogResult.OK, Location = new Point(130, 185), Size = new Size(95, 30) };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Location = new Point(235, 185), Size = new Size(95, 30) };
        Controls.AddRange([apply, cancel]); AcceptButton = apply; CancelButton = cancel;
        RefreshPreview(); FormClosed += (_, _) => { _preview.Image?.Dispose(); _preview.Image = null; };
    }
    private void RefreshPreview() { var old = _preview.Image; _preview.Image = AvatarPreparation.Render(Avatar, _name, 96); old?.Dispose(); }
}
