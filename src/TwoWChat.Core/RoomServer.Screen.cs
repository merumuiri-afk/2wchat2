using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace TwoWChat.Core;

public sealed partial class RoomServer
{
    private ScreenSession? _screen;
    private long _screenRevision;

    // Call under _gate. Only feature-aware peers receive the new control type.
    private ScreenShareInfo[] ScreenSnapshot() => _screen == null ? [] : [_screen.Info];
    private void BroadcastScreens()
    {
        var message = new WireMessage("screens") { Screens = ScreenSnapshot(), Stamp = _screenRevision };
        foreach (var peer in _peers.Values) if (peer.SupportsScreenShare) peer.Queue(message);
    }

    private void ClosePeerScreenMedia(Peer peer)
    {
        if (_screen?.Info.OwnerId == peer.Info.Id) StopScreenSession(_screen);
        foreach (var connection in peer.ScreenConnections.ToArray()) connection.Close();
        peer.ScreenConnections.Clear(); peer.ScreenWatch = null;
    }

    private void StopScreenSession(ScreenSession session)
    {
        if (!ReferenceEquals(_screen, session)) return;
        _screen = null;
        _screenRevision++;
        session.Stop.Cancel(); session.Publisher.Close();
        foreach (var viewer in session.Viewers.Values) viewer.Complete();
        session.Viewers.Clear(); session.Latest = null;
        BroadcastScreens();
    }

    private async Task HandleScreenMediaAsync(TcpClient tcp, SslStream ssl, WireMessage hello, CancellationToken handshake)
    {
        Peer? owner = null;
        CancellationTokenSource? lifetime = null;
        ScreenSession? publishing = null;
        ScreenViewer? viewing = null;
        string? error = null;
        byte[]? providedKey = null;
        long readyRevision = 0;
        try
        {
            try { providedKey = Convert.FromBase64String(hello.ScreenKey ?? ""); } catch (FormatException) { }
            lock (_gate)
            {
                if (hello.Version != Protocol.Version || !_tokens.TryGetValue(hello.Token, out owner) || !owner.SupportsScreenShare ||
                    owner.Stop.IsCancellationRequested || providedKey == null || providedKey.Length != 32 ||
                    !CryptographicOperations.FixedTimeEquals(owner.ScreenKey, providedKey)) error = "Нет доступа к демонстрации экрана. Подключитесь к комнате заново.";
                else if (hello.Text == "publish")
                {
                    if (_screen != null) error = "В комнате уже идёт демонстрация. Дождитесь её окончания.";
                    else if (hello.Screen is not { Width: >= 1 and <= ScreenWire.MaxWidth, Height: >= 1 and <= ScreenWire.MaxHeight }) error = "Недопустимый размер демонстрации экрана.";
                    else
                    {
                        var title = Protocol.CleanName(hello.Screen.Title, 80);
                        if (title.Length == 0) title = "Экран";
                        publishing = new ScreenSession(new ScreenShareInfo(owner.Info.Id, owner.Info.Name, title, hello.Screen.Width, hello.Screen.Height), tcp);
                        _screen = publishing;
                        _screenRevision++;
                    }
                }
                else if (hello.Text == "watch")
                {
                    if (_screen == null || _screen.Info.OwnerId != hello.Id) error = "Демонстрация уже завершена.";
                    else
                    {
                        // One watched media socket per participant, replacing any old viewer.
                        owner.ScreenWatch?.Close();
                        viewing = new ScreenViewer(_screen, owner.Info.Id, tcp);
                        _screen.Viewers[owner.Info.Id] = viewing;
                        owner.ScreenWatch = tcp;
                        if (_screen.Latest != null) viewing.Frames.Writer.TryWrite(_screen.Latest);
                    }
                }
                else error = "Неизвестный режим демонстрации экрана.";

                if (error == null)
                {
                    owner!.ScreenConnections.Add(tcp);
                    readyRevision = _screenRevision;
                    lifetime = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, owner.Stop.Token, (publishing ?? viewing!.Session).Stop.Token);
                }
            }
            if (providedKey != null) CryptographicOperations.ZeroMemory(providedKey);
            if (error != null)
            {
                await Protocol.WriteAsync(ssl, new WireMessage("error") { Text = error }, handshake);
                return;
            }

            var readyInfo = (publishing ?? viewing!.Session).Info;
            await Protocol.WriteAsync(ssl, new WireMessage("screen_ready") { Screen = readyInfo, Screens = [readyInfo], Stamp = readyRevision }, handshake);
            if (publishing != null)
            {
                lock (_gate) if (ReferenceEquals(_screen, publishing)) BroadcastScreens();
                await ReadScreenPublisherAsync(ssl, publishing, lifetime!.Token);
            }
            else await WriteScreenViewerAsync(ssl, viewing!, lifetime!.Token);
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            if (providedKey != null) CryptographicOperations.ZeroMemory(providedKey);
            lifetime?.Dispose();
            lock (_gate)
            {
                if (publishing != null) StopScreenSession(publishing);
                if (viewing != null)
                {
                    if (viewing.Session.Viewers.TryGetValue(viewing.PeerId, out var current) && ReferenceEquals(current, viewing)) viewing.Session.Viewers.Remove(viewing.PeerId);
                    viewing.Complete();
                }
                if (owner != null)
                {
                    owner.ScreenConnections.Remove(tcp);
                    if (ReferenceEquals(owner.ScreenWatch, tcp)) owner.ScreenWatch = null;
                }
            }
            publishing?.Stop.Dispose();
        }
    }

    private async Task ReadScreenPublisherAsync(SslStream ssl, ScreenSession session, CancellationToken ct)
    {
        var rate = new RateLimit(15, 3);
        long windowStart = Environment.TickCount64; var windowBytes = 0;
        while (!ct.IsCancellationRequested)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct); idle.CancelAfter(TimeSpan.FromSeconds(15));
            var jpeg = await ScreenWire.ReadFrameAsync(ssl, idle.Token);
            if (jpeg == null) return;
            if (Environment.TickCount64 - windowStart >= 1000) { windowBytes = 0; windowStart = Environment.TickCount64; }
            windowBytes += jpeg.Length;
            if (windowBytes > 5 * 1024 * 1024) throw new InvalidDataException("Превышена скорость демонстрации.");
            if (!rate.Take()) continue;
            var (_, width, height) = ImageAttachments.Inspect(jpeg);
            if (width > session.Info.Width || height > session.Info.Height) throw new InvalidDataException("Кадр больше объявленного размера экрана.");
            lock (_gate)
            {
                if (!ReferenceEquals(_screen, session)) return;
                session.Latest = jpeg;
                foreach (var viewer in session.Viewers.Values) viewer.Frames.Writer.TryWrite(jpeg);
            }
        }
    }

    private static async Task WriteScreenViewerAsync(SslStream ssl, ScreenViewer viewer, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Monitor the idle read side too: a closed viewer must not linger until
        // the publisher next sends a frame. No client data follows the handshake.
        var closed = DetectViewerCloseAsync(ssl, stop);
        try
        {
            await foreach (var jpeg in viewer.Frames.Reader.ReadAllAsync(stop.Token))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(3));
                await ScreenWire.WriteFrameAsync(ssl, jpeg, timeout.Token);
            }
        }
        finally
        {
            stop.Cancel(); viewer.Tcp.Close();
            try { await closed; } catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    private static async Task DetectViewerCloseAsync(SslStream ssl, CancellationTokenSource stop)
    {
        try { var read = await ssl.ReadAsync(new byte[1], stop.Token); if (read != 0) throw new InvalidDataException("Неожиданные данные канала просмотра."); }
        finally { stop.Cancel(); }
    }

    private sealed class ScreenSession(ScreenShareInfo info, TcpClient publisher)
    {
        public ScreenShareInfo Info { get; } = info;
        public TcpClient Publisher { get; } = publisher;
        public CancellationTokenSource Stop { get; } = new();
        public Dictionary<Guid, ScreenViewer> Viewers { get; } = [];
        public byte[]? Latest;
    }
    private sealed class ScreenViewer(ScreenSession session, Guid peerId, TcpClient tcp)
    {
        public ScreenSession Session { get; } = session;
        public Guid PeerId { get; } = peerId;
        public TcpClient Tcp { get; } = tcp;
        public Channel<byte[]> Frames { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        public void Complete() { Frames.Writer.TryComplete(); Tcp.Close(); }
    }
}
