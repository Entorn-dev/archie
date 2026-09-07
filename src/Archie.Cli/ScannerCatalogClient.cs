using System.Net;
using System.Text.Json;
using Archie.Contracts;
using Archie.Runner;
using NuGet.Versioning;

namespace Archie.Cli;

internal sealed record ScannerCatalogLoad(
    ScannerCatalog Catalog,
    bool UsedCachedCatalog,
    DateTimeOffset RetrievedAt,
    string? RefreshFailure);

internal sealed class ScannerCatalogClient : IDisposable
{
    private const int MaxRedirects = 5;
    private const int MaxEnvelopeBytes = 1024 * 1024;
    private const int MaxCatalogReleases = 10_000;
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(2);
    private static readonly Uri DefaultCatalogUrl = new("https://catalog.entorn.dev/catalog.json");
    private readonly InstalledScannerStore store;
    private readonly ScannerTrustVerifier verifier;
    private readonly HttpClient client;
    private readonly Uri catalogUrl;

    public ScannerCatalogClient(InstalledScannerStore store)
        : this(store, new HttpClientHandler { AllowAutoRedirect = false }, CatalogUrl(), new ScannerTrustVerifier()) { }

    internal ScannerCatalogClient(
        InstalledScannerStore store,
        HttpMessageHandler handler,
        Uri catalogUrl,
        ScannerTrustVerifier verifier)
    {
        this.store = store;
        this.catalogUrl = catalogUrl;
        this.verifier = verifier;
        client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Archie/1.0");
    }

    internal string CachePath => Path.Combine(store.RootDirectory, "catalog", "catalog.json");

    public async Task<ScannerCatalog> RefreshAsync(CancellationToken cancellationToken)
    {
        ValidateAuthoredUrl(catalogUrl, "scanner catalog URL", requireGitHubRelease: false);
        var envelopeBytes = await GetBytesAsync(catalogUrl, MaxEnvelopeBytes, allowRedirectQuery: false, cancellationToken);
        var catalog = ParseVerified(envelopeBytes);
        await WriteCacheAsync(envelopeBytes, cancellationToken);
        return catalog;
    }

    public async Task<ScannerCatalog?> ReadCachedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(CachePath)) return null;
        var info = new FileInfo(CachePath);
        if (info.Length is <= 0 or > MaxEnvelopeBytes)
            throw new InvalidDataException("Cached scanner catalog exceeds its size limit.");
        return ParseVerified(await File.ReadAllBytesAsync(CachePath, cancellationToken));
    }

    public async Task<ScannerCatalogLoad> RefreshOrCachedAsync(CancellationToken cancellationToken)
    {
        try
        {
            var catalog = await RefreshAsync(cancellationToken);
            return new(catalog, false, File.GetLastWriteTimeUtc(CachePath), null);
        }
        catch (Exception exception) when (IsRefreshFailure(exception) && !cancellationToken.IsCancellationRequested)
        {
            var cached = await ReadCachedAsync(cancellationToken);
            if (cached is null)
                throw new InvalidDataException(
                    $"Scanner catalog refresh failed and no verified cached catalog is available: {exception.Message}", exception);
            return new(cached, true, File.GetLastWriteTimeUtc(CachePath), exception.Message);
        }
    }

    public async Task<string> DownloadAsync(ScannerCatalogRelease release, CancellationToken cancellationToken)
    {
        ValidateAuthoredUrl(release.AssetUrl, "scanner package asset URL", requireGitHubRelease: true);
        Directory.CreateDirectory(store.RootDirectory);
        SetOwnerOnlyDirectory(store.RootDirectory);
        var temporary = Path.Combine(store.RootDirectory, $".download.{Guid.NewGuid():N}.tmp");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DownloadTimeout);
        try
        {
            await using var destination = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var response = await SendAsync(release.AssetUrl, allowRedirectQuery: true, timeout.Token);
            if (response.Content.Headers.ContentLength is { } contentLength && contentLength != release.CompressedBytes)
                throw new InvalidDataException("Scanner package download size does not match the signed catalog.");
            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
            await CopyBoundedAsync(source, destination, release.CompressedBytes, timeout.Token);
            await destination.FlushAsync(timeout.Token);
            if (destination.Length != release.CompressedBytes)
                throw new InvalidDataException("Scanner package download size does not match the signed catalog.");
            return temporary;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw new HttpRequestException("Download timed out.", exception);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    public void Dispose() => client.Dispose();

    private ScannerCatalog ParseVerified(byte[] envelopeBytes)
    {
        if (ArtifactSchemaValidator.Validate(envelopeBytes) != "signed-scanner-catalog/v1")
            throw new InvalidDataException("Expected a signed scanner catalog envelope.");
        using var envelopeStream = new MemoryStream(envelopeBytes, writable: false);
        var envelope = ContractJson.ReadSignedScannerCatalog(envelopeStream);
        var payload = verifier.VerifyCatalog(envelope);
        if (payload.Length > MaxEnvelopeBytes)
            throw new InvalidDataException("Verified scanner catalog payload exceeds its size limit.");
        if (ArtifactSchemaValidator.Validate(payload) != "scanner-catalog/v1")
            throw new InvalidDataException("Expected a scanner catalog payload.");
        using var payloadStream = new MemoryStream(payload, writable: false);
        var catalog = ContractJson.ReadScannerCatalog(payloadStream);
        ValidateCatalog(catalog);
        return catalog;
    }

    private static void ValidateCatalog(ScannerCatalog catalog)
    {
        if (catalog.SchemaVersion != "scanner-catalog/v1" || catalog.Releases.Count > MaxCatalogReleases)
            throw new InvalidDataException("Scanner catalog uses unsupported metadata or exceeds its release limit.");
        var releases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var release in catalog.Releases)
        {
            if (!NuGetVersion.TryParse(release.Version, out var version) ||
                !VersionRange.TryParse(release.ArchieVersionRange, out _))
                throw new InvalidDataException($"Scanner catalog release '{release.Id}' has invalid version metadata.");
            if (!releases.Add($"{release.Id}\0{version.ToNormalizedString()}\0{release.Platform}\0{release.Architecture}"))
                throw new InvalidDataException("Scanner catalog contains a duplicate semantic release.");
            if (release.ProtocolVersion != "scanner/v1" || release.Channel != "stable" ||
                !release.Permissions.ReadRepository || release.Permissions.Network || release.Permissions.Environment)
                throw new InvalidDataException($"Scanner catalog release '{release.Id}' has unsupported protocol, channel, or permissions.");
            ValidateAuthoredUrl(release.AssetUrl, "scanner package asset URL", requireGitHubRelease: true);
            ValidateAuthoredUrl(new Uri(release.SourceRepository), "scanner source repository URL", requireGitHubRelease: false);
            ValidateAuthoredUrl(release.ReleaseNotesUrl, "scanner release notes URL", requireGitHubRelease: false);
        }
    }

    private async Task<byte[]> GetBytesAsync(
        Uri url,
        int maximumBytes,
        bool allowRedirectQuery,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DownloadTimeout);
        try
        {
            using var response = await SendAsync(url, allowRedirectQuery, timeout.Token);
            if (response.Content.Headers.ContentLength is { } length && length > maximumBytes)
                throw new InvalidDataException($"Download exceeds the {maximumBytes} byte limit.");
            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var destination = new MemoryStream();
            await CopyBoundedAsync(source, destination, maximumBytes, timeout.Token);
            return destination.ToArray();
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException("Download timed out.", exception);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        Uri initial,
        bool allowRedirectQuery,
        CancellationToken cancellationToken)
    {
        var current = initial;
        for (var redirect = 0; ; redirect++)
        {
            var response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!IsRedirect(response.StatusCode))
            {
                if (!response.IsSuccessStatusCode)
                {
                    var status = (int)response.StatusCode;
                    response.Dispose();
                    throw new HttpRequestException($"Download failed with HTTP status {status}.");
                }
                return response;
            }
            if (redirect >= MaxRedirects)
            {
                response.Dispose();
                throw new HttpRequestException($"Download exceeded the {MaxRedirects} redirect limit.");
            }
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new HttpRequestException("Download redirect did not include a location.");
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
            ValidateRedirectUrl(current, allowRedirectQuery);
        }
    }

    private static async Task CopyBoundedAsync(
        Stream source,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            total += read;
            if (total > maximumBytes) throw new InvalidDataException($"Download exceeds the {maximumBytes} byte limit.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private async Task WriteCacheAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(CachePath)!;
        Directory.CreateDirectory(directory);
        SetOwnerOnlyDirectory(directory);
        var temporary = Path.Combine(directory, $".catalog.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, CachePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void ValidateAuthoredUrl(Uri url, string name, bool requireGitHubRelease)
    {
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(url.UserInfo) ||
            !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
            throw new InvalidDataException($"The {name} must be an HTTPS URL without userinfo, query, or fragment.");
        if (requireGitHubRelease &&
            (url.Host != "github.com" || !url.AbsolutePath.Contains("/releases/download/", StringComparison.Ordinal)))
            throw new InvalidDataException("Scanner package assets must use a direct GitHub Release URL.");
    }

    private static void ValidateRedirectUrl(Uri url, bool allowQuery)
    {
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(url.UserInfo) ||
            !string.IsNullOrEmpty(url.Fragment) || !allowQuery && !string.IsNullOrEmpty(url.Query))
            throw new InvalidDataException("Download redirect violates the HTTPS URL policy.");
        if (allowQuery && url.Host != "github.com" && !url.Host.EndsWith(".githubusercontent.com", StringComparison.Ordinal))
            throw new InvalidDataException("Scanner package redirect left the approved GitHub download hosts.");
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static bool IsRefreshFailure(Exception exception) => exception is
        HttpRequestException or InvalidDataException or JsonException or IOException or UnauthorizedAccessException;

    private static Uri CatalogUrl()
    {
#if DEBUG
        var configured = Environment.GetEnvironmentVariable("ARCHIE_SCANNER_CATALOG_URL");
        return string.IsNullOrWhiteSpace(configured) ? DefaultCatalogUrl : new Uri(configured, UriKind.Absolute);
#else
        return DefaultCatalogUrl;
#endif
    }

    private static void SetOwnerOnlyDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
