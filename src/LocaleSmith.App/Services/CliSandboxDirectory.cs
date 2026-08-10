namespace LocaleSmith.App.Services;

internal static class CliSandboxDirectory
{
    private const string SandboxDirectoryName = "CliSandbox";

    public static string CreateUnderAppDataRoot(string appDataRoot)
    {
        var sandboxPath = GetExpectedPath(appDataRoot);
        RejectReparsePointsInExistingAncestry(sandboxPath);
        Directory.CreateDirectory(sandboxPath);
        return ValidateExisting(appDataRoot, sandboxPath);
    }

    public static string ValidateExisting(string appDataRoot, string sandboxPath)
    {
        var expectedPath = GetExpectedPath(appDataRoot);
        var fullSandboxPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sandboxPath));
        if (!string.Equals(expectedPath, fullSandboxPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The CLI sandbox must remain under the application-data root.");
        }

        RejectReparsePointsInExistingAncestry(fullSandboxPath);
        if (!Directory.Exists(fullSandboxPath))
        {
            throw new DirectoryNotFoundException("The private CLI sandbox does not exist.");
        }

        return fullSandboxPath;
    }

    private static string GetExpectedPath(string appDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataRoot);
        var fullAppDataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDataRoot));
        var sandboxPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.Combine(fullAppDataRoot, SandboxDirectoryName)));
        if (!sandboxPath.StartsWith(
                fullAppDataRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The CLI sandbox escaped the application-data root.");
        }

        return sandboxPath;
    }

    private static void RejectReparsePointsInExistingAncestry(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
        {
            try
            {
                if ((File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0 ||
                    current.LinkTarget is not null)
                {
                    throw new InvalidDataException(
                        "The private CLI sandbox cannot traverse a symbolic link or reparse point.");
                }
            }
            catch (FileNotFoundException)
            {
                // Missing descendants are created only after every existing ancestor is checked.
            }
            catch (DirectoryNotFoundException)
            {
                // Missing descendants are created only after every existing ancestor is checked.
            }
        }
    }
}
