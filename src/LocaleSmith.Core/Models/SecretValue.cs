using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace LocaleSmith.Core.Models;

public sealed class SecretValue : IDisposable
{
    private char[]? _characters;

    public SecretValue(ReadOnlySpan<char> value)
    {
        _characters = value.ToArray();
    }

    public int Length => GetCharacters().Length;

    /// <summary>
    /// Materializes the value for an API that only accepts strings. Keep the returned string scoped as narrowly as possible.
    /// </summary>
    public string DangerousGetString() => new(GetCharacters());

    /// <summary>Copies the secret into caller-owned memory that the caller can explicitly clear.</summary>
    public void CopyTo(Span<char> destination)
    {
        var characters = GetCharacters();
        if (destination.Length < characters.Length)
        {
            throw new ArgumentException("The destination is too small for the secret.", nameof(destination));
        }

        characters.CopyTo(destination);
    }

    public void Dispose()
    {
        var characters = Interlocked.Exchange(ref _characters, null);
        if (characters is not null)
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(characters.AsSpan()));
        }
    }

    private ReadOnlySpan<char> GetCharacters()
    {
        ObjectDisposedException.ThrowIf(_characters is null, this);
        return _characters;
    }
}
