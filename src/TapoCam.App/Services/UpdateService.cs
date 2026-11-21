using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace TapoCam.App.Services;

internal sealed record AppUpdate(Version Version, Uri InstallerUrl, long InstallerSize, string Sha256);

internal sealed class UpdateService : IDisposable
{
    private const string ReleaseApiUrl = "https://api.github.com/repos/theaidguild/tapocam-windows/releases/latest";
    private const string ReleaseDownloadBase = "https://github.com/theaidguild/tapocam-windows/releases/download/";
    private const long MaximumInstallerSize = 250 * 1024 * 1024;
    private readonly HttpClient _http;

    public UpdateService(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler())
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TapoCam-Updater/1.0");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<AppUpdate?> CheckForUpdateAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseApiUrl);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var release = document.RootElement;
        if (GetBoolean(release, "draft") || GetBoolean(release, "prerelease"))
            return null;

        var tag = GetString(release, "tag_name");
        var version = ParseStableVersion(tag);
        if (version <= currentVersion)
            return null;

        var expectedName = $"TapoCam-{version}-win-x64-setup.exe";
        var assets = GetArray(release, "assets");
        var matches = assets.EnumerateArray()
            .Where(asset => string.Equals(GetString(asset, "name"), expectedName, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException($"O Release {tag} não contém exatamente um instalador {expectedName}.");

        var installer = matches[0];
        var size = GetInt64(installer, "size");
        if (size is <= 0 or > MaximumInstallerSize)
            throw new InvalidDataException("O tamanho do instalador publicado é inválido.");

        var digest = GetString(installer, "digest");
        if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || digest.Length != 71)
            throw new InvalidDataException("O Release não fornece um digest SHA-256 válido para o instalador.");
        var sha256 = digest["sha256:".Length..];
        try { _ = Convert.FromHexString(sha256); }
        catch (FormatException ex) { throw new InvalidDataException("O digest SHA-256 do instalador é inválido.", ex); }

        var expectedUrl = $"{ReleaseDownloadBase}{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(expectedName)}";
        var downloadUrl = GetString(installer, "browser_download_url");
        if (!string.Equals(downloadUrl, expectedUrl, StringComparison.Ordinal))
            throw new InvalidDataException("A URL do instalador não corresponde ao Release esperado.");

        return new(version, new Uri(expectedUrl), size, sha256);
    }

    public async Task<string> DownloadAndVerifyAsync(AppUpdate update, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TapoCam", "Updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var installerPath = Path.Combine(directory, $"TapoCam-{update.Version}-win-x64-setup.exe");

        try
        {
            using var response = await _http.GetAsync(update.InstallerUrl,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumInstallerSize)
                throw new InvalidDataException("O instalador baixado excede o tamanho permitido.");

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = new FileStream(installerPath, FileMode.CreateNew, FileAccess.ReadWrite,
                             FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[64 * 1024];
                long totalBytes = 0;
                int bytesRead;
                while ((bytesRead = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    totalBytes += bytesRead;
                    if (totalBytes > update.InstallerSize || totalBytes > MaximumInstallerSize)
                        throw new InvalidDataException("O instalador baixado excede o tamanho publicado no Release.");
                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                }
                if (totalBytes != update.InstallerSize)
                    throw new InvalidDataException("O tamanho do instalador baixado não corresponde ao Release.");
                destination.Position = 0;
                var actualHash = await SHA256.HashDataAsync(destination, cancellationToken);
                var expectedHash = Convert.FromHexString(update.Sha256);
                if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                    throw new InvalidDataException("O SHA-256 do instalador não corresponde ao Release.");
            }

            return installerPath;
        }
        catch
        {
            TryDelete(installerPath);
            TryDeleteDirectory(directory);
            throw;
        }
    }

    private static Version ParseStableVersion(string tag)
    {
        if (!tag.StartsWith('v') || !Version.TryParse(tag[1..], out var version) ||
            version.Build < 0 || version.Revision >= 0 || !string.Equals(version.ToString(3), tag[1..], StringComparison.Ordinal))
            throw new InvalidDataException($"A tag do Release '{tag}' não é uma versão estável válida.");
        return version;
    }

    private static JsonElement GetArray(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array)
            return value;
        throw new InvalidDataException($"O campo '{property}' do Release está ausente ou inválido.");
    }

    private static string GetString(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()))
            return value.GetString()!;
        throw new InvalidDataException($"O campo '{property}' do Release está ausente ou inválido.");
    }

    private static bool GetBoolean(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var value) &&
            (value.ValueKind is JsonValueKind.True or JsonValueKind.False))
            return value.GetBoolean();
        throw new InvalidDataException($"O campo '{property}' do Release está ausente ou inválido.");
    }

    private static long GetInt64(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out var number))
            return number;
        throw new InvalidDataException($"O campo '{property}' do Release está ausente ou inválido.");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose() => _http.Dispose();
}
