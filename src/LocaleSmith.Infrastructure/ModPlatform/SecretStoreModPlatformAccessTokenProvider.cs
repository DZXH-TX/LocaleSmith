using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;

namespace LocaleSmith.Infrastructure.ModPlatform;

/// <summary>Resolves the optional personal access token from the existing credential-backed secret store.</summary>
public sealed class SecretStoreModPlatformAccessTokenProvider : IModPlatformAccessTokenProvider
{
    public const string DefaultSecretReference = "integrations/mctx-mod-hub/pat";

    private readonly ISecretResolver _secrets;
    private readonly string _secretReference;

    public SecretStoreModPlatformAccessTokenProvider(
        ISecretResolver secrets,
        string secretReference = DefaultSecretReference)
    {
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        ArgumentException.ThrowIfNullOrWhiteSpace(secretReference);
        _secretReference = secretReference;
    }

    public ValueTask<SecretValue?> ResolveAsync(CancellationToken cancellationToken = default) =>
        _secrets.ResolveAsync(_secretReference, cancellationToken);
}
