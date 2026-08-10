using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using JaxI18n.Core.Models;

namespace JaxI18n.Core.Services;

public sealed record IncrementalTranslationPlan(
    IReadOnlyList<TranslationEntry> PendingEntries,
    IReadOnlyDictionary<string, string> CurrentHashes);

public static class IncrementalTranslationPlanner
{
    public static IncrementalTranslationPlan Create(
        IEnumerable<TranslationEntry> entries,
        IReadOnlyDictionary<string, string>? previousHashes = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        previousHashes ??= new Dictionary<string, string>(StringComparer.Ordinal);

        var pending = new List<TranslationEntry>();
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            var hash = ComputeHash(entry);
            if (!hashes.TryAdd(entry.StableId, hash))
            {
                throw new ArgumentException($"Duplicate translation entry identity: {entry.StableId}", nameof(entries));
            }

            if (!previousHashes.TryGetValue(entry.StableId, out var previous) ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(hash),
                    Encoding.ASCII.GetBytes(previous)))
            {
                pending.Add(entry);
            }
        }

        return new IncrementalTranslationPlan(pending, hashes);
    }

    public static string ComputeHash(TranslationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendLengthPrefixed(hash, entry.RelativePath);
        AppendLengthPrefixed(hash, entry.Key ?? string.Empty);
        AppendLengthPrefixed(hash, entry.SourceText);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendLengthPrefixed(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
        CryptographicOperations.ZeroMemory(bytes);
    }
}
