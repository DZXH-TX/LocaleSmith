using System.Runtime.CompilerServices;
using JaxI18n.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace JaxI18n.App.Behaviors;

/// <summary>Opt-in hooks for consistent app-owned reveal, status, expand, and press feedback.</summary>
public static class AppMotion
{
    private static readonly ConditionalWeakTable<FrameworkElement, RevealRegistration> RevealRegistrations = new();
    private static readonly ConditionalWeakTable<InfoBar, StatusRegistration> StatusRegistrations = new();

    public static readonly DependencyProperty RevealProperty = DependencyProperty.RegisterAttached(
        "Reveal",
        typeof(bool),
        typeof(AppMotion),
        new PropertyMetadata(false, OnRevealChanged));

    public static readonly DependencyProperty ButtonFeedbackProperty = DependencyProperty.RegisterAttached(
        "ButtonFeedback",
        typeof(bool),
        typeof(AppMotion),
        new PropertyMetadata(false, OnButtonFeedbackChanged));

    public static readonly DependencyProperty StatusFeedbackProperty = DependencyProperty.RegisterAttached(
        "StatusFeedback",
        typeof(bool),
        typeof(AppMotion),
        new PropertyMetadata(false, OnStatusFeedbackChanged));

    public static readonly DependencyProperty ExpandFeedbackProperty = DependencyProperty.RegisterAttached(
        "ExpandFeedback",
        typeof(bool),
        typeof(AppMotion),
        new PropertyMetadata(false, OnExpandFeedbackChanged));

    public static readonly DependencyProperty SelectionFeedbackProperty = DependencyProperty.RegisterAttached(
        "SelectionFeedback",
        typeof(bool),
        typeof(AppMotion),
        new PropertyMetadata(false, OnSelectionFeedbackChanged));

    public static bool GetReveal(DependencyObject element) => (bool)element.GetValue(RevealProperty);

    public static void SetReveal(DependencyObject element, bool value) => element.SetValue(RevealProperty, value);

    public static bool GetButtonFeedback(DependencyObject element) =>
        (bool)element.GetValue(ButtonFeedbackProperty);

    public static void SetButtonFeedback(DependencyObject element, bool value) =>
        element.SetValue(ButtonFeedbackProperty, value);

    public static bool GetStatusFeedback(DependencyObject element) =>
        (bool)element.GetValue(StatusFeedbackProperty);

    public static void SetStatusFeedback(DependencyObject element, bool value) =>
        element.SetValue(StatusFeedbackProperty, value);

    public static bool GetExpandFeedback(DependencyObject element) =>
        (bool)element.GetValue(ExpandFeedbackProperty);

    public static void SetExpandFeedback(DependencyObject element, bool value) =>
        element.SetValue(ExpandFeedbackProperty, value);

    public static bool GetSelectionFeedback(DependencyObject element) =>
        (bool)element.GetValue(SelectionFeedbackProperty);

    public static void SetSelectionFeedback(DependencyObject element, bool value) =>
        element.SetValue(SelectionFeedbackProperty, value);

    private static void OnRevealChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element || args.NewValue is not bool enabled)
        {
            return;
        }

        if (enabled)
        {
            var registration = RevealRegistrations.GetOrCreateValue(element);
            if (registration.IsAttached)
            {
                return;
            }

            registration.IsAttached = true;
            element.Loaded += OnRevealLoaded;
            registration.VisibilityCallbackToken = element.RegisterPropertyChangedCallback(
                UIElement.VisibilityProperty,
                OnRevealVisibilityChanged);
            if (element.IsLoaded && element.Visibility == Visibility.Visible)
            {
                AnimateReveal(element);
            }
        }
        else if (RevealRegistrations.TryGetValue(element, out var registration) && registration.IsAttached)
        {
            element.Loaded -= OnRevealLoaded;
            element.UnregisterPropertyChangedCallback(
                UIElement.VisibilityProperty,
                registration.VisibilityCallbackToken);
            registration.IsAttached = false;
            MotionService?.Cancel(element);
        }
    }

    private static void OnButtonFeedbackChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not ButtonBase button || args.NewValue is not bool enabled)
        {
            return;
        }

        if (enabled)
        {
            button.PointerPressed += OnButtonPointerPressed;
            button.PointerReleased += OnButtonPointerReleased;
            button.PointerCanceled += OnButtonPointerCanceled;
            button.PointerCaptureLost += OnButtonPointerCaptureLost;
        }
        else
        {
            button.PointerPressed -= OnButtonPointerPressed;
            button.PointerReleased -= OnButtonPointerReleased;
            button.PointerCanceled -= OnButtonPointerCanceled;
            button.PointerCaptureLost -= OnButtonPointerCaptureLost;
            MotionService?.Cancel(button);
        }
    }

    private static void OnStatusFeedbackChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not InfoBar infoBar || args.NewValue is not bool enabled)
        {
            return;
        }

        if (enabled)
        {
            var registration = StatusRegistrations.GetOrCreateValue(infoBar);
            if (registration.IsAttached)
            {
                return;
            }

            registration.IsAttached = true;
            registration.IsOpenCallbackToken = infoBar.RegisterPropertyChangedCallback(
                InfoBar.IsOpenProperty,
                OnInfoBarIsOpenChanged);
        }
        else if (StatusRegistrations.TryGetValue(infoBar, out var registration) && registration.IsAttached)
        {
            infoBar.UnregisterPropertyChangedCallback(
                InfoBar.IsOpenProperty,
                registration.IsOpenCallbackToken);
            registration.IsAttached = false;
            MotionService?.Cancel(infoBar);
        }
    }

    private static void OnExpandFeedbackChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not Expander expander || args.NewValue is not bool enabled)
        {
            return;
        }

        if (enabled)
        {
            expander.Expanding += OnExpanderExpanding;
            expander.Collapsed += OnExpanderCollapsed;
        }
        else
        {
            expander.Expanding -= OnExpanderExpanding;
            expander.Collapsed -= OnExpanderCollapsed;
            if (expander.Content is FrameworkElement content)
            {
                MotionService?.Cancel(content);
            }
        }
    }

    private static void OnSelectionFeedbackChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not ToggleButton toggle || args.NewValue is not bool enabled)
        {
            return;
        }

        if (enabled)
        {
            toggle.Checked += OnToggleChecked;
        }
        else
        {
            toggle.Checked -= OnToggleChecked;
            MotionService?.Cancel(toggle);
        }
    }

    private static void OnRevealLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Visibility: Visibility.Visible } element)
        {
            AnimateReveal(element);
        }
    }

    private static void OnRevealVisibilityChanged(DependencyObject sender, DependencyProperty property)
    {
        if (sender is FrameworkElement { IsLoaded: true, Visibility: Visibility.Visible } element)
        {
            AnimateReveal(element);
        }
    }

    private static void OnInfoBarIsOpenChanged(DependencyObject sender, DependencyProperty property)
    {
        if (sender is InfoBar { IsOpen: true } infoBar)
        {
            AnimateReveal(infoBar);
        }
    }

    private static void OnExpanderExpanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        sender.DispatcherQueue.TryEnqueue(() =>
        {
            if (sender.Content is FrameworkElement content)
            {
                AnimateReveal(content);
            }
        });
    }

    private static void OnExpanderCollapsed(Expander sender, ExpanderCollapsedEventArgs args)
    {
        if (sender.Content is FrameworkElement content)
        {
            MotionService?.Cancel(content);
        }
    }

    private static void OnButtonPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (sender is ButtonBase button)
        {
            MotionService?.AnimateButtonFeedback(button, pressed: true);
        }
    }

    private static void OnToggleChecked(object sender, RoutedEventArgs args)
    {
        if (sender is ToggleButton toggle)
        {
            MotionService?.AnimateSelectionFeedback(toggle);
        }
    }

    private static void OnButtonPointerReleased(object sender, PointerRoutedEventArgs args) =>
        ReleaseButton(sender);

    private static void OnButtonPointerCanceled(object sender, PointerRoutedEventArgs args) =>
        ReleaseButton(sender);

    private static void OnButtonPointerCaptureLost(object sender, PointerRoutedEventArgs args) =>
        ReleaseButton(sender);

    private static void ReleaseButton(object sender)
    {
        if (sender is ButtonBase button)
        {
            MotionService?.AnimateButtonFeedback(button, pressed: false);
        }
    }

    private static void AnimateReveal(FrameworkElement element) =>
        MotionService?.AnimateReveal(element);

    private static AppMotionService? MotionService
    {
        get
        {
            try
            {
                return App.Services.GetService(typeof(AppMotionService)) as AppMotionService;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    private sealed class RevealRegistration
    {
        public bool IsAttached { get; set; }

        public long VisibilityCallbackToken { get; set; }
    }

    private sealed class StatusRegistration
    {
        public bool IsAttached { get; set; }

        public long IsOpenCallbackToken { get; set; }
    }
}
