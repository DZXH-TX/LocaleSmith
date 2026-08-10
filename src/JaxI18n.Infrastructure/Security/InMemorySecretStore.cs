using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using JaxI18n.Core.Abstractions;
using JaxI18n.Core.Models;

namespace JaxI18n.Infrastructure.Security;

/// <summary>Cross-platform volatile store intended for tests and ephemeral sessions only.</summary>
public sealed class InMemorySecretStore : ISecretStore, IDisposable
{
    private readonly ConcurrentDictionary<string, char[]> _values = new(StringComparer.Ordinal);
    private bool _disposed;

    public ValueTask<SecretValue?> ResolveAsync(string reference, CancellationToken cancellationToken = default)
    {
        ValidateReference(reference);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ValueTask.FromResult<SecretValue?>(
            _values.TryGetValue(reference, out var value) ? new SecretValue(value) : null);
    }

    public ValueTask SetAsync(
        string reference,
        ReadOnlyMemory<char> secret,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(reference);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (secret.IsEmpty)
        {
            throw new ArgumentException("Secrets cannot be empty.", nameof(secret));
        }

        var replacement = secret.ToArray();
        _values.AddOrUpdate(
            reference,
            replacement,
            (_, previous) =>
            {
                Clear(previous);
                return replacement;
            });
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> DeleteAsync(string reference, CancellationToken cancellationToken = default)
    {
        ValidateReference(reference);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_values.TryRemove(reference, out var value))
        {
            return ValueTask.FromResult(false);
        }

        Clear(value);
        return ValueTask.FromResult(true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var pair in _values)
        {
            Clear(pair.Value);
        }

        _values.Clear();
        _disposed = true;
    }

    private static void ValidateReference(string reference) => ArgumentException.ThrowIfNullOrWhiteSpace(reference);

    private static void Clear(char[] value) =>
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(value.AsSpan()));
}
