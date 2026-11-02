using System.Runtime.InteropServices;
using TwoWChat.Core;
using TwoWChat.Images;

namespace TwoWChat.UI;

public sealed partial class MainForm
{
    private CancellationTokenSource? _imageSendStop;
    private bool _sendingImage, _preparingImage;
    private void UpdateImageButton()
    {
        _attachImage.Enabled = _client?.Connected == true && _client.SupportsImages && !_sendingImage && !_preparingImage;
        _tips.SetToolTip(_attachImage, _client?.Connected == true && !_client.SupportsImages ? "Для картинок владелец комнаты должен обновиться до 2WChat 1.2." : "PNG, JPEG, GIF, BMP. Также можно вставить картинку Ctrl+V или перетащить файл в чат.");
    }
    private bool CanSendImage()
    {
        if (_client?.Connected != true) return false;
        if (!_client.SupportsImages) { ShowError("Для отправки картинок владелец комнаты должен обновиться до версии 1.2."); return false; }
        if (_sendingImage || _preparingImage) { AppendNotice("Дождитесь подготовки или отправки предыдущего изображения."); return false; }
        return true;
    }
    private async Task ChooseImageAsync()
    {
        if (!CanSendImage()) return;
        using var dialog = new OpenFileDialog { Title = "Отправить изображение", Filter = "Изображения (*.png;*.jpg;*.jpeg;*.gif;*.bmp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp", CheckFileExists = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) await SendImageFileAsync(dialog.FileName);
    }
    private async Task SendImageFileAsync(string file)
    {
        if (!CanSendImage()) return;
        var client = _client; _preparingImage = true; UpdateImageButton();
        try { var image = await Task.Run(() => ImagePreparation.FromFile(file)); if (_client == client) await ConfirmImageAsync(image); }
        catch (Exception e) when (IsImageError(e)) { ShowError("Не удалось подготовить изображение: " + e.Message); }
        finally { _preparingImage = false; if (!IsDisposed) UpdateImageButton(); }
    }
    private static bool IsImageError(Exception e) => e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or ExternalException or InvalidOperationException or OutOfMemoryException;
    private static bool ClipboardHasImage()
    {
        try { return Clipboard.ContainsImage() || Clipboard.ContainsFileDropList(); } catch (ExternalException) { return false; }
    }
    private async Task PasteImageAsync()
    {
        if (!CanSendImage()) return;
        try
        {
            if (Clipboard.ContainsImage())
            {
                using var source = Clipboard.GetImage();
                if (source == null) return;
                using var bitmap = new Bitmap(source);
                var client = _client; _preparingImage = true; UpdateImageButton();
                try
                {
                    var image = await Task.Run(() => ImagePreparation.FromBitmap(bitmap));
                    if (_client == client) await ConfirmImageAsync(image);
                }
                finally { _preparingImage = false; if (!IsDisposed) UpdateImageButton(); }
            }
            else if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().ToArray();
                if (files.Length != 1 || !ImagePreparation.IsImageFile(files[0])) { ShowError("Скопируйте одно изображение PNG, JPEG, GIF или BMP."); return; }
                await SendImageFileAsync(files[0]);
            }
        }
        catch (Exception e) when (IsImageError(e)) { ShowError("Не удалось вставить изображение: " + e.Message); }
    }
    private async Task ConfirmImageAsync(ImagePayload image)
    {
        var client = _client;
        if (client?.Connected != true || !client.SupportsImages || _sendingImage) return;
        using var preview = new ImageViewerForm(image, true);
        if (preview.ShowDialog(this) != DialogResult.OK) return;
        if (_client == client) await SendPreparedImageAsync(image, preview.Caption);
    }
    private async Task SendPreparedImageAsync(ImagePayload image, string caption)
    {
        var client = _client;
        if (client?.Connected != true) return;
        _sendingImage = true; _imageSendStop = new CancellationTokenSource(); UpdateImageButton();
        try
        {
            var progress = new Progress<int>(percent => { if (_client == client && _sendingImage) _connectionStatus.Text = $"Отправка изображения: {percent}%"; });
            await client.SendImageAsync(image, caption, progress, _imageSendStop.Token);
        }
        catch (Exception e) when (IsImageError(e) || e is OperationCanceledException or TimeoutException)
        { if (!_stopping && !_closing) AppendNotice("Изображение не отправлено: " + e.Message); }
        finally
        {
            _imageSendStop.Dispose(); _imageSendStop = null; _sendingImage = false;
            if (!IsDisposed) { UpdateImageButton(); _connectionStatus.Text = _client?.Connected == true ? (_server == null ? "Подключено" : "Вы — владелец комнаты") : "Нет подключения"; }
        }
    }
    private void OpenImage(ImagePayload image)
    {
        try { using var viewer = new ImageViewerForm(image); viewer.ShowDialog(this); }
        catch (Exception e) when (IsImageError(e)) { ShowError("Не удалось открыть изображение: " + e.Message); }
    }
    private void InitializeImageDrop()
    {
        foreach (var surface in new Control[] { this, _chat, _message })
        {
            surface.AllowDrop = true;
            surface.DragEnter += (_, e) =>
            {
                var files = e.Data?.GetData(DataFormats.FileDrop) as string[];
                e.Effect = _client?.Connected == true && _client.SupportsImages && !_sendingImage && !_preparingImage && files is { Length: 1 } && ImagePreparation.IsImageFile(files[0]) ? DragDropEffects.Copy : DragDropEffects.None;
            };
            surface.DragDrop += async (_, e) =>
            { if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files) await SendImageFileAsync(files[0]); };
        }
    }
}
