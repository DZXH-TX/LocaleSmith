using LocaleSmith.Presentation.Abstractions;
using Microsoft.Windows.ApplicationModel.Resources;

namespace LocaleSmith.App.Services;

public sealed class WinUiTextProvider : IUiTextProvider
{
    private readonly Func<string, string?> _getResourceString;

    public WinUiTextProvider()
        : this(new ResourceLoader().GetString)
    {
    }

    internal WinUiTextProvider(Func<string, string?> getResourceString)
    {
        _getResourceString = getResourceString ?? throw new ArgumentNullException(nameof(getResourceString));
    }

    public string GetText(string key, string fallback, params object?[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(fallback);
        var localized = _getResourceString(key);
        var template = string.IsNullOrWhiteSpace(localized) ? fallback : localized;
        return arguments.Length == 0
            ? template
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, template, arguments);
    }
}
