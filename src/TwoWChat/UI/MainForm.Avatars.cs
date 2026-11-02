using TwoWChat.Images;

namespace TwoWChat.UI;

public sealed partial class MainForm
{
    private readonly ImageList _avatarIcons = new() { ImageSize = new Size(32, 32), ColorDepth = ColorDepth.Depth32Bit };
    private async Task OpenAvatarAsync()
    {
        if (_busy) return;
        using var dialog = new AvatarForm(_settings.Avatar, _name.Text);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _settings.Avatar = dialog.Avatar; SaveSettings();
        var client = _client;
        if (client?.Connected == true)
        {
            try { await client.SetAvatarAsync(_settings.Avatar ?? []); }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { AppendNotice("Аватарка сохранена, но не отправлена: " + e.Message); }
            if (!client.SupportsAvatars) AppendNotice("Для показа аватарок владелец комнаты должен обновить 2WChat до 1.6.");
        }
    }
    private void RefreshAvatarImages()
    {
        _avatarIcons.Images.Clear();
        _ = _avatarIcons.Handle;
        foreach (ListViewItem item in _people.Items)
        {
            if (item.Tag is not Core.PeerInfo peer) continue;
            using var image = AvatarPreparation.Render(_client?.GetAvatar(peer.Id), peer.Name);
            _avatarIcons.Images.Add(peer.Id.ToString(), image); item.ImageKey = peer.Id.ToString();
        }
        _chat.RefreshAvatars(id => _client?.GetAvatar(id));
    }
}
