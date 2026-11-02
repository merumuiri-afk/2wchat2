using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using TwoWChat.Core;
using TwoWChat.Images;

namespace TwoWChat.UI;

public sealed class ChatView : UserControl
{
    private readonly FlowLayoutPanel _flow = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Color.White, Padding = new Padding(4) };
    private readonly List<ChatRow> _rows = [];
    private readonly ContextMenuStrip _menu = new();
    private ChatRow? _contextRow;
    private bool _layingOut;
    private bool _followLatest = true;
    public event Action<ImagePayload>? ImageOpenRequested;
    public event Action<ImageInfo>? ImageRequested;
    public event Action<string>? Error;
    public int ImageCount => _rows.Count(r => r.Entry?.Image != null);
    public int LoadedImageCount => _rows.Count(r => r.Payload != null && r.HasThumbnail);
    [AllowNull]
    public override string Text { get => _rows == null ? "" : string.Concat(_rows.Select(r => r.PlainText)); set => base.Text = value; }

    public ChatView()
    {
        BackColor = Color.White; BorderStyle = BorderStyle.Fixed3D; DoubleBuffered = true;
        Controls.Add(_flow); _flow.SizeChanged += (_, _) => LayoutRows();
        _flow.Scroll += (_, _) => _followLatest = _flow.VerticalScroll.Value + _flow.VerticalScroll.LargeChange >= _flow.VerticalScroll.Maximum - 24;
        VisibleChanged += (_, _) => { if (Visible && _followLatest) ScrollToLatest(); };
        var copy = new ToolStripMenuItem("Копировать сообщение"); copy.Click += (_, _) => CopyText(_contextRow?.PlainText ?? "");
        var all = new ToolStripMenuItem("Копировать всю переписку"); all.Click += (_, _) => CopyText(Text);
        var open = new ToolStripMenuItem("Открыть изображение"); open.Click += (_, _) => _contextRow?.Open();
        var save = new ToolStripMenuItem("Сохранить изображение…"); save.Click += (_, _) => { if (_contextRow?.Payload is { } image) ImageViewerForm.SaveImage(FindForm()!, image); };
        _menu.Items.AddRange([copy, all, new ToolStripSeparator(), open, save]);
        _menu.Opening += (_, _) =>
        {
            _contextRow = _menu.SourceControl?.Tag as ChatRow;
            copy.Enabled = _contextRow != null; open.Enabled = _contextRow?.Entry?.Image != null; save.Enabled = _contextRow?.Payload != null;
        };
        _flow.ContextMenuStrip = _menu;
    }

    public void AddMessage(ChatEntry entry, bool own)
    {
        var row = new ChatRow(entry, null, own, Font, _menu);
        row.OpenRequested += image => ImageOpenRequested?.Invoke(image);
        row.DownloadRequested += info => ImageRequested?.Invoke(info);
        Add(row);
    }
    public void AddNotice(string text) => Add(new ChatRow(null, text, false, Font, _menu));

    private void Add(ChatRow row)
    {
        _rows.Add(row); row.Width = RowWidth; row.Reflow(); _flow.Controls.Add(row); Trim();
        if (_followLatest) ScrollToLatest();
    }
    private int RowWidth => Math.Max(120, _flow.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 12);
    private void LayoutRows()
    {
        if (_layingOut || IsDisposed) return; _layingOut = true;
        try { _flow.SuspendLayout(); foreach (var row in _rows) { row.Width = RowWidth; row.Reflow(); } }
        finally { _flow.ResumeLayout(true); _layingOut = false; }
        if (_followLatest) ScrollToLatest();
    }
    public void ScrollToLatest()
    {
        if (IsDisposed) return;
        _flow.PerformLayout();
        var height = _rows.Sum(r => r.Height + r.Margin.Vertical) + _flow.Padding.Vertical;
        _flow.AutoScrollPosition = new Point(0, Math.Max(0, height - _flow.ClientSize.Height));
    }
    public bool ImageVisible(Guid id)
    {
        var row = _rows.FirstOrDefault(r => r.Entry?.Image?.Id == id);
        return row != null && row.HasThumbnail && row.Bottom > 0 && row.Top < _flow.ClientSize.Height;
    }
    private void Trim()
    {
        while (_rows.Count > 180 || _rows.Sum(r => r.PlainText.Length) > 250000 || _rows.Sum(r => r.Payload?.Data.Length ?? 0) > Protocol.ImageCacheBytes)
        {
            var first = _rows[0]; _rows.RemoveAt(0); _flow.Controls.Remove(first); first.Dispose();
        }
    }
    public async Task SetImageAsync(ImagePayload image)
    {
        var rows = _rows.Where(r => r.Entry?.Image?.Id == image.Info.Id && r.Payload == null).ToArray();
        foreach (var row in rows)
        {
            try { await row.LoadImageAsync(image); }
            catch (Exception e) when (e is ArgumentException or ExternalException or IOException or OutOfMemoryException)
            { if (!row.IsDisposed) row.SetError("Не удалось открыть изображение. Нажмите для повтора."); }
        }
        if (!IsDisposed) Trim();
    }
    public void ImageFailed(Guid id, string text)
    { foreach (var row in _rows.Where(r => r.Entry?.Image?.Id == id)) row.SetError(text + " Нажмите для повтора."); }
    public ImagePayload? GetImage(Guid id) => _rows.FirstOrDefault(r => r.Entry?.Image?.Id == id)?.Payload;
    private void CopyText(string text)
    {
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); } catch (ExternalException) { Error?.Invoke("Буфер обмена занят. Повторите копирование."); }
    }
    protected override void Dispose(bool disposing) { if (disposing) _menu.Dispose(); base.Dispose(disposing); }

    private sealed class ChatRow : Panel
    {
        public ChatEntry? Entry { get; }
        public ImagePayload? Payload { get; private set; }
        public string PlainText { get; }
        private readonly Label _header = new();
        private readonly TextBox _body = new();
        private readonly PictureBox _picture = new();
        private readonly Label _placeholder = new();
        private readonly LinkLabel _imageLink = new();
        private readonly Font _headingFont;
        private readonly bool _showBody;
        private bool _loading;
        public bool HasThumbnail => _picture.Image != null;
        public event Action<ImagePayload>? OpenRequested;
        public event Action<ImageInfo>? DownloadRequested;

        public ChatRow(ChatEntry? entry, string? notice, bool own, Font font, ContextMenuStrip menu)
        {
            Entry = entry; Font = font; BackColor = Color.White; Margin = new Padding(0, 0, 0, 5); DoubleBuffered = true;
            _showBody = entry?.Image == null || entry.Text != "Изображение: " + entry.Image.FileName;
            _headingFont = new Font(font, FontStyle.Bold);
            if (entry != null)
            {
                _header.Text = $"[{entry.Time.LocalDateTime:HH:mm:ss}] {entry.Name}";
                _header.Font = _headingFont; _header.ForeColor = own ? Color.FromArgb(10, 36, 106) : Color.DarkGreen;
                PlainText = $"[{entry.Time.LocalDateTime:HH:mm:ss}] {entry.Name}: {entry.Text}{Environment.NewLine}";
            }
            else PlainText = notice ?? "";
            _header.AutoSize = false;
            _body.Text = entry?.Text ?? notice; _body.Font = font; _body.Multiline = true; _body.ReadOnly = true;
            _body.BackColor = Color.White; _body.BorderStyle = BorderStyle.None; _body.WordWrap = true;
            _body.ForeColor = entry == null ? Color.DimGray : Color.Black; _body.TabStop = false;
            Controls.Add(_header); Controls.Add(_body);
            if (entry?.Image is { } image)
            {
                _picture.SizeMode = PictureBoxSizeMode.Zoom; _picture.BorderStyle = BorderStyle.FixedSingle; _picture.Cursor = Cursors.Hand;
                _picture.AccessibleName = "Изображение: " + image.FileName; _picture.Visible = false;
                _placeholder.Text = "Загрузка изображения…"; _placeholder.BackColor = Color.FromArgb(240, 240, 240); _placeholder.TextAlign = ContentAlignment.MiddleCenter; _placeholder.Cursor = Cursors.Hand;
                _imageLink.Text = ImagePreparation.Description(image) + "\nНажмите для просмотра";
                _imageLink.LinkClicked += (_, _) => Open(); _picture.Click += (_, _) => Open(); _placeholder.Click += (_, _) => Open();
                Controls.AddRange([_picture, _placeholder, _imageLink]);
                _body.Visible = _showBody;
            }
            foreach (Control control in Controls) { control.Tag = this; control.ContextMenuStrip = menu; }
            Tag = this; ContextMenuStrip = menu;
        }
        public void Reflow()
        {
            var width = Math.Max(100, Width - 8); var top = 0;
            _header.Visible = Entry != null;
            if (Entry != null) { _header.SetBounds(0, top, width, 20); top += 21; }
            if (_showBody)
            {
                var measured = TextRenderer.MeasureText(_body.Text, Font, new Size(width - 4, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
                var height = Math.Max(Font.Height + 4, measured.Height + 5); _body.SetBounds(0, top, width, height); top += height + 3;
            }
            if (Entry?.Image is { } info)
            {
                var pictureWidth = Math.Min(320, width); var pictureHeight = Math.Clamp((int)(pictureWidth * (double)info.Height / info.Width), 65, 220);
                _picture.SetBounds(0, top, pictureWidth, pictureHeight); _placeholder.SetBounds(0, top, pictureWidth, pictureHeight);
                _imageLink.SetBounds(0, top + pictureHeight + 3, width, 35); top += pictureHeight + 40;
            }
            Height = Math.Max(22, top + 2);
        }
        public async Task LoadImageAsync(ImagePayload image)
        {
            if (_loading || IsDisposed || Payload != null) return; _loading = true;
            try
            {
                var thumbnail = await Task.Run(() => ImagePreparation.Thumbnail(image));
                if (IsDisposed) { thumbnail.Dispose(); return; }
                Payload = image; var old = _picture.Image; _picture.Image = thumbnail; old?.Dispose();
                _picture.Visible = true; _placeholder.Visible = false;
            }
            finally { _loading = false; }
        }
        public void SetError(string message) { _placeholder.Text = message; if (Payload == null) _placeholder.Visible = true; }
        public void Open()
        {
            if (Payload != null) OpenRequested?.Invoke(Payload);
            else if (Entry?.Image != null) { _placeholder.Text = "Загрузка изображения…"; DownloadRequested?.Invoke(Entry.Image); }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { var bitmap = _picture.Image; _picture.Image = null; bitmap?.Dispose(); _headingFont.Dispose(); Payload = null; }
            base.Dispose(disposing);
        }
    }
}
