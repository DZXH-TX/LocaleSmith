using System.Security.Cryptography;
using System.Text;

namespace LocaleSmith.Infrastructure.Security;

/// <summary>
/// A crash-safe cross-process lock backed by an exclusively opened per-user file.
/// Unlike a named mutex, the lease may safely cross async continuation threads.
/// </summary>
internal sealed class SecurityOperationLock : IDisposable
{
    private const int SharingViolation = 32;
    private const int LockViolation = 33;
    private readonly FileStream _lease;

    private SecurityOperationLock(FileStream lease)
    {
        _lease = lease;
    }

    public static async ValueTask<SecurityOperationLock> AcquireAsync(
        string operation,
        string key,
        CancellationToken cancellationToken,
        string? securityLockRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        string lockRoot;
        if (string.IsNullOrWhiteSpace(securityLockRoot))
        {
            var localApplicationData = System.Environment.GetFolderPath(
                System.Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localApplicationData))
            {
                throw new InvalidOperationException("The per-user application-data directory is unavailable.");
            }

            var applicationDataRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(localApplicationData));
            lockRoot = Path.GetFullPath(
                Path.Combine(applicationDataRoot, "LocaleSmith", "SecurityLocks"));
            if (!lockRoot.StartsWith(
                    applicationDataRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The security-lock directory escaped application data.");
            }
        }
        else
        {
            lockRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(securityLockRoot));
            var filesystemRoot = Path.GetPathRoot(lockRoot)
                ?? throw new ArgumentException(
                    "The security-lock directory does not have a filesystem root.",
                    nameof(securityLockRoot));
            if (string.Equals(lockRoot, filesystemRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "A filesystem root cannot be used as the security-lock directory.",
                    nameof(securityLockRoot));
            }
        }

        Directory.CreateDirectory(lockRoot);
        var path = Path.Combine(lockRoot, $"{GetName(operation, key)}.lock");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var lease = new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
                return new SecurityOperationLock(lease);
            }
            catch (IOException exception) when (IsLockContention(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public void Dispose() => _lease.Dispose();

    private static bool IsLockContention(IOException exception) =>
        (exception.HResult & 0xFFFF) is SharingViolation or LockViolation;

    private static string GetName(string operation, string key)
    {
        var material = Encoding.UTF8.GetBytes($"{operation}\0{key}");
        try
        {
            return Convert.ToHexString(SHA256.HashData(material));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }
}
