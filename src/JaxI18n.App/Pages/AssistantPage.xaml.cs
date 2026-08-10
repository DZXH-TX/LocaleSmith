using System.Collections.Specialized;
using JaxI18n.App.Dialogs;
using JaxI18n.App.Services;
using JaxI18n.Core.Models;
using JaxI18n.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JaxI18n.App.Pages;

public sealed partial class AssistantPage : Page
{
    private const int MaximumQueuedProposals = 4;
    private readonly Queue<CliCommand> _pendingProposals = [];
    private ContentDialog? _activeDialog;
    private bool _isDrainingDialogs;
    private bool _isSubscribed;
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
        }

        ViewModel.RefreshModelSources();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _navigationGeneration++;
        if (_isSubscribed)
        {
            ViewModel.CliProposalsRequested -= OnCliProposalsRequested;
            ViewModel.Messages.CollectionChanged -= OnMessagesChanged;
            _isSubscribed = false;
        }

        _pendingProposals.Clear();
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
        if (ViewModel.Messages.Count == 0)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() => ConversationList.ScrollIntoView(ViewModel.Messages[^1]));
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
                   _pendingProposals.TryDequeue(out CliCommand? command))
            {
                var confirmation = await factory.CreateAsync(command).ConfigureAwait(true);
                if (!_isSubscribed || generation != _navigationGeneration || XamlRoot is null)
                {
                    confirmation.Dispose();
                    break;
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
        }
    }
}
