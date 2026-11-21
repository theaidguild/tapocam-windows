using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TapoCam.App.Services;

namespace TapoCam.App.Tests;

public sealed class UpdateServiceTests
{
    private const string InstallerName = "TapoCam-1.0.2-win-x64-setup.exe";
    private const string InstallerUrl = "https://github.com/theaidguild/tapocam-windows/releases/download/v1.0.2/TapoCam-1.0.2-win-x64-setup.exe";

    [Fact]
    public async Task FindsNewStableReleaseAndUsesItsInstallerDigest()
    {
        using var service = new UpdateService(new StubHandler(_ =>
            JsonResponse(ReleaseJson("v1.0.2", Hash([1, 2, 3]), [1, 2, 3]))));

        var update = await service.CheckForUpdateAsync(new Version(1, 0, 1));

        Assert.NotNull(update);
        Assert.Equal(new Version(1, 0, 2), update.Version);
        Assert.Equal(new Uri(InstallerUrl), update.InstallerUrl);
        Assert.Equal(Hash([1, 2, 3]), update.Sha256);
        Assert.Equal(3, update.InstallerSize);
    }

    [Fact]
    public async Task DoesNotOfferSameOrOlderRelease()
    {
        using var service = new UpdateService(new StubHandler(_ => JsonResponse(
            ReleaseJson("v1.0.2", Hash([1]), [1]))));

        Assert.Null(await service.CheckForUpdateAsync(new Version(1, 0, 2)));
        Assert.Null(await service.CheckForUpdateAsync(new Version(1, 0, 3)));
    }

    [Fact]
    public async Task IgnoresPrerelease()
    {
        using var service = new UpdateService(new StubHandler(_ => JsonResponse(
            ReleaseJson("v1.0.2", Hash([1]), [1], prerelease: true))));

        Assert.Null(await service.CheckForUpdateAsync(new Version(1, 0, 1)));
    }

    [Fact]
    public async Task RejectsMissingOrMismatchedDigestMetadata()
    {
        using var missingDigest = new UpdateService(new StubHandler(_ => JsonResponse(
            ReleaseJson("v1.0.2", null, [1]))));
        using var mismatchedUrl = new UpdateService(new StubHandler(_ => JsonResponse(
            ReleaseJson("v1.0.2", Hash([1]), [1], url: "https://example.com/installer.exe"))));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            missingDigest.CheckForUpdateAsync(new Version(1, 0, 1)));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            mismatchedUrl.CheckForUpdateAsync(new Version(1, 0, 1)));
    }

    [Fact]
    public async Task RejectsMissingInstallerAndMalformedReleaseVersion()
    {
        using var missingInstaller = new UpdateService(new StubHandler(_ => JsonResponse(
            ReleaseJson("v1.0.2", Hash([1]), [1], assetName: "other.exe"))));
        using var malformedVersion = new UpdateService(new StubHandler(_ => JsonResponse(
            ReleaseJson("v1.0.2-rc.1", Hash([1]), [1]))));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            missingInstaller.CheckForUpdateAsync(new Version(1, 0, 1)));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            malformedVersion.CheckForUpdateAsync(new Version(1, 0, 1)));
    }

    [Fact]
    public async Task DownloadsInstallerOnlyWhenSizeAndSha256Match()
    {
        byte[] installer = [3, 5, 7, 9];
        using var service = new UpdateService(new StubHandler(request =>
            request.RequestUri?.AbsoluteUri == InstallerUrl
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(installer) }
                : JsonResponse(ReleaseJson("v1.0.2", Hash(installer), installer))));
        var update = await service.CheckForUpdateAsync(new Version(1, 0, 1));

        Assert.NotNull(update);
        var path = await service.DownloadAndVerifyAsync(update);
        try
        {
            Assert.Equal(installer, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public async Task DeletesInstallerWhenSha256DoesNotMatch()
    {
        byte[] installer = [2, 4, 6];
        using var service = new UpdateService(new StubHandler(request =>
            request.RequestUri?.AbsoluteUri == InstallerUrl
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(installer) }
                : JsonResponse(ReleaseJson("v1.0.2", Hash([1, 1, 1]), installer))));
        var update = await service.CheckForUpdateAsync(new Version(1, 0, 1));

        Assert.NotNull(update);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAndVerifyAsync(update));
    }

    private static string ReleaseJson(string tag, string? digest, byte[] content, bool prerelease = false,
        string? url = null, string? assetName = null) =>
        JsonSerializer.Serialize(new
        {
            tag_name = tag,
            draft = false,
            prerelease,
            assets = new[]
            {
                new
                {
                    name = assetName ?? $"TapoCam-{tag[1..]}-win-x64-setup.exe",
                    browser_download_url = url ?? $"https://github.com/theaidguild/tapocam-windows/releases/download/{tag}/{InstallerName.Replace("1.0.2", tag[1..], StringComparison.Ordinal)}",
                    digest = digest is null ? null : $"sha256:{digest}",
                    size = content.Length
                }
            }
        });

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
