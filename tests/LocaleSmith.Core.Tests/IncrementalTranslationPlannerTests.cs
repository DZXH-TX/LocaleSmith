using LocaleSmith.Core.Models;
using LocaleSmith.Core.Services;

namespace LocaleSmith.Core.Tests;

public sealed class IncrementalTranslationPlannerTests
{
    [Fact]
    public void OnlyNewOrChangedEntriesArePending()
    {
        var unchanged = new TranslationEntry("assets/demo/lang/en_us.json", "item.demo", "Demo item");
        var changed = new TranslationEntry("assets/demo/lang/en_us.json", "item.changed", "New text");
        var previous = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [unchanged.StableId] = IncrementalTranslationPlanner.ComputeHash(unchanged),
            [changed.StableId] = IncrementalTranslationPlanner.ComputeHash(
                new TranslationEntry(changed.RelativePath, changed.Key, "Old text"))
        };

        var plan = IncrementalTranslationPlanner.Create([unchanged, changed], previous);

        var pending = Assert.Single(plan.PendingEntries);
        Assert.Same(changed, pending);
        Assert.Equal(2, plan.CurrentHashes.Count);
    }

    [Fact]
    public void PathAndKeyArePartOfTheFingerprint()
    {
        var first = new TranslationEntry("one.json", "same", "text");
        var second = new TranslationEntry("two.json", "same", "text");

        Assert.NotEqual(
            IncrementalTranslationPlanner.ComputeHash(first),
            IncrementalTranslationPlanner.ComputeHash(second));
    }

    [Fact]
    public void DuplicateStableIdentityIsRejected()
    {
        var entry = new TranslationEntry("same.json", "key", "text");

        Assert.Throws<ArgumentException>(() => IncrementalTranslationPlanner.Create([entry, entry]));
    }

    [Fact]
    public void TranslationRequestDefaultsToFormalStyleOnly()
    {
        var request = new TranslationBatchRequest([new TranslationEntry("pack.txt", null, "Description")]);

        Assert.Equal("zh_CN", request.TargetLanguage);
        Assert.Equal(TranslationStyle.Formal, Assert.Single(request.Styles));
    }

    [Fact]
    public void TranslationRequestRejectsMultipleOrUnknownStyles()
    {
        var entry = new TranslationEntry("pack.txt", null, "Description");

        Assert.Throws<ArgumentException>(() => new TranslationBatchRequest(
            [entry],
            styles: new HashSet<TranslationStyle>
            {
                TranslationStyle.Formal,
                TranslationStyle.Informal
            }));
        Assert.Throws<ArgumentException>(() => new TranslationBatchRequest(
            [entry],
            styles: new HashSet<TranslationStyle> { (TranslationStyle)99 }));
    }
}
