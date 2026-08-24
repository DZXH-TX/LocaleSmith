using LocaleSmith.Core.Models;

namespace LocaleSmith.Core.Abstractions;

public interface IModPlatformClient
{
    Task<ModPlatformMeta> GetMetaAsync(CancellationToken cancellationToken = default);

    Task<ModPlatformAuthSession> VerifyApplicationLoginAsync(
        string username,
        ReadOnlyMemory<char> password,
        ReadOnlyMemory<char> applicationToken,
        CancellationToken cancellationToken = default);

    Task<ModPlatformAuthSession> VerifyApplicationTokenAsync(
        string username,
        ReadOnlyMemory<char> applicationToken,
        CancellationToken cancellationToken = default);

    Task<ModPlatformAuthSession> GetAuthenticatedSessionAsync(
        CancellationToken cancellationToken = default);

    Task<ModPlatformPage<ModPlatformModSummary>> GetModsAsync(
        ModPlatformSearchOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<ModPlatformModDetail> GetModAsync(
        string idOrSlug,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModPlatformTag>> GetTagsAsync(CancellationToken cancellationToken = default);

    Task<ModPlatformUploadSession> CreateUploadAsync(
        ModPlatformCreateUploadRequest request,
        CancellationToken cancellationToken = default);

    Task<ModPlatformUploadSession> GetUploadAsync(
        Guid uploadId,
        CancellationToken cancellationToken = default);

    Task UploadChunkAsync(
        ModPlatformUploadSession upload,
        int chunkIndex,
        Stream content,
        string chunkSha256,
        CancellationToken cancellationToken = default);

    Task<ModPlatformCompletedUpload> CompleteUploadAsync(
        Guid uploadId,
        ModPlatformCompleteUploadRequest request,
        CancellationToken cancellationToken = default);

    Task AbortUploadAsync(
        Guid uploadId,
        CancellationToken cancellationToken = default);

    Task<ModPlatformPage<ModPlatformForumThread>> GetThreadsAsync(
        Guid modId,
        int page = 1,
        int pageSize = 30,
        CancellationToken cancellationToken = default);

    Task<ModPlatformForumThread> GetThreadAsync(
        Guid threadId,
        CancellationToken cancellationToken = default);

    Task<ModPlatformPage<ModPlatformForumPost>> GetPostsAsync(
        Guid threadId,
        int page = 1,
        int pageSize = 30,
        CancellationToken cancellationToken = default);

    Task<ModPlatformForumThread> CreateThreadAsync(
        Guid modId,
        string title,
        string content,
        CancellationToken cancellationToken = default);

    Task<ModPlatformForumPost> CreatePostAsync(
        Guid threadId,
        string content,
        CancellationToken cancellationToken = default);

    Task<ModPlatformReport> CreateReportAsync(
        ModPlatformReportRequest request,
        CancellationToken cancellationToken = default);

    Task ReportPostAsync(
        Guid postId,
        string reason,
        CancellationToken cancellationToken = default);
}

public interface IModPlatformArtifactDownloader
{
    Task DownloadAsync(
        ModPlatformVersion artifact,
        string destinationPath,
        IProgress<ModPlatformDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IModPlatformAccessTokenProvider
{
    ValueTask<SecretValue?> ResolveAsync(CancellationToken cancellationToken = default);
}

/// <summary>Stable API error contract exposed without coupling presentation code to HTTP transport.</summary>
public interface IModPlatformServiceError
{
    string Code { get; }
}

/// <summary>Manages the Mod platform PAT without exposing it through application configuration.</summary>
public interface IModPlatformCredentialService
{
    ValueTask<bool> IsConfiguredAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(
        ReadOnlyMemory<char> token,
        CancellationToken cancellationToken = default);

    ValueTask<bool> DeleteAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Authenticated MCTX billing and accelerated-download API. Implementations must never place
/// service tickets, Microsoft Store ID keys, PATs, or signed download grants in logs, configuration,
/// telemetry, diagnostics, or persistence. Signed grant URLs may exist only as short-lived request URIs.
/// </summary>
public interface IModPlatformBillingClient
{
    Task<ModPlatformMeta> GetMetaAsync(CancellationToken cancellationToken = default);

    Task<ModPlatformAuthSession> GetAuthenticatedSessionAsync(
        CancellationToken cancellationToken = default);

    Task<MicrosoftStoreServiceTicket> RequestMicrosoftStoreServiceTicketAsync(
        CancellationToken cancellationToken = default);

    Task VerifyMicrosoftStorePurchaseAsync(
        SecretValue storeIdKey,
        CancellationToken cancellationToken = default);

    Task<MicrosoftStoreEntitlements> GetEntitlementsAsync(
        CancellationToken cancellationToken = default);

    Task<ModPlatformDownloadSources> GetDownloadSourcesAsync(
        Guid versionId,
        CancellationToken cancellationToken = default);

    Task<ModPlatformAcceleratedDownloadGrant> CreateAcceleratedDownloadGrantAsync(
        Guid versionId,
        CancellationToken cancellationToken = default);
}

/// <summary>Platform-neutral facade over Windows.Services.Store for testable presentation code.</summary>
public interface IMicrosoftStorefront
{
    Task<MicrosoftStoreProductInfo?> GetSubscriptionAsync(
        CancellationToken cancellationToken = default);

    Task<MicrosoftStorePurchaseOutcome> RequestSubscriptionPurchaseAsync(
        CancellationToken cancellationToken = default);

    Task<bool> IsSubscriptionInUserCollectionAsync(
        CancellationToken cancellationToken = default);

    Task<SecretValue> GetCustomerPurchaseIdAsync(
        SecretValue serviceTicket,
        string publisherUserId,
        CancellationToken cancellationToken = default);
}

public interface IModPlatformAcceleratedArtifactDownloader
{
    Task DownloadAsync(
        ModPlatformVersion artifact,
        ModPlatformAcceleratedDownloadGrant initialGrant,
        Func<CancellationToken, Task<ModPlatformAcceleratedDownloadGrant>> renewGrantAsync,
        string destinationPath,
        IProgress<ModPlatformDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IModPlatformArtifactDownloadCoordinator
{
    Task<ModPlatformAccelerationAvailability> GetAccelerationAvailabilityAsync(
        ModPlatformVersion artifact,
        CancellationToken cancellationToken = default);

    Task<ModPlatformArtifactDownloadResult> DownloadAsync(
        ModPlatformVersion artifact,
        string destinationPath,
        ModPlatformDownloadRoute requestedRoute,
        IProgress<ModPlatformDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
