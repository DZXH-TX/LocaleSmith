using Microsoft.UI.Xaml.Data;
using Microsoft.Windows.ApplicationModel.Resources;

namespace LocaleSmith.App.Converters;

public sealed class TranslationLogViewModeDisplayConverter : IValueConverter
{
    private readonly Func<string, string?> _getResourceString;

    public TranslationLogViewModeDisplayConverter()
        : this(new ResourceLoader().GetString)
    {
    }

    internal TranslationLogViewModeDisplayConverter(Func<string, string?> getResourceString)
    {
        _getResourceString = getResourceString ?? throw new ArgumentNullException(nameof(getResourceString));
    }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = value?.ToString() switch
        {
            "Debug" => "LogViewModeDebug",
            "AllLevels" => "LogViewModeAllLevels",
            _ => null
        };
        if (key is null)
        {
            return value?.ToString() ?? string.Empty;
        }

        var localized = _getResourceString(key);
        return string.IsNullOrWhiteSpace(localized)
            ? key == "LogViewModeDebug" ? "Debug" : "All levels"
            : localized;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
