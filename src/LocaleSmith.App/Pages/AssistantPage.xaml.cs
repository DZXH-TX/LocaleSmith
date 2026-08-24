using System.Collections.Specialized;
using System.ComponentModel;
using LocaleSmith.App.Dialogs;
using LocaleSmith.App.Services;
using LocaleSmith.Core.Models;
using LocaleSmith.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LocaleSmith.App.Pages;

public sealed partial class AssistantPage : Page
{
    private const int MaximumQueuedProposals = 4;
    private const double FollowLatestThreshold = 48;
    private readonly Queue<CliCommand> _pendingProposals = [];
    private readonly HashSet<AssistantChatMessageViewModel> _observedMessages = [];
    private ContentDialog? _activeDialog;
    private ScrollViewer? _conversationScrollViewer;
    private bool _followLatest = true;
    private bool _isDrainingDialogs;
    private bool _isSubscribed;
    private bool _scrollQueued;
    private int _navigationGeneration;

    private AssistantViewModel ViewModel { get; }

    public AssistantPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<AssistantViewModel>();
        DataContext = ViewModel;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (!_isSubscribed)
        {
            ViewModel.CliProposalsRequested += OnCliProposalsRequested;
            ViewModel.Messages.CollectionChanged += OnMessagesChanged;
            _isSubscribed = true;
            _navigationGeneration++;
            SynchronizeMessageObservers();
        }

        ViewModel.RefreshModelSources();
        ViewModel.RefreshProjects();
        ViewModel.PublishPendingCliProposals();
        StartPendingProposalDrain();
        BindConversationScrollViewer();
        QueueScrollToLatest(force: true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _navigationGeneration++;
        if (_isSubscribed)
        {
            ViewModel.CliProposalsRequested -= OnCliProposalsRequested;
            ViewModel.Messages.CollectionChanged -= OnMessagesChanged;
            ClearMessageObservers();
            UnbindConversationScrollViewer();
            _isSubscribed = false;
            _scrollQueued = false;
        }

        try
        {
            _activeDialog?.Hide();
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not AccessViolationException)
        {
            // The dialog can finish between Unloaded and this UI-thread callback.
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.NewItems?.OfType<AssistantChatMessageViewModel>().Any(static message => message.IsUser) == true)
        {
            _followLatest = true;
        }

        SynchronizeMessageObservers();
        QueueScrollToLatest();
    }

    private void SynchronizeMessageObservers()
    {
        var current = ViewModel.Messages.ToHashSet();
        foreach (AssistantChatMessageViewModel message in _observedMessages
                     .Where(message => !current.Contains(message))
                     .ToArray())
        {
            message.PropertyChanged -= OnMessagePropertyChanged;
            message.Activities.CollectionChanged -= OnMessageActivitiesChanged;
            _observedMessages.Remove(message);
        }

        foreach (AssistantChatMessageViewModel message in current)
        {
            if (!_observedMessages.Add(message))
            {
                continue;
            }

            message.PropertyChanged += OnMessagePropertyChanged;
            message.Activities.CollectionChanged += OnMessageActivitiesChanged;
        }
    }

    private void ClearMessageObservers()
    {
        foreach (AssistantChatMessageViewModel message in _observedMessages)
        {
            message.PropertyChanged -= OnMessagePropertyChanged;
            message.Activities.CollectionChanged -= OnMessageActivitiesChanged;
        }

        _observedMessages.Clear();
    }

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is (nameof(AssistantChatMessageViewModel.Content) or
            nameof(AssistantChatMessageViewModel.HasContent) or
            nameof(AssistantChatMessageViewModel.IsRunning) or
            nameof(AssistantChatMessageViewModel.HasActivities) or
            nameof(AssistantChatMessageViewModel.TaskStatus) or
            nameof(AssistantChatMessageViewModel.HasTaskStatus) or
            nameof(AssistantChatMessageViewModel.HasUsage) or
            nameof(AssistantChatMessageViewModel.UsageSummary)) &&
            sender is AssistantChatMessageViewModel message &&
            ViewModel.Messages.Count > 0 &&
            ReferenceEquals(message, ViewModel.Messages[^1]))
        {
            QueueScrollToLatest();
        }
    }

    private void OnMessageActivitiesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (ViewModel.Messages.Count > 0 && ReferenceEquals(sender, ViewModel.Messages[^1].Activities))
        {
            QueueScrollToLatest();
        }
    }

    private void QueueScrollToLatest(bool force = false)
    {
        if (!_isSubscribed ||
            _scrollQueued ||
            ViewModel.Messages.Count == 0 ||
            (!force && !_followLatest))
        {
            return;
        }

        _scrollQueued = true;
        int generation = _navigationGeneration;
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                _scrollQueued = false;
                if (_isSubscribed &&
                    generation == _navigationGeneration &&
                    ViewModel.Messages.Count > 0)
                {
                    BindConversationScrollViewer();
                    ConversationList.ScrollIntoView(ViewModel.Messages[^1]);
                }
            }))
        {
            _scrollQueued = false;
        }
    }

    private void BindConversationScrollViewer()
    {
        ScrollViewer? scrollViewer = FindDescendant<ScrollViewer>(ConversationList);
        if (ReferenceEquals(_conversationScrollViewer, scrollViewer))
        {
            return;
        }

        UnbindConversationScrollViewer();
        _conversationScrollViewer = scrollViewer;
        if (_conversationScrollViewer is not null)
        {
            _conversationScrollViewer.ViewChanged += OnConversationViewChanged;
        }
    }

    private void UnbindConversationScrollViewer()
    {
        if (_conversationScrollViewer is not null)
        {
            _conversationScrollViewer.ViewChanged -= OnConversationViewChanged;
            _conversationScrollViewer = null;
        }
    }

    private void OnConversationViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (sender is ScrollViewer scrollViewer)
        {
            _followLatest = scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset <= FollowLatestThreshold;
        }
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private async void OnCliProposalsRequested(
        object? sender,
        CliProposalsRequestedEventArgs args)
    {
        foreach (CliCommand command in args.Commands)
        {
            if (_pendingProposals.Count >= MaximumQueuedProposals)
            {
                break;
            }

            _pendingProposals.Enqueue(command);
        }

        if (_isDrainingDialogs)
        {
            return;
        }

        _isDrainingDialogs = true;
        int generation = _navigationGeneration;
        try
        {
            var factory = App.Services.GetRequiredService<CliConfirmationViewModelFactory>();
            while (_isSubscribed &&
                   generation == _navigationGeneration &&
                   _pendingProposals.TryPeek(out CliCommand? command))
            {
                var confirmation = await factory.CreateAsync(command).ConfigureAwait(true);
                if (!_isSubscribed || generation != _navigationGeneration || XamlRoot is null)
                {
                    confirmation.Dispose();
                    break;
                }

                if (!_pendingProposals.TryDequeue(out CliCommand? claimedCommand) ||
                    !ReferenceEquals(claimedCommand, command))
                {
                    confirmation.Dispose();
                    continue;
                }

                var dialog = new CliConfirmationDialog(confirmation)
                {
                    XamlRoot = XamlRoot
                };
                _activeDialog = dialog;
                try
                {
                    await dialog.ShowAsync();
                }
                finally
                {
                    if (ReferenceEquals(_activeDialog, dialog))
                    {
                        _activeDialog = null;
                    }

                    confirmation.Dispose();
                }
            }
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not AccessViolationException)
        {
            _pendingProposals.Clear();
            if (_isSubscribed && generation == _navigationGeneration)
            {
                ViewModel.ReportCliProposalReviewFailure();
            }
        }
        finally
        {
            _isDrainingDialogs = false;
            StartPendingProposalDrain();
        }
    }

    private void StartPendingProposalDrain()
    {
        if (!_isSubscribed || _isDrainingDialogs || _pendingProposals.Count == 0)
        {
            return;
        }

        OnCliProposalsRequested(
            this,
            new CliProposalsRequestedEventArgs(Array.Empty<CliCommand>()));
    }
}
