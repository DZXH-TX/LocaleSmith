using JaxI18n.Presentation.Abstractions;
using Windows.ApplicationModel.Resources;

namespace JaxI18n.App.Services;

public sealed class WinUiTextProvider : IUiTextProvider
{
    private readonly ResourceLoader _resourceLoader = ResourceLoader.GetForViewIndependentUse();

    public string GetText(string key, string fallback, params object?[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(fallback);
        var localized = _resourceLoader.GetString(key);
        var template = string.IsNullOrWhiteSpace(localized) ? fallback : localized;
        return arguments.Length == 0
            ? template
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, template, arguments);
    }
}
