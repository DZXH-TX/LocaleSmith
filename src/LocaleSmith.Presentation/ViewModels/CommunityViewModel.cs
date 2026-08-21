using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;
using LocaleSmith.Presentation.Abstractions;

namespace LocaleSmith.Presentation.ViewModels;

/// <summary>Coordinates the native, plain-text Mod community experience.</summary>
public sealed class CommunityViewModel : ViewModelBase, IDisposable
{
    private const int ModPageSize = 50;
    private const int ThreadPageSize = 50;
    private const int PostPageSize = 50;
    private const int ReportDetailsMinimumLength = 4;
    private const int ReportDetailsMaximumLength = 1_900;
    private static readonly Uri DefaultTermsUri = new("https://dow.dzxh-tx.cn/terms");
    private static readonly Uri DefaultCommunityGuidelinesUri =
        new("https://dow.dzxh-tx.cn/community-guidelines");

    private readonly IModPlatformClient _client;
    private readonly IModPlatformCredentialService _credentials;
    private readonly IUiTextProvider _text;
    private CancellationTokenSource? _activeOperation;
    private CancellationTokenSource? _activeReportOperation;
    private Func<Task>? _retryOperation;
    private readonly HashSet<string> _reportTargetTypes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _grantedScopes = new(StringComparer.Ordinal);
    private string _searchQuery = string.Empty;
    private ModPlatformModSummary? _selectedMod;
    private ModPlatformForumThread? _selectedThread;
    private string _newThreadTitle = string.Empty;
    private string _newThreadContent = string.Empty;
    private string _replyContent = string.Empty;
    private string? _activeSearchQuery;
    private int _modsPage;
    private int _threadsPage;
    private int _postsPage;
    private long _totalMods;
    private long _totalThreads;
    private long _totalPosts;
    private bool _isPatConfigured;
    private ModPlatformUser? _currentUser;
    private bool _supportsApplicationLogin;
    private bool _isReportFeatureAvailable;
    private bool _isReportSubmitting;
    private string? _reportErrorMessage;
    private string? _reportStatusMessage;
    private Uri _termsUri = DefaultTermsUri;
    private Uri _communityGuidelinesUri = DefaultCommunityGuidelinesUri;
    private bool _initialized;
    private bool _disposed;

    public CommunityViewModel(
        IModPlatformClient client,
        IModPlatformCredentialService credentials,
        IUiTextProvider? text = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _text = text ?? FallbackUiTextProvider.Instance;

        SearchCommand = new AsyncRelayCommand(SearchAsync, CanStartOperation);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, CanStartOperation);
        RetryCommand = new AsyncRelayCommand(RetryAsync, CanRetry);
        CancelCommand = new RelayCommand(CancelActiveOperation, () => CanCancel);
        LoadMoreModsCommand = new AsyncRelayCommand(LoadMoreModsAsync, CanLoadMoreMods);
        LoadMoreThreadsCommand = new AsyncRelayCommand(LoadMoreThreadsAsync, CanLoadMoreThreads);
        LoadMorePostsCommand = new AsyncRelayCommand(LoadMorePostsAsync, CanLoadMorePosts);
        CreateThreadCommand = new AsyncRelayCommand(CreateThreadAsync, CanCreateThread);
        CreateReplyCommand = new AsyncRelayCommand(CreateReplyAsync, CanCreateReply);
        DeletePatCommand = new AsyncRelayCommand(DeletePatAsync, CanManageCredential);
    }

    public ObservableCollection<ModPlatformModSummary> Mods { get; } = [];

    public ObservableCollection<ModPlatformForumThread> Threads { get; } = [];

    public ObservableCollection<ModPlatformForumPost> Posts { get; } = [];

    public ObservableCollection<CommunityReportCategoryOption> ReportCategories { get; } = [];

    public IAsyncRelayCommand SearchCommand { get; }

    public IAsyncRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand RetryCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public IAsyncRelayCommand LoadMoreModsCommand { get; }

    public IAsyncRelayCommand LoadMoreThreadsCommand { get; }

    public IAsyncRelayCommand LoadMorePostsCommand { get; }

    public IAsyncRelayCommand CreateThreadCommand { get; }

    public IAsyncRelayCommand CreateReplyCommand { get; }

    public IAsyncRelayCommand DeletePatCommand { get; }

    public string SearchQuery
    {
        get => _searchQuery;
        set => SetProperty(ref _searchQuery, value);
    }

    public ModPlatformModSummary? SelectedMod
    {
        get => _selectedMod;
        private set
        {
            if (SetProperty(ref _selectedMod, value))
            {
                OnPropertyChanged(nameof(HasSelectedMod));
                CreateThreadCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public ModPlatformForumThread? SelectedThread
    {
        get => _selectedThread;
        private set
        {
            if (SetProperty(ref _selectedThread, value))
            {
                OnPropertyChanged(nameof(HasSelectedThread));
                OnPropertyChanged(nameof(ShowSelectThreadPrompt));
                OnPropertyChanged(nameof(IsSelectedThreadLocked));
                CreateReplyCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string NewThreadTitle
    {
        get => _newThreadTitle;
        set
        {
            if (SetProperty(ref _newThreadTitle, value))
            {
                CreateThreadCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string NewThreadContent
    {
        get => _newThreadContent;
        set
        {
            if (SetProperty(ref _newThreadContent, value))
            {
                CreateThreadCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string ReplyContent
    {
        get => _replyContent;
        set
        {
            if (SetProperty(ref _replyContent, value))
            {
                CreateReplyCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsPatConfigured
    {
        get => _isPatConfigured;
        private set
        {
            if (SetProperty(ref _isPatConfigured, value))
            {
                OnPropertyChanged(nameof(ShowPatGuidance));
                OnPropertyChanged(nameof(IsAuthenticated));
                OnPropertyChanged(nameof(ShowSignInForm));
                OnPropertyChanged(nameof(CanWriteForum));
                OnPropertyChanged(nameof(HasReportPermission));
                CreateThreadCommand.NotifyCanExecuteChanged();
                CreateReplyCommand.NotifyCanExecuteChanged();
                DeletePatCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanDeletePat));
                OnPropertyChanged(nameof(CanReportPosts));
                OnPropertyChanged(nameof(CanReportContent));
            }
        }
    }

    public ModPlatformUser? CurrentUser
    {
        get => _currentUser;
        private set
        {
            if (SetProperty(ref _currentUser, value))
            {
                OnPropertyChanged(nameof(CurrentUsername));
                OnPropertyChanged(nameof(SignedInDisplayText));
                OnPropertyChanged(nameof(IsAuthenticated));
                OnPropertyChanged(nameof(ShowSignInForm));
                OnPropertyChanged(nameof(CanWriteForum));
                OnPropertyChanged(nameof(HasReportPermission));
            }
        }
    }

    public string CurrentUsername => CurrentUser?.Username ?? string.Empty;

    public string SignedInDisplayText => CurrentUser is null
        ? string.Empty
        : Text("CommunitySignedInDisplayText", "Signed in as {0}", CurrentUser.Username);

    public bool IsAuthenticated => IsPatConfigured && CurrentUser is not null;

    public bool SupportsApplicationLogin => _supportsApplicationLogin;

    public bool CanWriteForum => IsAuthenticated && _grantedScopes.Contains("forum:write");

    public bool HasReportPermission => IsAuthenticated
        && (_grantedScopes.Contains("reports:write") || _grantedScopes.Contains("forum:write"));

    public string GrantedScopesText => _grantedScopes.Count == 0
        ? Text("CommunityGrantedScopesNone", "No optional permissions")
        : string.Join(", ", _grantedScopes.Order(StringComparer.Ordinal));

    public bool ShowSignInForm => !IsAuthenticated;

    public bool ShowPatGuidance => !IsAuthenticated;

    public bool IsInitialized => _initialized;

    public bool CanSavePat => !IsBusy && !IsAuthenticated;

    public bool CanDeletePat => !IsBusy && IsPatConfigured;

    public bool CanReportPosts => !IsBusy && CanReportContent;

    public bool IsReportFeatureAvailable
    {
        get => _isReportFeatureAvailable;
        private set
        {
            if (SetProperty(ref _isReportFeatureAvailable, value))
            {
                OnPropertyChanged(nameof(CanReportContent));
                OnPropertyChanged(nameof(CanReportPosts));
            }
        }
    }

    public bool IsReportSubmitting
    {
        get => _isReportSubmitting;
        private set
        {
            if (SetProperty(ref _isReportSubmitting, value))
            {
                OnPropertyChanged(nameof(CanReportContent));
                OnPropertyChanged(nameof(CanReportPosts));
            }
        }
    }

    public bool CanReportContent =>
        IsReportFeatureAvailable && HasReportPermission && !IsReportSubmitting;

    public string? ReportErrorMessage
    {
        get => _reportErrorMessage;
        private set
        {
            if (SetProperty(ref _reportErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasReportError));
            }
        }
    }

    public bool HasReportError => !string.IsNullOrWhiteSpace(ReportErrorMessage);

    public string? ReportStatusMessage
    {
        get => _reportStatusMessage;
        private set
        {
            if (SetProperty(ref _reportStatusMessage, value))
            {
                OnPropertyChanged(nameof(HasReportStatus));
            }
        }
    }

    public bool HasReportStatus => !string.IsNullOrWhiteSpace(ReportStatusMessage);

    public Uri TermsUri
    {
        get => _termsUri;
        private set => SetProperty(ref _termsUri, value);
    }

    public Uri CommunityGuidelinesUri
    {
        get => _communityGuidelinesUri;
        private set => SetProperty(ref _communityGuidelinesUri, value);
    }

    public bool HasSelectedMod => SelectedMod is not null;

    public bool HasSelectedThread => SelectedThread is not null;

    public bool IsSelectedThreadLocked =>
        SelectedThread is { Locked: true } ||
        SelectedThread is { Status: not "open" };

    public bool HasMods => Mods.Count > 0;

    public bool ShowModsEmptyState => !IsBusy && !HasError && Mods.Count == 0;

    public bool HasThreads => Threads.Count > 0;

    public bool ShowThreadsEmptyState =>
        !IsBusy && !HasError && SelectedMod is not null && Threads.Count == 0;

    public bool ShowSelectThreadPrompt =>
        !IsBusy && !HasError && Threads.Count > 0 && SelectedThread is null;

    public bool HasPosts => Posts.Count > 0;

    public bool ShowPostsEmptyState =>
        !IsBusy && !HasError && SelectedThread is not null && Posts.Count == 0;

    public bool CanCancel => IsBusy;

    public bool HasMoreMods => Mods.Count < _totalMods;

    public bool HasMoreThreads => Threads.Count < _totalThreads;

    public bool HasMorePosts => Posts.Count < _totalPosts;

    public string PostsLoadedText => Text(
        "CommunityPostsLoadedText",
        "Loaded {0} of {1} posts",
        Posts.Count,
        _totalPosts);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
        {
            return;
        }

        _activeSearchQuery = null;
        var completed = await RunOperationAsync(
            async token =>
            {
                var meta = await _client.GetMetaAsync(token).ConfigureAwait(true);
                token.ThrowIfCancellationRequested();
                ApplyAuthenticationCapabilities(meta);
                ApplyReportingCapabilities(meta);
                await RestoreAuthenticatedSessionAsync(token).ConfigureAwait(true);
                await LoadModsAsync(
                        page: 1,
                        append: false,
                        query: _activeSearchQuery,
                        cancellationToken: token)
                    .ConfigureAwait(true);
            },
            () => InitializeAsync(cancellationToken),
            cancellationToken).ConfigureAwait(true);
        if (completed)
        {
            _initialized = true;
            OnPropertyChanged(nameof(IsInitialized));
        }
    }

    public async Task SelectModAsync(
        ModPlatformModSummary? mod,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (mod is null || SelectedMod?.Id == mod.Id)
        {
            return;
        }

        SelectedMod = mod;
        SelectedThread = null;
        Replace(Threads, []);
        Replace(Posts, []);
        SetTotalThreads(0);
        SetTotalPosts(0);
        await RunOperationAsync(
            token => LoadThreadsAsync(mod.Id, page: 1, append: false, token),
            () => ReloadThreadsAsync(mod.Id),
            cancellationToken).ConfigureAwait(true);
    }

    public async Task SelectThreadAsync(
        ModPlatformForumThread? thread,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (thread is null || SelectedThread?.Id == thread.Id)
        {
            return;
        }

        SelectedThread = thread;
        Replace(Posts, []);
        SetTotalPosts(0);
        await RunOperationAsync(
            token => LoadPostsAsync(thread.Id, page: 1, append: false, token),
            () => ReloadPostsAsync(thread.Id),
            cancellationToken).ConfigureAwait(true);
    }

    public async Task SignInAsync(
        string username,
        ReadOnlyMemory<char> password,
        ReadOnlyMemory<char> applicationToken,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanSavePat)
        {
            return;
        }

        var normalizedUsername = username?.Trim() ?? string.Empty;
        if (normalizedUsername.Length == 0 || password.IsEmpty || applicationToken.IsEmpty)
        {
            ErrorMessage = Text(
                "CommunityApplicationLoginFieldsRequiredError",
                "Enter the username, password, and application token.");
            return;
        }

        await RunOperationAsync(
            async operationToken =>
            {
                var session = SupportsApplicationLogin
                    ? await _client.VerifyApplicationLoginAsync(
                            normalizedUsername,
                            password,
                            applicationToken,
                            operationToken)
                        .ConfigureAwait(true)
                    : await _client.VerifyApplicationTokenAsync(
                            normalizedUsername,
                            applicationToken,
                            operationToken)
                        .ConfigureAwait(true);
                operationToken.ThrowIfCancellationRequested();
                await _credentials.SaveAsync(applicationToken, operationToken).ConfigureAwait(true);
                operationToken.ThrowIfCancellationRequested();
                ApplyAuthenticatedSession(session);
                StatusMessage = SupportsApplicationLogin
                    ? Text(
                        "CommunityApplicationLoginSuccessStatus",
                        "Signed in successfully. The application token was saved in Windows Credential Manager; the password was not saved.")
                    : Text(
                        "CommunityLegacyTokenLoginSuccessStatus",
                        "The server does not support account-password verification. The application token and username were verified in compatibility mode; the password was not sent or saved.");
            },
            retryOperation: null,
            cancellationToken,
            GetApplicationLoginErrorMessage).ConfigureAwait(true);
    }

    public bool SupportsReportTarget(string targetType) =>
        !string.IsNullOrWhiteSpace(targetType) && _reportTargetTypes.Contains(targetType);

    public bool IsReportInputValid(
        CommunityReportTarget? target,
        string? category,
        string? details)
    {
        if (target is null
            || !CanReportContent
            || !SupportsReportTarget(target.TargetType)
            || target.TargetId == Guid.Empty
            || string.IsNullOrWhiteSpace(category)
            || ReportCategories.All(option => !string.Equals(
                option.Code,
                category,
                StringComparison.Ordinal)))
        {
            return false;
        }

        var length = (details ?? string.Empty).Trim().EnumerateRunes().Count();
        return length is >= ReportDetailsMinimumLength and <= ReportDetailsMaximumLength;
    }

    public void ResetReportFeedback()
    {
        ReportErrorMessage = null;
        ReportStatusMessage = null;
    }

    public async Task<bool> SubmitReportAsync(
        CommunityReportTarget? target,
        string? category,
        string? details,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsReportSubmitting)
        {
            return false;
        }

        ResetReportFeedback();
        if (!IsReportFeatureAvailable || target is null || !SupportsReportTarget(target.TargetType))
        {
            ReportErrorMessage = Text(
                "CommunityReportUnavailableError",
                "Reporting is not available for this content. Review the community guidelines and try again after refreshing.");
            return false;
        }

        if (!IsAuthenticated)
        {
            ReportErrorMessage = Text(
                "CommunityReportPatRequiredError",
                "Save a personal access token with reports:write or forum:write permission, then try again.");
            return false;
        }

        if (!HasReportPermission)
        {
            ReportErrorMessage = Text(
                "CommunityReportScopeError",
                "The saved token needs reports:write or compatible forum:write permission.");
            return false;
        }

        if (!IsReportInputValid(target, category, details))
        {
            ReportErrorMessage = Text(
                "CommunityReportInputInvalid",
                "Choose a category and enter details between 4 and 1,900 characters.");
            return false;
        }

        var normalizedDetails = details!.Trim();
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeReportOperation = source;
        IsReportSubmitting = true;
        try
        {
            await _client.CreateReportAsync(
                    new ModPlatformReportRequest(
                        target.TargetType,
                        target.TargetId,
                        category!,
                        normalizedDetails),
                    source.Token)
                .ConfigureAwait(true);
            source.Token.ThrowIfCancellationRequested();
            ReportStatusMessage = Text(
                "CommunityReportSubmittedStatus",
                "The report was submitted for moderation.");
            StatusMessage = ReportStatusMessage;
            ErrorMessage = null;
            return true;
        }
        catch (OperationCanceledException)
        {
            if (!source.IsCancellationRequested)
            {
                ReportErrorMessage = Text(
                    "CommunityReportTimeoutError",
                    "The report request timed out. Check the network and submit it again.");
            }

            return false;
        }
        catch (Exception exception)
        {
            ReportErrorMessage = GetReportErrorMessage(exception);
            return false;
        }
        finally
        {
            if (ReferenceEquals(_activeReportOperation, source))
            {
                _activeReportOperation = null;
                IsReportSubmitting = false;
            }

            source.Dispose();
        }
    }

    public async Task ReportPostAsync(
        ModPlatformForumPost? post,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var target = post is null
            ? null
            : new CommunityReportTarget(
                ModPlatformReportTargetTypes.ForumPost,
                post.Id,
                post.AuthorName);
        await SubmitReportAsync(
                target,
                ModPlatformReportCategories.Other,
                reason,
                cancellationToken)
            .ConfigureAwait(true);
    }

    public void CancelActiveOperation()
    {
        if (_disposed)
        {
            return;
        }

        _activeOperation?.Cancel();
        _activeReportOperation?.Cancel();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _activeOperation?.Cancel();
        _activeReportOperation?.Cancel();
        _activeOperation?.Dispose();
        _activeReportOperation?.Dispose();
        _activeOperation = null;
        _activeReportOperation = null;
    }

    private bool CanStartOperation() => !IsBusy;

    private bool CanRetry() => !IsBusy && _retryOperation is not null;

    private bool CanManageCredential() => !IsBusy && IsPatConfigured;

    private bool CanLoadMoreMods() => !IsBusy && HasMoreMods;

    private bool CanLoadMoreThreads() =>
        !IsBusy && SelectedMod is not null && HasMoreThreads;

    private bool CanLoadMorePosts() => !IsBusy && SelectedThread is not null && HasMorePosts;

    private bool CanCreateThread()
    {
        var titleLength = NewThreadTitle.Trim().EnumerateRunes().Count();
        var contentLength = NewThreadContent.Trim().EnumerateRunes().Count();
        return !IsBusy
            && CanWriteForum
            && SelectedMod is not null
            && titleLength is >= 4 and <= 160
            && contentLength is >= 1 and <= 100_000;
    }

    private bool CanCreateReply()
    {
        var contentLength = ReplyContent.Trim().EnumerateRunes().Count();
        return !IsBusy
            && CanWriteForum
            && SelectedThread is { Locked: false, Status: "open" }
            && contentLength is >= 1 and <= 100_000;
    }

    private async Task SearchAsync()
    {
        var query = string.IsNullOrWhiteSpace(SearchQuery) ? null : SearchQuery.Trim();
        await ExecuteSearchAsync(query).ConfigureAwait(true);
    }

    private async Task ExecuteSearchAsync(string? query)
    {
        _activeSearchQuery = query;
        SelectedMod = null;
        SelectedThread = null;
        Replace(Mods, []);
        Replace(Threads, []);
        Replace(Posts, []);
        SetTotalMods(0);
        SetTotalThreads(0);
        SetTotalPosts(0);
        await RunOperationAsync(
            token => LoadModsAsync(page: 1, append: false, query, token),
            () => ExecuteSearchAsync(query),
            CancellationToken.None).ConfigureAwait(true);
    }

    private async Task RefreshAsync()
    {
        var selectedModId = SelectedMod?.Id;
        var selectedThreadId = SelectedThread?.Id;
        await RunOperationAsync(
            async token =>
            {
                var meta = await _client.GetMetaAsync(token).ConfigureAwait(true);
                token.ThrowIfCancellationRequested();
                ApplyAuthenticationCapabilities(meta);
                ApplyReportingCapabilities(meta);
                await RestoreAuthenticatedSessionAsync(token).ConfigureAwait(true);
                await LoadModsAsync(
                        page: 1,
                        append: false,
                        query: _activeSearchQuery,
                        cancellationToken: token)
                    .ConfigureAwait(true);
                SelectedMod = Mods.FirstOrDefault(item => item.Id == selectedModId);
                if (SelectedMod is null)
                {
                    SelectedThread = null;
                    Replace(Threads, []);
                    Replace(Posts, []);
                    SetTotalThreads(0);
                    SetTotalPosts(0);
                    return;
                }

                await LoadThreadsAsync(SelectedMod.Id, page: 1, append: false, token)
                    .ConfigureAwait(true);
                SelectedThread = Threads.FirstOrDefault(item => item.Id == selectedThreadId);
                if (SelectedThread is null)
                {
                    Replace(Posts, []);
                    SetTotalPosts(0);
                    return;
                }

                await LoadPostsAsync(SelectedThread.Id, page: 1, append: false, token).ConfigureAwait(true);
            },
            RefreshAsync,
            CancellationToken.None).ConfigureAwait(true);
    }

    private async Task RetryAsync()
    {
        var retry = _retryOperation;
        if (retry is not null)
        {
            await retry().ConfigureAwait(true);
        }
    }

    private async Task LoadMoreModsAsync()
    {
        var nextPage = _modsPage + 1;
        var query = _activeSearchQuery;
        await RunOperationAsync(
            token => LoadModsAsync(nextPage, append: true, query, token),
            LoadMoreModsAsync,
            CancellationToken.None).ConfigureAwait(true);
    }

    private async Task LoadMoreThreadsAsync()
    {
        var mod = SelectedMod;
        if (mod is null)
        {
            return;
        }

        var nextPage = _threadsPage + 1;
        await RunOperationAsync(
            token => LoadThreadsAsync(mod.Id, nextPage, append: true, token),
            LoadMoreThreadsAsync,
            CancellationToken.None).ConfigureAwait(true);
    }

    private async Task LoadMorePostsAsync()
    {
        var thread = SelectedThread;
        if (thread is null)
        {
            return;
        }

        var nextPage = _postsPage + 1;
        await RunOperationAsync(
            token => LoadPostsAsync(thread.Id, nextPage, append: true, token),
            LoadMorePostsAsync,
            CancellationToken.None).ConfigureAwait(true);
    }

    private async Task CreateThreadAsync()
    {
        var mod = SelectedMod;
        if (mod is null)
        {
            return;
        }

        var title = NewThreadTitle;
        var content = NewThreadContent;
        ModPlatformForumThread? created = null;
        var createdSuccessfully = await RunOperationAsync(
            async token =>
            {
                var result = await _client
                    .CreateThreadAsync(mod.Id, title, content, token)
                    .ConfigureAwait(true);
                token.ThrowIfCancellationRequested();
                created = result;
            },
            retryOperation: null,
            CancellationToken.None).ConfigureAwait(true);
        if (!createdSuccessfully || created is null)
        {
            return;
        }

        NewThreadTitle = string.Empty;
        NewThreadContent = string.Empty;
        if (Threads.All(item => item.Id != created.Id))
        {
            Threads.Insert(0, created);
        }

        SetTotalThreads(Math.Max(_totalThreads + 1, Threads.Count));
        SelectedThread = created;
        Replace(Posts, []);
        SetTotalPosts(0);
        RaiseCollectionState();

        var postsLoaded = await RunOperationAsync(
            token => LoadPostsAsync(created.Id, page: 1, append: false, token),
            () => ReloadPostsAsync(created.Id),
            CancellationToken.None).ConfigureAwait(true);
        if (postsLoaded)
        {
            StatusMessage = Text("CommunityThreadCreatedStatus", "The discussion was created.");
        }
    }

    private async Task CreateReplyAsync()
    {
        var thread = SelectedThread;
        if (thread is null)
        {
            return;
        }

        var content = ReplyContent;
        await RunOperationAsync(
            async token =>
            {
                var created = await _client
                    .CreatePostAsync(thread.Id, content, token)
                    .ConfigureAwait(true);
                token.ThrowIfCancellationRequested();
                if (SelectedThread?.Id != thread.Id)
                {
                    return;
                }

                ReplyContent = string.Empty;
                if (Posts.All(item => item.Id != created.Id))
                {
                    Posts.Add(created);
                }

                SetTotalPosts(Math.Max(_totalPosts + 1, Posts.Count));
                StatusMessage = Text("CommunityReplyCreatedStatus", "Your reply was posted.");
            },
            retryOperation: null,
            CancellationToken.None).ConfigureAwait(true);
    }

    private async Task DeletePatAsync()
    {
        if (!CanDeletePat)
        {
            return;
        }

        await RunOperationAsync(
            async token =>
            {
                await _credentials.DeleteAsync(token).ConfigureAwait(true);
                token.ThrowIfCancellationRequested();
                ClearAuthenticatedSession(hasStoredCredential: false);
                StatusMessage = Text(
                    "CommunityPatDeletedStatus",
                    "The local personal access token was removed. Revoke it on the website if needed.");
            },
            DeletePatAsync,
            CancellationToken.None).ConfigureAwait(true);
    }

    private Task ReloadThreadsAsync(Guid modId)
    {
        if (SelectedMod?.Id != modId)
        {
            return Task.CompletedTask;
        }

        return RunOperationAsync(
            token => LoadThreadsAsync(modId, page: 1, append: false, token),
            () => ReloadThreadsAsync(modId),
            CancellationToken.None);
    }

    private Task ReloadPostsAsync(Guid threadId)
    {
        if (SelectedThread?.Id != threadId)
        {
            return Task.CompletedTask;
        }

        return RunOperationAsync(
            token => LoadPostsAsync(threadId, page: 1, append: false, token),
            () => ReloadPostsAsync(threadId),
            CancellationToken.None);
    }

    private async Task LoadModsAsync(
        int page,
        bool append,
        string? query,
        CancellationToken cancellationToken)
    {
        var result = await _client.GetModsAsync(
            new ModPlatformSearchOptions(
                Page: page,
                PageSize: ModPageSize,
                Query: query),
            cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(_activeSearchQuery, query, StringComparison.Ordinal))
        {
            return;
        }

        var previousCount = Mods.Count;
        if (append)
        {
            AppendDistinct(Mods, result.Data, static item => item.Id);
        }
        else
        {
            Replace(Mods, result.Data);
        }

        _modsPage = page;
        SetTotalMods(
            append && Mods.Count == previousCount
                ? Mods.Count
                : Math.Max(result.Total, Mods.Count));
        RaiseCollectionState();
    }

    private async Task LoadThreadsAsync(
        Guid modId,
        int page,
        bool append,
        CancellationToken cancellationToken)
    {
        var result = await _client
            .GetThreadsAsync(modId, page, ThreadPageSize, cancellationToken)
            .ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        if (SelectedMod?.Id != modId)
        {
            return;
        }

        var previousCount = Threads.Count;
        if (append)
        {
            AppendDistinct(Threads, result.Data, static item => item.Id);
        }
        else
        {
            Replace(Threads, result.Data);
        }

        _threadsPage = page;
        SetTotalThreads(
            append && Threads.Count == previousCount
                ? Threads.Count
                : Math.Max(result.Total, Threads.Count));
        RaiseCollectionState();
    }

    private async Task LoadPostsAsync(
        Guid threadId,
        int page,
        bool append,
        CancellationToken cancellationToken)
    {
        var result = await _client
            .GetPostsAsync(threadId, page, PostPageSize, cancellationToken)
            .ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        if (SelectedThread?.Id != threadId)
        {
            return;
        }

        var previousCount = Posts.Count;
        if (!append)
        {
            Replace(Posts, result.Data);
        }
        else
        {
            foreach (var post in result.Data)
            {
                if (Posts.All(existing => existing.Id != post.Id))
                {
                    Posts.Add(post);
                }
            }
        }

        _postsPage = page;
        SetTotalPosts(
            append && Posts.Count == previousCount
                ? Posts.Count
                : Math.Max(result.Total, Posts.Count));
        RaiseCollectionState();
    }

    private void ApplyAuthenticatedSession(ModPlatformAuthSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _grantedScopes.Clear();
        foreach (var scope in session.Scopes)
        {
            if (!string.IsNullOrWhiteSpace(scope))
            {
                _grantedScopes.Add(scope);
            }
        }

        CurrentUser = session.User;
        IsPatConfigured = true;
        RaiseAuthenticationCapabilities();
    }

    private void ClearAuthenticatedSession(bool hasStoredCredential)
    {
        _grantedScopes.Clear();
        CurrentUser = null;
        IsPatConfigured = hasStoredCredential;
        RaiseAuthenticationCapabilities();
    }

    private void RaiseAuthenticationCapabilities()
    {
        OnPropertyChanged(nameof(CanWriteForum));
        OnPropertyChanged(nameof(HasReportPermission));
        OnPropertyChanged(nameof(GrantedScopesText));
        OnPropertyChanged(nameof(CanReportContent));
        OnPropertyChanged(nameof(CanReportPosts));
        CreateThreadCommand.NotifyCanExecuteChanged();
        CreateReplyCommand.NotifyCanExecuteChanged();
    }

    private async Task RestoreAuthenticatedSessionAsync(CancellationToken cancellationToken)
    {
        var isConfigured = await _credentials
            .IsConfiguredAsync(cancellationToken)
            .ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        if (!isConfigured)
        {
            ClearAuthenticatedSession(hasStoredCredential: false);
            return;
        }

        IsPatConfigured = true;

        try
        {
            var session = await _client
                .GetAuthenticatedSessionAsync(cancellationToken)
                .ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            ApplyAuthenticatedSession(session);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            (exception as IModPlatformServiceError)?.Code is "unauthorized" or "forbidden")
        {
            await _credentials.DeleteAsync(cancellationToken).ConfigureAwait(true);
            ClearAuthenticatedSession(hasStoredCredential: false);
            StatusMessage = Text(
                "CommunityStoredLoginExpiredStatus",
                "The saved application token is invalid or expired. Sign in again with a current token.");
        }
        catch (Exception exception) when (exception is HttpRequestException
            || exception is IModPlatformServiceError)
        {
            // A transient service failure must not delete a potentially valid Credential Manager entry.
            ClearAuthenticatedSession(hasStoredCredential: true);
            StatusMessage = Text(
                "CommunityStoredLoginUnavailableStatus",
                "The saved account could not be verified right now. Public browsing remains available; refresh to try again.");
        }
    }

    private async Task<bool> RunOperationAsync(
        Func<CancellationToken, Task> operation,
        Func<Task>? retryOperation,
        CancellationToken cancellationToken,
        Func<Exception, string>? errorMessageFactory = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(operation);
        _activeOperation?.Cancel();
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeOperation = source;
        _retryOperation = null;
        ErrorMessage = null;
        StatusMessage = null;
        SetBusy(true);

        try
        {
            await operation(source.Token).ConfigureAwait(true);
            return ReferenceEquals(_activeOperation, source);
        }
        catch (OperationCanceledException)
        {
            if (!ReferenceEquals(_activeOperation, source))
            {
                return false;
            }

            if (!source.IsCancellationRequested)
            {
                ErrorMessage = Text(
                    "CommunityTimeoutError",
                    "The community request timed out. Check the network and try again.");
                _retryOperation = retryOperation;
                RaiseCollectionState();
            }

            return false;
        }
        catch (Exception exception)
        {
            if (!ReferenceEquals(_activeOperation, source))
            {
                return false;
            }

            ErrorMessage = errorMessageFactory?.Invoke(exception) ?? exception switch
            {
                HttpRequestException => Text(
                    "CommunityNetworkError",
                    "The community service could not be reached. Check the network and try again."),
                _ => Text(
                    "CommunityOperationFailed",
                    "The community operation failed. Review the inputs and try again.")
            };
            _retryOperation = retryOperation;
            RaiseCollectionState();
            return false;
        }
        finally
        {
            if (ReferenceEquals(_activeOperation, source))
            {
                _activeOperation = null;
                SetBusy(false);
            }

            source.Dispose();
        }
    }

    private void SetBusy(bool value)
    {
        IsBusy = value;
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanSavePat));
        OnPropertyChanged(nameof(CanDeletePat));
        OnPropertyChanged(nameof(CanReportPosts));
        OnPropertyChanged(nameof(CanReportContent));
        RaiseCollectionState();
        SearchCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        LoadMoreModsCommand.NotifyCanExecuteChanged();
        LoadMoreThreadsCommand.NotifyCanExecuteChanged();
        LoadMorePostsCommand.NotifyCanExecuteChanged();
        CreateThreadCommand.NotifyCanExecuteChanged();
        CreateReplyCommand.NotifyCanExecuteChanged();
        DeletePatCommand.NotifyCanExecuteChanged();
    }

    private void SetTotalMods(long value)
    {
        _totalMods = Math.Max(0, value);
        if (_totalMods == 0)
        {
            _modsPage = 0;
        }

        OnPropertyChanged(nameof(HasMoreMods));
        LoadMoreModsCommand.NotifyCanExecuteChanged();
    }

    private void SetTotalThreads(long value)
    {
        _totalThreads = Math.Max(0, value);
        if (_totalThreads == 0)
        {
            _threadsPage = 0;
        }

        OnPropertyChanged(nameof(HasMoreThreads));
        LoadMoreThreadsCommand.NotifyCanExecuteChanged();
    }

    private void SetTotalPosts(long value)
    {
        _totalPosts = Math.Max(0, value);
        if (_totalPosts == 0)
        {
            _postsPage = 0;
        }

        OnPropertyChanged(nameof(HasMorePosts));
        OnPropertyChanged(nameof(PostsLoadedText));
        LoadMorePostsCommand.NotifyCanExecuteChanged();
    }

    private void RaiseCollectionState()
    {
        OnPropertyChanged(nameof(HasMods));
        OnPropertyChanged(nameof(ShowModsEmptyState));
        OnPropertyChanged(nameof(HasThreads));
        OnPropertyChanged(nameof(ShowThreadsEmptyState));
        OnPropertyChanged(nameof(ShowSelectThreadPrompt));
        OnPropertyChanged(nameof(HasPosts));
        OnPropertyChanged(nameof(ShowPostsEmptyState));
        OnPropertyChanged(nameof(HasMoreMods));
        OnPropertyChanged(nameof(HasMoreThreads));
        OnPropertyChanged(nameof(PostsLoadedText));
        OnPropertyChanged(nameof(HasMorePosts));
    }

    private void ApplyAuthenticationCapabilities(ModPlatformMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);
        var value = meta.Features.Contains("application_login_v1", StringComparer.Ordinal);
        if (_supportsApplicationLogin != value)
        {
            _supportsApplicationLogin = value;
            OnPropertyChanged(nameof(SupportsApplicationLogin));
        }
    }

    private void ApplyReportingCapabilities(ModPlatformMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);
        var reporting = meta.Reporting;
        var featureAvailable = meta.Features.Contains("content_reports_v1", StringComparer.Ordinal)
            && reporting is not null;

        _reportTargetTypes.Clear();
        ReportCategories.Clear();
        if (featureAvailable)
        {
            foreach (var targetType in reporting!.TargetTypes)
            {
                if (ModPlatformReportTargetTypes.All.Contains(targetType))
                {
                    _reportTargetTypes.Add(targetType);
                }
            }

            foreach (var category in reporting.Categories)
            {
                if (ModPlatformReportCategories.All.Contains(category))
                {
                    ReportCategories.Add(new CommunityReportCategoryOption(
                        category,
                        GetReportCategoryDisplayName(category)));
                }
            }

            TermsUri = ResolvePolicyUri(reporting.TermsUrl, DefaultTermsUri);
            CommunityGuidelinesUri = ResolvePolicyUri(
                reporting.CommunityGuidelinesUrl,
                DefaultCommunityGuidelinesUri);
        }
        else
        {
            TermsUri = DefaultTermsUri;
            CommunityGuidelinesUri = DefaultCommunityGuidelinesUri;
        }

        IsReportFeatureAvailable = featureAvailable
            && _reportTargetTypes.Count > 0
            && ReportCategories.Count > 0;
    }

    private string GetReportCategoryDisplayName(string category) => category switch
    {
        ModPlatformReportCategories.Spam => Text("CommunityReportCategorySpam", "Spam or misleading content"),
        ModPlatformReportCategories.Harassment => Text("CommunityReportCategoryHarassment", "Harassment or bullying"),
        ModPlatformReportCategories.HateSpeech => Text("CommunityReportCategoryHateSpeech", "Hate speech"),
        ModPlatformReportCategories.SexualContent => Text("CommunityReportCategorySexualContent", "Sexual content"),
        ModPlatformReportCategories.Violence => Text("CommunityReportCategoryViolence", "Violence or threats"),
        ModPlatformReportCategories.IllegalContent => Text("CommunityReportCategoryIllegalContent", "Illegal content"),
        ModPlatformReportCategories.Malware => Text("CommunityReportCategoryMalware", "Malware or harmful files"),
        ModPlatformReportCategories.Copyright => Text("CommunityReportCategoryCopyright", "Copyright infringement"),
        ModPlatformReportCategories.Privacy => Text("CommunityReportCategoryPrivacy", "Privacy violation"),
        ModPlatformReportCategories.Impersonation => Text("CommunityReportCategoryImpersonation", "Impersonation"),
        ModPlatformReportCategories.ChildSafety => Text("CommunityReportCategoryChildSafety", "Child safety"),
        ModPlatformReportCategories.Other => Text("CommunityReportCategoryOther", "Other violation"),
        _ => category
    };

    private string GetReportErrorMessage(Exception exception)
    {
        var code = (exception as IModPlatformServiceError)?.Code;
        return code switch
        {
            "already_reported" => Text(
                "CommunityReportDuplicateError",
                "You already reported this content. Moderators can review the existing report."),
            "invalid_report_target_type" or "not_found" => Text(
                "CommunityReportTargetUnavailableError",
                "This content is no longer available for reporting. Refresh the community page and try again."),
            "invalid_report_category" => Text(
                "CommunityReportCategoryInvalidError",
                "That category is no longer accepted. Refresh the category list and choose another category."),
            "invalid_report_details" => Text(
                "CommunityReportDetailsInvalidError",
                "Enter report details between 4 and 1,900 characters."),
            "unauthorized" => Text(
                "CommunityReportUnauthorizedError",
                "The saved personal access token is invalid or expired. Replace it and submit the report again."),
            "forbidden" => Text(
                "CommunityReportScopeError",
                "The saved token needs reports:write or compatible forum:write permission."),
            "rate_limited" => Text(
                "CommunityReportRateLimitedError",
                "Too many reports were submitted recently. Wait a moment, then try again."),
            "security_service_unavailable" => Text(
                "CommunityReportSecurityUnavailableError",
                "Reporting is temporarily unavailable because the security service is offline. Try again later."),
            "request_timeout" => Text(
                "CommunityReportTimeoutError",
                "The report request timed out. Check the network and submit it again."),
            "network_error" => Text(
                "CommunityReportNetworkError",
                "The report service could not be reached. Check the network and submit it again."),
            "invalid_response" => Text(
                "CommunityReportInvalidResponseError",
                "The report service returned an unexpected response. Refresh and try again later."),
            _ when exception is HttpRequestException => Text(
                "CommunityReportNetworkError",
                "The report service could not be reached. Check the network and submit it again."),
            _ => Text(
                "CommunityReportFailedError",
                "The report could not be submitted. Review the details and try again later.")
        };
    }

    private string GetApplicationLoginErrorMessage(Exception exception)
    {
        var code = (exception as IModPlatformServiceError)?.Code;
        return code switch
        {
            "unauthorized" or "invalid_credentials" => Text(
                "CommunityApplicationLoginUnauthorizedError",
                "The username, password, or application token is incorrect or expired."),
            "forbidden" => Text(
                "CommunityApplicationLoginScopeError",
                "The account or application token is not permitted to use this sign-in endpoint."),
            "rate_limited" => Text(
                "CommunityApplicationLoginRateLimitedError",
                "Too many sign-in attempts were made. Wait a moment, then try again."),
            "request_timeout" => Text(
                "CommunityApplicationLoginTimeoutError",
                "The sign-in request timed out. Check the network and try again."),
            "network_error" => Text(
                "CommunityApplicationLoginNetworkError",
                "The account service could not be reached. Check the network and try again."),
            "invalid_response" => Text(
                "CommunityApplicationLoginInvalidResponseError",
                "The account service returned an unexpected response. Try again later."),
            _ when exception is HttpRequestException => Text(
                "CommunityApplicationLoginNetworkError",
                "The account service could not be reached. Check the network and try again."),
            _ => Text(
                "CommunityApplicationLoginFailedError",
                "The account could not be verified. Check all three fields and try again.")
        };
    }

    private static Uri ResolvePolicyUri(string value, Uri fallback) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Fragment)
            ? uri
            : fallback;

    private string Text(string key, string fallback, params object?[] arguments) =>
        _text.GetText(key, fallback, arguments);

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }

    private static void AppendDistinct<T>(
        ObservableCollection<T> target,
        IEnumerable<T> values,
        Func<T, Guid> getId)
    {
        var existingIds = target.Select(getId).ToHashSet();
        foreach (var value in values)
        {
            if (existingIds.Add(getId(value)))
            {
                target.Add(value);
            }
        }
    }
}

public sealed record CommunityReportCategoryOption(string Code, string DisplayName);

public sealed record CommunityReportTarget(string TargetType, Guid TargetId, string DisplayName);
