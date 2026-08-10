namespace JaxI18n.Archive.ClassFile;

/// <summary>
/// Describes one string constant loaded by an <c>ldc</c> or <c>ldc_w</c>
/// instruction in a Java class file.
/// </summary>
public sealed record ClassFileLiteralCandidate(
    string ClassName,
    string MethodName,
    string MethodDescriptor,
    int BytecodeOffset,
    string Opcode,
    ushort ConstantPoolIndex,
    string Value,
    string SuggestedKey,
    bool IsSafe,
    string? UnsafeReason);

/// <summary>
/// Selects an analyzed occurrence for rewriting. The original location and
/// value are repeated deliberately so stale analysis cannot rewrite a
/// different instruction.
/// </summary>
public sealed record ClassFileRewriteSelection(
    string ClassName,
    string MethodName,
    string MethodDescriptor,
    int BytecodeOffset,
    string ExpectedValue,
    string TranslationKey);

/// <summary>
/// Records one rewrite that was applied and subsequently verified.
/// </summary>
public sealed record ClassFileRewriteAppliedCandidate(
    string ClassName,
    string MethodName,
    string MethodDescriptor,
    int BytecodeOffset,
    string OriginalValue,
    string TranslationKey,
    ushort TranslationKeyConstantPoolIndex,
    ushort TranslatableMethodConstantPoolIndex);

/// <summary>
/// Contains a rebuilt class file and the verified rewrites it contains.
/// </summary>
public sealed record ClassFileRewriteResult(
    byte[] Bytes,
    IReadOnlyList<ClassFileRewriteAppliedCandidate> AppliedCandidates);
