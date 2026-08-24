using LocaleSmith.Archive.ClassFile;

namespace LocaleSmith.Archive.Tests;

public sealed class MojangComponentLiteralClassFileRewriterTests
{
    [Fact]
    public void AnalyzesAndRewritesExactLiteralOccurrence()
    {
        byte[] original = ClassFileFixtureBuilder.CreateSafeLiteralClass("Open settings");

        ClassFileLiteralCandidate candidate = Assert.Single(
            MojangComponentLiteralClassFileRewriter.Analyze(original, "samplemod"));

        Assert.True(candidate.IsSafe, candidate.UnsafeReason);
        Assert.Equal("example/Test", candidate.ClassName);
        Assert.Equal("run", candidate.MethodName);
        Assert.Equal("()V", candidate.MethodDescriptor);
        Assert.Equal(0, candidate.BytecodeOffset);
        Assert.Equal("ldc", candidate.Opcode);
        Assert.Equal((ushort)9, candidate.ConstantPoolIndex);
        Assert.Equal("Open settings", candidate.Value);
        Assert.StartsWith("samplemod.hardcoded.example.test.run.", candidate.SuggestedKey);

        ClassFileRewriteSelection selection = Select(candidate, "samplemod.ui.open_settings");
        ClassFileRewriteResult rewritten = MojangComponentLiteralClassFileRewriter.Rewrite(
            original,
            new[] { selection });

        ClassFileRewriteAppliedCandidate applied = Assert.Single(rewritten.AppliedCandidates);
        Assert.Equal(selection.TranslationKey, applied.TranslationKey);
        Assert.Equal(selection.BytecodeOffset, applied.BytecodeOffset);
        Assert.True(applied.TranslationKeyConstantPoolIndex > candidate.ConstantPoolIndex);
        Assert.False(original.AsSpan().SequenceEqual(rewritten.Bytes));
        MojangComponentLiteralClassFileRewriter.VerifyApplied(rewritten.Bytes, new[] { selection });

        ClassFileLiteralCandidate stagedCandidate = Assert.Single(
            MojangComponentLiteralClassFileRewriter.Analyze(rewritten.Bytes, "samplemod"));
        Assert.Equal("samplemod.ui.open_settings", stagedCandidate.Value);
        Assert.False(stagedCandidate.IsSafe);
    }

    [Fact]
    public void AppendsIndependentConstantsWhenOriginalPoolEntriesAreShared()
    {
        byte[] original = ClassFileFixtureBuilder.Create(
            ClassFileFixtureKind.SharedConstantPool,
            "Shared text");
        IReadOnlyList<ClassFileLiteralCandidate> candidates =
            MojangComponentLiteralClassFileRewriter.Analyze(original, "samplemod");

        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, candidate => Assert.True(candidate.IsSafe, candidate.UnsafeReason));
        Assert.All(candidates, candidate => Assert.Equal((ushort)9, candidate.ConstantPoolIndex));
        Assert.NotEqual(candidates[0].SuggestedKey, candidates[1].SuggestedKey);
        Assert.Equal(
            candidates.Select(static candidate => candidate.SuggestedKey),
            MojangComponentLiteralClassFileRewriter.Analyze(original, "samplemod")
                .Select(static candidate => candidate.SuggestedKey));

        ClassFileRewriteSelection selection = Select(candidates[0], "samplemod.shared.first");
        ClassFileRewriteResult rewritten = MojangComponentLiteralClassFileRewriter.Rewrite(
            original,
            new[] { selection });
        IReadOnlyList<ClassFileLiteralCandidate> staged =
            MojangComponentLiteralClassFileRewriter.Analyze(rewritten.Bytes, "samplemod");

        Assert.Equal(2, staged.Count);
        Assert.Equal("samplemod.shared.first", staged[0].Value);
        Assert.NotEqual((ushort)9, staged[0].ConstantPoolIndex);
        Assert.False(staged[0].IsSafe);
        Assert.Equal("Shared text", staged[1].Value);
        Assert.Equal((ushort)9, staged[1].ConstantPoolIndex);
        Assert.True(staged[1].IsSafe, staged[1].UnsafeReason);
    }

    [Fact]
    public void CapacityPlanKeepsSafeNarrowLdcSubsetWithoutWideningBytecode()
    {
        byte[] original = ClassFileFixtureBuilder.Create(
            ClassFileFixtureKind.SharedConstantPoolNearLdcLimit,
            "Capacity-limited text");

        IReadOnlyList<ClassFileLiteralCandidate> candidates =
            MojangComponentLiteralClassFileRewriter.Analyze(original, "samplemod");

        Assert.Equal(2, candidates.Count);
        Assert.True(candidates[0].IsSafe, candidates[0].UnsafeReason);
        Assert.False(candidates[1].IsSafe);
        Assert.Contains("without widening", candidates[1].UnsafeReason, StringComparison.Ordinal);
        ClassFileRewriteSelection safeSelection = Select(
            candidates[0],
            "samplemod.capacity.first");
        ClassFileRewriteResult rewritten = MojangComponentLiteralClassFileRewriter.Rewrite(
            original,
            [safeSelection]);

        Assert.Single(rewritten.AppliedCandidates);
        MojangComponentLiteralClassFileRewriter.VerifyApplied(rewritten.Bytes, [safeSelection]);
        IReadOnlyList<ClassFileLiteralCandidate> staged =
            MojangComponentLiteralClassFileRewriter.Analyze(rewritten.Bytes, "samplemod");
        Assert.Equal("samplemod.capacity.first", staged[0].Value);
        Assert.Equal("Capacity-limited text", staged[1].Value);
        Assert.Throws<InvalidDataException>(() =>
            MojangComponentLiteralClassFileRewriter.Rewrite(
                original,
                candidates.Select((candidate, index) => Select(
                    candidate,
                    $"samplemod.capacity.{index}"))
                    .ToArray()));
    }

    [Theory]
    [InlineData((int)ClassFileFixtureKind.NonAdjacent)]
    [InlineData((int)ClassFileFixtureKind.WrongMethod)]
    [InlineData((int)ClassFileFixtureKind.BranchTargetsInvocation)]
    [InlineData((int)ClassFileFixtureKind.ExceptionBoundaryAtInvocation)]
    public void RetainsButRejectsUnsafeStringLoads(int fixtureKind)
    {
        var kind = (ClassFileFixtureKind)fixtureKind;
        byte[] bytes = ClassFileFixtureBuilder.Create(kind);

        ClassFileLiteralCandidate candidate = Assert.Single(
            MojangComponentLiteralClassFileRewriter.Analyze(bytes, "samplemod"));

        Assert.False(candidate.IsSafe);
        Assert.False(string.IsNullOrWhiteSpace(candidate.UnsafeReason));
        Assert.Throws<InvalidDataException>(() =>
            MojangComponentLiteralClassFileRewriter.Rewrite(
                bytes,
                new[] { Select(candidate, "samplemod.rejected") }));
    }

    [Fact]
    public void SupportsExactInterfaceMethodReferenceAndPreservesItsKind()
    {
        byte[] bytes = ClassFileFixtureBuilder.Create(
            ClassFileFixtureKind.InterfaceMethodReference,
            "Interface literal");
        ClassFileLiteralCandidate candidate = Assert.Single(
            MojangComponentLiteralClassFileRewriter.Analyze(bytes, "samplemod"));
        Assert.True(candidate.IsSafe, candidate.UnsafeReason);
        ClassFileRewriteSelection selection = Select(candidate, "samplemod.interface.literal");

        ClassFileRewriteResult result = MojangComponentLiteralClassFileRewriter.Rewrite(
            bytes,
            new[] { selection });

        MojangComponentLiteralClassFileRewriter.VerifyApplied(result.Bytes, new[] { selection });
    }

    [Fact]
    public void LdcWideCanAddressAnAppendedConstantAboveByteRange()
    {
        byte[] bytes = ClassFileFixtureBuilder.Create(ClassFileFixtureKind.LdcWide, "Wide literal");
        ClassFileLiteralCandidate candidate = Assert.Single(
            MojangComponentLiteralClassFileRewriter.Analyze(bytes, "samplemod"));
        Assert.True(candidate.IsSafe, candidate.UnsafeReason);
        Assert.Equal("ldc_w", candidate.Opcode);

        ClassFileRewriteSelection selection = Select(candidate, "samplemod.wide.literal");
        ClassFileRewriteResult result = MojangComponentLiteralClassFileRewriter.Rewrite(
            bytes,
            new[] { selection });

        Assert.True(Assert.Single(result.AppliedCandidates).TranslationKeyConstantPoolIndex > byte.MaxValue);
        MojangComponentLiteralClassFileRewriter.VerifyApplied(result.Bytes, new[] { selection });
    }

    [Fact]
    public void VerifyAppliedRejectsControlFlowTamperingIntoSecondInstruction()
    {
        const string key = "samplemod.tampered.literal";
        byte[] staged = ClassFileFixtureBuilder.Create(
            ClassFileFixtureKind.BranchTargetsTranslatable,
            key);
        var selection = new ClassFileRewriteSelection(
            "example/Test",
            "run",
            "()V",
            0,
            "Original literal",
            key);

        Assert.Throws<InvalidDataException>(() =>
            MojangComponentLiteralClassFileRewriter.VerifyApplied(staged, new[] { selection }));
    }

    [Fact]
    public void RejectsStaleSelectionInsteadOfRewritingAnotherValue()
    {
        byte[] bytes = ClassFileFixtureBuilder.CreateSafeLiteralClass("Current value");
        ClassFileLiteralCandidate candidate = Assert.Single(
            MojangComponentLiteralClassFileRewriter.Analyze(bytes, "samplemod"));
        ClassFileRewriteSelection stale = Select(candidate, "samplemod.current") with
        {
            ExpectedValue = "Old value"
        };

        Assert.Throws<InvalidDataException>(() =>
            MojangComponentLiteralClassFileRewriter.Rewrite(bytes, new[] { stale }));
    }

    [Fact]
    public void FailsClosedForTruncatedUnknownAndMisalignedBytecode()
    {
        byte[] valid = ClassFileFixtureBuilder.CreateSafeLiteralClass();
        byte[] truncated = valid[..^1];
        byte[] unknownOpcode = ClassFileFixtureBuilder.Create(ClassFileFixtureKind.UnknownOpcode);
        byte[] misalignedBranch = ClassFileFixtureBuilder.Create(
            ClassFileFixtureKind.BranchTargetsInstructionInterior);

        Assert.Throws<InvalidDataException>(() =>
            MojangComponentLiteralClassFileRewriter.Analyze(truncated, "samplemod"));
        Assert.Throws<InvalidDataException>(() =>
            MojangComponentLiteralClassFileRewriter.Analyze(unknownOpcode, "samplemod"));
        Assert.Throws<InvalidDataException>(() =>
            MojangComponentLiteralClassFileRewriter.Analyze(misalignedBranch, "samplemod"));
    }

    [Fact]
    public void FailsClosedForUnknownConstantPoolTag()
    {
        byte[] bytes = ClassFileFixtureBuilder.CreateSafeLiteralClass();
        bytes[10] = 99;

        Assert.Throws<InvalidDataException>(() =>
            MojangComponentLiteralClassFileRewriter.Analyze(bytes, "samplemod"));
    }

    private static ClassFileRewriteSelection Select(
        ClassFileLiteralCandidate candidate,
        string translationKey) =>
        new(
            candidate.ClassName,
            candidate.MethodName,
            candidate.MethodDescriptor,
            candidate.BytecodeOffset,
            candidate.Value,
            translationKey);
}
