using TwoWChat.Core;
using TwoWChat.Images;

namespace TwoWChat.UI;

public sealed partial class MainForm
{
    private async Task OpenStickersAsync()
    {
        var client = _client;
        using var picker = new StickerPickerForm();
        if (picker.ShowDialog(this) != DialogResult.OK || picker.Selected == null) return;
        if (client == null || _client != client || !client.Connected) { ShowError("Стикер сохранён. Подключитесь к комнате для отправки."); return; }
        if (!CanSendImage()) return;
        await SendPreparedImageAsync(picker.Selected, "Стикер");
    }
    private void SaveAsSticker(ImagePayload image)
    {
        try { StickerLibrary.Save(StickerLibrary.Prepare(image)); AppendNotice("Стикер добавлен в вашу коллекцию."); }
        catch (Exception e) when (IsImageError(e) || e is InvalidDataException) { ShowError(e.Message); }
    }
}
