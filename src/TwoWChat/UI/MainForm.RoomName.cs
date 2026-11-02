using TwoWChat.Core;

namespace TwoWChat.UI;

public sealed partial class MainForm
{
    private async Task ProposeRoomNameAsync()
    {
        var client = _client;
        if (client?.Connected != true) { ShowError("Сначала подключитесь к комнате."); return; }
        using var dialog = new Form { Text = "Предложить название комнаты", ClientSize = new Size(360, 110), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        var text = new TextBox { Text = client.Room, MaxLength = 40, Location = new Point(12, 15), Width = 330 };
        var apply = new Button { Text = "Предложить", DialogResult = DialogResult.OK, Location = new Point(120, 60), Width = 110 };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Location = new Point(240, 60) };
        dialog.Controls.AddRange([text, apply, cancel]); dialog.AcceptButton = apply; dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var name = Protocol.CleanName(text.Text, 40); if (name.Length == 0) return;
        try { await client.SendChatAsync("Предлагаю название комнаты: " + name); }
        catch (IOException e) { ShowError(e.Message); }
    }
}
