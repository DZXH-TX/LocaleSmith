using System.Runtime.InteropServices;
using Windows.ApplicationModel;

namespace LocaleSmith.App.Services;

/// <summary>
/// Keeps production package state separate from unpackaged and development-package state.
/// Only the exact Partner Center identity is allowed to use production settings and credentials.
/// </summary>
internal sealed record ApplicationStorageScope(
    string AppDataRoot,
    string CredentialTargetPrefix,
    bool IsProduction)
{
    internal const string ProductionPackageFamilyName = "CRTech.LocaleSmith_pxtspj1qm7b2r";
    internal const string ProductionDirectoryName = "LocaleSmith";
    internal const string ProductionCredentialTargetPrefix = "LocaleSmith";
    internal const string DevelopmentDirectoryName = "LocaleSmith.Dev";
    internal const string DevelopmentCredentialTargetPrefix = "LocaleSmith.Dev";

    public static ApplicationStorageScope Detect()
    {
        var localApplicationData = System.Environment.GetFolderPath(
            System.Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("The per-user application-data directory is unavailable.");
        }

        return Resolve(localApplicationData, TryGetPackageFamilyName());
    }

    internal static ApplicationStorageScope Resolve(
        string localApplicationData,
        string? packageFamilyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationData);
        var localRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localApplicationData));
        var isProduction = string.Equals(
            packageFamilyName,
            ProductionPackageFamilyName,
            StringComparison.OrdinalIgnoreCase);
        var directoryName = isProduction
            ? ProductionDirectoryName
            : DevelopmentDirectoryName;
        var credentialTargetPrefix = isProduction
            ? ProductionCredentialTargetPrefix
            : DevelopmentCredentialTargetPrefix;
        var appDataRoot = Path.GetFullPath(Path.Combine(localRoot, directoryName));
        if (!appDataRoot.StartsWith(
                localRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The application-data directory escaped LocalAppData.");
        }

        return new ApplicationStorageScope(appDataRoot, credentialTargetPrefix, isProduction);
    }

    private static string? TryGetPackageFamilyName()
    {
        try
        {
            return Package.Current.Id.FamilyName;
        }
        catch (Exception exception) when (exception is
            COMException or
            InvalidOperationException or
            PlatformNotSupportedException)
        {
            // An unpackaged process has no Package.Current identity and must never use production state.
            return null;
        }
    }
}
