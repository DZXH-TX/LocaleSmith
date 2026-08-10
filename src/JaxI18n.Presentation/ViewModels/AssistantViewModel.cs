using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JaxI18n.Core.Models;
using JaxI18n.Presentation.Abstractions;
using JaxI18n.Presentation.Models;

namespace JaxI18n.Presentation.ViewModels;

public sealed class AssistantChatMessageViewModel : ObservableObject
{
    public AssistantChatMessageViewModel(
        ModelMessageRole role,
        string content,
        IUiTextProvider? text = null)
    {
        if (role is not (ModelMessageRole.User or ModelMessageRole.Assistant))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        var textProvider = text ?? FallbackUiTextProvider.Instance;
        Role = role;
        Content = content;
        RoleLabel = role == ModelMessageRole.User
            ? textProvider.GetText("AssistantRoleUser", "You")
            : textProvider.GetText("AssistantRoleModel", "Assistant");
    }

    public ModelMessageRole Role { get; }

    public string RoleLabel { get; }

    public string Content { get; }

    public bool IsUser => Role == ModelMessageRole.User;
}

public sealed class AssistantViewModel : ViewModelBase, IDisposable
{
    private readonly IModelAssistantService _assistantService;
    private readonly IModelSelectionService _selectionService;
    private readonly IUiTextProvider _text;
    private readonly List<ModelMessage> _conversation = [];
    private ModelSourceOptionViewModel? _selectedModelSource;
    private string _draft = string.Empty;
    private CancellationTokenSource? _sendCancellation;
    private bool _disposed;

    public AssistantViewModel(
        IModelAssistantService assistantService,
        IModelSelectionService selectionService,
        IUiTextProvider? text = null)
    {
        _assistantService = assistantService ?? throw new ArgumentNullException(nameof(assistantService));
        _selectionService = selectionService ?? throw new ArgumentNullException(nameof(selectionService));
        _text = text ?? FallbackUiTextProvider.Instance;
        SendCommand = new AsyncRelayCommand(SendAsync, CanSend);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        ClearCommand = new RelayCommand(Clear, () => !IsBusy && Messages.Count > 0);
    }

    public event EventHandler<CliProposalsRequestedEventArgs>? CliProposalsRequested;

    public ObservableCollection<ModelSourceOptionViewModel> ModelSources { get; } = [];

    public ObservableCollection<AssistantChatMessageViewModel> Messages { get; } = [];

    public IAsyncRelayCommand SendCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public IRelayCommand ClearCommand { get; }

    public ModelSourceOptionViewModel? SelectedModelSource
    {
        get => _selectedModelSource;
        set
        {
            string? previousSourceId = _selectedModelSource?.Id;
            if (SetProperty(ref _selectedModelSource, value))
            {
                if (previousSourceId is not null &&
                    !string.Equals(previousSourceId, value?.Id, StringComparison.Ordinal))
                {
                    _sendCancellation?.Cancel();
                    ResetConversation();
                    StatusMessage = Text(
                        "AssistantModelChangedConversationCleared",
                        "Model source changed. The previous conversation was cleared to prevent cross-provider disclosure.");
                }

                OnPropertyChanged(nameof(HasModelSource));
                SendCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasModelSource => SelectedModelSource is not null;

    public string Draft
    {
        get => _draft;
        set
        {
            if (SetProperty(ref _draft, value))
            {
                OnPropertyChanged(nameof(DraftLength));
                SendCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public int DraftLength => Draft.Length;

    public bool IsConversationEmpty => Messages.Count == 0;

    public void RefreshModelSources()
    {
        ThrowIfDisposed();
        string? selectedId = SelectedModelSource?.Id ?? _selectionService.SelectedSource?.Id;
        ModelSources.Clear();
        foreach (ModelSource source in _selectionService.Sources)
        {
            ModelSources.Add(new ModelSourceOptionViewModel(source));
        }

        SelectedModelSource = ModelSources.FirstOrDefault(source => source.Id == selectedId)
            ?? ModelSources.FirstOrDefault();
        ErrorMessage = ModelSources.Count == 0
            ? Text("AssistantNoModel", "Configure a model source before using the assistant.")
            : null;
    }

    public void ReportCliProposalReviewFailure()
    {
        ThrowIfDisposed();
        ErrorMessage = Text(
            "AssistantCliReviewFailed",
            "The command proposal could not be opened for review. Nothing was executed.");
    }

    private bool CanSend() =>
        !IsBusy &&
        SelectedModelSource is not null &&
        !string.IsNullOrWhiteSpace(Draft);

    private async Task SendAsync()
    {
        ThrowIfDisposed();
        if (!CanSend() || SelectedModelSource is null)
        {
            return;
        }

        string text = Draft;
        string sourceId = SelectedModelSource.Id;
        var userMessage = new ModelMessage(ModelMessageRole.User, text);
        _conversation.Add(userMessage);
        Messages.Add(new AssistantChatMessageViewModel(ModelMessageRole.User, text, _text));
        OnPropertyChanged(nameof(IsConversationEmpty));
        Draft = string.Empty;
        ErrorMessage = null;
        StatusMessage = Text("AssistantThinking", "The model is working…");
        IsBusy = true;
        NotifyCommandStates();
        using var cancellation = new CancellationTokenSource();
        _sendCancellation = cancellation;
        try
        {
            ModelAssistantCompletion completion = await _assistantService
                .CompleteAsync(sourceId, _conversation.ToArray(), cancellation.Token)
                .ConfigureAwait(true);
            if (!string.Equals(SelectedModelSource?.Id, sourceId, StringComparison.Ordinal))
            {
                StatusMessage = Text(
                    "AssistantModelChangedConversationCleared",
                    "Model source changed. The previous conversation was cleared to prevent cross-provider disclosure.");
                return;
            }

            if (string.IsNullOrWhiteSpace(completion.Content))
            {
                throw new InvalidDataException("The model returned an empty assistant response.");
            }

            var assistantMessage = new ModelMessage(ModelMessageRole.Assistant, completion.Content);
            _conversation.Add(assistantMessage);
            Messages.Add(new AssistantChatMessageViewModel(
                ModelMessageRole.Assistant,
                completion.Content,
                _text));
            OnPropertyChanged(nameof(IsConversationEmpty));
            StatusMessage = completion.ProposedCommands.Count == 0
                ? Text("AssistantComplete", "Response complete.")
                : Text(
                    "AssistantProposalReviewRequired",
                    "Response complete. {0} command proposal(s) require separate review.",
                    completion.ProposedCommands.Count);
            if (completion.ProposedCommands.Count > 0)
            {
                CliProposalsRequested?.Invoke(
                    this,
                    new CliProposalsRequestedEventArgs(completion.ProposedCommands));
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            RollBackPendingUserMessage(userMessage, text, sourceId);
            StatusMessage = string.Equals(SelectedModelSource?.Id, sourceId, StringComparison.Ordinal)
                ? Text("AssistantCancelled", "The assistant request was cancelled.")
                : Text(
                    "AssistantModelChangedConversationCleared",
                    "Model source changed. The previous conversation was cleared to prevent cross-provider disclosure.");
        }
        catch (ModelServiceException exception)
        {
            RollBackPendingUserMessage(userMessage, text, sourceId);
            ErrorMessage = Text(
                "AssistantRequestFailedWithDetails",
                "The assistant request failed: {0}",
                exception.Message);
            StatusMessage = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RollBackPendingUserMessage(userMessage, text, sourceId);
            ErrorMessage = Text(
                "AssistantRequestFailed",
                "The assistant request failed. Check the selected model source and try again.");
            StatusMessage = null;
        }
        finally
        {
            if (ReferenceEquals(_sendCancellation, cancellation))
            {
                _sendCancellation = null;
            }

            IsBusy = false;
            NotifyCommandStates();
        }
    }

    private void Cancel() => _sendCancellation?.Cancel();

    private void Clear()
    {
        if (IsBusy)
        {
            return;
        }

        ResetConversation();
        ErrorMessage = null;
        StatusMessage = Text("AssistantCleared", "Conversation cleared.");
        NotifyCommandStates();
    }

    private void NotifyCommandStates()
    {
        SendCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
    }

    private void ResetConversation()
    {
        _conversation.Clear();
        Messages.Clear();
        OnPropertyChanged(nameof(IsConversationEmpty));
    }

    private void RollBackPendingUserMessage(
        ModelMessage userMessage,
        string originalText,
        string sourceId)
    {
        if (_conversation.Count > 0 && ReferenceEquals(_conversation[^1], userMessage))
        {
            _conversation.RemoveAt(_conversation.Count - 1);
        }

        if (Messages.Count > 0 &&
            Messages[^1].Role == ModelMessageRole.User &&
            string.Equals(Messages[^1].Content, originalText, StringComparison.Ordinal))
        {
            Messages.RemoveAt(Messages.Count - 1);
            OnPropertyChanged(nameof(IsConversationEmpty));
        }

        if (string.Equals(SelectedModelSource?.Id, sourceId, StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(Draft))
        {
            Draft = originalText;
        }
    }

    private string Text(string key, string fallback, params object?[] arguments) =>
        _text.GetText(key, fallback, arguments);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _sendCancellation?.Cancel();
        _sendCancellation?.Dispose();
        _sendCancellation = null;
        _disposed = true;
    }
}

public sealed class CliProposalsRequestedEventArgs(IReadOnlyList<CliCommand> commands) : EventArgs
{
    public IReadOnlyList<CliCommand> Commands { get; } =
        commands?.ToArray() ?? throw new ArgumentNullException(nameof(commands));
}
