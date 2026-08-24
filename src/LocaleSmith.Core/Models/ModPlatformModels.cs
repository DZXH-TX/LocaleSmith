using System.Text.Json.Serialization;

namespace LocaleSmith.Core.Models;

public sealed record ModPlatformMeta(
    string Service,
    [property: JsonPropertyName("build_id")] string BuildId,
    [property: JsonPropertyName("supported_api_majors")] IReadOnlyList<int> SupportedApiMajors,
    [property: JsonPropertyName("preferred_api_major")] int PreferredApiMajor,
    IReadOnlyList<string> Features,
    ModPlatformLimits Limits,
    ModPlatformTurnstile Turnstile,
    [property: JsonPropertyName("server_time")] DateTimeOffset ServerTime,
    ModPlatformArtifactCapabilities? Artifacts = null,
    ModPlatformReportingCapabilities? Reporting = null,
    [property: JsonPropertyName("microsoft_store")] MicrosoftStoreMeta? MicrosoftStore = null);

public sealed record ModPlatformReportingCapabilities(
    [property: JsonPropertyName("terms_url")] string TermsUrl,
    [property: JsonPropertyName("community_guidelines_url")] string CommunityGuidelinesUrl,
    [property: JsonPropertyName("target_types")] IReadOnlyList<string> TargetTypes,
    IReadOnlyList<string> Categories);

public sealed record ModPlatformArtifactCapabilities(
    [property: JsonPropertyName("allowed_extensions")] IReadOnlyList<string> AllowedExtensions,
    [property: JsonPropertyName("allowed_mime_types")] IReadOnlyList<string> AllowedMimeTypes,
    IReadOnlyList<string> Validation);

public sealed record ModPlatformLimits(
    [property: JsonPropertyName("max_mod_bytes")] long MaxModBytes,
    [property: JsonPropertyName("upload_chunk_bytes")] long UploadChunkBytes,
    [property: JsonPropertyName("upload_concurrency")] int UploadConcurrency,
    [property: JsonPropertyName("download_range_concurrency")] int DownloadRangeConcurrency);

public sealed record ModPlatformTurnstile(
    bool Required,
    [property: JsonPropertyName("site_key")] string? SiteKey);

public sealed record ModPlatformUser(
    Guid Id,
    string Username,
    string Role);

public sealed record ModPlatformAuthSession(
    ModPlatformUser User,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<string> Scopes);

public sealed record ModPlatformPage<T>(
    IReadOnlyList<T> Data,
    long Page,
    [property: JsonPropertyName("page_size")] long PageSize,
    long Total);

public sealed record ModPlatformTag(
    Guid Id,
    string Slug,
    string Name,
    string Description,
    string Color,
    [property: JsonPropertyName("is_official")] bool IsOfficial);

public sealed record ModPlatformVersion(
    Guid Id,
    [property: JsonPropertyName("version_name")] string VersionName,
    [property: JsonPropertyName("game_versions")] IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Loaders,
    string Changelog,
    string Filename,
    long Size,
    string Sha256,
    long Downloads,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("download_url")] string DownloadUrl);

public sealed record ModPlatformModSummary(
    Guid Id,
    string Slug,
    string Title,
    string Summary,
    string Status,
    [property: JsonPropertyName("is_official")] bool IsOfficial,
    [property: JsonPropertyName("owner_name")] string OwnerName,
    long Downloads,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
    IReadOnlyList<ModPlatformTag> Tags,
    [property: JsonPropertyName("latest_version")] ModPlatformVersion? LatestVersion,
    [property: JsonPropertyName("owner_id")] Guid OwnerId)
{
    public bool HasLatestVersion => LatestVersion is not null;
}

public sealed record ModPlatformPermissions(
    [property: JsonPropertyName("can_edit")] bool CanEdit,
    [property: JsonPropertyName("can_delete")] bool CanDelete,
    [property: JsonPropertyName("can_moderate")] bool CanModerate);

public sealed record ModPlatformModDetail(
    Guid Id,
    string Slug,
    string Title,
    string Summary,
    string Status,
    [property: JsonPropertyName("is_official")] bool IsOfficial,
    [property: JsonPropertyName("owner_name")] string OwnerName,
    long Downloads,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
    IReadOnlyList<ModPlatformTag> Tags,
    [property: JsonPropertyName("latest_version")] ModPlatformVersion? LatestVersion,
    string Description,
    IReadOnlyList<ModPlatformVersion> Versions,
    ModPlatformPermissions Permissions,
    [property: JsonPropertyName("owner_id")] Guid OwnerId);

public sealed record ModPlatformForumThread(
    Guid Id,
    [property: JsonPropertyName("mod_id")] Guid ModId,
    string Title,
    [property: JsonPropertyName("author_id")] Guid AuthorId,
    [property: JsonPropertyName("author_name")] string AuthorName,
    [property: JsonPropertyName("reply_count")] long ReplyCount,
    string Status,
    bool Locked,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record ModPlatformForumPost(
    Guid Id,
    [property: JsonPropertyName("thread_id")] Guid ThreadId,
    [property: JsonPropertyName("author_id")] Guid AuthorId,
    [property: JsonPropertyName("author_name")] string AuthorName,
    [property: JsonPropertyName("content_markdown")] string ContentMarkdown,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record ModPlatformReportRequest(
    [property: JsonPropertyName("target_type")] string TargetType,
    [property: JsonPropertyName("target_id")] Guid TargetId,
    string Category,
    string Details);

public sealed record ModPlatformReport(
    Guid Id,
    [property: JsonPropertyName("target_type")] string TargetType,
    [property: JsonPropertyName("target_id")] Guid TargetId,
    string Category,
    string Status,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

public static class ModPlatformReportTargetTypes
{
    public const string Mod = "mod";
    public const string ModVersion = "mod_version";
    public const string ForumThread = "forum_thread";
    public const string ForumPost = "forum_post";
    public const string User = "user";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Mod,
        ModVersion,
        ForumThread,
        ForumPost,
        User
    };
}

public static class ModPlatformReportCategories
{
    public const string Spam = "spam";
    public const string Harassment = "harassment";
    public const string HateSpeech = "hate_speech";
    public const string SexualContent = "sexual_content";
    public const string Violence = "violence";
    public const string IllegalContent = "illegal_content";
    public const string Malware = "malware";
    public const string Copyright = "copyright";
    public const string Privacy = "privacy";
    public const string Impersonation = "impersonation";
    public const string ChildSafety = "child_safety";
    public const string Other = "other";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Spam,
        Harassment,
        HateSpeech,
        SexualContent,
        Violence,
        IllegalContent,
        Malware,
        Copyright,
        Privacy,
        Impersonation,
        ChildSafety,
        Other
    };
}

public sealed record ModPlatformSearchOptions(
    int Page = 1,
    int PageSize = 20,
    string? Query = null,
    string? Tag = null,
    string? Loader = null,
    string? GameVersion = null,
    string Sort = "recent");

public sealed record ModPlatformCreateUploadRequest(
    string Filename,
    long Size,
    string Sha256);

public sealed record ModPlatformUploadSession(
    Guid Id,
    string Filename,
    long Size,
    [property: JsonPropertyName("chunk_size")] long ChunkSize,
    [property: JsonPropertyName("total_chunks")] int TotalChunks,
    [property: JsonPropertyName("expected_sha256")] string ExpectedSha256,
    string Status,
    [property: JsonPropertyName("uploaded_chunks")] IReadOnlyList<int> UploadedChunks,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

public sealed record ModPlatformCompleteUploadRequest(
    [property: JsonPropertyName("mod_id")] Guid? ModId,
    string Title,
    string Summary,
    string Description,
    [property: JsonPropertyName("version_name")] string VersionName,
    [property: JsonPropertyName("game_versions")] IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Loaders,
    [property: JsonPropertyName("tag_ids")] IReadOnlyList<Guid> TagIds,
    string Changelog = "",
    bool Publish = false,
    [property: JsonPropertyName("is_official")] bool IsOfficial = false);

public sealed record ModPlatformCompletedUpload(
    [property: JsonPropertyName("mod_id")] Guid ModId,
    [property: JsonPropertyName("version_id")] Guid VersionId,
    string Status,
    string Sha256,
    long Size);

public sealed record ModPlatformDownloadProgress(long BytesReceived, long TotalBytes)
{
    public double Fraction => TotalBytes <= 0
        ? 0
        : Math.Clamp((double)BytesReceived / TotalBytes, 0, 1);
}
