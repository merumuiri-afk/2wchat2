using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using TwoWChat.Audio;
using TwoWChat.Core;

internal static partial class Program
{
    private static async Task CheckRoomAsync()
    {
        using var cert = ServerIdentity.Create();
        var server = new RoomServer(cert, "Комната теста", "пароль"); server.Start(0, IPAddress.Loopback);
        var port = server.Port; var pin = server.Fingerprint;
        var clients = new List<RoomClient>();
        try
        {
            async Task<RoomClient> Join(string name, string password = "пароль")
            {
                var client = new RoomClient(); clients.Add(client);
                await client.ConnectAsync("127.0.0.1", port, name, password, false, false, p => p == pin);
                return client;
            }
            var a = await Join("Алиса"); var b = await Join("Боб"); var c = await Join("Вика 👋");
            await Until(() => a.Members.Length == 3 && b.Members.Length == 3, "Three clients join and receive the same member list");
            Check(a.Room == "Комната теста", "Room name is delivered in the TLS welcome");
            var rejected = false;
            try { await Join("Чужой", "неверно"); } catch (IOException e) { rejected = e.Message.Contains("пароль"); }
            Check(rejected, "Wrong room password is rejected");
            rejected = false;
            try { await Join("алиса"); } catch (IOException e) { rejected = e.Message.Contains("занято"); }
            Check(rejected, "Duplicate nicknames are rejected without case sensitivity");
            var blocked = new RoomClient(); clients.Add(blocked); rejected = false;
            try { await blocked.ConnectAsync("127.0.0.1", port, "Blocked", "пароль", false, false, _ => false); }
            catch (AuthenticationException) { rejected = true; }
            Check(rejected, "Untrusted TLS certificate cannot establish a room session");
            var messages = new ConcurrentQueue<ChatEntry>();
            b.Message += m => { if (m.Chat != null) messages.Enqueue(m.Chat); };
            await a.SendChatAsync("Привет!\nЮникод, эмодзи 👋 и русский текст.");
            await Until(() => messages.Count == 1, "Russian multiline text reaches another client");
            Check(messages.TryPeek(out var chat) && chat.Sender == a.Id && chat.Name == "Алиса", "Server assigns authenticated sender identity");
            await a.SendAsync(new WireMessage("chat") { Id = b.Id, Name = "Подмена", Text = "Проверка подмены" });
            await Until(() => messages.Count == 2, "A second message is delivered");
            Check(messages.Last().Sender == a.Id && messages.Last().Name == "Алиса", "Client cannot spoof a chat sender");
            var late = await Join("Поздний");
            Check(late.History.Any(h => h.Text.Contains("Юникод")), "Late joiner receives recent message history");
            await Until(() => a.UdpReady && b.UdpReady && c.UdpReady && late.UdpReady, "Encrypted UDP keepalives work for four clients");
            var voiceB = new ConcurrentQueue<VoiceFrame>(); var voiceC = new ConcurrentQueue<VoiceFrame>();
            var ownVoice = 0;
            b.Voice += voiceB.Enqueue; c.Voice += voiceC.Enqueue; a.Voice += _ => Interlocked.Increment(ref ownVoice);
            using var encoder = new VoiceEncoder();
            for (uint i = 0; i < 40; i++) { a.SendVoice(i, 90, encoder.Encode(Tone((int)i))); await Task.Delay(20); }
            await Until(() => voiceB.Count >= 35 && voiceC.Count >= 35, "Opus voice is relayed to every listening participant");
            Check(ownVoice == 0, "Server does not echo microphone audio back to its sender");
            Check(voiceB.All(v => v.Speaker == a.Id), "Server stamps voice with the authenticated speaker ID");
            using var decoder = new VoiceDecoder(); var pcm = new short[960]; double rms = 0;
            foreach (var frame in voiceB) { decoder.Decode(frame.Opus, pcm); rms += Rms(pcm); }
            Check(rms / voiceB.Count > 1500, "Encrypted network voice decodes into audible PCM");
            await a.SetStateAsync(true, false);
            await Until(() => b.Members.Single(p => p.Id == a.Id).MicMuted, "Microphone mute is visible to other participants");
            await Task.Delay(100); var before = voiceB.Count;
            for (uint i = 40; i < 45; i++) { a.SendVoice(i, 90, encoder.Encode(Tone((int)i))); await Task.Delay(20); }
            await Task.Delay(100);
            Check(voiceB.Count == before, "Server rejects voice sent by a muted participant");
            await a.SetStateAsync(false, false); await b.SetStateAsync(false, true);
            await Until(() => a.Members.Single(p => p.Id == b.Id).Deafened && !a.Members.Single(p => p.Id == a.Id).MicMuted, "Deafen and unmute propagate through the room");
            before = voiceB.Count; var cBefore = voiceC.Count;
            for (uint i = 45; i < 55; i++) { a.SendVoice(i, 90, encoder.Encode(Tone((int)i))); await Task.Delay(20); }
            await Task.Delay(150);
            Check(voiceB.Count == before && voiceC.Count > cBefore, "Deafened client receives no voice while other clients continue");
            await b.SetStateAsync(false, false);
            await Until(() => !a.Members.Single(p => p.Id == b.Id).Deafened, "Deafen can be reversed");
            using (var garbage = new UdpClient(AddressFamily.InterNetwork))
                for (var i = 0; i < 100; i++) await garbage.SendAsync(new byte[70], new IPEndPoint(IPAddress.Loopback, port));
            await a.SendChatAsync("После некорректных UDP-пакетов всё работает.");
            await Until(() => messages.Any(m => m.Text.Contains("некорректных")), "Malformed unauthenticated datagrams do not break chat");
            await late.DisposeAsync();
            await Until(() => a.Members.Length == 3, "Disconnect removes a participant from the room");

            // Fill history with escaping-heavy text, then ensure welcome stays bounded.
            for (var i = 0; i < 5; i++)
            {
                await a.SendChatAsync(new string('"', 1900) + " Привет " + i);
                await Task.Delay(520);
            }
            var historyClient = await Join("История");
            Check(historyClient.Connected && historyClient.History.Length > 0, "Escaping-heavy history still fits the welcome frame");
            var active = 4;
            for (var i = active; i < Protocol.MaxPeople; i++) await Join("Гость " + i);
            await Until(() => a.Members.Length == Protocol.MaxPeople, "Room supports the advertised sixteen participants");
            rejected = false;
            try { await Join("Лишний"); } catch (IOException e) { rejected = e.Message.Contains("заполнена"); }
            Check(rejected, "Room capacity is enforced");
            server.Kick(c.Id);
            await Until(() => !c.Connected && a.Members.Length == Protocol.MaxPeople - 1, "Host can disconnect a participant");
            await server.DisposeAsync();
            await Until(() => !a.Connected && !b.Connected, "Closing the host disconnects clients cleanly");
            await using var restarted = new RoomServer(cert, "Ещё раз", ""); restarted.Start(port, IPAddress.Loopback);
            var again = new RoomClient(); clients.Add(again);
            await again.ConnectAsync("127.0.0.1", port, "Снова", "", false, false, p => p == pin);
            Check(again.Connected, "Server and client can reconnect using the same TCP and UDP port");
        }
        finally
        {
            foreach (var client in clients) await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    private static async Task CheckHardwareAsync()
    {
        var errors = new ConcurrentQueue<string>(); var captured = 0;
        using var audio = new AudioEngine(); audio.Error += errors.Enqueue;
        audio.Encoded += (_, _, _) => Interlocked.Increment(ref captured);
        audio.Start(-1, -1);
        await Task.Delay(1200);
        Notes.Add("Input devices: " + string.Join("; ", AudioEngine.InputDevices().Select(d => d.Name)));
        Notes.Add("Output devices: " + string.Join("; ", AudioEngine.OutputDevices().Select(d => d.Name)));
        Notes.AddRange(errors);
        Check(audio.HasOutput, "Windows playback device opens and consumes the mixer");
        Check(audio.HasMicrophone && captured >= 20, "Windows microphone delivers PCM frames to the Opus encoder");
        audio.MicMuted = true; await Task.Delay(80); var before = captured; await Task.Delay(160);
        Check(captured == before, "Microphone mute stops actual capture frames from being encoded");
        audio.MicMuted = false; audio.Deafened = true; await Task.Delay(80); before = captured; await Task.Delay(160);
        Check(captured == before, "Deafen also blocks actual microphone transmission");
        audio.Deafened = false; audio.PushToTalk = true; audio.TalkHeld = false; await Task.Delay(80); before = captured; await Task.Delay(160);
        Check(captured == before, "Push-to-talk blocks microphone transmission when released");
        audio.TalkHeld = true; await Task.Delay(180);
        Check(captured > before, "Push-to-talk transmits microphone frames while held");
        using var encoder = new VoiceEncoder(); var id = Guid.NewGuid();
        for (uint i = 0; i < 10; i++) { audio.AddVoice(new VoiceFrame(id, i, 10, encoder.Encode(Tone((int)i, 440, 500)))); await Task.Delay(20); }
        Check(errors.IsEmpty, "Audio devices run without capture or playback errors");
        using var notification = new MessageNotificationSound(); notification.Error += errors.Enqueue;
        Check(notification.TryPlay(-1, .2f) && notification.IsPlaying, "Embedded message notification plays on the Windows output device");
        Check(!notification.TryPlay(-1, .2f) && notification.PlayCount == 1, "Message bursts do not overlap or restart a playing notification clip");
        notification.Stop();
        Check(!notification.IsPlaying && !notification.TryPlay(-1, 0), "Notification playback stops immediately and zero volume remains silent");
        Notes.AddRange(errors);
        Check(errors.IsEmpty, "Voice and notification playback coexist without audio errors: " + string.Join("; ", errors));
    }
}
