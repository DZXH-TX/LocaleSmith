namespace LocaleSmith.Mcp;

public sealed class McpServerOptions
{
    public const int DefaultMaximumMessageBytes = 64 * 1024;
    public const int DefaultMaximumOutputCharacters = 32 * 1024;

    public int MaximumMessageBytes { get; init; } = DefaultMaximumMessageBytes;

    public int MaximumOutputCharacters { get; init; } = DefaultMaximumOutputCharacters;

    public int MaximumRequestsPerWindow { get; init; } = 120;

    public TimeSpan RateLimitWindow { get; init; } = TimeSpan.FromMinutes(1);

    public int MaximumConcurrentToolCalls { get; init; } = 4;

    public bool EnableCliExecution { get; init; }

    internal void Validate()
    {
        if (MaximumMessageBytes is < 256 or > 4 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumMessageBytes),
                "The maximum MCP message size must be between 256 bytes and 4 MiB.");
        }

        if (MaximumOutputCharacters is < 256 or > 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumOutputCharacters),
                "The maximum MCP output size must be between 256 characters and 1 MiB.");
        }

        if (MaximumRequestsPerWindow is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumRequestsPerWindow),
                "The request limit must be between 1 and 10,000.");
        }

        if (RateLimitWindow < TimeSpan.FromSeconds(1) || RateLimitWindow > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(RateLimitWindow),
                "The rate-limit window must be between one second and one hour.");
        }

        if (MaximumConcurrentToolCalls is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumConcurrentToolCalls),
                "The concurrent tool-call limit must be between 1 and 32.");
        }
    }
}
