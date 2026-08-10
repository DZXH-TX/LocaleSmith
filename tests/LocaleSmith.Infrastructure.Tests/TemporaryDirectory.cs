namespace LocaleSmith.Infrastructure.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    private readonly string _root;

    public TemporaryDirectory()
    {
        _root = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(AppContext.BaseDirectory, ".test-artifacts"));
        Directory.CreateDirectory(_root);
        Path = System.IO.Path.Combine(_root, $"localesmith-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        var resolved = System.IO.Path.GetFullPath(Path);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!resolved.StartsWith(_root + System.IO.Path.DirectorySeparatorChar, comparison))
        {
            throw new InvalidOperationException("Refusing to clean a directory outside the test temporary root.");
        }

        if (Directory.Exists(resolved))
        {
            Directory.Delete(resolved, recursive: true);
        }
    }
}
