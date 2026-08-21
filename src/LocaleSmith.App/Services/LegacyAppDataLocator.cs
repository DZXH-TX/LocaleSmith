using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Windows.Management.Deployment;

namespace LocaleSmith.App.Services;

/// <summary>
/// Locates legacy state outside the current package's AppData virtualization boundary.
/// </summary>
public static class LegacyAppDataLocator
{
    private const string JaxI18nPackageName = "JaxI18n.Desktop";
    private const string JaxI18nDirectoryName = "JaxI18n";
    private const string LocaleSmithPackageName = "LocaleSmith.Desktop";
    private const string LocaleSmithDirectoryName = "LocaleSmith";
    private static readonly string[] LegacyPackageNames =
        [JaxI18nPackageName, LocaleSmithPackageName];
    private const uint NoPackageRedirection = 0x00010000;
    private static readonly Guid LocalAppDataFolderId =
        new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");

    public static string GetUnredirectedLocalApplicationDataPath()
    {
        var folderId = LocalAppDataFolderId;
        var result = SHGetKnownFolderPath(
            ref folderId,
            NoPackageRedirection,
            nint.Zero,
            out var rawPath);
        try
        {
            if (result >= 0 && rawPath != nint.Zero)
            {
                var path = Marshal.PtrToStringUni(rawPath);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return Path.GetFullPath(path);
                }
            }
        }
        finally
        {
            if (rawPath != nint.Zero)
            {
                Marshal.FreeCoTaskMem(rawPath);
            }
        }

        var environmentPath = System.Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrWhiteSpace(environmentPath))
        {
            return Path.GetFullPath(environmentPath);
        }

        Marshal.ThrowExceptionForHR(result);
        throw new InvalidOperationException("The unredirected LocalAppData folder is unavailable.");
    }

    public static IReadOnlyList<string> FindLegacyRoots(string unredirectedLocalAppDataPath)
    {
        return FindLegacyRoots(
            unredirectedLocalAppDataPath,
            FindRegisteredLegacyPackageFamilyNames());
    }

    internal static IReadOnlyList<string> FindLegacyRoots(
        string unredirectedLocalAppDataPath,
        IEnumerable<string> registeredPackageFamilyNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unredirectedLocalAppDataPath);
        ArgumentNullException.ThrowIfNull(registeredPackageFamilyNames);
        var localAppDataRoot = Path.GetFullPath(unredirectedLocalAppDataPath);
        var roots = new List<string>();
        var uniqueRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packagesRoot = Path.Combine(localAppDataRoot, "Packages");

        if (Directory.Exists(packagesRoot))
        {
            foreach (var packageFamilyName in registeredPackageFamilyNames
                         .Where(IsSafeLegacyPackageFamilyName)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(GetLegacyPackagePriority)
                         .ThenBy(static name => name, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(static name => name, StringComparer.Ordinal))
            {
                var directoryName = GetLegacyDirectoryName(packageFamilyName);
                var candidate = Path.Combine(
                    packagesRoot,
                    packageFamilyName,
                    "LocalCache",
                    "Local",
                    directoryName);
                AddIfSafeExistingDirectory(candidate, packagesRoot, roots, uniqueRoots);
            }
        }

        AddIfSafeExistingDirectory(
            Path.Combine(localAppDataRoot, JaxI18nDirectoryName),
            localAppDataRoot,
            roots,
            uniqueRoots);
        return roots;
    }

    private static string[] FindRegisteredLegacyPackageFamilyNames()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
            return new PackageManager()
                .FindPackagesForUser(string.Empty)
                .Where(static package => IsLegacyPackageName(package.Id.Name))
                .Select(static package => package.Id.FamilyName)
                .Where(IsSafeLegacyPackageFamilyName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static name => name, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception) when (exception is
            COMException or
            IOException or
            InvalidOperationException or
            PlatformNotSupportedException or
            UnauthorizedAccessException)
        {
            // Package registration discovery is best effort. The unpackaged fallback is still checked.
            return [];
        }
    }

    private static bool IsSafeLegacyPackageFamilyName(string? packageFamilyName)
    {
        const int MaximumPackageFamilyNameLength = 255;
        return !string.IsNullOrWhiteSpace(packageFamilyName)
            && packageFamilyName.Length <= MaximumPackageFamilyNameLength
            && TryGetLegacyPackageName(packageFamilyName, out var packageName)
            && packageFamilyName.Length > packageName.Length + 1
            && packageFamilyName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && string.Equals(Path.GetFileName(packageFamilyName), packageFamilyName, StringComparison.Ordinal)
            && packageFamilyName.IndexOfAny(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0;
    }

    private static bool IsLegacyPackageName(string? packageName) =>
        string.Equals(packageName, JaxI18nPackageName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(packageName, LocaleSmithPackageName, StringComparison.OrdinalIgnoreCase);

    private static bool TryGetLegacyPackageName(string packageFamilyName, out string packageName)
    {
        foreach (var candidate in LegacyPackageNames)
        {
            if (packageFamilyName.StartsWith(
                    candidate + "_",
                    StringComparison.OrdinalIgnoreCase))
            {
                packageName = candidate;
                return true;
            }
        }

        packageName = string.Empty;
        return false;
    }

    private static string GetLegacyDirectoryName(string packageFamilyName)
    {
        _ = TryGetLegacyPackageName(packageFamilyName, out var packageName);
        return string.Equals(packageName, LocaleSmithPackageName, StringComparison.OrdinalIgnoreCase)
            ? LocaleSmithDirectoryName
            : JaxI18nDirectoryName;
    }

    private static int GetLegacyPackagePriority(string packageFamilyName) =>
        packageFamilyName.StartsWith(
            LocaleSmithPackageName + "_",
            StringComparison.OrdinalIgnoreCase)
            ? 0
            : 1;

    private static void AddIfSafeExistingDirectory(
        string candidate,
        string containmentRoot,
        List<string> roots,
        HashSet<string> uniqueRoots)
    {
        var fullCandidate = Path.GetFullPath(candidate);
        var fullContainmentRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(containmentRoot));
        if (!IsWithin(fullCandidate, fullContainmentRoot)
            || !Directory.Exists(fullCandidate)
            || ContainsReparsePoint(fullCandidate, fullContainmentRoot))
        {
            return;
        }

        if (uniqueRoots.Add(fullCandidate))
        {
            roots.Add(fullCandidate);
        }
    }

    private static bool ContainsReparsePoint(string path, string containmentRoot)
    {
        try
        {
            for (var current = new DirectoryInfo(path);
                 current is not null
                 && IsWithin(current.FullName, containmentRoot);
                 current = current.Parent)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }

                if (PathsEqual(current.FullName, containmentRoot))
                {
                    break;
                }
            }
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }

        return false;
    }

    private static bool IsWithin(string candidate, string root)
    {
        candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return PathsEqual(candidate, root)
            || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);

    [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute")]
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(
        ref Guid folderId,
        uint flags,
        nint token,
        out nint path);
}
