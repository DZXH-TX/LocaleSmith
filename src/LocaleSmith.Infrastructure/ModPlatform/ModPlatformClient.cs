using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;

namespace LocaleSmith.Infrastructure.ModPlatform;

/// <summary>Typed client for the stable, versioned MCTX Mod Hub inline API.</summary>
public sealed class ModPlatformClient : IModPlatformClient, IDisposable
{
    public static readonly Uri ProductionBaseUri = new("https://api.dzxh-tx.cn/");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 32
    };

    private static readonly HashSet<string> UploadSessionStatuses = new(StringComparer.Ordinal)
    {
        "created",
        "uploading",
        "assembling",
        "completed",
        "expired",
        "aborted",
        "failed"
    };

    private static readonly HashSet<string> CompletedUploadStatuses = new(StringComparer.Ordinal)
    {
        "draft",
        "pending_review",
        "published"
    };

    private readonly HttpClient _httpClient;
    private readonly HttpClient _transferHttpClient;
    private readonly Uri _baseUri;
    private readonly IModPlatformAccessTokenProvider? _accessTokenProvider;
    private readonly bool _ownsHttpClient;
    private readonly bool _ownsTransferHttpClient;
    private bool _disposed;

    public ModPlatformClient(
        HttpClient httpClient,
        Uri baseUri,
        IModPlatformAccessTokenProvider? accessTokenProvider = null)
        : this(
            httpClient,
            httpClient,
            baseUri,
            accessTokenProvider,
            ownsHttpClient: false,
            ownsTransferHttpClient: false)
    {
    }

    internal ModPlatformClient(
        HttpClient httpClient,
        HttpClient transferHttpClient,
        Uri baseUri,
        IModPlatformAccessTokenProvider? accessTokenProvider = null)
        : this(
            httpClient,
            transferHttpClient,
            baseUri,
            accessTokenProvider,
            ownsHttpClient: false,
            ownsTransferHttpClient: false)
    {
    }

    private ModPlatformClient(
        HttpClient httpClient,
        HttpClient transferHttpClient,
        Uri baseUri,
        IModPlatformAccessTokenProvider? accessTokenProvider,
        bool ownsHttpClient,
        bool ownsTransferHttpClient,
        bool allowLoopbackHttp = false)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _transferHttpClient = transferHttpClient ?? throw new ArgumentNullException(nameof(transferHttpClient));
        _baseUri = ValidateBaseUri(baseUri, allowLoopbackHttp);
        _accessTokenProvider = accessTokenProvider;
        _ownsHttpClient = ownsHttpClient;
        _ownsTransferHttpClient = ownsTransferHttpClient;
    }

    public static ModPlatformClient CreateProduction(ISecretResolver secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        return new ModPlatformClient(
            ModPlatformHttpClientFactory.Create(),
            ModPlatformHttpClientFactory.CreateForTransfer(),
            ProductionBaseUri,
            new SecretStoreModPlatformAccessTokenProvider(secrets),
            ownsHttpClient: true,
            ownsTransferHttpClient: true);
    }

    public static ModPlatformClient CreateForApplication(ISecretResolver secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        var baseUri = ModPlatformEndpointPolicy.ResolveApplicationBaseUri();
        return new ModPlatformClient(
            ModPlatformHttpClientFactory.Create(),
            ModPlatformHttpClientFactory.CreateForTransfer(),
            baseUri,
            new SecretStoreModPlatformAccessTokenProvider(secrets),
            ownsHttpClient: true,
            ownsTransferHttpClient: true,
            allowLoopbackHttp: baseUri.IsLoopback);
    }

    public Task<ModPlatformMeta> GetMetaAsync(CancellationToken cancellationToken = default) =>
        SendAsync<ModPlatformMeta>(
            HttpMethod.Get,
            "api/meta",
            null,
            authorize: false,
            cancellationToken,
            expectedStatus: HttpStatusCode.OK);

    public async Task<ModPlatformAuthSession> VerifyApplicationLoginAsync(
        string username,
        ReadOnlyMemory<char> password,
        ReadOnlyMemory<char> applicationToken,
        CancellationToken cancellationToken = default)
    {
        var normalizedUsername = ValidateLoginUsername(username);
        ValidatePassword(password);
        ValidateApplicationToken(applicationToken);
        using var content = new ApplicationLoginJsonContent(normalizedUsername, password);
        var result = await SendExplicitTokenAuthAsync(
            HttpMethod.Post,
            "api/v1/auth/application-login",
            content,
            applicationToken,
            cancellationToken).ConfigureAwait(false);
        return MapAuthSession(result);
    }

    public async Task<ModPlatformAuthSession> VerifyApplicationTokenAsync(
        string username,
        ReadOnlyMemory<char> applicationToken,
        CancellationToken cancellationToken = default)
    {
        var normalizedUsername = ValidateLoginUsername(username);
        ValidateApplicationToken(applicationToken);
        var result = await SendExplicitTokenAuthAsync(
            HttpMethod.Get,
            "api/v1/auth/session",
            content: null,
            applicationToken,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(result.User.Username, normalizedUsername, StringComparison.OrdinalIgnoreCase))
        {
            throw CreateAuthenticationException(HttpStatusCode.Unauthorized);
        }

        return MapAuthSession(result);
    }

    public async Task<ModPlatformAuthSession> GetAuthenticatedSessionAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<ModPlatformAuthResponse>(
            HttpMethod.Get,
            "api/v1/auth/session",
            null,
            authorize: true,
            cancellationToken,
            expectedStatus: HttpStatusCode.OK).ConfigureAwait(false);
        return MapAuthSession(result);
    }

    public Task<ModPlatformPage<ModPlatformModSummary>> GetModsAsync(
        ModPlatformSearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ModPlatformSearchOptions();
        if (options.Page < 1 || options.PageSize is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Page must be positive and page size must be between 1 and 50.");
        }

        if (options.Sort is not ("recent" or "updated" or "downloads" or "name"))
        {
            throw new ArgumentException(
                "Sort must be recent, updated, downloads, or name.",
                nameof(options));
        }

        var query = new List<KeyValuePair<string, string>>
        {
            new("page", options.Page.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("page_size", options.PageSize.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("sort", options.Sort)
        };
        AddOptional(query, "q", options.Query, 100, nameof(options.Query));
        AddOptional(query, "tag", options.Tag, 64, nameof(options.Tag));
        AddOptional(query, "loader", options.Loader, 32, nameof(options.Loader));
        AddOptional(query, "game_version", options.GameVersion, 32, nameof(options.GameVersion));
        return SendAsync<ModPlatformPage<ModPlatformModSummary>>(
            HttpMethod.Get,
            $"api/v1/mods?{BuildQuery(query)}",
            null,
            authorize: false,
            cancellationToken,
            expectedStatus: HttpStatusCode.OK);
    }

    public Task<ModPlatformModDetail> GetModAsync(
        string idOrSlug,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idOrSlug);
        if (idOrSlug.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(idOrSlug), "The Mod identifier is too long.");
        }

        return SendAsync<ModPlatformModDetail>(
            HttpMethod.Get,
            $"api/v1/mods/{Uri.EscapeDataString(idOrSlug.Trim())}",
            null,
            authorize: false,
            cancellationToken,
            expectedStatus: HttpStatusCode.OK);
    }

    public async Task<IReadOnlyList<ModPlatformTag>> GetTagsAsync(
        CancellationToken cancellationToken = default) =>
        await SendAsync<List<ModPlatformTag>>(
            HttpMethod.Get,
            "api/v1/tags",
            null,
            authorize: false,
            cancellationToken,
            expectedStatus: HttpStatusCode.OK).ConfigureAwait(false);

    public Task<ModPlatformUploadSession> CreateUploadAsync(
        ModPlatformCreateUploadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var filename = ValidateArtifactFilename(request.Filename);
        if (request.Size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Artifact size must be positive.");
        }

        var sha256 = NormalizeSha256(request.Sha256, nameof(request));
        return SendAsync<ModPlatformUploadSession>(
            HttpMethod.Post,
            "api/v1/uploads",
            request with { Filename = filename, Sha256 = sha256 },
            authorize: true,
            cancellationToken,
            expectedStatus: HttpStatusCode.Created);
    }

    public Task<ModPlatformUploadSession> GetUploadAsync(
        Guid uploadId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(uploadId, nameof(uploadId));
        return SendAsync<ModPlatformUploadSession>(
            HttpMethod.Get,
            $"api/v1/uploads/{uploadId:D}",
            null,
            authorize: true,
            cancellationToken,
            expectedStatus: HttpStatusCode.OK);
    }

    public async Task UploadChunkAsync(
        ModPlatformUploadSession upload,
        int chunkIndex,
        Stream content,
        string chunkSha256,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentNullException.ThrowIfNull(content);
        ValidateIdentifier(upload.Id, nameof(upload));
        if (!content.CanRead)
        {
            throw new ArgumentException("Upload chunk streams must be readable.", nameof(content));
        }

        if (upload.Size <= 0 || upload.ChunkSize <= 0 || upload.TotalChunks <= 0)
        {
            throw new ArgumentException("The upload session has an invalid fixed chunk layout.", nameof(upload));
        }

        var expectedTotalChunks = checked((upload.Size - 1) / upload.ChunkSize + 1);
        if (expectedTotalChunks != upload.TotalChunks)
        {
            throw new ArgumentException(
                "The upload session total chunk count does not match its fixed chunk layout.",
                nameof(upload));
        }

        if (chunkIndex < 0 || chunkIndex >= upload.TotalChunks)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkIndex));
        }

        var start = checked((long)chunkIndex * upload.ChunkSize);
        var expectedLength = chunkIndex == upload.TotalChunks - 1
            ? upload.Size - start
            : upload.ChunkSize;
        if (expectedLength <= 0
            || (content.CanSeek && content.Length - content.Position < expectedLength))
        {
            throw new ArgumentException(
                "The chunk stream is shorter than the server-provided fixed layout.",
                nameof(content));
        }

        var sha256 = NormalizeSha256(chunkSha256, nameof(chunkSha256));
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            ResolveUri($"api/v1/uploads/{upload.Id:D}/chunks/{chunkIndex}"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("LocaleSmith/1.0");
        request.Headers.TryAddWithoutValidation("X-Chunk-SHA256", sha256);
        request.Content = new FixedLengthStreamContent(content, expectedLength);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentRange = new ContentRangeHeaderValue(
            start,
            checked(start + expectedLength - 1),
            upload.Size);

        using var token = await ResolveRequiredTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.DangerousGetString());
        using var response = await SendHttpAsync(
            _transferHttpClient,
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        ModPlatformApiContract.ValidateVersionHeader(response, request.RequestUri);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateExceptionAsync(response, cancellationToken).ConfigureAwait(false);
        }

        if (response.StatusCode != HttpStatusCode.NoContent)
        {
            throw CreateInvalidResponseException(response.StatusCode);
        }
    }

    public Task<ModPlatformCompletedUpload> CompleteUploadAsync(
        Guid uploadId,
        ModPlatformCompleteUploadRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(uploadId, nameof(uploadId));
        ArgumentNullException.ThrowIfNull(request);
        if (request.IsOfficial)
        {
            throw new ArgumentException(
                "LocaleSmith personal access tokens cannot publish official Mod artifacts.",
                nameof(request));
        }

        ValidateCompleteUploadRequest(request);

        return SendAsync<ModPlatformCompletedUpload>(
            HttpMethod.Post,
            $"api/v1/uploads/{uploadId:D}/complete",
            request,
            authorize: true,
            cancellationToken,
            transportClient: _transferHttpClient,
            expectedStatus: HttpStatusCode.Created);
    }

    public Task AbortUploadAsync(
        Guid uploadId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(uploadId, nameof(uploadId));
        return SendNoContentAsync(
            HttpMethod.Delete,
            $"api/v1/uploads/{uploadId:D}",
            null,
            authorize: true,
            cancellationToken);
    }

    public Task<ModPlatformPage<ModPlatformForumThread>> GetThreadsAsync(
        Guid modId,
        int page = 1,
        int pageSize = 30,
        CancellationToken cancellationToken = default)
    {
        ValidatePage(page, pageSize, 50);
        return SendAsync<ModPlatformPage<ModPlatformForumThread>>(
            HttpMethod.Get,
            $"api/v1/mods/{modId:D}/threads?page={page}&page_size={pageSize}",
            null,
            authorize: false,
            cancellationToken,
            expectedStatus: HttpStatusCode.OK);
    }

    public Task<ModPlatformForumThread> GetThreadAsync(
        Guid threadId,
        CancellationToken cancellationToken = default) =>
        SendAsync<ModPlatformForumThread>(
            HttpMethod.Get,
            $"api/v1/threads/{threadId:D}",
            null,
            authorize: false,
            cancellationToken,
            expectedStatus: HttpStatusCode.OK);

    public Task<ModPlatformPage<ModPlatformForumPost>> GetPostsAsync(
        Guid threadId,
        int page = 1,
        int pageSize = 30,
        CancellationToken cancellationToken = default)
    {
        ValidatePage(page, pageSize, 100);
        return SendAsync<ModPlatformPage<ModPlatformForumPost>>(
            HttpMethod.Get,
            $"api/v1/threads/{threadId:D}/posts?page={page}&page_size={pageSize}",
            null,
            authorize: false,
            cancellationToken,
            expectedStatus: HttpStatusCode.OK);
    }

    public Task<ModPlatformForumThread> CreateThreadAsync(
        Guid modId,
        string title,
        string content,
        CancellationToken cancellationToken = default)
    {
        ValidateForumText(title, 4, 160, nameof(title));
        ValidateForumText(content, 1, 100_000, nameof(content));
        return SendAsync<ModPlatformForumThread>(
            HttpMethod.Post,
            $"api/v1/mods/{modId:D}/threads",
            new { title = title.Trim(), content = content.Trim() },
            authorize: true,
            cancellationToken,
            expectedStatus: HttpStatusCode.Created);
    }

    public Task<ModPlatformForumPost> CreatePostAsync(
        Guid threadId,
        string content,
        CancellationToken cancellationToken = default)
    {
        ValidateForumText(content, 1, 100_000, nameof(content));
        return SendAsync<ModPlatformForumPost>(
            HttpMethod.Post,
            $"api/v1/threads/{threadId:D}/posts",
            new { content = content.Trim() },
            authorize: true,
            cancellationToken,
            expectedStatus: HttpStatusCode.Created);
    }

    public async Task<ModPlatformReport> CreateReportAsync(
        ModPlatformReportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ModPlatformReportTargetTypes.All.Contains(request.TargetType))
        {
            throw new ArgumentException("The report target type is not supported.", nameof(request));
        }

        ValidateIdentifier(request.TargetId, nameof(request));
        if (!ModPlatformReportCategories.All.Contains(request.Category))
        {
            throw new ArgumentException("The report category is not supported.", nameof(request));
        }

        ValidateForumText(request.Details, 4, 1_900, nameof(request));
        var normalizedRequest = request with { Details = request.Details.Trim() };
        var report = await SendAsync<ModPlatformReport>(
                HttpMethod.Post,
                "api/v1/reports",
                normalizedRequest,
                authorize: true,
                cancellationToken,
                expectedStatus: HttpStatusCode.Created)
            .ConfigureAwait(false);
        if (!string.Equals(report.Status, "open", StringComparison.Ordinal)
            || !string.Equals(report.TargetType, normalizedRequest.TargetType, StringComparison.Ordinal)
            || report.TargetId != normalizedRequest.TargetId
            || !string.Equals(report.Category, normalizedRequest.Category, StringComparison.Ordinal))
        {
            throw CreateInvalidResponseException(HttpStatusCode.Created);
        }

        return report;
    }

    public Task ReportPostAsync(
        Guid postId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(postId, nameof(postId));
        ValidateForumText(reason, 4, 2_000, nameof(reason));
        return SendNoContentAsync(
            HttpMethod.Post,
            $"api/v1/posts/{postId:D}/reports",
            new { reason = reason.Trim() },
            authorize: true,
            cancellationToken);
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

        if (_ownsTransferHttpClient && !ReferenceEquals(_transferHttpClient, _httpClient))
        {
            _transferHttpClient.Dispose();
        }

        _disposed = true;
    }

    private async Task<ModPlatformAuthResponse> SendExplicitTokenAuthAsync(
        HttpMethod method,
        string relativePath,
        HttpContent? content,
        ReadOnlyMemory<char> applicationToken,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var request = new HttpRequestMessage(method, ResolveUri(relativePath));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("LocaleSmith/1.0");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            new string(applicationToken.Span));
        request.Content = content;

        using var response = await SendHttpAsync(
            _httpClient,
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);
        ModPlatformApiContract.ValidateVersionHeader(response, request.RequestUri);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateApplicationAuthenticationExceptionAsync(response, cancellationToken)
                .ConfigureAwait(false);
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw CreateInvalidResponseException(response.StatusCode);
        }

        ModPlatformAuthResponse? result;
        try
        {
            result = await response.Content
                .ReadFromJsonAsync<ModPlatformAuthResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        {
            throw CreateInvalidResponseException(response.StatusCode);
        }

        if (result is null || !IsValidAuthResponse(result))
        {
            throw CreateInvalidResponseException(response.StatusCode);
        }

        return result;
    }

    private static ModPlatformAuthSession MapAuthSession(ModPlatformAuthResponse result)
    {
        var scopes = result.Scopes?.ToArray() ?? [];
        if (scopes.Contains("admin", StringComparer.Ordinal)
            || scopes.Contains("*", StringComparer.Ordinal))
        {
            throw new ModPlatformException(
                HttpStatusCode.Forbidden,
                "forbidden",
                "LocaleSmith does not retain controlled high-privilege application tokens.");
        }

        return new ModPlatformAuthSession(result.User, result.ExpiresAt, scopes);
    }

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string relativePath,
        object? body,
        bool authorize,
        CancellationToken cancellationToken,
        HttpClient? transportClient = null,
        HttpStatusCode? expectedStatus = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var request = new HttpRequestMessage(method, ResolveUri(relativePath));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("LocaleSmith/1.0");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        using var token = authorize ? await ResolveRequiredTokenAsync(cancellationToken).ConfigureAwait(false) : null;
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.DangerousGetString());
        }

        using var response = await SendHttpAsync(
            transportClient ?? _httpClient,
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);
        ModPlatformApiContract.ValidateVersionHeader(response, request.RequestUri);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateExceptionAsync(response, cancellationToken).ConfigureAwait(false);
        }

        if (expectedStatus is not null && response.StatusCode != expectedStatus)
        {
            throw CreateInvalidResponseException(response.StatusCode);
        }

        T? result;
        try
        {
            result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        {
            throw CreateInvalidResponseException(response.StatusCode);
        }

        if (result is null || !HasValidResponseContract(result))
        {
            throw CreateInvalidResponseException(response.StatusCode);
        }

        return result;
    }

    private async Task SendNoContentAsync(
        HttpMethod method,
        string relativePath,
        object? body,
        bool authorize,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var request = new HttpRequestMessage(method, ResolveUri(relativePath));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("LocaleSmith/1.0");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        using var token = authorize
            ? await ResolveRequiredTokenAsync(cancellationToken).ConfigureAwait(false)
            : null;
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.DangerousGetString());
        }

        using var response = await SendHttpAsync(
            _httpClient,
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);
        ModPlatformApiContract.ValidateVersionHeader(response, request.RequestUri);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateExceptionAsync(response, cancellationToken).ConfigureAwait(false);
        }

        if (response.StatusCode != HttpStatusCode.NoContent)
        {
            throw CreateInvalidResponseException(response.StatusCode);
        }
    }

    private static async Task<HttpResponseMessage> SendHttpAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        try
        {
            return await httpClient.SendAsync(
                request,
                completionOption,
                cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModPlatformException(
                HttpStatusCode.RequestTimeout,
                "request_timeout",
                "The Mod platform request timed out.");
        }
        catch (HttpRequestException)
        {
            throw new ModPlatformException(
                HttpStatusCode.ServiceUnavailable,
                "network_error",
                "The Mod platform network request failed.");
        }
    }

    private static ModPlatformException CreateInvalidResponseException(HttpStatusCode statusCode) => new(
        statusCode,
        "invalid_response",
        "The Mod platform returned an empty or invalid JSON response.");

    private static ModPlatformException CreateAuthenticationException(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.Unauthorized
            ? new ModPlatformException(
                statusCode,
                "invalid_credentials",
                "The Mod platform account does not match the supplied application token.")
            : new ModPlatformException(
                statusCode,
                $"http_{(int)statusCode}",
                $"The Mod platform application login failed with HTTP {(int)statusCode}.");

    private static async Task<ModPlatformException> CreateApplicationAuthenticationExceptionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var parsed = await CreateExceptionAsync(response, cancellationToken).ConfigureAwait(false);
        return (response.StatusCode, parsed.Code) switch
        {
            (HttpStatusCode.Unauthorized, "invalid_credentials") => new ModPlatformException(
                response.StatusCode,
                "invalid_credentials",
                "The Mod platform rejected the account credentials."),
            (HttpStatusCode.Unauthorized, _) => new ModPlatformException(
                response.StatusCode,
                "unauthorized",
                "The Mod platform rejected the application token."),
            (HttpStatusCode.Forbidden, _) => new ModPlatformException(
                response.StatusCode,
                "forbidden",
                "The Mod platform does not permit this application token for account verification."),
            (HttpStatusCode.TooManyRequests, _) => new ModPlatformException(
                response.StatusCode,
                "rate_limited",
                "The Mod platform temporarily rate limited account verification."),
            (HttpStatusCode.RequestTimeout, _) => new ModPlatformException(
                response.StatusCode,
                "request_timeout",
                "The Mod platform account verification request timed out."),
            (HttpStatusCode.ServiceUnavailable, "security_service_unavailable") => new ModPlatformException(
                response.StatusCode,
                "security_service_unavailable",
                "The Mod platform security service is temporarily unavailable."),
            _ => new ModPlatformException(
                response.StatusCode,
                $"http_{(int)response.StatusCode}",
                $"The Mod platform application login failed with HTTP {(int)response.StatusCode}.")
        };
    }

    private static bool HasValidResponseContract<T>(T result) => result switch
    {
        ModPlatformMeta meta => IsValidMeta(meta),
        ModPlatformAuthResponse auth => IsValidAuthResponse(auth),
        ModPlatformPage<ModPlatformModSummary> page => IsValidPage(page, IsValidModSummary),
        ModPlatformPage<ModPlatformForumThread> page => IsValidPage(page, IsValidThread),
        ModPlatformPage<ModPlatformForumPost> page => IsValidPage(page, IsValidPost),
        IReadOnlyList<ModPlatformTag> tags => tags.All(IsValidTag),
        ModPlatformModDetail mod => IsValidModDetail(mod),
        ModPlatformForumThread thread => IsValidThread(thread),
        ModPlatformForumPost post => IsValidPost(post),
        ModPlatformReport report => IsValidReport(report),
        ModPlatformUploadSession upload => IsValidUploadSession(upload),
        ModPlatformCompletedUpload upload => IsValidCompletedUpload(upload),
        _ => false
    };

    private static bool IsValidAuthResponse(ModPlatformAuthResponse? auth) =>
        auth is not null
        && IsValidUser(auth.User)
        && auth.CsrfToken is null
        && (auth.ExpiresAt is null || auth.ExpiresAt.Value != default)
        && (auth.Scopes is null
            || (auth.Scopes.Count <= 64
                && IsValidStringList(auth.Scopes)
                && auth.Scopes.Distinct(StringComparer.Ordinal).Count() == auth.Scopes.Count));

    private static bool IsValidUser(ModPlatformUser? user) =>
        user is not null
        && user.Id != Guid.Empty
        && IsValidUsername(user.Username)
        && user.Role is "user" or "admin";

    private static bool IsValidMeta(ModPlatformMeta? meta) =>
        meta is not null
        && string.Equals(meta.Service, "MCTX Mod Hub", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(meta.BuildId)
        && meta.SupportedApiMajors is { Count: > 0 }
        && meta.SupportedApiMajors.All(static major => major > 0)
        && meta.SupportedApiMajors.Contains(1)
        && meta.PreferredApiMajor > 0
        && meta.SupportedApiMajors.Contains(meta.PreferredApiMajor)
        && meta.Features is { Count: > 0 }
        && IsValidStringList(meta.Features)
        && meta.Features.Distinct(StringComparer.Ordinal).Count() == meta.Features.Count
        && meta.Features.Contains("personal_access_token_v1", StringComparer.Ordinal)
        && meta.Features.Contains("forum_v1", StringComparer.Ordinal)
        && IsValidLimits(meta.Limits)
        && IsValidTurnstile(meta.Turnstile)
        && meta.ServerTime != default
        && (meta.Artifacts is null || IsValidArtifactCapabilities(meta.Artifacts))
        && (meta.Reporting is null || IsValidReportingCapabilities(meta.Reporting))
        && (!meta.Features.Contains("artifact_types_v1", StringComparer.Ordinal)
            || IsValidArtifactCapabilities(meta.Artifacts))
        && (!meta.Features.Contains("content_reports_v1", StringComparer.Ordinal)
            || IsValidReportingCapabilities(meta.Reporting));

    private static bool IsValidReportingCapabilities(ModPlatformReportingCapabilities? reporting) =>
        reporting is not null
        && IsValidHttpsUrl(reporting.TermsUrl)
        && IsValidHttpsUrl(reporting.CommunityGuidelinesUrl)
        && reporting.TargetTypes is { Count: > 0 }
        && IsValidStringList(reporting.TargetTypes)
        && reporting.TargetTypes.Any(ModPlatformReportTargetTypes.All.Contains)
        && reporting.Categories is { Count: > 0 }
        && IsValidStringList(reporting.Categories)
        && reporting.Categories.Any(ModPlatformReportCategories.All.Contains);

    private static bool IsValidHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Fragment);

    private static bool IsValidArtifactCapabilities(ModPlatformArtifactCapabilities? artifacts) =>
        artifacts is not null
        && artifacts.AllowedExtensions is { Count: > 0 }
        && artifacts.AllowedExtensions.All(static value =>
            !string.IsNullOrWhiteSpace(value) && value[0] == '.')
        && artifacts.AllowedMimeTypes is { Count: > 0 }
        && artifacts.AllowedMimeTypes.All(static value => !string.IsNullOrWhiteSpace(value))
        && IsValidStringList(artifacts.Validation);

    private static bool IsValidLimits(ModPlatformLimits? limits) =>
        limits is not null
        && limits.MaxModBytes > 0
        && limits.UploadChunkBytes > 0
        && limits.UploadChunkBytes <= limits.MaxModBytes
        && limits.UploadConcurrency > 0
        && limits.DownloadRangeConcurrency > 0;

    private static bool IsValidTurnstile(ModPlatformTurnstile? turnstile) =>
        turnstile is not null
        && (!turnstile.Required || !string.IsNullOrWhiteSpace(turnstile.SiteKey));

    private static bool IsValidPage<T>(ModPlatformPage<T>? page, Func<T?, bool> validateItem)
        where T : class =>
        page is not null
        && page.Data is not null
        && page.Page > 0
        && page.PageSize > 0
        && page.Total >= 0
        && page.Data.Count <= page.PageSize
        && page.Data.All(validateItem);

    private static bool IsValidTag(ModPlatformTag? tag) =>
        tag is not null
        && tag.Id != Guid.Empty
        && !string.IsNullOrWhiteSpace(tag.Slug)
        && !string.IsNullOrWhiteSpace(tag.Name)
        && tag.Description is not null
        && tag.Color is not null;

    private static bool IsValidVersion(ModPlatformVersion? version) =>
        version is not null
        && version.Id != Guid.Empty
        && !string.IsNullOrWhiteSpace(version.VersionName)
        && IsValidStringList(version.GameVersions)
        && IsValidStringList(version.Loaders)
        && version.Changelog is not null
        && !string.IsNullOrWhiteSpace(version.Filename)
        && version.Size > 0
        && IsValidSha256(version.Sha256)
        && version.Downloads >= 0
        && version.CreatedAt != default
        && !string.IsNullOrWhiteSpace(version.DownloadUrl);

    private static bool IsValidModSummary(ModPlatformModSummary? mod) =>
        mod is not null
        && mod.Id != Guid.Empty
        && !string.IsNullOrWhiteSpace(mod.Slug)
        && !string.IsNullOrWhiteSpace(mod.Title)
        && mod.Summary is not null
        && string.Equals(mod.Status, "published", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(mod.OwnerName)
        && mod.OwnerId != Guid.Empty
        && mod.Downloads >= 0
        && mod.UpdatedAt != default
        && mod.Tags is not null
        && mod.Tags.All(IsValidTag)
        && (mod.LatestVersion is null || IsValidVersion(mod.LatestVersion));

    private static bool IsValidModDetail(ModPlatformModDetail? mod) =>
        mod is not null
        && mod.Id != Guid.Empty
        && !string.IsNullOrWhiteSpace(mod.Slug)
        && !string.IsNullOrWhiteSpace(mod.Title)
        && mod.Summary is not null
        && string.Equals(mod.Status, "published", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(mod.OwnerName)
        && mod.OwnerId != Guid.Empty
        && mod.Downloads >= 0
        && mod.UpdatedAt != default
        && mod.Tags is not null
        && mod.Tags.All(IsValidTag)
        && (mod.LatestVersion is null || IsValidVersion(mod.LatestVersion))
        && mod.Description is not null
        && mod.Versions is not null
        && mod.Versions.All(IsValidVersion)
        && mod.Permissions is not null;

    private static bool IsValidThread(ModPlatformForumThread? thread) =>
        thread is not null
        && thread.Id != Guid.Empty
        && thread.ModId != Guid.Empty
        && !string.IsNullOrWhiteSpace(thread.Title)
        && thread.AuthorId != Guid.Empty
        && !string.IsNullOrWhiteSpace(thread.AuthorName)
        && thread.ReplyCount >= 0
        && thread.Status is "open" or "closed"
        && thread.CreatedAt != default
        && thread.UpdatedAt != default;

    private static bool IsValidPost(ModPlatformForumPost? post) =>
        post is not null
        && post.Id != Guid.Empty
        && post.ThreadId != Guid.Empty
        && post.AuthorId != Guid.Empty
        && !string.IsNullOrWhiteSpace(post.AuthorName)
        && !string.IsNullOrWhiteSpace(post.ContentMarkdown)
        && post.CreatedAt != default
        && post.UpdatedAt != default;

    private static bool IsValidReport(ModPlatformReport? report) =>
        report is not null
        && report.Id != Guid.Empty
        && ModPlatformReportTargetTypes.All.Contains(report.TargetType)
        && report.TargetId != Guid.Empty
        && ModPlatformReportCategories.All.Contains(report.Category)
        && string.Equals(report.Status, "open", StringComparison.Ordinal)
        && report.CreatedAt != default;

    private static bool IsValidUploadSession(ModPlatformUploadSession? upload)
    {
        if (upload is null
            || upload.Id == Guid.Empty
            || !IsArtifactFilenameContractValid(upload.Filename)
            || upload.Size <= 0
            || upload.ChunkSize <= 0
            || upload.TotalChunks <= 0
            || !IsCanonicalSha256(upload.ExpectedSha256)
            || !UploadSessionStatuses.Contains(upload.Status)
            || upload.UploadedChunks is null
            || upload.ExpiresAt == default)
        {
            return false;
        }

        var expectedTotalChunks = (upload.Size - 1) / upload.ChunkSize + 1;
        return expectedTotalChunks == upload.TotalChunks
            && upload.UploadedChunks.All(index => index >= 0 && index < upload.TotalChunks)
            && upload.UploadedChunks.Distinct().Count() == upload.UploadedChunks.Count;
    }

    private static bool IsValidCompletedUpload(ModPlatformCompletedUpload? upload) =>
        upload is not null
        && upload.ModId != Guid.Empty
        && upload.VersionId != Guid.Empty
        && CompletedUploadStatuses.Contains(upload.Status)
        && IsCanonicalSha256(upload.Sha256)
        && upload.Size > 0;

    private static bool IsValidStringList(IReadOnlyList<string>? values) =>
        values is not null && values.All(static value => !string.IsNullOrWhiteSpace(value));

    private static bool IsValidSha256(string? value) =>
        value is { Length: 64 }
        && value.All(static character => Uri.IsHexDigit(character));

    private static bool IsCanonicalSha256(string? value) =>
        value is { Length: 64 }
        && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private async ValueTask<SecretValue> ResolveRequiredTokenAsync(CancellationToken cancellationToken)
    {
        var token = _accessTokenProvider is null
            ? null
            : await _accessTokenProvider.ResolveAsync(cancellationToken).ConfigureAwait(false);
        if (token is null || token.Length == 0)
        {
            token?.Dispose();
            throw new InvalidOperationException(
                "A Mod platform personal access token with the required scope is required.");
        }

        return token;
    }

    private Uri ResolveUri(string relativePath)
    {
        if (Uri.TryCreate(relativePath, UriKind.Absolute, out _)
            || relativePath.StartsWith("//", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Mod platform API paths must be same-origin relative paths.");
        }

        var resolved = new Uri(_baseUri, relativePath.TrimStart('/'));
        if (!string.Equals(resolved.Scheme, _baseUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(resolved.Host, _baseUri.Host, StringComparison.OrdinalIgnoreCase)
            || resolved.Port != _baseUri.Port)
        {
            throw new InvalidOperationException("The resolved Mod platform endpoint changed origin.");
        }

        return resolved;
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

    private static async Task<ModPlatformException> CreateExceptionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var code = $"http_{(int)response.StatusCode}";
        var message = $"The Mod platform request failed with HTTP {(int)response.StatusCode}.";
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var limited = new MemoryStream();
            var buffer = new byte[4096];
            while (limited.Length < 32 * 1024)
            {
                var remaining = (int)Math.Min(buffer.Length, 32 * 1024 - limited.Length);
                var read = await stream.ReadAsync(buffer.AsMemory(0, remaining), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                limited.Write(buffer, 0, read);
            }

            var envelope = JsonSerializer.Deserialize<ErrorEnvelope>(limited.ToArray(), JsonOptions);
            if (!string.IsNullOrWhiteSpace(envelope?.Error?.Code))
            {
                code = envelope.Error.Code;
            }

            if (!string.IsNullOrWhiteSpace(envelope?.Error?.Message))
            {
                message = envelope.Error.Message;
            }
        }
        catch (Exception error) when (error is JsonException or IOException or NotSupportedException)
        {
            // Preserve the status-based fallback when the error body is absent or malformed.
        }

        return new ModPlatformException(response.StatusCode, code, message);
    }

    private static string ValidateLoginUsername(string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        var normalized = username.Trim();
        if (!IsValidUsername(normalized))
        {
            throw new ArgumentException(
                "Usernames must contain 3 to 32 letters, numbers, underscores, or hyphens.",
                nameof(username));
        }

        return normalized;
    }

    private static bool IsValidUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username)
            || !string.Equals(username, username.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        var length = 0;
        foreach (var rune in username.EnumerateRunes())
        {
            if (!Rune.IsLetterOrDigit(rune) && rune.Value is not ('_' or '-'))
            {
                return false;
            }

            length++;
            if (length > 32)
            {
                return false;
            }
        }

        return length >= 3;
    }

    private static void ValidatePassword(ReadOnlyMemory<char> password)
    {
        var length = 0;
        foreach (var _ in password.Span.EnumerateRunes())
        {
            length++;
            if (length > 128)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(password),
                    "Passwords cannot exceed 128 characters.");
            }
        }

        if (length == 0)
        {
            throw new ArgumentException("A password is required.", nameof(password));
        }
    }

    private static void ValidateApplicationToken(ReadOnlyMemory<char> applicationToken)
    {
        if (applicationToken.IsEmpty)
        {
            throw new ArgumentException("An application token is required.", nameof(applicationToken));
        }

        if (applicationToken.Length > 512)
        {
            throw new ArgumentOutOfRangeException(
                nameof(applicationToken),
                "Application tokens cannot exceed 512 characters.");
        }

        foreach (var character in applicationToken.Span)
        {
            if (!char.IsAsciiLetterOrDigit(character)
                && character is not ('-' or '_' or '.' or '~'))
            {
                throw new ArgumentException(
                    "The application token contains invalid characters.",
                    nameof(applicationToken));
            }
        }
    }

    private static void AddOptional(
        List<KeyValuePair<string, string>> query,
        string name,
        string? value,
        int maximumLength,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var normalized = value.Trim();
        var length = 0;
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (System.Text.Rune.GetUnicodeCategory(rune) == System.Globalization.UnicodeCategory.Control)
            {
                throw new ArgumentException("Filter values cannot contain control characters.", parameterName);
            }

            length++;
            if (length > maximumLength)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    $"Filter values cannot exceed {maximumLength} characters.");
            }
        }

        query.Add(new KeyValuePair<string, string>(name, normalized));
    }

    private static string BuildQuery(IEnumerable<KeyValuePair<string, string>> query) =>
        string.Join(
            "&",
            query.Select(static pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

    private static void ValidatePage(int page, int pageSize, int maximumPageSize)
    {
        if (page < 1 || pageSize < 1 || pageSize > maximumPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize),
                $"Page must be positive and page size must be between 1 and {maximumPageSize}.");
        }
    }

    private static void ValidateForumText(string value, int minimumLength, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var length = value.Trim().EnumerateRunes().Count();
        if (length < minimumLength || length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Text length must be between {minimumLength} and {maximumLength} characters.");
        }
    }

    private static string ValidateArtifactFilename(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var filename = value.Trim();
        if (filename.Contains('/') || filename.Contains('\\'))
        {
            throw new ArgumentException("Artifact filenames cannot contain a path.", nameof(value));
        }

        if (!IsArtifactFilenameContractValid(filename))
        {
            throw new ArgumentException(
                "Mod platform artifact filenames must be 1-180 characters without controls and use .jar or .zip.",
                nameof(value));
        }

        return filename;
    }

    private static bool IsArtifactFilenameContractValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Contains('/')
            || value.Contains('\\')
            || value.EnumerateRunes().Count() > 180
            || ContainsControl(value))
        {
            return false;
        }

        var extension = Path.GetExtension(value);
        return string.Equals(extension, ".jar", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateCompleteUploadRequest(ModPlatformCompleteUploadRequest request)
    {
        ValidateTextLength(request.Title, 3, 100, nameof(request.Title));
        ValidateTextLength(request.Summary, 10, 240, nameof(request.Summary));
        ValidateMarkdown(request.Description, 50_000, nameof(request.Description));
        ValidateTextLength(request.VersionName, 1, 100, nameof(request.VersionName));
        ValidateStringArray(request.GameVersions, 16, 32, nameof(request.GameVersions));
        ValidateStringArray(request.Loaders, 8, 32, nameof(request.Loaders));
        ValidateMarkdown(request.Changelog, 20_000, nameof(request.Changelog));
        if (request.TagIds is null || request.TagIds.Count is < 1 or > 12)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "A completed upload must select between 1 and 12 tags.");
        }

        if (request.TagIds.Distinct().Count() != request.TagIds.Count)
        {
            throw new ArgumentException("A completed upload cannot contain duplicate tags.", nameof(request));
        }
    }

    private static void ValidateTextLength(string? value, int minimumLength, int maximumLength, string parameterName)
    {
        var length = value?.Trim().EnumerateRunes().Count() ?? 0;
        if (length < minimumLength || length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Text length must be between {minimumLength} and {maximumLength} characters.");
        }
    }

    private static void ValidateStringArray(
        IReadOnlyList<string>? values,
        int maximumItems,
        int maximumLength,
        string parameterName)
    {
        if (values is null
            || values.Count is < 1
            || values.Count > maximumItems
            || values.Any(value => !IsValidStringArrayValue(value, maximumLength)))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Values must contain 1-{maximumItems} entries of at most {maximumLength} characters without controls.");
        }
    }

    private static bool IsValidStringArrayValue(string? value, int maximumLength)
    {
        if (value is null || ContainsControl(value))
        {
            return false;
        }

        var length = 0;
        foreach (var _ in value.Trim().EnumerateRunes())
        {
            length++;
            if (length > maximumLength)
            {
                return false;
            }
        }

        return length > 0;
    }

    private static void ValidateMarkdown(string? value, int maximumLength, string parameterName)
    {
        if (value is null
            || value.EnumerateRunes().Count() > maximumLength
            || value.Contains('\0')
            || value.Contains('<')
            || value.Contains('>'))
        {
            throw new ArgumentException(
                $"Markdown must not exceed {maximumLength} characters or contain raw HTML or NUL characters.",
                parameterName);
        }
    }

    private static bool ContainsControl(string value) =>
        value.EnumerateRunes().Any(static rune =>
            Rune.GetUnicodeCategory(rune) == System.Globalization.UnicodeCategory.Control);

    private static string NormalizeSha256(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "SHA-256 values must contain exactly 64 hexadecimal characters.",
                parameterName);
        }

        return normalized;
    }

    private static void ValidateIdentifier(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Identifiers cannot be empty.", parameterName);
        }
    }

    private sealed record ModPlatformAuthResponse(
        ModPlatformUser User,
        [property: JsonPropertyName("csrf_token")] string? CsrfToken,
        [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt,
        IReadOnlyList<string>? Scopes = null);

    private sealed class ApplicationLoginJsonContent : HttpContent
    {
        private readonly string _username;
        private ReadOnlyMemory<char> _password;

        internal ApplicationLoginJsonContent(string username, ReadOnlyMemory<char> password)
        {
            _username = username;
            _password = password;
            Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8"
            };
        }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) => SerializeAsync(stream, CancellationToken.None);

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken) => SerializeAsync(stream, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            _password = default;
            base.Dispose(disposing);
        }

        private async Task SerializeAsync(Stream stream, CancellationToken cancellationToken)
        {
            using var buffer = new ZeroingPooledBufferWriter();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("username"u8, _username.AsSpan());
                writer.WriteString("password"u8, _password.Span);
                writer.WriteEndObject();
                writer.Flush();
            }

            await stream.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class ZeroingPooledBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private byte[]? _buffer = ArrayPool<byte>.Shared.Rent(256);
        private int _written;

        internal ReadOnlyMemory<byte> WrittenMemory =>
            GetBuffer().AsMemory(0, _written);

        public void Advance(int count)
        {
            var buffer = GetBuffer();
            if (count < 0 || count > buffer.Length - _written)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return GetBuffer().AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return GetBuffer().AsSpan(_written);
        }

        public void Dispose()
        {
            var buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is null)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
            _written = 0;
        }

        private byte[] GetBuffer() =>
            _buffer ?? throw new ObjectDisposedException(nameof(ZeroingPooledBufferWriter));

        private void EnsureCapacity(int sizeHint)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);

            if (sizeHint == 0)
            {
                sizeHint = 1;
            }

            var buffer = GetBuffer();
            if (sizeHint <= buffer.Length - _written)
            {
                return;
            }

            var required = checked(_written + sizeHint);
            var replacement = ArrayPool<byte>.Shared.Rent(Math.Max(required, buffer.Length * 2));
            buffer.AsSpan(0, _written).CopyTo(replacement);
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
            _buffer = replacement;
        }
    }

    private sealed class FixedLengthStreamContent(Stream content, long length) : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) => CopyExactAsync(stream, CancellationToken.None);

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken) => CopyExactAsync(stream, cancellationToken);

        protected override bool TryComputeLength(out long computedLength)
        {
            computedLength = length;
            return true;
        }

        private async Task CopyExactAsync(Stream destination, CancellationToken cancellationToken)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            try
            {
                var remaining = length;
                while (remaining > 0)
                {
                    var read = await content.ReadAsync(
                        buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                        cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("The upload chunk ended before its declared fixed length.");
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    remaining -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private sealed record ErrorEnvelope(ErrorDetail? Error);

    private sealed record ErrorDetail(string Code, string Message);
}
