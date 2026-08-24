using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using LocaleSmith.Core.Models;
using LocaleSmith.Infrastructure.ModPlatform;

namespace LocaleSmith.Infrastructure.Tests;

public sealed class ModPlatformAcceleratedArtifactDownloaderTests
{
    private const string EntityTag = "\"private-replica-v1\"";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse(
        "2026-08-24T12:00:00Z",
        System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task UsesSeparateHeadAndCredentialFreeFourWayRangeGets()
    {
        var payload = Enumerable.Range(0, 4096).Select(static value => (byte)(value % 251)).ToArray();
        var artifact = CreateArtifact(payload);
        var activeGets = 0;
        var peakGets = 0;
        var headCalls = 0;
        var getRanges = new List<(long From, long To)>();
        var rangeLock = new object();
        var allGetsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHandler(async (request, cancellationToken) =>
        {
            AssertCredentialFree(request);
            Assert.Equal("storage.example", request.RequestUri?.Host);
            if (request.Method == HttpMethod.Head)
            {
                Interlocked.Increment(ref headCalls);
                return HeadResponse(payload.Length);
            }

            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(EntityTag, request.Headers.IfRange?.EntityTag?.Tag);
            var range = Assert.Single(request.Headers.Range!.Ranges);
            var from = Assert.IsType<long>(range.From);
            var to = Assert.IsType<long>(range.To);
            lock (rangeLock)
            {
                getRanges.Add((from, to));
            }

            var active = Interlocked.Increment(ref activeGets);
            UpdateMaximum(ref peakGets, active);
            if (active == 4)
            {
                allGetsStarted.TrySetResult();
            }

            await allGetsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Interlocked.Decrement(ref activeGets);
            return RangeResponse(payload, from, to);
        });
        using var httpClient = new HttpClient(handler);
        using var downloader = new ModPlatformAcceleratedArtifactDownloader(
            httpClient,
            new FixedTimeProvider(Now));
        using var temporary = new TemporaryDirectory();
        var destination = Path.Combine(temporary.Path, artifact.Filename);

        await downloader.DownloadAsync(
            artifact,
            CreateGrant(artifact, parallel: true),
            _ => throw new InvalidOperationException("Renewal was not expected."),
            destination,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, headCalls);
        Assert.Equal(4, peakGets);
        Assert.Equal(
            [(0L, 1023L), (1024L, 2047L), (2048L, 3071L), (3072L, 4095L)],
            getRanges.OrderBy(static range => range.From));
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(ModPlatformAcceleratedArtifactDownloader.GetMetadataPath(destination)));
    }

    [Fact]
    public async Task ExpiredRangeGrantIsRenewedAndResumesOnlyMissingRange()
    {
        var payload = Enumerable.Range(0, 1024).Select(static value => (byte)(value % 239)).ToArray();
        var artifact = CreateArtifact(payload);
        var firstRangeRejected = 0;
        var zeroRangeRequests = 0;
        using var handler = new StubHandler((request, _) =>
        {
            AssertCredentialFree(request);
            if (request.Method == HttpMethod.Head)
            {
                return Task.FromResult(HeadResponse(payload.Length));
            }

            var range = Assert.Single(request.Headers.Range!.Ranges);
            var from = Assert.IsType<long>(range.From);
            var to = Assert.IsType<long>(range.To);
            if (from == 0)
            {
                Interlocked.Increment(ref zeroRangeRequests);
                if (Interlocked.Exchange(ref firstRangeRejected, 1) == 0)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
                }
            }

            return Task.FromResult(RangeResponse(payload, from, to));
        });
        using var httpClient = new HttpClient(handler);
        using var downloader = new ModPlatformAcceleratedArtifactDownloader(
            httpClient,
            new FixedTimeProvider(Now));
        using var temporary = new TemporaryDirectory();
        var destination = Path.Combine(temporary.Path, artifact.Filename);
        var renewals = 0;

        await downloader.DownloadAsync(
            artifact,
            CreateGrant(artifact, parallel: true),
            _ =>
            {
                renewals++;
                return Task.FromResult(CreateGrant(artifact, parallel: true));
            },
            destination,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, renewals);
        Assert.Equal(2, zeroRangeRequests);
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FailedGrantMetadataNeverPersistsUrlOriginOrSignature()
    {
        var payload = Enumerable.Range(0, 128).Select(static value => (byte)value).ToArray();
        var artifact = CreateArtifact(payload);
        using var handler = new StubHandler((request, _) => Task.FromResult(
            request.Method == HttpMethod.Head
                ? HeadResponse(payload.Length)
                : new HttpResponseMessage(HttpStatusCode.Forbidden)));
        using var httpClient = new HttpClient(handler);
        using var downloader = new ModPlatformAcceleratedArtifactDownloader(
            httpClient,
            new FixedTimeProvider(Now));
        using var temporary = new TemporaryDirectory();
        var destination = Path.Combine(temporary.Path, artifact.Filename);

        await Assert.ThrowsAsync<AcceleratedDownloadException>(
            () => downloader.DownloadAsync(
                artifact,
                CreateGrant(artifact, parallel: true),
                _ => throw new AcceleratedDownloadException("entitlement_expired"),
                destination,
                cancellationToken: TestContext.Current.CancellationToken));

        var metadataPath = ModPlatformAcceleratedArtifactDownloader.GetMetadataPath(destination);
        var metadata = await File.ReadAllTextAsync(metadataPath, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("storage.example", metadata, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("signature", metadata, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("get_url", metadata, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("head_url", metadata, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MismatchedSignedOriginsAreRejectedBeforeNetworkAccess()
    {
        var payload = "payload"u8.ToArray();
        var artifact = CreateArtifact(payload);
        var calls = 0;
        using var handler = new StubHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var httpClient = new HttpClient(handler);
        using var downloader = new ModPlatformAcceleratedArtifactDownloader(
            httpClient,
            new FixedTimeProvider(Now));
        using var temporary = new TemporaryDirectory();
        var grant = new ModPlatformAcceleratedDownloadGrant(
            Guid.NewGuid(),
            artifact.Id,
            "https://storage.example/demo.jar?signature=get".AsSpan(),
            "https://attacker.example/demo.jar?signature=head".AsSpan(),
            Now.AddMinutes(10),
            artifact.DownloadUrl,
            artifact.Size,
            artifact.Sha256,
            true,
            true);

        await Assert.ThrowsAsync<AcceleratedDownloadException>(
            () => downloader.DownloadAsync(
                artifact,
                grant,
                _ => throw new InvalidOperationException(),
                Path.Combine(temporary.Path, artifact.Filename),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, calls);
    }

    private static ModPlatformVersion CreateArtifact(byte[] payload)
    {
        var versionId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        return new ModPlatformVersion(
            versionId,
            "1.0.0",
            ["1.21.1"],
            ["fabric"],
            string.Empty,
            "demo.jar",
            payload.Length,
            Convert.ToHexStringLower(SHA256.HashData(payload)),
            0,
            Now,
            $"/api/v1/files/{versionId:D}/download");
    }

    private static ModPlatformAcceleratedDownloadGrant CreateGrant(
        ModPlatformVersion artifact,
        bool parallel) => new(
            Guid.NewGuid(),
            artifact.Id,
            "https://storage.example/private/demo.jar?method=get&signature=secret".AsSpan(),
            "https://storage.example/private/demo.jar?method=head&signature=secret".AsSpan(),
            Now.AddMinutes(10),
            artifact.DownloadUrl,
            artifact.Size,
            artifact.Sha256,
            true,
            parallel);

    private static HttpResponseMessage HeadResponse(long length)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([])
        };
        response.Headers.ETag = new EntityTagHeaderValue(EntityTag);
        response.Content.Headers.ContentLength = length;
        return response;
    }

    private static HttpResponseMessage RangeResponse(byte[] payload, long from, long to)
    {
        var length = checked((int)(to - from + 1));
        var content = payload.AsSpan(checked((int)from), length).ToArray();
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(content)
        };
        response.Headers.ETag = new EntityTagHeaderValue(EntityTag);
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, payload.Length);
        return response;
    }

    private static void AssertCredentialFree(HttpRequestMessage request)
    {
        Assert.Null(request.Headers.Authorization);
        Assert.False(request.Headers.Contains("Cookie"));
        Assert.False(request.Headers.Contains("Proxy-Authorization"));
        Assert.Null(request.Headers.Referrer);
        Assert.Equal("identity", Assert.Single(request.Headers.AcceptEncoding).Value);
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (value <= current || Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => callback(request, cancellationToken);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("localesmith-accelerated-tests-").FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
