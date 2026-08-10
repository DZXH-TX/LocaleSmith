namespace JaxI18n.Presentation.Models;

/// <summary>Pure policy shared by the presentation layer and the WinUI animation service.</summary>
public static class AnimationPreferencePolicy
{
    public const int ButtonFeedbackDurationMilliseconds = 150;

    public const int RevealDurationMilliseconds = 180;

    public const int PageTransitionDurationMilliseconds = 240;

    public static bool ShouldRunAppAnimations(
        bool systemAnimationsEnabled,
        bool forceAppAnimations) =>
        systemAnimationsEnabled || forceAppAnimations;
}
