using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;

namespace LocaleSmith.Infrastructure.ModPlatform;

/// <summary>
/// Rechecks the complete server-authoritative gate before every private grant. Any accelerated
/// failure falls back to the existing same-origin downloader without exposing grant details.
/// </summary>
public sealed class ModPlatformArtifactDownloadCoordinator : IModPlatformArtifactDownloadCoordinator
{
    private static readonly TimeSpan GrantExpirySafetyMargin = TimeSpan.FromSeconds(45);
    private readonly IModPlatformBillingClient _client;
    private readonly IModPlatformCredentialService _credentials;
    private readonly IModPlatformArtifactDownloader _defaultDownloader;
    private readonly IModPlatformAcceleratedArtifactDownloader _acceleratedDownloader;

    public ModPlatformArtifactDownloadCoordinator(
        IModPlatformBillingClient client,
        IModPlatformCredentialService credentials,
        IModPlatformArtifactDownloader defaultDownloader,
        IModPlatformAcceleratedArtifactDownloader acceleratedDownloader)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _defaultDownloader = defaultDownloader ?? throw new ArgumentNullException(nameof(defaultDownloader));
        _acceleratedDownloader = acceleratedDownloader ?? throw new ArgumentNullException(nameof(acceleratedDownloader));
    }

    public async Task<ModPlatformAccelerationAvailability> GetAccelerationAvailabilityAsync(
        ModPlatformVersion artifact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        try
        {
            var context = await GetAuthorizedContextAsync(artifact, cancellationToken).ConfigureAwait(false);
            return context is null
                ? ModPlatformAccelerationAvailability.Unavailable("accelerated_unavailable")
                : new ModPlatformAccelerationAvailability(
                    true,
                    null,
                    context.Sources.AdditionalSource.BrowserParallelRangeEnabled == true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ModPlatformAccelerationAvailability.Unavailable(GetSafeReason(exception));
        }
    }

    public async Task<ModPlatformArtifactDownloadResult> DownloadAsync(
        ModPlatformVersion artifact,
        string destinationPath,
        ModPlatformDownloadRoute requestedRoute,
        IProgress<ModPlatformDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (requestedRoute == ModPlatformDownloadRoute.Default)
        {
            await _defaultDownloader
                .DownloadAsync(artifact, destinationPath, progress, cancellationToken)
                .ConfigureAwait(false);
            return new ModPlatformArtifactDownloadResult(
                requestedRoute,
                ModPlatformDownloadRoute.Default,
                FellBack: false,
                SafeReasonCode: null);
        }

        try
        {
            using var grant = await AcquireGrantAsync(artifact, cancellationToken).ConfigureAwait(false);
            await _acceleratedDownloader.DownloadAsync(
                artifact,
                grant,
                token => AcquireGrantAsync(artifact, token),
                destinationPath,
                progress,
                cancellationToken).ConfigureAwait(false);
            return new ModPlatformArtifactDownloadResult(
                requestedRoute,
                ModPlatformDownloadRoute.DomesticAcceleration,
                FellBack: false,
                SafeReasonCode: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsSafeFallbackFailure(exception))
        {
            TryDeleteAcceleratedState(destinationPath);
            await _defaultDownloader
                .DownloadAsync(artifact, destinationPath, progress, cancellationToken)
                .ConfigureAwait(false);
            return new ModPlatformArtifactDownloadResult(
                requestedRoute,
                ModPlatformDownloadRoute.Default,
                FellBack: true,
                SafeReasonCode: GetSafeReason(exception));
        }
    }

    private async Task<ModPlatformAcceleratedDownloadGrant> AcquireGrantAsync(
        ModPlatformVersion artifact,
        CancellationToken cancellationToken)
    {
        var context = await GetAuthorizedContextAsync(artifact, cancellationToken).ConfigureAwait(false)
            ?? throw new AcceleratedDownloadException("accelerated_unavailable");
        var grant = await _client
            .CreateAcceleratedDownloadGrantAsync(artifact.Id, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (grant.VersionId != artifact.Id
                || grant.Size != artifact.Size
                || !string.Equals(grant.Sha256, artifact.Sha256, StringComparison.Ordinal)
                || grant.ExpiresAt <= context.ServerTime + GrantExpirySafetyMargin
                || !string.Equals(
                    grant.FallbackUrl,
                    context.DefaultSource.DownloadUrl,
                    StringComparison.Ordinal))
            {
                throw new AcceleratedDownloadException("accelerated_grant_mismatch");
            }

            return grant;
        }
        catch
        {
            grant.Dispose();
            throw;
        }
    }

    private async Task<AuthorizedAccelerationContext?> GetAuthorizedContextAsync(
        ModPlatformVersion artifact,
        CancellationToken cancellationToken)
    {
        var meta = await _client.GetMetaAsync(cancellationToken).ConfigureAwait(false);
        if (!meta.Features.Contains(
                MicrosoftStoreBillingContract.Capability,
                StringComparer.Ordinal)
            || !meta.Features.Contains(
                MicrosoftStoreBillingContract.AcceleratedDownloadsCapability,
                StringComparer.Ordinal))
        {
            throw new AcceleratedDownloadException("accelerated_feature_unavailable");
        }

        if (!await _credentials.IsConfiguredAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new AcceleratedDownloadException("auth_required");
        }

        var session = await _client.GetAuthenticatedSessionAsync(cancellationToken).ConfigureAwait(false);
        if (!session.Scopes.Contains("downloads:accelerated", StringComparer.Ordinal))
        {
            throw new AcceleratedDownloadException("insufficient_scope");
        }

        var entitlements = await _client.GetEntitlementsAsync(cancellationToken).ConfigureAwait(false);
        if (!entitlements.Data.Any(entitlement => entitlement.IsUsable(entitlements.ServerTime)))
        {
            throw new AcceleratedDownloadException("entitlement_required");
        }

        var sources = await _client
            .GetDownloadSourcesAsync(artifact.Id, cancellationToken)
            .ConfigureAwait(false);
        var defaultSource = sources.Sources.SingleOrDefault(static source => source.Id == "default");
        if (sources.VersionId != artifact.Id
            || sources.Size != artifact.Size
            || !string.Equals(sources.Sha256, artifact.Sha256, StringComparison.Ordinal)
            || !string.Equals(sources.Filename, artifact.Filename, StringComparison.Ordinal)
            || defaultSource is null
            || !string.Equals(defaultSource.DownloadUrl, artifact.DownloadUrl, StringComparison.Ordinal))
        {
            throw new AcceleratedDownloadException("download_sources_mismatch");
        }

        if (!sources.IsAccelerationAvailable)
        {
            throw new AcceleratedDownloadException(
                sources.AdditionalSource.ReasonCode ?? "accelerated_source_unavailable");
        }

        return new AuthorizedAccelerationContext(
            sources,
            defaultSource,
            entitlements.ServerTime);
    }

    private static bool IsSafeFallbackFailure(Exception exception) => exception is
        AcceleratedDownloadException
        or ModPlatformException
        or HttpRequestException
        or InvalidOperationException;

    private static string GetSafeReason(Exception exception) => exception switch
    {
        AcceleratedDownloadException accelerated => accelerated.SafeCode,
        IModPlatformServiceError service => service.Code,
        HttpRequestException => "accelerated_network_error",
        InvalidOperationException => "accelerated_unavailable",
        _ => "accelerated_unavailable"
    };

    private static void TryDeleteAcceleratedState(string destinationPath)
    {
        try
        {
            var destination = Path.GetFullPath(destinationPath);
            File.Delete(ModPlatformAcceleratedArtifactDownloader.GetPartialPath(destination));
            File.Delete(ModPlatformAcceleratedArtifactDownloader.GetMetadataPath(destination));
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            // The sidecar contains no URL or credential. A later attempt can safely replace it.
        }
    }

    private sealed record AuthorizedAccelerationContext(
        ModPlatformDownloadSources Sources,
        ModPlatformDownloadSource DefaultSource,
        DateTimeOffset ServerTime);
}
