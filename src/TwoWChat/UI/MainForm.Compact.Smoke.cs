using TwoWChat.Core;

namespace TwoWChat.UI;

public sealed partial class MainForm
{
    private async Task RunCompactUiSmokeAsync(string directory, List<string> results, RoomClient bot)
    {
        EnterCompact(); await Task.Delay(150);
        if (Visible || _compact?.Visible != true || _compact.Controls.OfType<FlowLayoutPanel>().Single().Controls.Count != 2) throw new IOException("Compact mode did not hide main window or show members");
        var compact = _compact;
        compact.UpdatePeople(_client!.Members, id => _client.GetAvatar(id), id => id == bot.Id);
        var tiles = compact.Controls.OfType<FlowLayoutPanel>().Single().Controls.Cast<Panel>().ToArray();
        if (!tiles.Any(t => t.BackColor == Color.LimeGreen && t.Controls.OfType<Label>().Single().Text == "Говорит")) throw new IOException("Speaking indicator did not illuminate");
        using (var image = new Bitmap(compact.Width, compact.Height)) { compact.DrawToBitmap(image, new Rectangle(Point.Empty, compact.Size)); image.Save(Path.Combine(directory, "compact-speaking.png")); }
        var count = _notifications.ReceivedCount;
        await bot.SendChatAsync("Уведомление с аватаркой в мини-режиме"); await Task.Delay(250);
        var popup = Application.OpenForms.OfType<MessagePopupForm>().SingleOrDefault();
        if (popup == null || _notifications.ReceivedCount != count + 1 || Visible || Form.ActiveForm == popup) throw new IOException("Hidden main window did not receive a non-activating avatar popup");
        using (var image = new Bitmap(popup.Width, popup.Height)) { popup.DrawToBitmap(image, new Rectangle(Point.Empty, popup.Size)); image.Save(Path.Combine(directory, "avatar-notification.png")); }
        results.Add("Compact mode hides main chat, displays room avatars and highlights a speaking participant");
        results.Add("Incoming message while main chat is hidden shows an avatar notification without restoring the main window");
        compact.UpdatePeople(_client.Members.Select(p => p.Id == bot.Id ? p with { MicMuted = true } : p).ToArray(), id => _client.GetAvatar(id), _ => true);
        if (compact.Controls.OfType<FlowLayoutPanel>().Single().Controls.Cast<Panel>().All(p => p.Controls.OfType<Label>().Single().Text != "Микр. выкл.")) throw new IOException("Muted compact state was not rendered");
        results.Add("Compact microphone mute state overrides the speaking indicator");
        typeof(Control).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(popup.Controls.OfType<Label>().First(), [EventArgs.Empty]);
        await Task.Delay(100);
        if (!Visible || _compact != null) throw new IOException("Notification click did not restore main chat");
        results.Add("Clicking avatar notification restores main chat and closes compact mode");
        EnterCompact(); await Task.Delay(50);
        _compact!.Controls.OfType<Button>().Single().PerformClick();
        if (!Visible || _compact != null) throw new IOException("Open chat button did not restore main window");
        results.Add("Compact Open chat button restores the main window");
        EnterCompact(); compact = _compact!;
        compact.UpdatePeople([], _ => null, _ => false);
        if (compact.Controls.OfType<FlowLayoutPanel>().Single().Controls.Count != 0) throw new IOException("Compact participant cleanup failed");
        popup.Close(); compact.Close(); await Task.Delay(100);
        if (!Visible || _compact != null || compact.IsDisposed != true) throw new IOException("Closing compact mode did not restore main chat");
        results.Add("Compact participant removal clears tiles and closing the mini window restores main chat");
    }
}
