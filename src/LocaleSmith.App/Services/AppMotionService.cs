using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using LocaleSmith.Presentation.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation.Metadata;
using Windows.UI.ViewManagement;

namespace LocaleSmith.App.Services;

/// <summary>
/// Owns the app's short transform/opacity transitions and gates them against the Windows
/// accessibility preference. Every element has at most one active storyboard.
/// </summary>
public sealed class AppMotionService : IDisposable
{
    private readonly UISettings _uiSettings = new();
    private readonly ConditionalWeakTable<FrameworkElement, AnimationState> _states = new();
    private readonly bool _canObserveSystemPreference;
    private volatile bool _systemAnimationsEnabled;
    private bool _disposed;

    public AppMotionService()
    {
        _systemAnimationsEnabled = _uiSettings.AnimationsEnabled;
        _canObserveSystemPreference = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) &&
            ApiInformation.IsEventPresent(
            "Windows.UI.ViewManagement.UISettings",
            "AnimationsEnabledChanged");
        if (_canObserveSystemPreference && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            SubscribeToSystemPreference();
        }
    }

    public bool ForceAppAnimations { get; private set; }

    public bool SystemAnimationsEnabled => _systemAnimationsEnabled;

    public bool ShouldAnimate => AnimationPreferencePolicy.ShouldRunAppAnimations(
        SystemAnimationsEnabled,
        ForceAppAnimations);

    public void SetForceAppAnimations(bool forceAppAnimations) =>
        ForceAppAnimations = forceAppAnimations;

    public void AnimatePageEntrance(FrameworkElement element) => AnimateEntrance(
        element,
        offset: 12,
        AnimationPreferencePolicy.PageTransitionDurationMilliseconds);

    public void AnimateReveal(FrameworkElement element) => AnimateEntrance(
        element,
        offset: 8,
        AnimationPreferencePolicy.RevealDurationMilliseconds);

    public void AnimateSelectionFeedback(FrameworkElement element)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(element);
        var state = _states.GetOrCreateValue(element);
        InitializeState(element, state);
        StopCurrent(state);
        ResetElement(element, state);
        if (!ShouldAnimate || state.Transform is not { } transform)
        {
            return;
        }

        var storyboard = new Storyboard();
        AddAnimation(
            storyboard,
            element,
            "Opacity",
            state.RestingOpacity * 0.82,
            state.RestingOpacity,
            AnimationPreferencePolicy.RevealDurationMilliseconds);
        AddAnimation(
            storyboard,
            transform,
            "ScaleX",
            0.985,
            1,
            AnimationPreferencePolicy.RevealDurationMilliseconds);
        AddAnimation(
            storyboard,
            transform,
            "ScaleY",
            0.985,
            1,
            AnimationPreferencePolicy.RevealDurationMilliseconds);
        Start(state, storyboard);
    }

    public void AnimateButtonFeedback(FrameworkElement element, bool pressed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(element);
        var state = _states.GetOrCreateValue(element);
        InitializeState(element, state);
        StopCurrent(state);

        var transform = state.Transform;
        if (!ShouldAnimate || transform is null)
        {
            ResetElement(element, state);
            return;
        }

        var fromOpacity = element.Opacity;
        var fromScaleX = transform.ScaleX;
        var fromScaleY = transform.ScaleY;
        var toOpacity = pressed ? state.RestingOpacity * 0.9 : state.RestingOpacity;
        var toScale = pressed ? 0.985 : 1.0;

        element.Opacity = toOpacity;
        transform.ScaleX = toScale;
        transform.ScaleY = toScale;
        var storyboard = new Storyboard();
        AddAnimation(storyboard, element, "Opacity", fromOpacity, toOpacity,
            AnimationPreferencePolicy.ButtonFeedbackDurationMilliseconds);
        AddAnimation(storyboard, transform, "ScaleX", fromScaleX, toScale,
            AnimationPreferencePolicy.ButtonFeedbackDurationMilliseconds);
        AddAnimation(storyboard, transform, "ScaleY", fromScaleY, toScale,
            AnimationPreferencePolicy.ButtonFeedbackDurationMilliseconds);
        Start(state, storyboard);
    }

    public void Cancel(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (_states.TryGetValue(element, out var state))
        {
            StopCurrent(state);
            ResetElement(element, state);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_canObserveSystemPreference && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            UnsubscribeFromSystemPreference();
        }

        _disposed = true;
    }

    private void AnimateEntrance(FrameworkElement element, double offset, int durationMilliseconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(element);
        var state = _states.GetOrCreateValue(element);
        InitializeState(element, state);
        StopCurrent(state);
        ResetElement(element, state);
        if (!ShouldAnimate || element.Visibility != Visibility.Visible)
        {
            return;
        }

        var storyboard = new Storyboard();
        AddAnimation(storyboard, element, "Opacity", 0, state.RestingOpacity, durationMilliseconds);
        if (state.Transform is { } transform)
        {
            AddAnimation(storyboard, transform, "TranslateY", offset, 0, durationMilliseconds);
        }

        Start(state, storyboard);
    }

    private static void InitializeState(FrameworkElement element, AnimationState state)
    {
        if (state.IsInitialized)
        {
            return;
        }

        state.IsInitialized = true;
        state.RestingOpacity = element.Opacity;
        state.Transform = element.RenderTransform switch
        {
            null => CreateTransform(element),
            CompositeTransform transform => transform,
            _ => null
        };
    }

    private static CompositeTransform CreateTransform(FrameworkElement element)
    {
        var transform = new CompositeTransform();
        element.RenderTransform = transform;
        element.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        return transform;
    }

    private static void AddAnimation(
        Storyboard storyboard,
        DependencyObject target,
        string targetProperty,
        double from,
        double to,
        int durationMilliseconds)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(durationMilliseconds),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
            EnableDependentAnimation = false
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, targetProperty);
        storyboard.Children.Add(animation);
    }

    private static void Start(AnimationState state, Storyboard storyboard)
    {
        var generation = ++state.Generation;
        state.Current = storyboard;
        storyboard.Completed += (_, _) =>
        {
            if (state.Generation == generation)
            {
                state.Current = null;
            }
        };
        storyboard.Begin();
    }

    private static void StopCurrent(AnimationState state)
    {
        state.Generation++;
        state.Current?.Stop();
        state.Current = null;
    }

    private static void ResetElement(FrameworkElement element, AnimationState state)
    {
        element.Opacity = state.RestingOpacity;
        if (state.Transform is { } transform)
        {
            transform.TranslateY = 0;
            transform.ScaleX = 1;
            transform.ScaleY = 1;
        }
    }

    private void OnAnimationsEnabledChanged(
        UISettings sender,
        UISettingsAnimationsEnabledChangedEventArgs args) =>
        _systemAnimationsEnabled = sender.AnimationsEnabled;

    [SupportedOSPlatform("windows10.0.19041.0")]
    private void SubscribeToSystemPreference() =>
        _uiSettings.AnimationsEnabledChanged += OnAnimationsEnabledChanged;

    [SupportedOSPlatform("windows10.0.19041.0")]
    private void UnsubscribeFromSystemPreference() =>
        _uiSettings.AnimationsEnabledChanged -= OnAnimationsEnabledChanged;

    private sealed class AnimationState
    {
        public Storyboard? Current { get; set; }

        public int Generation { get; set; }

        public bool IsInitialized { get; set; }

        public double RestingOpacity { get; set; } = 1;

        public CompositeTransform? Transform { get; set; }
    }
}
