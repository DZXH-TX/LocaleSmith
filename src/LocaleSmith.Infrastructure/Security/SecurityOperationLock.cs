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
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var lockRoot = Path.Combine(Path.GetTempPath(), "LocaleSmith", "SecurityLocks");
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
