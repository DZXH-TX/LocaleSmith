using LocaleSmith.Presentation.Models;

namespace LocaleSmith.App.Services;

internal static class AppLanguagePreferenceStore
{
    private const string FileName = "display-language.txt";

    public static string LoadOrDefault(string appDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataRoot);
        try
        {
            var path = Path.Combine(Path.GetFullPath(appDataRoot), FileName);
            return File.Exists(path)
                ? AppDisplayLanguages.ResolveOrDefault(File.ReadAllText(path).Trim())
                : AppDisplayLanguages.DefaultLanguage;
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return AppDisplayLanguages.DefaultLanguage;
        }
    }

    public static void Save(string appDataRoot, string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataRoot);
        var canonicalLanguage = AppDisplayLanguages.ResolveOrDefault(language);
        if (!string.Equals(language, canonicalLanguage, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The display language is not supported.", nameof(language));
        }

        var root = Path.GetFullPath(appDataRoot);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, FileName);
        var temporaryPath = Path.Combine(root, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, canonicalLanguage);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
