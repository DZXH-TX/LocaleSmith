namespace JaxI18n.Infrastructure.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    private readonly string _root;

    public TemporaryDirectory()
    {
        _root = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
        Path = System.IO.Path.Combine(_root, $"jax-i18n-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        var resolved = System.IO.Path.GetFullPath(Path);
        if (!resolved.StartsWith(_root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to clean a directory outside the test temporary root.");
        }

        if (Directory.Exists(resolved))
        {
            Directory.Delete(resolved, recursive: true);
        }
    }
}
