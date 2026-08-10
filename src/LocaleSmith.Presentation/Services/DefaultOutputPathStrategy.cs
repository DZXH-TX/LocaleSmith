using LocaleSmith.Presentation.Abstractions;

namespace LocaleSmith.Presentation.Services;

public sealed class DefaultOutputPathStrategy : IOutputPathStrategy
{
    private const string OutputDirectoryName = "LocaleSmith.Output";
    private readonly IAppConfigurationService _configurationService;

    public DefaultOutputPathStrategy(IAppConfigurationService configurationService)
    {
        _configurationService = configurationService
            ?? throw new ArgumentNullException(nameof(configurationService));
    }

    public async Task<string> CreateOutputPathAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var sourceFullPath = Path.GetFullPath(sourcePath);
        var sourceIsDirectory = Directory.Exists(sourceFullPath);
        if (!sourceIsDirectory && !File.Exists(sourceFullPath))
        {
            throw new FileNotFoundException("The source package does not exist.", sourceFullPath);
        }

        var configuration = await _configurationService
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(configuration.WorkspacePath))
        {
            throw new InvalidOperationException("A configured workspace path is required.");
        }

        var workspaceRoot = Path.GetFullPath(configuration.WorkspacePath);
        if (IsPathRoot(workspaceRoot))
        {
            throw new InvalidOperationException("A drive or share root cannot be used as the workspace.");
        }

        var outputRoot = Path.GetFullPath(Path.Combine(workspaceRoot, OutputDirectoryName));
        if (!IsSameOrDescendant(outputRoot, workspaceRoot))
        {
            throw new InvalidOperationException("The output directory escapes the configured workspace.");
        }

        if (sourceIsDirectory && IsSameOrDescendant(outputRoot, sourceFullPath))
        {
            throw new InvalidOperationException(
                "The output directory cannot be the source directory or one of its descendants.");
        }

        EnsureHierarchyHasNoReparsePoints(workspaceRoot, allowMissing: true);
        Directory.CreateDirectory(workspaceRoot);
        EnsureHierarchyHasNoReparsePoints(workspaceRoot, allowMissing: false);
        EnsureHierarchyHasNoReparsePoints(outputRoot, allowMissing: true);
        Directory.CreateDirectory(outputRoot);
        EnsureHierarchyHasNoReparsePoints(outputRoot, allowMissing: false);

        var sourceName = sourceIsDirectory
            ? new DirectoryInfo(sourceFullPath).Name
            : Path.GetFileNameWithoutExtension(sourceFullPath);
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            throw new InvalidOperationException("The source package name is invalid.");
        }

        var extension = sourceIsDirectory ? ".zip" : Path.GetExtension(sourceFullPath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".zip";
        }

        var outputPath = Path.GetFullPath(
            Path.Combine(outputRoot, $"{sourceName}.zh_CN{extension}"));
        if (!IsSameOrDescendant(outputPath, outputRoot) ||
            string.Equals(outputPath, sourceFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The generated output path is unsafe.");
        }

        EnsureHierarchyHasNoReparsePoints(outputPath, allowMissing: true);
        return outputPath;
    }

    private static void EnsureHierarchyHasNoReparsePoints(string path, bool allowMissing)
    {
        for (var current = Path.GetFullPath(path); current is not null;)
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException($"Reparse points are not allowed in output paths: {current}");
                }
            }
            catch (FileNotFoundException) when (allowMissing)
            {
            }
            catch (DirectoryNotFoundException) when (allowMissing)
            {
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }
    }

    private static bool IsPathRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        return root is not null &&
            string.Equals(
                Path.TrimEndingDirectorySeparator(path),
                Path.TrimEndingDirectorySeparator(root),
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameOrDescendant(string candidatePath, string ancestorPath)
    {
        var relative = Path.GetRelativePath(
            Path.GetFullPath(ancestorPath),
            Path.GetFullPath(candidatePath));
        return string.Equals(relative, ".", StringComparison.Ordinal) ||
            (!Path.IsPathRooted(relative) &&
             !string.Equals(relative, "..", StringComparison.Ordinal) &&
             !relative.StartsWith(
                 string.Concat("..", Path.DirectorySeparatorChar),
                 StringComparison.Ordinal) &&
             !relative.StartsWith(
                 string.Concat("..", Path.AltDirectorySeparatorChar),
                 StringComparison.Ordinal));
    }
}
