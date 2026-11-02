using TwoWChat.Core;
using TwoWChat.Images;

namespace TwoWChat.UI;

internal sealed class CompactChatForm : Form
{
    private readonly FlowLayoutPanel _people = new() { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true };
    private readonly Dictionary<Guid, (Panel Tile, PictureBox Avatar, Label State, byte[]? Bytes)> _tiles = [];
    private readonly ToolTip _tips = new();
    public CompactChatForm(Action restore)
    {
        Text = "2WChat — мини"; TopMost = true; ShowInTaskbar = true; Font = new Font("Tahoma", 8);
        BackColor = Color.FromArgb(212, 208, 200); ClientSize = new Size(250, 160); MinimumSize = new Size(120, 130);
        StartPosition = FormStartPosition.Manual;
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 768); Location = new Point(area.Right - Width - 20, area.Top + 30);
        var open = new Button { Text = "Открыть чат", Dock = DockStyle.Bottom, Height = 28 }; open.Click += (_, _) => restore();
        Controls.Add(_people); Controls.Add(open);
        FormClosed += (_, _) => { _tips.Dispose(); restore(); };
    }
    public void UpdatePeople(PeerInfo[] members, Func<Guid, byte[]?> avatar, Func<Guid, bool> speaking)
    {
        foreach (var id in _tiles.Keys.Where(id => members.All(m => m.Id != id)).ToArray())
        { var tile = _tiles[id]; tile.Avatar.Image?.Dispose(); _people.Controls.Remove(tile.Tile); tile.Tile.Dispose(); _tiles.Remove(id); }
        foreach (var peer in members)
        {
            var bytes = avatar(peer.Id);
            if (!_tiles.TryGetValue(peer.Id, out var tile))
            {
                var panel = new Panel { Size = new Size(72, 94), Padding = new Padding(3), Margin = new Padding(3) };
                var picture = new PictureBox { Location = new Point(4, 4), Size = new Size(64, 64) };
                var label = new Label { Location = new Point(2, 70), Size = new Size(68, 20), TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true };
                panel.Controls.AddRange([picture, label]); _people.Controls.Add(panel);
                tile = (panel, picture, label, null); _tiles.Add(peer.Id, tile);
                _tips.SetToolTip(picture, peer.Name);
            }
            if (tile.Avatar.Image == null || !ReferenceEquals(tile.Bytes, bytes))
            { var old = tile.Avatar.Image; tile.Avatar.Image = AvatarPreparation.Render(bytes, peer.Name, 64); old?.Dispose(); tile.Bytes = bytes; _tiles[peer.Id] = tile; }
            var active = !peer.MicMuted && !peer.Deafened && speaking(peer.Id);
            tile.Tile.BackColor = active ? Color.LimeGreen : Color.FromArgb(170, 170, 170);
            tile.State.Text = peer.Deafened ? "Без звука" : peer.MicMuted ? "Микр. выкл." : active ? "Говорит" : "Молчит";
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { foreach (var tile in _tiles.Values) { tile.Avatar.Image?.Dispose(); tile.Avatar.Image = null; } _tiles.Clear(); }
        base.Dispose(disposing);
    }
}
