using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;

namespace LocaleSmith.Infrastructure.ModPlatform;

/// <summary>
/// Downloads a server-authorized private replica without ever attaching MCTX credentials to the
/// object-storage origin. Signed GET/HEAD URLs remain in memory and are never written to metadata.
/// </summary>
public sealed class ModPlatformAcceleratedArtifactDownloader :
    IModPlatformAcceleratedArtifactDownloader,
    IDisposable
{
    private const int MaximumParallelRanges = 4;
    private const int MaximumGrantRefreshes = 3;
    private static readonly TimeSpan ExpirySafetyMargin = TimeSpan.FromSeconds(45);
    private static readonly JsonSerializerOptions MetadataJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    public ModPlatformAcceleratedArtifactDownloader(
        HttpClient httpClient,
        TimeProvider? timeProvider = null)
        : this(httpClient, timeProvider ?? TimeProvider.System, ownsHttpClient: false)
    {
    }

    private ModPlatformAcceleratedArtifactDownloader(
        HttpClient httpClient,
        TimeProvider timeProvider,
        bool ownsHttpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _ownsHttpClient = ownsHttpClient;
    }

    public static ModPlatformAcceleratedArtifactDownloader CreateForApplication() =>
        new(AcceleratedDownloadHttpClientFactory.Create(), TimeProvider.System, ownsHttpClient: true);

    public async Task DownloadAsync(
        ModPlatformVersion artifact,
        ModPlatformAcceleratedDownloadGrant initialGrant,
        Func<CancellationToken, Task<ModPlatformAcceleratedDownloadGrant>> renewGrantAsync,
        string destinationPath,
        IProgress<ModPlatformDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(initialGrant);
        ArgumentNullException.ThrowIfNull(renewGrantAsync);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ValidateArtifact(artifact);

        var destination = Path.GetFullPath(destinationPath);
        var parent = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("The destination path has no parent directory.", nameof(destinationPath));
        Directory.CreateDirectory(parent);
        if (Directory.Exists(destination))
        {
            throw new IOException("The artifact destination points to a directory.");
        }

        if (File.Exists(destination)
            && new FileInfo(destination).Length == artifact.Size
            && await HasExpectedHashAsync(destination, artifact.Sha256, cancellationToken).ConfigureAwait(false))
        {
            initialGrant.Dispose();
            progress?.Report(new ModPlatformDownloadProgress(artifact.Size, artifact.Size));
            return;
        }

        var partialPath = GetPartialPath(destination);
        var metadataPath = GetMetadataPath(destination);
        var currentGrant = initialGrant;
        try
        {
            ValidateGrant(artifact, currentGrant);
            var entityTag = await GetStrongEntityTagAsync(
                currentGrant,
                artifact.Size,
                cancellationToken).ConfigureAwait(false);
            var ranges = await PreparePartialAsync(
                partialPath,
                metadataPath,
                artifact,
                entityTag,
                currentGrant.BrowserParallelRangeEnabled,
                cancellationToken).ConfigureAwait(false);

            var refreshes = 0;
            var zeroProgressRounds = 0;
            while (ranges.Any(static range => !range.IsComplete))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsNearExpiry(currentGrant))
                {
                    if (refreshes >= MaximumGrantRefreshes)
                    {
                        throw new AcceleratedDownloadException("accelerated_grant_expired");
                    }

                    currentGrant.Dispose();
                    currentGrant = await RenewGrantAsync(
                        artifact,
                        renewGrantAsync,
                        entityTag,
                        cancellationToken).ConfigureAwait(false);
                    refreshes++;
                }

                var before = ranges.Sum(static range => range.Completed);
                var failures = await DownloadGenerationAsync(
                    currentGrant,
                    entityTag,
                    partialPath,
                    artifact.Size,
                    ranges,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                await SaveMetadataAsync(
                    metadataPath,
                    artifact,
                    entityTag,
                    ranges,
                    cancellationToken).ConfigureAwait(false);
                if (failures.IsEmpty)
                {
                    continue;
                }

                var unexpectedFailure = failures.FirstOrDefault(static failure =>
                    failure is not AcceleratedDownloadException);
                if (unexpectedFailure is not null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo
                        .Capture(unexpectedFailure)
                        .Throw();
                }

                var protocolFailure = failures
                    .OfType<AcceleratedDownloadException>()
                    .FirstOrDefault(static failure => !IsRetryable(failure.SafeCode));
                if (protocolFailure is not null)
                {
                    throw protocolFailure;
                }

                zeroProgressRounds = ranges.Sum(static range => range.Completed) == before
                    ? zeroProgressRounds + 1
                    : 0;
                if (refreshes >= MaximumGrantRefreshes || zeroProgressRounds >= 2)
                {
                    throw new AcceleratedDownloadException("accelerated_storage_unavailable");
                }

                currentGrant.Dispose();
                currentGrant = await RenewGrantAsync(
                    artifact,
                    renewGrantAsync,
                    entityTag,
                    cancellationToken).ConfigureAwait(false);
                refreshes++;
            }

            await FinalizeAsync(
                partialPath,
                metadataPath,
                destination,
                artifact,
                progress,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            currentGrant.Dispose();
        }
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

    internal static string GetPartialPath(string destinationPath) =>
        destinationPath + ".mctx.accelerated.partial";

    internal static string GetMetadataPath(string destinationPath) =>
        destinationPath + ".mctx.accelerated.partial.json";

    private async Task<ModPlatformAcceleratedDownloadGrant> RenewGrantAsync(
        ModPlatformVersion artifact,
        Func<CancellationToken, Task<ModPlatformAcceleratedDownloadGrant>> renewGrantAsync,
        string expectedEntityTag,
        CancellationToken cancellationToken)
    {
        var grant = await renewGrantAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateGrant(artifact, grant);
            var actualEntityTag = await GetStrongEntityTagAsync(
                grant,
                artifact.Size,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualEntityTag, expectedEntityTag, StringComparison.Ordinal))
            {
                throw new AcceleratedDownloadException("accelerated_etag_changed");
            }

            return grant;
        }
        catch
        {
            grant.Dispose();
            throw;
        }
    }

    private async Task<ConcurrentQueue<Exception>> DownloadGenerationAsync(
        ModPlatformAcceleratedDownloadGrant grant,
        string entityTag,
        string partialPath,
        long totalSize,
        IReadOnlyList<RangeState> ranges,
        IProgress<ModPlatformDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var failures = new ConcurrentQueue<Exception>();
        var tasks = ranges
            .Where(static range => !range.IsComplete)
            .Select(async range =>
            {
                try
                {
                    await DownloadRangeAsync(
                        grant,
                        entityTag,
                        partialPath,
                        range,
                        cancellationToken).ConfigureAwait(false);
                    progress?.Report(new ModPlatformDownloadProgress(
                        ranges.Sum(static item => item.Completed),
                        totalSize));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failures.Enqueue(exception);
                }
            })
            .ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return failures;
    }

    private async Task DownloadRangeAsync(
        ModPlatformAcceleratedDownloadGrant grant,
        string entityTag,
        string partialPath,
        RangeState range,
        CancellationToken cancellationToken)
    {
        if (IsNearExpiry(grant))
        {
            throw new AcceleratedDownloadException("accelerated_grant_expiring");
        }

        var start = checked(range.Start + range.Completed);
        if (start > range.End)
        {
            return;
        }

        var getUrl = grant.DangerousGetUrl();
        var requestUri = ValidateSignedUri(getUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.AcceptEncoding.Clear();
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        request.Headers.Range = new RangeHeaderValue(start, range.End);
        request.Headers.IfRange = new RangeConditionHeaderValue(entityTag);
        EnsureCredentialFree(request);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AcceleratedDownloadException("accelerated_storage_timeout");
        }
        catch (HttpRequestException)
        {
            throw new AcceleratedDownloadException("accelerated_storage_unavailable");
        }

        using (response)
        {
            EnsureExactOrigin(
                requestUri,
                response.RequestMessage?.RequestUri ?? request.RequestUri);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new AcceleratedDownloadException("accelerated_grant_expired");
            }

            if (response.StatusCode != HttpStatusCode.PartialContent)
            {
                throw new AcceleratedDownloadException("accelerated_range_rejected");
            }

            ValidateRangeResponse(response, entityTag, start, range.End);
            await using var input = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var output = new FileStream(
                partialPath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            output.Position = start;
            var remaining = checked(range.End - start + 1);
            var buffer = new byte[128 * 1024];
            while (remaining > 0)
            {
                var read = await input.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new AcceleratedDownloadException("accelerated_range_truncated");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
                range.AddCompleted(read);
            }

            if (await input.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0)
            {
                throw new AcceleratedDownloadException("accelerated_range_overlong");
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> GetStrongEntityTagAsync(
        ModPlatformAcceleratedDownloadGrant grant,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        if (IsNearExpiry(grant))
        {
            throw new AcceleratedDownloadException("accelerated_grant_expiring");
        }

        var headUrl = grant.DangerousGetHeadUrl();
        var requestUri = ValidateSignedUri(headUrl);
        using var request = new HttpRequestMessage(HttpMethod.Head, requestUri);
        request.Headers.AcceptEncoding.Clear();
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        EnsureCredentialFree(request);
        using var response = await SendStorageAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureExactOrigin(
            requestUri,
            response.RequestMessage?.RequestUri ?? request.RequestUri);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new AcceleratedDownloadException("accelerated_grant_expired");
        }

        if (response.StatusCode != HttpStatusCode.OK
            || response.Content.Headers.ContentLength != expectedSize
            || response.Headers.ETag is not { IsWeak: false, Tag: { Length: > 0 } tag })
        {
            throw new AcceleratedDownloadException("accelerated_head_invalid");
        }

        return tag;
    }

    private async Task<HttpResponseMessage> SendStorageAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AcceleratedDownloadException("accelerated_storage_timeout");
        }
        catch (HttpRequestException)
        {
            throw new AcceleratedDownloadException("accelerated_storage_unavailable");
        }
    }

    private static async Task<List<RangeState>> PreparePartialAsync(
        string partialPath,
        string metadataPath,
        ModPlatformVersion artifact,
        string entityTag,
        bool parallelRanges,
        CancellationToken cancellationToken)
    {
        List<RangeState>? ranges = null;
        if (File.Exists(partialPath) && File.Exists(metadataPath))
        {
            try
            {
                await using var input = File.OpenRead(metadataPath);
                var metadata = await JsonSerializer.DeserializeAsync<AcceleratedMetadata>(
                    input,
                    MetadataJsonOptions,
                    cancellationToken).ConfigureAwait(false);
                if (metadata is not null
                    && metadata.VersionId == artifact.Id
                    && metadata.Size == artifact.Size
                    && metadata.Sha256 == artifact.Sha256
                    && metadata.ETag == entityTag
                    && new FileInfo(partialPath).Length == artifact.Size)
                {
                    ranges = RestoreRanges(metadata.Ranges, artifact.Size);
                }
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                ranges = null;
            }
        }

        if (ranges is null)
        {
            var count = parallelRanges ? MaximumParallelRanges : 1;
            ranges = CreateRanges(artifact.Size, count);
            await using (var output = new FileStream(
                partialPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite,
                4096,
                FileOptions.Asynchronous | FileOptions.RandomAccess))
            {
                output.SetLength(artifact.Size);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await SaveMetadataAsync(
                metadataPath,
                artifact,
                entityTag,
                ranges,
                cancellationToken).ConfigureAwait(false);
        }

        return ranges;
    }

    private static List<RangeState> CreateRanges(long size, int requestedCount)
    {
        var count = (int)Math.Min(Math.Max(1, requestedCount), size);
        var ranges = new List<RangeState>(count);
        var baseLength = size / count;
        var remainder = size % count;
        var start = 0L;
        for (var index = 0; index < count; index++)
        {
            var length = baseLength + (index < remainder ? 1 : 0);
            var end = checked(start + length - 1);
            ranges.Add(new RangeState(start, end, 0));
            start = checked(end + 1);
        }

        return ranges;
    }

    private static List<RangeState>? RestoreRanges(
        IReadOnlyList<RangeCheckpoint>? checkpoints,
        long expectedSize)
    {
        if (checkpoints is not { Count: > 0 and <= MaximumParallelRanges })
        {
            return null;
        }

        var ranges = new List<RangeState>(checkpoints.Count);
        var nextStart = 0L;
        foreach (var checkpoint in checkpoints)
        {
            var length = checkpoint.End - checkpoint.Start + 1;
            if (checkpoint.Start != nextStart
                || checkpoint.End < checkpoint.Start
                || checkpoint.Completed < 0
                || checkpoint.Completed > length)
            {
                return null;
            }

            ranges.Add(new RangeState(checkpoint.Start, checkpoint.End, checkpoint.Completed));
            nextStart = checkpoint.End + 1;
        }

        return nextStart == expectedSize ? ranges : null;
    }

    private static async Task SaveMetadataAsync(
        string metadataPath,
        ModPlatformVersion artifact,
        string entityTag,
        IReadOnlyList<RangeState> ranges,
        CancellationToken cancellationToken)
    {
        var metadata = new AcceleratedMetadata(
            artifact.Id,
            artifact.Size,
            artifact.Sha256,
            entityTag,
            ranges.Select(static range => new RangeCheckpoint(
                range.Start,
                range.End,
                range.Completed)).ToArray());
        var temporaryPath = metadataPath + ".tmp";
        await using (var output = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(
                output,
                metadata,
                MetadataJsonOptions,
                cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, metadataPath, overwrite: true);
    }

    private static async Task FinalizeAsync(
        string partialPath,
        string metadataPath,
        string destination,
        ModPlatformVersion artifact,
        IProgress<ModPlatformDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(partialPath).Length != artifact.Size
            || !await HasExpectedHashAsync(partialPath, artifact.Sha256, cancellationToken).ConfigureAwait(false))
        {
            File.Delete(partialPath);
            File.Delete(metadataPath);
            throw new AcceleratedDownloadException("accelerated_integrity_failed");
        }

        File.Move(partialPath, destination, overwrite: true);
        File.Delete(metadataPath);
        progress?.Report(new ModPlatformDownloadProgress(artifact.Size, artifact.Size));
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

    private bool IsNearExpiry(ModPlatformAcceleratedDownloadGrant grant) =>
        grant.ExpiresAt <= _timeProvider.GetUtcNow() + ExpirySafetyMargin;

    private static Uri ValidateSignedUri(string value)
    {
        if (value.Length > 32768
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment)
            || string.IsNullOrEmpty(uri.Query))
        {
            throw new AcceleratedDownloadException("accelerated_grant_invalid");
        }

        return uri;
    }

    private static void EnsureExactOrigin(Uri expected, Uri? actual)
    {
        if (actual is null
            || !string.Equals(expected.Scheme, actual.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.Host, actual.Host, StringComparison.OrdinalIgnoreCase)
            || expected.Port != actual.Port)
        {
            throw new AcceleratedDownloadException("accelerated_origin_changed");
        }
    }

    private static void EnsureCredentialFree(HttpRequestMessage request)
    {
        if (request.Headers.Authorization is not null
            || request.Headers.Contains("Cookie")
            || request.Headers.Contains("Proxy-Authorization")
            || request.Headers.Referrer is not null)
        {
            throw new AcceleratedDownloadException("accelerated_request_not_credential_free");
        }
    }

    private static void ValidateRangeResponse(
        HttpResponseMessage response,
        string expectedEntityTag,
        long expectedStart,
        long expectedEnd)
    {
        if (response.Headers.ETag is not { IsWeak: false } entityTag
            || !string.Equals(entityTag.Tag, expectedEntityTag, StringComparison.Ordinal)
            || response.Content.Headers.ContentRange is not { } range
            || range.From != expectedStart
            || range.To != expectedEnd
            || response.Content.Headers.ContentLength != expectedEnd - expectedStart + 1)
        {
            throw new AcceleratedDownloadException("accelerated_range_invalid");
        }
    }

    private static void ValidateArtifact(ModPlatformVersion artifact)
    {
        if (artifact.Id == Guid.Empty
            || artifact.Size <= 0
            || artifact.Sha256.Length != 64
            || artifact.Sha256.Any(static character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException("The artifact identity, size, or SHA-256 is invalid.", nameof(artifact));
        }
    }

    private static bool IsRetryable(string safeCode) => safeCode is
        "accelerated_grant_expired"
        or "accelerated_grant_expiring"
        or "accelerated_storage_timeout"
        or "accelerated_storage_unavailable"
        or "accelerated_range_truncated";

    private static void ValidateGrant(
        ModPlatformVersion artifact,
        ModPlatformAcceleratedDownloadGrant grant)
    {
        if (grant.GrantId == Guid.Empty
            || grant.VersionId != artifact.Id
            || grant.Size != artifact.Size
            || !string.Equals(grant.Sha256, artifact.Sha256, StringComparison.Ordinal)
            || !grant.SupportsRange)
        {
            throw new AcceleratedDownloadException("accelerated_grant_mismatch");
        }

        var getUri = ValidateSignedUri(grant.DangerousGetUrl());
        var headUri = ValidateSignedUri(grant.DangerousGetHeadUrl());
        if (getUri == headUri)
        {
            throw new AcceleratedDownloadException("accelerated_grant_invalid");
        }

        EnsureExactOrigin(getUri, headUri);
    }

    private sealed class RangeState(long start, long end, long completed)
    {
        private long _completed = completed;

        internal long Start { get; } = start;

        internal long End { get; } = end;

        internal long Completed => Interlocked.Read(ref _completed);

        internal bool IsComplete => Completed == End - Start + 1;

        internal void AddCompleted(int count) => Interlocked.Add(ref _completed, count);
    }

    private sealed record AcceleratedMetadata(
        [property: JsonPropertyName("version_id")] Guid VersionId,
        long Size,
        string Sha256,
        [property: JsonPropertyName("etag")] string ETag,
        IReadOnlyList<RangeCheckpoint> Ranges);

    private sealed record RangeCheckpoint(long Start, long End, long Completed);
}

internal sealed class AcceleratedDownloadException(string safeCode) : Exception
{
    internal string SafeCode { get; } = safeCode;
}
