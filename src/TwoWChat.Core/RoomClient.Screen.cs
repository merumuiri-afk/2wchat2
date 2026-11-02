using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace TwoWChat.Core;

public sealed partial class RoomClient
{
    private IPEndPoint? _screenEndpoint;
    private string _screenFingerprint = "";
    private Guid _screenToken;
    private byte[]? _screenKey;
    private readonly SemaphoreSlim _publishOperations = new(1, 1), _watchOperations = new(1, 1);
    private MediaConnection? _publishingScreen, _watchingScreen;
    private ScreenShareInfo[] _screens = [];
    private readonly object _screenStateGate = new();
    private long _screenRevision;
    public bool SupportsScreenShare { get; private set; }
    public ScreenShareInfo[] Screens => Volatile.Read(ref _screens);
    public bool IsScreenSharing => Volatile.Read(ref _publishingScreen) is { Closed: false };
    public Guid? WatchingScreenId => Volatile.Read(ref _watchingScreen) is { Closed: false } viewing ? viewing.OwnerId : null;
    public event Action<ScreenShareInfo[]>? ScreensChanged;
    public event Action<ScreenFrame>? ScreenFrameReceived;
    public event Action<string>? ScreenShareEnded;
    public event Action<string>? ScreenWatchEnded;

    private void InitializeScreenTransport(WireMessage welcome, IPEndPoint remote)
    {
        SupportsScreenShare = welcome.SupportsScreenShare;
        if (!SupportsScreenShare) return;
        try { _screenKey = Convert.FromBase64String(welcome.ScreenKey ?? ""); }
        catch (FormatException e) { throw new InvalidDataException("Некорректный ключ демонстрации экрана.", e); }
        if (_screenKey.Length != 32) throw new InvalidDataException("Некорректный ключ демонстрации экрана.");
        _screenEndpoint = remote; _screenToken = welcome.Token;
        _screenRevision = welcome.Stamp;
        Volatile.Write(ref _screens, ValidScreens(welcome.Screens ?? []));
    }
    private static ScreenShareInfo[] ValidScreens(ScreenShareInfo[] screens) => screens.Where(s => s.OwnerId != Guid.Empty &&
        s.Width is >= 1 and <= ScreenWire.MaxWidth && s.Height is >= 1 and <= ScreenWire.MaxHeight).Take(1).ToArray();
    private bool UpdateScreens(ScreenShareInfo[] screens, long revision)
    {
        lock (_screenStateGate)
        {
            if (revision < _screenRevision) return false;
            var snapshot = ValidScreens(screens);
            var changed = revision != _screenRevision || !_screens.SequenceEqual(snapshot);
            _screenRevision = revision; Volatile.Write(ref _screens, snapshot);
            // Media EOF owns connection teardown. A snapshot is informational;
            // it must never close a newer media socket established in parallel.
            if (changed) ScreensChanged?.Invoke(snapshot);
            return true;
        }
    }

    public async Task StartScreenShareAsync(string title, int width, int height)
    {
        if (width is < 1 or > ScreenWire.MaxWidth || height is < 1 or > ScreenWire.MaxHeight) throw new ArgumentOutOfRangeException(nameof(width), "Демонстрация поддерживает размер до 1920×1080.");
        EnsureScreenSupport();
        await _publishOperations.WaitAsync(_stop.Token);
        try
        {
            if (IsScreenSharing) throw new InvalidOperationException("Вы уже показываете экран.");
            var connection = await OpenScreenMediaAsync("publish", Guid.Empty, new ScreenShareInfo(Id, "", Protocol.CleanName(title, 80), width, height));
            Volatile.Write(ref _publishingScreen, connection);
            // The ready response authenticates the new stream even if the primary
            // control socket's screens snapshot arrives a moment later.
            connection.Task = PublishScreenAsync(connection);
        }
        finally { _publishOperations.Release(); }
    }

    public bool SendScreenFrame(byte[] jpeg)
    {
        ScreenWire.ValidateFrame(jpeg);
        var connection = Volatile.Read(ref _publishingScreen);
        return connection is { Closed: false } && connection.Frames.Writer.TryWrite(jpeg);
    }

    public async Task StopScreenShareAsync()
    {
        await _publishOperations.WaitAsync();
        try
        {
            var connection = Interlocked.Exchange(ref _publishingScreen, null);
            if (connection == null) return;
            connection.Close(); await AwaitMediaAsync(connection);
            var until = Environment.TickCount64 + 2000;
            while (Connected && Screens.Any(s => s.OwnerId == Id) && Environment.TickCount64 < until) await Task.Delay(20);
        }
        finally { _publishOperations.Release(); }
    }

    public async Task WatchScreenAsync(Guid ownerId)
    {
        EnsureScreenSupport();
        await _watchOperations.WaitAsync(_stop.Token);
        try
        {
            var previous = Interlocked.Exchange(ref _watchingScreen, null);
            if (previous != null) { previous.Close(); await AwaitMediaAsync(previous); }
            var connection = await OpenScreenMediaAsync("watch", ownerId, null);
            Volatile.Write(ref _watchingScreen, connection);
            connection.Task = ReceiveScreenAsync(connection);
        }
        finally { _watchOperations.Release(); }
    }

    public async Task StopWatchingScreenAsync()
    {
        await _watchOperations.WaitAsync();
        try
        {
            var connection = Interlocked.Exchange(ref _watchingScreen, null);
            if (connection == null) return;
            connection.Close(); await AwaitMediaAsync(connection);
        }
        finally { _watchOperations.Release(); }
    }

    private void EnsureScreenSupport()
    {
        if (!Connected) throw new IOException("Нет подключения к комнате.");
        if (!SupportsScreenShare) throw new InvalidOperationException("Владелец комнаты должен обновить 2WChat для демонстрации экрана.");
    }
    private async Task<MediaConnection> OpenScreenMediaAsync(string role, Guid ownerId, ScreenShareInfo? info)
    {
        EnsureScreenSupport();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        SslStream? ssl = null;
        try
        {
            await tcp.ConnectAsync(_screenEndpoint!, timeout.Token);
            ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, _) => cert != null && ServerIdentity.Fingerprint(cert) == _screenFingerprint);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "2WChat", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
            }, timeout.Token);
            await Protocol.WriteAsync(ssl, new WireMessage("screen_media")
            {
                Token = _screenToken, ScreenKey = Convert.ToBase64String(_screenKey!), Text = role, Id = ownerId,
                Screen = info, SupportsScreenShare = true
            }, timeout.Token);
            var ready = await Protocol.ReadAsync(ssl, timeout.Token) ?? throw new IOException("Сервер закрыл соединение демонстрации.");
            if (ready.Type != "screen_ready" || ready.Screen == null) throw new IOException(ready.Text ?? "Демонстрация недоступна.");
            UpdateScreens(ready.Screens ?? [ready.Screen], ready.Stamp);
            return new MediaConnection(tcp, ssl, ready.Screen.OwnerId, _stop.Token);
        }
        catch { ssl?.Dispose(); tcp.Dispose(); throw; }
    }

    private async Task PublishScreenAsync(MediaConnection connection)
    {
        string reason = "Демонстрация экрана завершена.";
        // A read-side monitor observes server shutdown even when capture is idle.
        var monitor = MonitorPublisherCloseAsync(connection);
        try
        {
            await foreach (var jpeg in connection.Frames.Reader.ReadAllAsync(connection.Stop.Token))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(connection.Stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(3));
                await ScreenWire.WriteFrameAsync(connection.Stream, jpeg, timeout.Token);
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        { reason = "Соединение демонстрации экрана завершено."; }
        finally
        {
            connection.Close();
            try { await monitor; } catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            var endedCurrent = ReferenceEquals(Interlocked.CompareExchange(ref _publishingScreen, null, connection), connection);
            connection.Dispose(); if (endedCurrent) ScreenShareEnded?.Invoke(reason);
        }
    }
    private static async Task MonitorPublisherCloseAsync(MediaConnection connection)
    {
        try { var read = await connection.Stream.ReadAsync(new byte[1], connection.Stop.Token); if (read != 0) throw new InvalidDataException("Неожиданные данные канала демонстрации."); }
        finally { connection.Close(); }
    }

    private async Task ReceiveScreenAsync(MediaConnection connection)
    {
        try
        {
            while (!connection.Stop.IsCancellationRequested)
            {
                var jpeg = await ScreenWire.ReadFrameAsync(connection.Stream, connection.Stop.Token);
                if (jpeg == null) break;
                if (ReferenceEquals(Volatile.Read(ref _watchingScreen), connection) && !connection.Closed)
                    ScreenFrameReceived?.Invoke(new ScreenFrame(connection.OwnerId, jpeg));
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            connection.Close();
            var endedCurrent = ReferenceEquals(Interlocked.CompareExchange(ref _watchingScreen, null, connection), connection);
            connection.Dispose(); if (endedCurrent) ScreenWatchEnded?.Invoke("Просмотр демонстрации завершён.");
        }
    }

    private void CloseScreenMedia()
    {
        Volatile.Read(ref _publishingScreen)?.Close(); Volatile.Read(ref _watchingScreen)?.Close();
    }
    private async Task DisposeScreenMediaAsync()
    {
        await StopScreenShareAsync(); await StopWatchingScreenAsync();
        if (_screenKey != null) { CryptographicOperations.ZeroMemory(_screenKey); _screenKey = null; }
        Volatile.Write(ref _screens, []);
    }
    private static async Task AwaitMediaAsync(MediaConnection connection)
    {
        try { await connection.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException or TimeoutException) { }
    }

    private sealed class MediaConnection(TcpClient tcp, SslStream stream, Guid ownerId, CancellationToken stop) : IDisposable
    {
        public TcpClient Tcp { get; } = tcp;
        public SslStream Stream { get; } = stream;
        public Guid OwnerId { get; } = ownerId;
        public CancellationTokenSource Stop { get; } = CancellationTokenSource.CreateLinkedTokenSource(stop);
        public Channel<byte[]> Frames { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        public Task Task { get; set; } = Task.CompletedTask;
        private int _closed;
        public bool Closed => Volatile.Read(ref _closed) != 0;
        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            Stop.Cancel(); Frames.Writer.TryComplete(); Tcp.Close();
        }
        public void Dispose() { Close(); Stream.Dispose(); Tcp.Dispose(); Stop.Dispose(); }
    }
}
