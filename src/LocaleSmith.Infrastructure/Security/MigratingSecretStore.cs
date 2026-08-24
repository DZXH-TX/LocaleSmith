using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;

namespace LocaleSmith.Infrastructure.Security;

/// <summary>
/// Reads from the current secret store first and lazily copies legacy values into it.
/// Legacy values are retained so an upgrade does not make rollback destructive.
/// </summary>
public sealed class MigratingSecretStore : ISecretStore
{
    private const string TombstoneValue = "deleted";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);
    private readonly ISecretStore _currentStore;
    private readonly ISecretStore _legacyStore;
    private readonly string? _securityLockRoot;

    public MigratingSecretStore(
        ISecretStore currentStore,
        ISecretStore legacyStore,
        string? securityLockRoot = null)
    {
        _currentStore = currentStore ?? throw new ArgumentNullException(nameof(currentStore));
        _legacyStore = legacyStore ?? throw new ArgumentNullException(nameof(legacyStore));
        _securityLockRoot = string.IsNullOrWhiteSpace(securityLockRoot)
            ? null
            : Path.GetFullPath(securityLockRoot);
    }

    public async ValueTask<SecretValue?> ResolveAsync(
        string reference,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(reference);
        cancellationToken.ThrowIfCancellationRequested();
        var gate = Locks.GetOrAdd(reference, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processGate = await SecurityOperationLock.AcquireAsync(
                "migrating-secret",
                reference,
                cancellationToken,
                _securityLockRoot).ConfigureAwait(false);
            using var tombstone = await _currentStore
                .ResolveAsync(GetTombstoneReference(reference), cancellationToken)
                .ConfigureAwait(false);
            if (tombstone is not null)
            {
                return null;
            }

            var current = await _currentStore.ResolveAsync(reference, cancellationToken).ConfigureAwait(false);
            if (current is not null)
            {
                return current;
            }

            using var legacy = await _legacyStore.ResolveAsync(reference, cancellationToken).ConfigureAwait(false);
            if (legacy is null)
            {
                return null;
            }

            using var latestTombstone = await _currentStore
                .ResolveAsync(GetTombstoneReference(reference), cancellationToken)
                .ConfigureAwait(false);
            if (latestTombstone is not null)
            {
                return null;
            }

            var latestCurrent = await _currentStore.ResolveAsync(reference, cancellationToken).ConfigureAwait(false);
            if (latestCurrent is not null)
            {
                return latestCurrent;
            }

            var buffer = new char[legacy.Length];
            try
            {
                legacy.CopyTo(buffer);
                await _currentStore.SetAsync(reference, buffer, cancellationToken).ConfigureAwait(false);
                return new SecretValue(buffer);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(buffer.AsSpan()));
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask SetAsync(
        string reference,
        ReadOnlyMemory<char> secret,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(reference);
        var gate = Locks.GetOrAdd(reference, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processGate = await SecurityOperationLock.AcquireAsync(
                "migrating-secret",
                reference,
                cancellationToken,
                _securityLockRoot).ConfigureAwait(false);
            await _currentStore.SetAsync(reference, secret, cancellationToken).ConfigureAwait(false);
            await _currentStore
                .DeleteAsync(GetTombstoneReference(reference), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<bool> DeleteAsync(
        string reference,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(reference);
        cancellationToken.ThrowIfCancellationRequested();
        var gate = Locks.GetOrAdd(reference, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processGate = await SecurityOperationLock.AcquireAsync(
                "migrating-secret",
                reference,
                cancellationToken,
                _securityLockRoot).ConfigureAwait(false);
            await _currentStore
                .SetAsync(GetTombstoneReference(reference), TombstoneValue.AsMemory(), cancellationToken)
                .ConfigureAwait(false);

            Exception? currentFailure = null;
            var currentDeleted = false;
            try
            {
                currentDeleted = await _currentStore.DeleteAsync(reference, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                currentFailure = exception;
            }

            bool legacyDeleted;
            try
            {
                legacyDeleted = await _legacyStore.DeleteAsync(reference, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception legacyFailure) when (currentFailure is not null)
            {
                throw new AggregateException(
                    "Deleting the secret failed in both the current and legacy stores.",
                    currentFailure,
                    legacyFailure);
            }

            if (currentFailure is not null)
            {
                ExceptionDispatchInfo.Capture(currentFailure).Throw();
            }

            return currentDeleted || legacyDeleted;
        }
        finally
        {
            gate.Release();
        }
    }

    private static string GetTombstoneReference(string reference)
    {
        var digest = Convert.ToHexString(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(reference)))
            .ToLowerInvariant();
        return $"migration-tombstone/{digest}";
    }

    private static void ValidateReference(string reference) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
}
