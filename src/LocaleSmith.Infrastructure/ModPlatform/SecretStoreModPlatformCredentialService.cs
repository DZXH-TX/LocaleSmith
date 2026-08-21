using LocaleSmith.Core.Abstractions;

namespace LocaleSmith.Infrastructure.ModPlatform;

/// <summary>Stores the Mod platform PAT only in the existing credential-backed secret store.</summary>
public sealed class SecretStoreModPlatformCredentialService : IModPlatformCredentialService
{
    private const int MaximumTokenLength = 256;
    private readonly ISecretStore _secrets;

    public SecretStoreModPlatformCredentialService(ISecretStore secrets)
    {
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
    }

    public async ValueTask<bool> IsConfiguredAsync(CancellationToken cancellationToken = default)
    {
        using var token = await _secrets
            .ResolveAsync(
                SecretStoreModPlatformAccessTokenProvider.DefaultSecretReference,
                cancellationToken)
            .ConfigureAwait(false);
        return token is { Length: > 0 };
    }

    public ValueTask SaveAsync(
        ReadOnlyMemory<char> token,
        CancellationToken cancellationToken = default)
    {
        Validate(token.Span);
        return _secrets.SetAsync(
            SecretStoreModPlatformAccessTokenProvider.DefaultSecretReference,
            token,
            cancellationToken);
    }

    public ValueTask<bool> DeleteAsync(CancellationToken cancellationToken = default) =>
        _secrets.DeleteAsync(
            SecretStoreModPlatformAccessTokenProvider.DefaultSecretReference,
            cancellationToken);

    private static void Validate(ReadOnlySpan<char> token)
    {
        var containsWhitespaceOrControl = false;
        foreach (var character in token)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                containsWhitespaceOrControl = true;
                break;
            }
        }

        if (token.Length is < 17 or > MaximumTokenLength
            || !token.StartsWith("mctx_pat_", StringComparison.Ordinal)
            || containsWhitespaceOrControl)
        {
            throw new ArgumentException(
                "The Mod platform credential must be a valid mctx_pat_ personal access token.",
                nameof(token));
        }
    }
}
