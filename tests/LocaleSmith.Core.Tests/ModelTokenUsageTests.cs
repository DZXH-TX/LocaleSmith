using LocaleSmith.Core.Models;

namespace LocaleSmith.Core.Tests;

public sealed class ModelTokenUsageTests
{
    [Fact]
    public void ProviderResponseUsesOfficialTotalAndLongCounts()
    {
        ModelTokenUsage usage = ModelTokenUsage.FromProviderResponse(
            3_000_000_000L,
            4_000_000_000L,
            9_000_000_000L)!;

        Assert.Equal(3_000_000_000L, usage.InputTokens);
        Assert.Equal(4_000_000_000L, usage.OutputTokens);
        Assert.Equal(9_000_000_000L, usage.TotalTokens);
        Assert.Equal(1, usage.ProviderCallCount);
        Assert.Equal(1, usage.CallsWithUsage);
        Assert.Equal(1, usage.CallsWithCompleteUsage);
        Assert.True(usage.IsComplete);
    }

    [Fact]
    public void ProviderResponseCalculatesExactTotalOnlyFromReportedComponents()
    {
        ModelTokenUsage usage = ModelTokenUsage.FromProviderResponse(7, 2)!;
        ModelTokenUsage outputOnly = ModelTokenUsage.FromProviderResponse(null, 4)!;

        Assert.Equal(9L, usage.TotalTokens);
        Assert.Null(outputOnly.InputTokens);
        Assert.Equal(4L, outputOnly.OutputTokens);
        Assert.Null(outputOnly.TotalTokens);
        Assert.Equal(1, outputOnly.CallsWithUsage);
        Assert.Equal(0, outputOnly.CallsWithCompleteUsage);
        Assert.False(outputOnly.IsComplete);
    }

    [Fact]
    public void MissingProviderCallPreservesKnownCountsAndMarksAggregateIncomplete()
    {
        ModelTokenUsage aggregate = ModelTokenUsage.Empty
            .AddProviderCall(ModelTokenUsage.FromProviderResponse(7, 2))
            .AddProviderCall(null);

        Assert.Equal(7L, aggregate.InputTokens);
        Assert.Equal(2L, aggregate.OutputTokens);
        Assert.Equal(9L, aggregate.TotalTokens);
        Assert.Equal(2, aggregate.ProviderCallCount);
        Assert.Equal(1, aggregate.CallsWithUsage);
        Assert.Equal(1, aggregate.CallsWithCompleteUsage);
        Assert.False(aggregate.IsComplete);
    }

    [Fact]
    public void PartialComponentUsageCannotMakeAggregateLookComplete()
    {
        ModelTokenUsage aggregate = ModelTokenUsage.Empty
            .AddProviderCall(ModelTokenUsage.FromProviderResponse(5, null))
            .AddProviderCall(ModelTokenUsage.FromProviderResponse(7, 2));

        Assert.Equal(2, aggregate.ProviderCallCount);
        Assert.Equal(2, aggregate.CallsWithUsage);
        Assert.Equal(1, aggregate.CallsWithCompleteUsage);
        Assert.Equal(12L, aggregate.InputTokens);
        Assert.Equal(2L, aggregate.OutputTokens);
        Assert.Equal(9L, aggregate.TotalTokens);
        Assert.False(aggregate.IsComplete);
    }

    [Fact]
    public void RejectsNegativeOrStructurallyImpossibleUsage()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ModelTokenUsage.FromProviderResponse(-1, 2));
        Assert.Throws<ArgumentException>(() =>
            new ModelTokenUsage(
                1,
                null,
                null,
                providerCallCount: 1,
                callsWithUsage: 0,
                callsWithCompleteUsage: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ModelTokenUsage(
                null,
                null,
                null,
                providerCallCount: 1,
                callsWithUsage: 2,
                callsWithCompleteUsage: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ModelTokenUsage(
                null,
                1,
                null,
                providerCallCount: 1,
                callsWithUsage: 1,
                callsWithCompleteUsage: 2));
    }

    [Fact]
    public void ModelResponseKeepsLegacyCountsAndStructuredAggregate()
    {
        var legacy = new ModelResponse("ok", inputTokens: 5, outputTokens: 3, totalTokens: 10);
        var aggregate = ModelTokenUsage.Empty
            .AddProviderCall(ModelTokenUsage.FromProviderResponse(5, 3, 10))
            .AddProviderCall(null);
        var structured = new ModelResponse("ok", usage: aggregate);

        Assert.Equal(5L, legacy.InputTokens);
        Assert.Equal(3L, legacy.OutputTokens);
        Assert.Equal(10L, legacy.TotalTokens);
        Assert.Same(aggregate, structured.Usage);
        Assert.False(structured.Usage!.IsComplete);
        Assert.Throws<ArgumentException>(() =>
            new ModelResponse("invalid", inputTokens: 1, usage: aggregate));
    }
}
