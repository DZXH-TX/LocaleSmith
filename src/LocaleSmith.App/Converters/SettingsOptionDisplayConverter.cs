using LocaleSmith.Presentation.Models;
using Microsoft.UI.Xaml.Data;
using Microsoft.Windows.ApplicationModel.Resources;

namespace LocaleSmith.App.Converters;

public sealed class SettingsOptionDisplayConverter : IValueConverter
{
    private readonly Func<string, string?> _getResourceString;

    public SettingsOptionDisplayConverter()
        : this(new ResourceLoader().GetString)
    {
    }

    internal SettingsOptionDisplayConverter(Func<string, string?> getResourceString)
    {
        _getResourceString = getResourceString ?? throw new ArgumentNullException(nameof(getResourceString));
    }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var option = value switch
        {
            string languageTag when AppDisplayLanguages.TryGet(languageTag, out var displayLanguage) =>
                new LocalizedOption(displayLanguage.ResourceKey, displayLanguage.FallbackDisplayName),
            AppThemePreference.System => new LocalizedOption("ThemeOptionSystem", "System"),
            AppThemePreference.Light => new LocalizedOption("ThemeOptionLight", "Light"),
            AppThemePreference.Dark => new LocalizedOption("ThemeOptionDark", "Dark"),
            _ => default
        };

        if (option.Key is null)
        {
            return value?.ToString() ?? string.Empty;
        }

        var localized = _getResourceString(option.Key);
        return string.IsNullOrWhiteSpace(localized) ? option.Fallback : localized;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();

    private readonly record struct LocalizedOption(string? Key, string Fallback);
}
