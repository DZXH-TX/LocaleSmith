namespace LocaleSmith.Core.Models;

/// <summary>
/// Provider-reported token usage aggregated across one or more provider calls.
/// Values are never estimated. When a provider omits an explicit total, the total is calculated
/// only when both provider-reported input and output counts are available.
/// </summary>
public sealed record ModelTokenUsage
{
    public ModelTokenUsage(
        long? inputTokens,
        long? outputTokens,
        long? totalTokens,
        int providerCallCount,
        int callsWithUsage,
        int callsWithCompleteUsage)
    {
        ValidateTokenCount(inputTokens, nameof(inputTokens));
        ValidateTokenCount(outputTokens, nameof(outputTokens));
        ValidateTokenCount(totalTokens, nameof(totalTokens));
        if (providerCallCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(providerCallCount),
                "Provider call count cannot be negative.");
        }

        if (callsWithUsage < 0 || callsWithUsage > providerCallCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(callsWithUsage),
                "Calls with usage must be between zero and the provider call count.");
        }

        if (callsWithCompleteUsage < 0 || callsWithCompleteUsage > callsWithUsage)
        {
            throw new ArgumentOutOfRangeException(
                nameof(callsWithCompleteUsage),
                "Calls with complete usage must be between zero and the calls with any usage.");
        }

        if (callsWithUsage == 0 &&
            (inputTokens is not null || outputTokens is not null || totalTokens is not null))
        {
            throw new ArgumentException(
                "Token counts require at least one provider call with reported usage.",
                nameof(callsWithUsage));
        }

        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        TotalTokens = totalTokens;
        ProviderCallCount = providerCallCount;
        CallsWithUsage = callsWithUsage;
        CallsWithCompleteUsage = callsWithCompleteUsage;
    }

    /// <summary>
    /// No provider calls were made, so the usage is complete without fabricating zero-valued
    /// provider fields.
    /// </summary>
    public static ModelTokenUsage Empty { get; } = new(null, null, null, 0, 0, 0);

    /// <summary>
    /// One provider call completed without recognized usage fields.
    /// </summary>
    public static ModelTokenUsage MissingProviderCall { get; } = new(null, null, null, 1, 0, 0);

    public long? InputTokens { get; }

    public long? OutputTokens { get; }

    public long? TotalTokens { get; }

    public int ProviderCallCount { get; }

    public int CallsWithUsage { get; }

    public int CallsWithCompleteUsage { get; }

    public bool IsComplete => CallsWithCompleteUsage == ProviderCallCount;

    /// <summary>
    /// Creates usage for one provider response. Returns <see langword="null"/> when the response
    /// contains no recognized usage fields. An official provider total takes precedence; otherwise
    /// an exact total is calculated only from provider-reported input and output counts.
    /// </summary>
    public static ModelTokenUsage? FromProviderResponse(
        long? inputTokens,
        long? outputTokens,
        long? providerReportedTotalTokens = null)
    {
        if (inputTokens is null && outputTokens is null && providerReportedTotalTokens is null)
        {
            return null;
        }

        long? totalTokens = providerReportedTotalTokens;
        if (totalTokens is null && inputTokens is { } input && outputTokens is { } output)
        {
            totalTokens = checked(input + output);
        }

        int callsWithCompleteUsage = providerReportedTotalTokens is not null ||
            (inputTokens is not null && outputTokens is not null)
                ? 1
                : 0;
        return new ModelTokenUsage(
            inputTokens,
            outputTokens,
            totalTokens,
            providerCallCount: 1,
            callsWithUsage: 1,
            callsWithCompleteUsage);
    }

    /// <summary>
    /// Adds a provider response to this aggregate. A missing response usage object still increments
    /// the provider call count and makes the aggregate incomplete.
    /// </summary>
    public ModelTokenUsage AddProviderCall(ModelTokenUsage? usage) =>
        Combine(usage ?? MissingProviderCall);

    public ModelTokenUsage Combine(ModelTokenUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return new ModelTokenUsage(
            AddKnownCounts(InputTokens, usage.InputTokens),
            AddKnownCounts(OutputTokens, usage.OutputTokens),
            AddKnownCounts(TotalTokens, usage.TotalTokens),
            checked(ProviderCallCount + usage.ProviderCallCount),
            checked(CallsWithUsage + usage.CallsWithUsage),
            checked(CallsWithCompleteUsage + usage.CallsWithCompleteUsage));
    }

    private static long? AddKnownCounts(long? left, long? right)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        return checked(left.Value + right.Value);
    }

    private static void ValidateTokenCount(long? value, string parameterName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Token counts cannot be negative.");
        }
    }
}
