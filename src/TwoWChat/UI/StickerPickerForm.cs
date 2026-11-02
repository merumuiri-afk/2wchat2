using TwoWChat.Core;
using TwoWChat.Images;

namespace TwoWChat.UI;

internal sealed class StickerPickerForm : Form
{
    private readonly FlowLayoutPanel _items = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly CheckBox _background = new() { Text = "Убрать однотонный фон при создании", AutoSize = true };
    public ImagePayload? Selected { get; private set; }
    public StickerPickerForm()
    {
        Text = "Мои стикеры"; Font = new Font("Tahoma", 9); BackColor = Color.FromArgb(212, 208, 200);
        ClientSize = new Size(440, 380); MinimumSize = new Size(340, 280); StartPosition = FormStartPosition.CenterParent;
        var tools = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 70 };
        var create = new Button { Text = "Создать из картинки…", AutoSize = true }; var close = new Button { Text = "Закрыть", DialogResult = DialogResult.Cancel, AutoSize = true };
        tools.Controls.AddRange([create, close, _background]); Controls.Add(_items); Controls.Add(tools); CancelButton = close;
        create.Click += (_, _) =>
        {
            using var file = new OpenFileDialog { Filter = "Изображения|*.png;*.jpg;*.jpeg;*.gif;*.bmp" };
            if (file.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                var sticker = StickerLibrary.Prepare(ImagePreparation.FromFile(file.FileName), _background.Checked);
                using var preview = new ImageViewerForm(sticker);
                preview.Text = "Предпросмотр стикера — закройте и подтвердите добавление"; preview.ShowDialog(this);
                if (MessageBox.Show(this, "Добавить этот стикер в коллекцию?", Text, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                StickerLibrary.Save(sticker); RefreshItems();
            }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException) { MessageBox.Show(this, e.Message, Text); }
        };
        RefreshItems();
    }
    private void RefreshItems()
    {
        foreach (Control item in _items.Controls.Cast<Control>().ToArray()) { if (item is PictureBox p) p.Image?.Dispose(); item.Dispose(); } _items.Controls.Clear();
        foreach (var file in StickerLibrary.Files().Take(100))
        {
            try
            {
                var sticker = StickerLibrary.Load(file);
                var image = new PictureBox { Size = new Size(88, 88), SizeMode = PictureBoxSizeMode.Zoom, Image = ImagePreparation.Decode(sticker), Cursor = Cursors.Hand, Margin = new Padding(6), BackColor = Color.WhiteSmoke };
                image.Click += (_, _) => { Selected = sticker; DialogResult = DialogResult.OK; Close(); };
                var menu = new ContextMenuStrip(); menu.Items.Add("Удалить из коллекции", null, (_, _) => { try { File.Delete(file); RefreshItems(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { MessageBox.Show(this, e.Message, Text); } }); image.ContextMenuStrip = menu;
                image.Disposed += (_, _) => menu.Dispose(); _items.Controls.Add(image);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or OutOfMemoryException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException) { }
        }
        if (_items.Controls.Count == 0) _items.Controls.Add(new Label { Text = "Пока пусто. Создайте свой первый стикер из картинки.", AutoSize = true });
    }
    protected override void Dispose(bool disposing)
    { if (disposing) foreach (var image in _items.Controls.OfType<PictureBox>()) { image.Image?.Dispose(); image.Image = null; } base.Dispose(disposing); }
}
