namespace LocaleSmith.Infrastructure.ModPlatform;

/// <summary>Validates transport-level invariants shared by every versioned Mod platform endpoint.</summary>
internal static class ModPlatformApiContract
{
    internal const string ApiVersion = "1.0";
    internal const string ApiVersionHeaderName = "X-API-Version";

    internal static void ValidateVersionHeader(HttpResponseMessage response, Uri? requestUri)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (requestUri is null
            || !requestUri.AbsolutePath.StartsWith("/api/v1/", StringComparison.Ordinal))
        {
            return;
        }

        if (!response.Headers.TryGetValues(ApiVersionHeaderName, out var values))
        {
            throw InvalidVersionResponse(response);
        }

        var declaredVersions = values.ToArray();
        if (declaredVersions.Length != 1
            || !string.Equals(declaredVersions[0], ApiVersion, StringComparison.Ordinal))
        {
            throw InvalidVersionResponse(response);
        }
    }

    private static ModPlatformException InvalidVersionResponse(HttpResponseMessage response) => new(
        response.StatusCode,
        "invalid_response",
        "The Mod platform response did not declare API version 1.0.");
}
