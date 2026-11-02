using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TwoWChat.Updates;

internal static partial class Program
{
    private static async Task CheckUpdatesAsync()
    {
        Check(UpdateService.Repository == "merumuiri-afk/2wchat2", "Updates use the public repository selected by the owner");
        Check(UpdateService.ParseVersion("v1.10.0") > UpdateService.ParseVersion("1.9.0"), "Update versions compare numerically");
        Check(UpdateService.ParseVersion("v2.0.0-beta") == null && UpdateService.ParseVersion("garbage") == null, "Invalid and prerelease version tags are rejected");
        var folder = Path.Combine(_reportFolder, "updates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var package = Path.Combine(folder, "test.zip");
        var assembly = typeof(UpdateService).Assembly.Location;
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(assembly, "2WChat.exe");
            using var writer = new StreamWriter(zip.CreateEntry("../settings.json").Open()); writer.Write("must never be extracted");
        }
        var bytes = await File.ReadAllBytesAsync(package);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var futureVersion = new Version(UpdateService.CurrentVersion.Major + 1, 0, 0, 0);
        var tag = "v" + futureVersion.ToString(3);
        var name = $"2WChat-{futureVersion.ToString(3)}-win-x64.zip";
        var download = $"https://github.com/{UpdateService.Repository}/releases/download/{tag}/{name}";
        string Release(string? digest = null, string? url = null, bool prerelease = false, string? releaseTag = null, long? size = null) => JsonSerializer.Serialize(new
        {
            tag_name = releaseTag ?? tag, draft = false, prerelease, body = "Описание выпуска",
            assets = new[] { new { name, browser_download_url = url ?? download, size = size ?? bytes.Length, digest = digest ?? "sha256:" + hash } }
        });
        using (var service = new UpdateService(new UpdateHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))))
            Check(await service.CheckAsync(default) == null, "A repository without releases leaves the app usable");
        using (var service = new UpdateService(new UpdateHandler(_ => JsonResponse(Release()))))
        {
            var release = await service.CheckAsync(default);
            Check(release?.Version == futureVersion && release.Sha256 == hash && release.Notes == "Описание выпуска", "GitHub release metadata selects the exact Windows asset and hash");
        }
        foreach (var json in new[] { Release(prerelease: true), Release(releaseTag: "v1.0.0") })
        {
            using var service = new UpdateService(new UpdateHandler(_ => JsonResponse(json)));
            Check(await service.CheckAsync(default) == null, "Old and prerelease releases never trigger installation");
        }
        foreach (var json in new[] { Release(digest: "sha256:bad"), Release(url: "https://github.com/other/app/releases/download/v2/file.zip"), Release(size: UpdateService.MaxPackageBytes + 1) })
        {
            using var service = new UpdateService(new UpdateHandler(_ => JsonResponse(json)));
            var rejected = false; try { await service.CheckAsync(default); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Untrusted release path, invalid hash or oversized asset is rejected");
        }
        var executable = Path.Combine(folder, "candidate.exe");
        UpdateService.ExtractExecutable(package, executable, UpdateService.CurrentVersion);
        Check(File.ReadAllBytes(executable).SequenceEqual(File.ReadAllBytes(assembly)) && !File.Exists(Path.Combine(_reportFolder, "settings.json")), "ZIP extraction installs only the exact executable and ignores traversal entries");
        var wrongVersion = false;
        try { UpdateService.ExtractExecutable(package, executable, futureVersion); } catch (InvalidDataException) { wrongVersion = true; }
        Check(wrongVersion, "Candidate executable version must match its release tag");
        var duplicateZip = Path.Combine(folder, "duplicate.zip");
        using (var zip = ZipFile.Open(duplicateZip, ZipArchiveMode.Create)) { zip.CreateEntry("2WChat.exe"); zip.CreateEntry("2WChat.exe"); }
        var duplicate = false;
        try { UpdateService.ExtractExecutable(duplicateZip, executable, UpdateService.CurrentVersion); } catch (InvalidDataException) { duplicate = true; }
        Check(duplicate, "Ambiguous duplicate executable entries are rejected");
        var validRelease = new UpdateRelease(UpdateService.CurrentVersion, "test", "", new Uri(download), bytes.Length, hash);
        using (var service = new UpdateService(new UpdateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })))
        {
            var staged = await service.DownloadAsync(validRelease, folder, null, default);
            Check(File.Exists(Path.Combine(staged, "candidate.exe")), "A verified complete download stages an executable before application shutdown");
        }
        using (var service = new UpdateService(new UpdateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })))
        {
            var corrupt = false;
            try { await service.DownloadAsync(validRelease with { Sha256 = new string('0', 64) }, folder, null, default); } catch (InvalidDataException) { corrupt = true; }
            Check(corrupt, "Downloaded packages with a mismatched SHA-256 never reach the installer");
            var truncated = false;
            try { await service.DownloadAsync(validRelease with { Size = bytes.Length + 1 }, folder, null, default); } catch (InvalidDataException) { truncated = true; }
            Check(truncated, "Truncated downloads are rejected before installation");
            using var stop = new CancellationTokenSource(); stop.Cancel();
            var cancelled = false;
            try { await service.DownloadAsync(validRelease, folder, null, stop.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Cancelling an update does not install a partial package");
        }
        using (var service = new UpdateService(new UpdateHandler(_ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://example.com/malware") } })))
        {
            var redirect = false; try { await service.CheckAsync(default); } catch (InvalidDataException) { redirect = true; }
            Check(redirect, "Update redirects cannot downgrade HTTPS or leave GitHub's download hosts");
        }
        var settings = JsonSerializer.Deserialize<TwoWChat.AppSettings>("{}")!;
        Check(settings.AutoCheckUpdates && settings.LastUpdateCheckUtc == null, "Existing profiles default to automatic checks with no stored credentials");
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class UpdateHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}
