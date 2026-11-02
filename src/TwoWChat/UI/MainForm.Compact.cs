namespace TwoWChat.UI;

public sealed partial class MainForm
{
    private CompactChatForm? _compact;
    private bool _restoringCompact;
    private void EnterCompact()
    {
        if (_compact != null) { _compact.Activate(); return; }
        _compact = new CompactChatForm(RestoreFromCompact); UpdateCompact(); _compact.Show(); Hide();
    }
    private void RestoreFromCompact()
    {
        if (_restoringCompact) return;
        _restoringCompact = true;
        try
        {
            var compact = _compact; _compact = null;
            if (compact != null && !compact.IsDisposed) compact.Close();
            if (!_closing && !IsDisposed) { Show(); WindowState = FormWindowState.Normal; Activate(); _notifications.ClearUnread(); }
        }
        finally { _restoringCompact = false; }
    }
    private void UpdateCompact()
    {
        _compact?.UpdatePeople(_client?.Members ?? [], id => _client?.GetAvatar(id), id => id == _client?.Id ? _audio?.Transmitting == true : _audio?.Mixer.IsSpeaking(id) == true);
    }
}
