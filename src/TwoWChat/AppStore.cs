using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using TwoWChat.Core;

namespace TwoWChat;

public sealed class AppSettings
{
    public string Name { get; set; } = Environment.UserName;
    public string Host { get; set; } = "";
    public int Port { get; set; } = Protocol.DefaultPort;
    public string RoomName { get; set; } = "Общая комната";
    public string InputDevice { get; set; } = "";
    public string OutputDevice { get; set; } = "";
    public int InputGain { get; set; } = 100;
    public int OutputVolume { get; set; } = 80;
    public bool NoiseGate { get; set; }
    public int GateDb { get; set; } = -42;
    public bool PushToTalk { get; set; }
    public int TalkKey { get; set; } = (int)Keys.F8;
    public int JitterMs { get; set; } = 60;
    public int Bitrate { get; set; } = 32000;
    public bool MicMuted { get; set; }
    public bool Deafened { get; set; }
    public bool MessageNotifications { get; set; } = true;
    public bool MessageNotificationSound { get; set; } = true;
    public int? NotificationVolume { get; set; }
    public bool AutoCheckUpdates { get; set; } = true;
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public int EffectiveNotificationVolume => Math.Clamp(NotificationVolume ?? OutputVolume, 0, 150);
    public Dictionary<string, string> TrustedHosts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void Normalize()
    {
        Name = Protocol.CleanName(Name); if (Name.Length == 0) Name = "Гость";
        Host ??= "";
        RoomName = Protocol.CleanName(RoomName, 40);
        InputDevice ??= ""; OutputDevice ??= "";
        Port = Math.Clamp(Port, 1024, 65535);
        InputGain = Math.Clamp(InputGain, 0, 200);
        OutputVolume = Math.Clamp(OutputVolume, 0, 150);
        NotificationVolume = EffectiveNotificationVolume;
        GateDb = Math.Clamp(GateDb, -60, -20);
        JitterMs = JitterMs is 40 or 60 or 100 or 160 ? JitterMs : 60;
        Bitrate = Bitrate is 24000 or 32000 or 48000 ? Bitrate : 32000;
        if (!Enum.IsDefined((Keys)TalkKey) || TalkKey == 0) TalkKey = (int)Keys.F8;
        TrustedHosts = new Dictionary<string, string>(TrustedHosts ?? [], StringComparer.OrdinalIgnoreCase);
    }
}

public static class AppStore
{
    public static string Folder { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "2WChat");
    public static void SetDataDirectory(string directory) => Folder = Path.GetFullPath(directory);
    public static AppSettings Load()
    {
        try
        {
            var path = Path.Combine(Folder, "settings.json");
            if (!File.Exists(path)) return new AppSettings();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
            settings.Normalize();
            return settings;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { Log("Settings read: " + e.Message); return new AppSettings(); }
    }
    public static void Save(AppSettings settings)
    {
        settings.Normalize();
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, "settings.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
    public static X509Certificate2 Identity()
    {
        Directory.CreateDirectory(Folder);
        var file = Path.Combine(Folder, "room-identity.pfx");
        if (File.Exists(file)) return X509CertificateLoader.LoadPkcs12FromFile(file, null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
        var identity = ServerIdentity.Create();
        var tmp = file + ".tmp";
        File.WriteAllBytes(tmp, identity.Export(X509ContentType.Pfx));
        File.Move(tmp, file, false);
        return identity;
    }
    public static void Log(string text)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, "diagnostics.log");
            if (File.Exists(path) && new FileInfo(path).Length > 512000) File.Move(path, path + ".old", true);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {text}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
