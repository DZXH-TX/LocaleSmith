using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;

namespace LocaleSmith.Infrastructure.ModPlatform;

/// <summary>
/// Downloads immutable Mod artifacts with HTTP Range/If-Range resume and verifies the complete SHA-256 before publish.
/// </summary>
public sealed partial class ModPlatformArtifactDownloader : IModPlatformArtifactDownloader, IDisposable
{
    private static readonly JsonSerializerOptions MetadataJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    public ModPlatformArtifactDownloader(HttpClient httpClient, Uri baseUri)
        : this(httpClient, baseUri, ownsHttpClient: false)
    {
    }

    private ModPlatformArtifactDownloader(
        HttpClient httpClient,
        Uri baseUri,
        bool ownsHttpClient,
        bool allowLoopbackHttp = false)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _baseUri = ValidateBaseUri(baseUri, allowLoopbackHttp);
        _ownsHttpClient = ownsHttpClient;
    }

    public static ModPlatformArtifactDownloader CreateProduction() =>
        new(
            ModPlatformHttpClientFactory.Create(TimeSpan.FromMinutes(30)),
            ModPlatformClient.ProductionBaseUri,
            ownsHttpClient: true);

    public static ModPlatformArtifactDownloader CreateForApplication()
    {
        var baseUri = ModPlatformEndpointPolicy.ResolveApplicationBaseUri();
        return new ModPlatformArtifactDownloader(
            ModPlatformHttpClientFactory.Create(TimeSpan.FromMinutes(30)),
            baseUri,
            ownsHttpClient: true,
            allowLoopbackHttp: baseUri.IsLoopback);
    }

    public async Task DownloadAsync(
        ModPlatformVersion artifact,
        string destinationPath,
        IProgress<ModPlatformDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (artifact.Size < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(artifact), "Artifact size cannot be negative.");
        }

        var expectedSha256 = artifact.Sha256.Trim().ToLowerInvariant();
        if (!Sha256Pattern().IsMatch(expectedSha256))
        {
            throw new ArgumentException("Artifact SHA-256 must contain exactly 64 hexadecimal characters.", nameof(artifact));
        }

        var destination = Path.GetFullPath(destinationPath);
        var parent = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("The destination path has no parent directory.", nameof(destinationPath));
        Directory.CreateDirectory(parent);
        if (Directory.Exists(destination))
        {
            throw new IOException("The artifact destination points to a directory.");
        }

        var downloadUri = ResolveDownloadUri(artifact.DownloadUrl);

        if (File.Exists(destination)
            && new FileInfo(destination).Length == artifact.Size
            && await HasExpectedHashAsync(destination, expectedSha256, cancellationToken).ConfigureAwait(false))
        {
            progress?.Report(new ModPlatformDownloadProgress(artifact.Size, artifact.Size));
            return;
        }

        var partialPath = GetPartialPath(destination);
        var metadataPath = GetMetadataPath(destination);
        var entityTag = await GetStrongEntityTagAsync(
            downloadUri,
            artifact.Size,
            cancellationToken).ConfigureAwait(false);
        await PreparePartialAsync(
            partialPath,
            metadataPath,
            new DownloadMetadata(expectedSha256, artifact.Size, entityTag),
            cancellationToken).ConfigureAwait(false);

        var existingLength = new FileInfo(partialPath).Length;
        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        request.Headers.AcceptEncoding.Clear();
        request.Headers.UserAgent.ParseAdd("LocaleSmith/1.0");
        if (existingLength > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingLength, null);
            request.Headers.IfRange = new RangeConditionHeaderValue(entityTag);
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        EnsureSameOrigin(response.RequestMessage?.RequestUri ?? request.RequestUri);
        ModPlatformApiContract.ValidateVersionHeader(response, request.RequestUri);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable
            && existingLength == artifact.Size)
        {
            await FinalizeAsync(
                partialPath,
                metadataPath,
                destination,
                artifact.Size,
                expectedSha256,
                progress,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (response.StatusCode is not HttpStatusCode.OK and not HttpStatusCode.PartialContent)
        {
            throw new ModPlatformException(
                response.StatusCode,
                $"http_{(int)response.StatusCode}",
                $"Artifact download failed with HTTP {(int)response.StatusCode}.");
        }

        ValidateResponseEtag(response, entityTag);
        var append = response.StatusCode == HttpStatusCode.PartialContent;
        if (append)
        {
            ValidateContentRange(response, existingLength, artifact.Size);
        }
        else
        {
            if (response.Content.Headers.ContentLength != artifact.Size)
            {
                throw new InvalidDataException("The artifact response length does not match its declared size.");
            }

            existingLength = 0;
        }

        await using (var output = new FileStream(
            partialPath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (!append)
            {
                output.SetLength(0);
            }

            output.Position = existingLength;
            progress?.Report(new ModPlatformDownloadProgress(existingLength, artifact.Size));
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[128 * 1024];
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (output.Position + read > artifact.Size)
                {
                    throw new InvalidDataException("The Mod platform returned more bytes than the declared artifact size.");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                progress?.Report(new ModPlatformDownloadProgress(output.Position, artifact.Size));
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        await FinalizeAsync(
            partialPath,
            metadataPath,
            destination,
            artifact.Size,
            expectedSha256,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        _disposed = true;
    }

    internal static string GetPartialPath(string destinationPath) => destinationPath + ".mctx.partial";

    internal static string GetMetadataPath(string destinationPath) => destinationPath + ".mctx.partial.json";

    private static async Task FinalizeAsync(
        string partialPath,
        string metadataPath,
        string destination,
        long expectedSize,
        string expectedSha256,
        IProgress<ModPlatformDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var actualSize = new FileInfo(partialPath).Length;
        if (actualSize != expectedSize)
        {
            throw new EndOfStreamException(
                $"Artifact download stopped at {actualSize} of {expectedSize} bytes and can be resumed.");
        }

        if (!await HasExpectedHashAsync(partialPath, expectedSha256, cancellationToken).ConfigureAwait(false))
        {
            File.Delete(partialPath);
            File.Delete(metadataPath);
            throw new InvalidDataException("The downloaded artifact failed SHA-256 verification and was discarded.");
        }

        File.Move(partialPath, destination, overwrite: true);
        File.Delete(metadataPath);
        progress?.Report(new ModPlatformDownloadProgress(expectedSize, expectedSize));
    }

    private static async Task PreparePartialAsync(
        string partialPath,
        string metadataPath,
        DownloadMetadata expected,
        CancellationToken cancellationToken)
    {
        var reuse = false;
        if (File.Exists(partialPath) && File.Exists(metadataPath))
        {
            try
            {
                await using var metadataStream = File.OpenRead(metadataPath);
                var existing = await JsonSerializer.DeserializeAsync<DownloadMetadata>(
                    metadataStream,
                    MetadataJsonOptions,
                    cancellationToken).ConfigureAwait(false);
                reuse = existing == expected && new FileInfo(partialPath).Length <= expected.Size;
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
            {
                reuse = false;
            }
        }

        if (!reuse)
        {
            await using (var reset = new FileStream(
                partialPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous))
            {
                await reset.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var metadataStream = new FileStream(
                metadataPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous);
            await JsonSerializer.SerializeAsync(
                metadataStream,
                expected,
                MetadataJsonOptions,
                cancellationToken).ConfigureAwait(false);
            await metadataStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> HasExpectedHashAsync(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(Convert.ToHexStringLower(hash), expectedSha256, StringComparison.Ordinal);
    }

    private Uri ResolveDownloadUri(string downloadUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadUrl);
        if (Uri.TryCreate(downloadUrl, UriKind.Absolute, out _)
            || downloadUrl.StartsWith("//", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Artifact download URLs must be same-origin relative paths.");
        }

        var resolved = new Uri(_baseUri, downloadUrl.TrimStart('/'));
        EnsureSameOrigin(resolved);
        return resolved;
    }

    private async Task<string> GetStrongEntityTagAsync(
        Uri downloadUri,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, downloadUri);
        request.Headers.AcceptEncoding.Clear();
        request.Headers.UserAgent.ParseAdd("LocaleSmith/1.0");
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        EnsureSameOrigin(response.RequestMessage?.RequestUri ?? request.RequestUri);
        ModPlatformApiContract.ValidateVersionHeader(response, request.RequestUri);
        if (!response.IsSuccessStatusCode)
        {
            throw new ModPlatformException(
                response.StatusCode,
                $"http_{(int)response.StatusCode}",
                $"Artifact metadata request failed with HTTP {(int)response.StatusCode}.");
        }

        if (response.Content.Headers.ContentLength != expectedSize)
        {
            throw new InvalidDataException("The artifact metadata length does not match its declared size.");
        }

        var responseTag = response.Headers.ETag;
        if (responseTag is null || responseTag.IsWeak || string.IsNullOrWhiteSpace(responseTag.Tag))
        {
            throw new InvalidDataException("The Mod platform did not return a strong artifact ETag.");
        }

        return responseTag.Tag;
    }

    private void EnsureSameOrigin(Uri? uri)
    {
        if (uri is null
            || !string.Equals(uri.Scheme, _baseUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, _baseUri.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != _baseUri.Port)
        {
            throw new InvalidOperationException("The artifact request left the configured Mod platform origin.");
        }
    }

    private static void ValidateResponseEtag(HttpResponseMessage response, string expectedEntityTag)
    {
        var responseTag = response.Headers.ETag;
        if (responseTag is null
            || responseTag.IsWeak
            || !string.Equals(responseTag.Tag, expectedEntityTag, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The artifact ETag changed during download.");
        }
    }

    private static void ValidateContentRange(HttpResponseMessage response, long expectedStart, long expectedSize)
    {
        var range = response.Content.Headers.ContentRange;
        if (range?.From != expectedStart
            || range.To != expectedSize - 1
            || range.Length != expectedSize
            || response.Content.Headers.ContentLength != expectedSize - expectedStart)
        {
            throw new InvalidDataException("The Mod platform returned an invalid Content-Range response.");
        }
    }

    private static Uri ValidateBaseUri(Uri baseUri, bool allowLoopbackHttp = false)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri
            || (!string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !(allowLoopbackHttp
                    && baseUri.IsLoopback
                    && string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
            || !string.IsNullOrEmpty(baseUri.UserInfo)
            || !string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment))
        {
            throw new ArgumentException("The Mod platform base URI must be an absolute HTTPS origin.", nameof(baseUri));
        }

        return new Uri(baseUri.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    private sealed record DownloadMetadata(
        string Sha256,
        long Size,
        [property: JsonPropertyName("etag")] string ETag);
}
