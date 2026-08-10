using LocaleSmith.Core.Models;
using LocaleSmith.Presentation.ViewModels;

namespace LocaleSmith.App;

internal static class ModelOptionSelectionMap
{
    public static bool TryResolvePreset(
        object? selectedItem,
        IReadOnlyList<ModelProviderPreset> availableOptions,
        out ModelProviderPreset preset)
    {
        ArgumentNullException.ThrowIfNull(availableOptions);
        if (selectedItem is ModelProviderPreset selectedPreset)
        {
            var canonical = availableOptions.FirstOrDefault(option =>
                string.Equals(option.Id, selectedPreset.Id, StringComparison.OrdinalIgnoreCase));
            if (canonical is not null)
            {
                preset = canonical;
                return true;
            }
        }

        preset = null!;
        return false;
    }

    public static bool TryResolveTokenLimitParameter(
        object? selectedItem,
        IReadOnlyList<TokenLimitParameterOption> availableOptions,
        out TokenLimitParameterOption option)
    {
        ArgumentNullException.ThrowIfNull(availableOptions);
        if (selectedItem is TokenLimitParameterOption selectedOption)
        {
            var canonical = availableOptions.FirstOrDefault(option =>
                option.Value == selectedOption.Value);
            if (canonical is not null)
            {
                option = canonical;
                return true;
            }
        }

        option = null!;
        return false;
    }
}
