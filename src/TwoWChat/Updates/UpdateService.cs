using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace TwoWChat.Updates;

public sealed record UpdateRelease(Version Version, string Tag, string Notes, Uri Download, long Size, string Sha256);

public sealed class UpdateService : IDisposable
{
    public const string Repository = "merumuiri-afk/2wchat2";
    public const long MaxPackageBytes = 160L * 1024 * 1024;
    public const long MaxExecutableBytes = 250L * 1024 * 1024;
    private readonly HttpClient _http;
    public static Version CurrentVersion => typeof(UpdateService).Assembly.GetName().Version!;

    public UpdateService(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("2WChat/" + CurrentVersion);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public static Version? ParseVersion(string? tag)
    {
        if (tag == null || !Version.TryParse(tag.TrimStart('v'), out var value) || value.Major < 0) return null;
        return new Version(value.Major, value.Minor, Math.Max(0, value.Build), Math.Max(0, value.Revision));
    }

    public async Task<UpdateRelease?> CheckAsync(CancellationToken cancellationToken)
    {
        using var response = await GetAsync(new Uri($"https://api.github.com/repos/{Repository}/releases/latest"), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null; // An empty release list is normal on first installation.
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var jsonBytes = new MemoryStream();
        await CopyBoundedAsync(input, jsonBytes, 1024 * 1024, null, cancellationToken);
        using var document = JsonDocument.Parse(jsonBytes.ToArray());
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = ParseVersion(tag);
        if (version == null || version <= CurrentVersion) return null;
        var expectedName = $"2WChat-{tag.TrimStart('v')}-win-x64.zip";
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != expectedName) continue;
            var size = asset.GetProperty("size").GetInt64();
            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            if (size <= 0 || size > MaxPackageBytes || digest == null || !digest.StartsWith("sha256:", StringComparison.Ordinal))
                throw new InvalidDataException("В выпуске отсутствует допустимый размер или SHA-256 архива.");
            var hash = digest[7..];
            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Некорректный SHA-256 выпуска.");
            var uri = new Uri(asset.GetProperty("browser_download_url").GetString()!);
            var expectedPath = $"/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{expectedName}";
            if (uri.Scheme != "https" || uri.Host != "github.com" || uri.AbsolutePath != expectedPath || uri.Query.Length != 0 || uri.UserInfo.Length != 0)
                throw new InvalidDataException("Ссылка обновления не принадлежит репозиторию 2WChat.");
            var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            return new UpdateRelease(version, tag, notes.Length > 12000 ? notes[..12000] : notes, uri, size, hash);
        }
        throw new InvalidDataException("В новом выпуске ещё нет архива для Windows x64. Попробуйте позднее.");
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken ct)
    {
        for (var redirects = 0; ; redirects++)
        {
            if (uri.Scheme != "https" || uri.UserInfo.Length != 0 || !uri.IsDefaultPort ||
                uri.Host is not ("api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new InvalidDataException("Недопустимый сервер обновлений.");
            var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (redirects >= 5 || location == null) throw new InvalidDataException("Слишком много перенаправлений обновления.");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
    }

    public async Task<string> DownloadAsync(UpdateRelease release, string updateRoot, IProgress<int>? progress, CancellationToken ct)
    {
        if (release.Size <= 0 || release.Size > MaxPackageBytes) throw new InvalidDataException("Слишком большой архив.");
        var folder = Path.Combine(updateRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "package.zip");
        try
        {
            using var response = await GetAsync(release.Download, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long declared && declared != release.Size) throw new InvalidDataException("Размер загрузки отличается от выпуска.");
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await CopyBoundedAsync(input, output, release.Size, n => progress?.Report((int)(n * 100 / release.Size)), ct);
                if (output.Length != release.Size) throw new InvalidDataException("Архив загружен не полностью.");
            }
            await VerifyHashAsync(path, release.Sha256, ct);
            // Validate the package before ever closing the main application.
            ExtractExecutable(path, Path.Combine(folder, "candidate.exe"), release.Version);
            return folder;
        }
        catch
        {
            foreach (var file in new[] { path, Path.Combine(folder, "candidate.exe") }) { try { File.Delete(file); } catch (IOException) { } }
            throw;
        }
    }

    public static async Task VerifyHashAsync(string path, string expected, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA-256 не совпадает. Обновление не будет установлено.");
    }

    public static void ExtractExecutable(string package, string destination, Version expectedVersion)
    {
        using var zip = ZipFile.OpenRead(package);
        if (zip.Entries.Count > 100) throw new InvalidDataException("Слишком много файлов в обновлении.");
        // Only the executable is installed. Profiles, extra local files and arbitrary ZIP paths are never extracted.
        var entries = zip.Entries.Where(e => e.FullName.Equals("2WChat.exe", StringComparison.Ordinal)).ToArray();
        if (entries.Length != 1 || entries[0].Length is <= 0 or > MaxExecutableBytes) throw new InvalidDataException("В архиве нет единственного допустимого 2WChat.exe.");
        using (var source = entries[0].Open())
        using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920]; long total = 0; int n;
            while ((n = source.Read(buffer)) != 0)
            {
                total += n;
                if (total > entries[0].Length || total > MaxExecutableBytes) throw new InvalidDataException("EXE превышает допустимый размер.");
                output.Write(buffer, 0, n);
            }
            if (total != entries[0].Length) throw new InvalidDataException("EXE повреждён.");
        }
        var actual = ParseVersion(FileVersionInfo.GetVersionInfo(destination).FileVersion);
        if (actual != expectedVersion) throw new InvalidDataException("Версия EXE не совпадает с версией выпуска.");
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long limit, Action<long>? progress, CancellationToken ct)
    {
        var buffer = new byte[81920]; long total = 0; int n;
        while ((n = await input.ReadAsync(buffer, ct)) != 0)
        {
            total += n;
            if (total > limit) throw new InvalidDataException("Ответ сервера превышает допустимый размер.");
            await output.WriteAsync(buffer.AsMemory(0, n), ct); progress?.Invoke(total);
        }
    }

    public void Dispose() => _http.Dispose();
}
