using System.Runtime.InteropServices;
using TwoWChat.Audio;
using TwoWChat.Core;

namespace TwoWChat.UI;

// Created and used on the UI thread. Notifications never activate the window;
// only the user's click on the tray icon/balloon brings the chat forward.
internal sealed class MessageNotifications : IDisposable
{
    private readonly Form _window;
    private readonly NotifyIcon _tray;
    private readonly MessageNotificationSound _sound = new();
    private AppSettings _settings = new();
    private bool _connected, _disposed;
    private string _outputDevice = "";
    private int _outputVolume = -1;
    private MessagePopupForm? _popup;
    public event Action? OpenRequested;
    public int UnreadCount { get; private set; }
    public int ReceivedCount { get; private set; }
    public int SoundPlayCount => _sound.PlayCount;
    public float LastSoundVolume => _sound.LastVolume;
    public bool SoundPlaying => _sound.IsPlaying;
    public event Action<int>? UnreadChanged;
    public event Action<string>? Error;

    public MessageNotifications(Form window, Icon? icon)
    {
        _window = window;
        _tray = new NotifyIcon { Icon = icon ?? SystemIcons.Application, Text = "2WChat" };
        _tray.BalloonTipClicked += (_, _) => OpenChat();
        _tray.DoubleClick += (_, _) => OpenChat();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть чат", null, (_, _) => OpenChat());
        _tray.ContextMenuStrip = menu;
        _sound.Error += text => Error?.Invoke(text);
    }

    public void Configure(AppSettings settings, bool connected)
    {
        if (_disposed) return;
        if (_outputDevice != settings.OutputDevice || _outputVolume != settings.EffectiveNotificationVolume) _sound.Stop();
        _outputDevice = settings.OutputDevice; _outputVolume = settings.EffectiveNotificationVolume;
        _settings = settings; _connected = connected;
        _tray.Visible = connected && settings.MessageNotifications;
        if (!connected || !settings.MessageNotifications || !settings.MessageNotificationSound || settings.Deafened || settings.EffectiveNotificationVolume == 0)
            _sound.Stop();
        if (!connected || !settings.MessageNotifications) ClearUnread();
        if (!connected || !settings.MessageNotifications) { _popup?.Close(); _popup = null; }
    }

    public bool Notify(ChatEntry chat, Guid self, bool history = false, byte[]? avatar = null)
    {
        if (_disposed || !_connected || !_settings.MessageNotifications || history || chat.Sender == self) return false;
        ReceivedCount++;
        if (_settings.MessageNotificationSound && !_settings.Deafened && _settings.EffectiveNotificationVolume > 0)
        {
            var output = AudioEngine.OutputDevices().FirstOrDefault(d => d.Name == _settings.OutputDevice)?.Number ?? -1;
            _sound.TryPlay(output, _settings.EffectiveNotificationVolume / 100f);
        }
        if (!_window.Visible || _window.WindowState == FormWindowState.Minimized || !ApplicationHasFocus())
        {
            UnreadCount = Math.Min(9999, UnreadCount + 1);
            UnreadChanged?.Invoke(UnreadCount);
            _tray.Text = $"2WChat — непрочитанных: {UnreadCount}";
            // A bounded, non-activating popup can display the sender's avatar.
            _popup?.Close();
            _popup = new MessagePopupForm(chat, avatar, OpenChat); _popup.Show();
            Flash(14);
        }
        return true;
    }

    public void ClearUnread()
    {
        if (_disposed) return;
        if (UnreadCount != 0) { UnreadCount = 0; UnreadChanged?.Invoke(0); }
        _tray.Text = "2WChat";
        Flash(0);
    }
    private bool ApplicationHasFocus()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        GetWindowThreadProcessId(foreground, out var pid);
        return pid == (uint)Environment.ProcessId;
    }
    private void OpenChat()
    {
        if (_disposed || _window.IsDisposed) return;
        OpenRequested?.Invoke();
        _window.Show();
        if (_window.WindowState == FormWindowState.Minimized) _window.WindowState = FormWindowState.Normal;
        _window.Activate(); ClearUnread();
    }
    private void Flash(uint flags)
    {
        if (!_window.IsHandleCreated || _window.IsDisposed) return;
        var info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = _window.Handle, Flags = flags, Count = 3 };
        FlashWindowEx(ref info);
    }
    public void Dispose()
    {
        if (_disposed) return;
        ClearUnread(); _disposed = true;
        _popup?.Close(); _popup = null;
        _sound.Dispose(); _tray.Visible = false;
        _tray.ContextMenuStrip?.Dispose(); _tray.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo { public uint Size; public IntPtr Window; public uint Flags; public uint Count; public uint Timeout; }
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FlashInfo info);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
