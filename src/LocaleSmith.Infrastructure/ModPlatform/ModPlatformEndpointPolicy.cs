namespace LocaleSmith.Infrastructure.ModPlatform;

/// <summary>
/// Keeps production pinned to the public HTTPS origin. A loopback override is accepted only when
/// the application is explicitly marked as a development process.
/// </summary>
public static class ModPlatformEndpointPolicy
{
    public const string EnvironmentNameVariable = "LOCALESMITH_ENVIRONMENT";
    public const string DevelopmentBaseUriVariable = "LOCALESMITH_MOD_PLATFORM_BASE_URI";

    public static Uri ResolveApplicationBaseUri() => Resolve(
        System.Environment.GetEnvironmentVariable(EnvironmentNameVariable),
        System.Environment.GetEnvironmentVariable(DevelopmentBaseUriVariable));

    internal static Uri Resolve(string? environmentName, string? configuredBaseUri)
    {
        if (string.IsNullOrWhiteSpace(configuredBaseUri))
        {
            return ModPlatformClient.ProductionBaseUri;
        }

        if (!string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{DevelopmentBaseUriVariable} is allowed only when {EnvironmentNameVariable}=Development.");
        }

        if (!Uri.TryCreate(configuredBaseUri.Trim(), UriKind.Absolute, out var candidate)
            || candidate.UserInfo.Length != 0
            || candidate.Query.Length != 0
            || candidate.Fragment.Length != 0
            || candidate.AbsolutePath != "/"
            || !candidate.IsLoopback
            || candidate.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                $"{DevelopmentBaseUriVariable} must be an HTTP(S) loopback origin without a path, query, or credentials.");
        }

        return new Uri(candidate.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
    }
}
