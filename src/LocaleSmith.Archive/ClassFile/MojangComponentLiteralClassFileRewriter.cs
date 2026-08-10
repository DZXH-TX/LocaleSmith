using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace LocaleSmith.Archive.ClassFile;

/// <summary>
/// Analyzes and rewrites only the verified Mojang Component.literal safe
/// subset. It never changes an existing constant-pool entry and never changes
/// bytecode length.
/// </summary>
public static class MojangComponentLiteralClassFileRewriter
{
    private const string ComponentOwner = "net/minecraft/network/chat/Component";
    private const string LiteralMethod = "literal";
    private const string TranslatableMethod = "translatable";
    private const string ComponentDescriptor =
        "(Ljava/lang/String;)Lnet/minecraft/network/chat/MutableComponent;";
    private const int ConstantsPerRewrite = 8;
    private const int MaxLiteralCharacters = 16_384;
    private const int MaxTranslationKeyLength = 240;

    /// <summary>
    /// Returns every parsable string loaded with ldc/ldc_w. Only exact,
    /// unbranched Component.literal occurrences are marked safe.
    /// </summary>
    public static IReadOnlyList<ClassFileLiteralCandidate> Analyze(
        ReadOnlySpan<byte> classBytes,
        string keyNamespace)
    {
        ArgumentNullException.ThrowIfNull(keyNamespace);
        ParsedClassFile parsed = ParsedClassFile.Parse(classBytes);
        string normalizedNamespace = NormalizeKeySegment(keyNamespace, 48, "mod");
        var candidates = new List<ClassFileLiteralCandidate>();

        foreach (ParsedMethod method in parsed.Methods)
        {
            if (method.Code is not { } code)
            {
                continue;
            }

            foreach (JavaInstruction instruction in code.Analysis.Instructions)
            {
                if (instruction.Opcode is not (0x12 or 0x13) ||
                    instruction.ConstantPoolIndex is not { } poolIndex ||
                    !parsed.ConstantPool.IsTag(poolIndex, ConstantPoolTag.String))
                {
                    continue;
                }

                string value = parsed.ConstantPool.GetString(poolIndex, "ldc string candidate");
                string? unsafeReason = GetUnsafeReason(parsed, code, instruction, value);
                candidates.Add(new ClassFileLiteralCandidate(
                    parsed.ClassName,
                    method.Name,
                    method.Descriptor,
                    instruction.Offset,
                    instruction.Mnemonic,
                    checked((ushort)poolIndex),
                    value,
                    CreateSuggestedKey(
                        normalizedNamespace,
                        parsed.ClassName,
                        method.Name,
                        method.Descriptor,
                        instruction.Offset,
                        value),
                    unsafeReason is null,
                    unsafeReason));
            }
        }

        return candidates.AsReadOnly();
    }

    /// <summary>
    /// Rebuilds the constant pool and rewrites selected safe occurrences. Each
    /// occurrence receives a completely independent constant chain.
    /// </summary>
    public static ClassFileRewriteResult Rewrite(
        ReadOnlySpan<byte> classBytes,
        IReadOnlyCollection<ClassFileRewriteSelection> selections)
    {
        ArgumentNullException.ThrowIfNull(selections);
        ParsedClassFile parsed = ParsedClassFile.Parse(classBytes);
        ClassFileRewriteSelection[] ordered = ValidateAndOrderSelections(selections);
        if ((long)parsed.ConstantPool.Count + ((long)ordered.Length * ConstantsPerRewrite) >
            ushort.MaxValue)
        {
            throw new InvalidDataException("Selected rewrites would exceed the JVM constant-pool limit.");
        }

        byte[] rewrittenTail = parsed.Tail.ToArray();
        var applied = new List<ClassFileRewriteAppliedCandidate>(ordered.Length);
        foreach (ClassFileRewriteSelection selection in ordered)
        {
            (ParsedMethod method, JavaInstruction load, JavaInstruction invocation) =
                ResolveSafeSelection(parsed, selection);

            int prospectiveStringIndex = checked(parsed.ConstantPool.Count + 1);
            if (load.Opcode == 0x12 && prospectiveStringIndex > byte.MaxValue)
            {
                throw new InvalidDataException(
                    $"ldc at {selection.BytecodeOffset} cannot address an appended string without changing bytecode length.");
            }

            ushort keyUtf8 = parsed.ConstantPool.AppendUtf8(selection.TranslationKey);
            ushort keyString = parsed.ConstantPool.AppendSingleIndex(ConstantPoolTag.String, keyUtf8);
            ushort ownerUtf8 = parsed.ConstantPool.AppendUtf8(ComponentOwner);
            ushort ownerClass = parsed.ConstantPool.AppendSingleIndex(ConstantPoolTag.Class, ownerUtf8);
            ushort methodUtf8 = parsed.ConstantPool.AppendUtf8(TranslatableMethod);
            ushort descriptorUtf8 = parsed.ConstantPool.AppendUtf8(ComponentDescriptor);
            ushort nameAndType = parsed.ConstantPool.AppendDoubleIndex(
                ConstantPoolTag.NameAndType,
                methodUtf8,
                descriptorUtf8);
            int originalMethodIndex = invocation.ConstantPoolIndex ??
                throw new InvalidDataException("Selected invokestatic has no constant-pool operand.");
            ConstantPoolTag methodReferenceTag = parsed.ConstantPool.Get(
                originalMethodIndex,
                "selected Component.literal invocation").Tag;
            ushort methodReference = parsed.ConstantPool.AppendDoubleIndex(
                methodReferenceTag,
                ownerClass,
                nameAndType);

            ParsedCode code = method.Code!;
            int loadOperand = checked(code.CodeOffsetInTail + load.Offset + 1);
            if (load.Opcode == 0x12)
            {
                rewrittenTail[loadOperand] = checked((byte)keyString);
            }
            else
            {
                BinaryPrimitives.WriteUInt16BigEndian(
                    rewrittenTail.AsSpan(loadOperand, sizeof(ushort)),
                    keyString);
            }

            BinaryPrimitives.WriteUInt16BigEndian(
                rewrittenTail.AsSpan(
                    checked(code.CodeOffsetInTail + invocation.Offset + 1),
                    sizeof(ushort)),
                methodReference);

            applied.Add(new ClassFileRewriteAppliedCandidate(
                selection.ClassName,
                selection.MethodName,
                selection.MethodDescriptor,
                selection.BytecodeOffset,
                selection.ExpectedValue,
                selection.TranslationKey,
                keyString,
                methodReference));
        }

        byte[] rebuilt = parsed.Rebuild(rewrittenTail);
        VerifyApplied(rebuilt, ordered);
        return new ClassFileRewriteResult(rebuilt, applied.AsReadOnly());
    }

    /// <summary>
    /// Verifies a staged artifact independently from Rewrite. Every selected
    /// location must now load its translation key and invoke the exact Mojang
    /// Component.translatable method.
    /// </summary>
    public static void VerifyApplied(
        ReadOnlySpan<byte> classBytes,
        IReadOnlyCollection<ClassFileRewriteSelection> selections)
    {
        ArgumentNullException.ThrowIfNull(selections);
        ParsedClassFile parsed = ParsedClassFile.Parse(classBytes);
        ClassFileRewriteSelection[] ordered = ValidateAndOrderSelections(selections);
        foreach (ClassFileRewriteSelection selection in ordered)
        {
            ParsedMethod method = FindUniqueMethod(parsed, selection);
            ParsedCode code = method.Code ?? throw new InvalidDataException(
                $"Selected method {selection.MethodName}{selection.MethodDescriptor} has no Code attribute.");
            if (!code.Analysis.InstructionsByOffset.TryGetValue(
                    selection.BytecodeOffset,
                    out JavaInstruction? load) ||
                load.Opcode is not (0x12 or 0x13) ||
                load.ConstantPoolIndex is not { } stringIndex ||
                !parsed.ConstantPool.IsTag(stringIndex, ConstantPoolTag.String))
            {
                throw new InvalidDataException(
                    $"Staged class does not contain the selected string load at offset {selection.BytecodeOffset}.");
            }

            string loaded = parsed.ConstantPool.GetString(stringIndex, "verified translation key");
            if (!string.Equals(loaded, selection.TranslationKey, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Staged class loads an unexpected translation key at offset {selection.BytecodeOffset}.");
            }

            JavaInstruction invocation = GetImmediatelyFollowingInstruction(code, load);
            if (!IsExactMethodReference(parsed.ConstantPool, invocation, TranslatableMethod))
            {
                throw new InvalidDataException(
                    $"Staged class does not invoke the exact Component.translatable method at offset {invocation.Offset}.");
            }

            if (code.Analysis.BranchTargets.Contains(invocation.Offset) ||
                code.ProtectedControlFlowPoints.Contains(invocation.Offset))
            {
                throw new InvalidDataException(
                    "Staged control flow or exception metadata enters the second rewritten instruction.");
            }

            if (IsExactMethodReference(parsed.ConstantPool, invocation, LiteralMethod))
            {
                throw new InvalidDataException("Original Component.literal invocation remains after rewriting.");
            }
        }
    }

    private static string? GetUnsafeReason(
        ParsedClassFile parsed,
        ParsedCode code,
        JavaInstruction load,
        string value)
    {
        if (value.Length == 0)
        {
            return "Empty literal strings are not externalized.";
        }

        if (value.Length > MaxLiteralCharacters)
        {
            return "Literal string exceeds the configured safety limit.";
        }

        if (!TryGetImmediatelyFollowingInstruction(code, load, out JavaInstruction? invocation) ||
            invocation is null)
        {
            return "String load is not immediately followed by another instruction.";
        }

        if (!IsExactMethodReference(parsed.ConstantPool, invocation, LiteralMethod))
        {
            return "String load is not immediately consumed by the exact Mojang Component.literal method.";
        }

        if (code.Analysis.BranchTargets.Contains(invocation.Offset) ||
            code.ProtectedControlFlowPoints.Contains(invocation.Offset))
        {
            return "Control flow or an exception-table boundary enters the second instruction.";
        }

        if ((long)parsed.ConstantPool.Count + ConstantsPerRewrite > ushort.MaxValue)
        {
            return "Constant pool has insufficient room for an independent rewrite chain.";
        }

        if (load.Opcode == 0x12 && parsed.ConstantPool.Count + 1 > byte.MaxValue)
        {
            return "ldc cannot address an appended constant without widening the instruction.";
        }

        return null;
    }

    private static (ParsedMethod Method, JavaInstruction Load, JavaInstruction Invocation)
        ResolveSafeSelection(ParsedClassFile parsed, ClassFileRewriteSelection selection)
    {
        ParsedMethod method = FindUniqueMethod(parsed, selection);
        ParsedCode code = method.Code ?? throw new InvalidDataException(
            $"Selected method {selection.MethodName}{selection.MethodDescriptor} has no Code attribute.");
        if (!code.Analysis.InstructionsByOffset.TryGetValue(
                selection.BytecodeOffset,
                out JavaInstruction? load) ||
            load.Opcode is not (0x12 or 0x13) ||
            load.ConstantPoolIndex is not { } poolIndex ||
            !parsed.ConstantPool.IsTag(poolIndex, ConstantPoolTag.String))
        {
            throw new InvalidDataException(
                $"Selection at bytecode offset {selection.BytecodeOffset} is not an ldc string occurrence.");
        }

        string value = parsed.ConstantPool.GetString(poolIndex, "selected literal");
        if (!string.Equals(value, selection.ExpectedValue, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Selection at bytecode offset {selection.BytecodeOffset} no longer has the expected value.");
        }

        string? unsafeReason = GetUnsafeReason(parsed, code, load, value);
        if (unsafeReason is not null)
        {
            throw new InvalidDataException(
                $"Selection at bytecode offset {selection.BytecodeOffset} is not safe: {unsafeReason}");
        }

        return (method, load, GetImmediatelyFollowingInstruction(code, load));
    }

    private static ParsedMethod FindUniqueMethod(
        ParsedClassFile parsed,
        ClassFileRewriteSelection selection)
    {
        if (!string.Equals(parsed.ClassName, selection.ClassName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Selection targets class {selection.ClassName}, not {parsed.ClassName}.");
        }

        ParsedMethod[] matches = parsed.Methods
            .Where(method =>
                string.Equals(method.Name, selection.MethodName, StringComparison.Ordinal) &&
                string.Equals(method.Descriptor, selection.MethodDescriptor, StringComparison.Ordinal))
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException(
                $"Selection does not identify exactly one method: {selection.MethodName}{selection.MethodDescriptor}.");
    }

    private static JavaInstruction GetImmediatelyFollowingInstruction(
        ParsedCode code,
        JavaInstruction instruction) =>
        TryGetImmediatelyFollowingInstruction(code, instruction, out JavaInstruction? next) && next is not null
            ? next
            : throw new InvalidDataException(
                $"No instruction immediately follows bytecode offset {instruction.Offset}.");

    private static bool TryGetImmediatelyFollowingInstruction(
        ParsedCode code,
        JavaInstruction instruction,
        out JavaInstruction? next) =>
        code.Analysis.InstructionsByOffset.TryGetValue(
            checked(instruction.Offset + instruction.Length),
            out next);

    private static bool IsExactMethodReference(
        ConstantPool pool,
        JavaInstruction instruction,
        string methodName)
    {
        if (instruction.Opcode != 0xb8 ||
            instruction.ConstantPoolIndex is not { } methodIndex)
        {
            return false;
        }

        ConstantPoolTag tag;
        if (pool.IsTag(methodIndex, ConstantPoolTag.Methodref))
        {
            tag = ConstantPoolTag.Methodref;
        }
        else if (pool.IsTag(methodIndex, ConstantPoolTag.InterfaceMethodref))
        {
            tag = ConstantPoolTag.InterfaceMethodref;
        }
        else
        {
            return false;
        }

        (string owner, string name, string descriptor) = pool.ResolveMethodReference(
            methodIndex,
            tag,
            "Component method invocation");
        return string.Equals(owner, ComponentOwner, StringComparison.Ordinal) &&
            string.Equals(name, methodName, StringComparison.Ordinal) &&
            string.Equals(descriptor, ComponentDescriptor, StringComparison.Ordinal);
    }

    private static ClassFileRewriteSelection[] ValidateAndOrderSelections(
        IReadOnlyCollection<ClassFileRewriteSelection> selections)
    {
        var locations = new HashSet<(string Class, string Method, string Descriptor, int Offset)>();
        foreach (ClassFileRewriteSelection selection in selections)
        {
            ArgumentNullException.ThrowIfNull(selection);
            if (string.IsNullOrEmpty(selection.ClassName) ||
                string.IsNullOrEmpty(selection.MethodName) ||
                string.IsNullOrEmpty(selection.MethodDescriptor) ||
                selection.BytecodeOffset < 0 ||
                selection.ExpectedValue is null)
            {
                throw new ArgumentException("Rewrite selection contains an invalid or missing location.", nameof(selections));
            }

            ValidateTranslationKey(selection.TranslationKey);
            var location = (
                selection.ClassName,
                selection.MethodName,
                selection.MethodDescriptor,
                selection.BytecodeOffset);
            if (!locations.Add(location))
            {
                throw new ArgumentException("Rewrite selections contain a duplicate location.", nameof(selections));
            }
        }

        return selections
            .OrderBy(static selection => selection.ClassName, StringComparer.Ordinal)
            .ThenBy(static selection => selection.MethodName, StringComparer.Ordinal)
            .ThenBy(static selection => selection.MethodDescriptor, StringComparer.Ordinal)
            .ThenBy(static selection => selection.BytecodeOffset)
            .ToArray();
    }

    private static void ValidateTranslationKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length is 0 or > MaxTranslationKeyLength ||
            key.Any(static character =>
                character is not (>= 'a' and <= 'z') and
                    not (>= '0' and <= '9') and
                    not '.' and not '_' and not '-'))
        {
            throw new ArgumentException(
                "Translation keys must contain 1-240 lowercase ASCII letters, digits, dots, underscores, or hyphens.",
                nameof(key));
        }
    }

    private static string CreateSuggestedKey(
        string keyNamespace,
        string className,
        string methodName,
        string descriptor,
        int offset,
        string value)
    {
        string classSegment = NormalizeKeySegment(className.Replace('/', '.'), 72, "class");
        string methodSegment = NormalizeKeySegment(methodName, 32, "method");
        string descriptorHash = StableHash(descriptor, 8);
        string valueHash = StableHash(value, 12);
        return $"{keyNamespace}.hardcoded.{classSegment}.{methodSegment}.{descriptorHash}.{offset:x4}.{valueHash}";
    }

    private static string NormalizeKeySegment(string value, int maximumLength, string fallback)
    {
        var builder = new StringBuilder(Math.Min(value.Length, maximumLength));
        bool previousSeparator = false;
        foreach (char character in value.ToLowerInvariant())
        {
            bool valid = character is >= 'a' and <= 'z' or >= '0' and <= '9';
            if (valid)
            {
                builder.Append(character);
                previousSeparator = false;
            }
            else if (!previousSeparator && builder.Length > 0)
            {
                builder.Append('.');
                previousSeparator = true;
            }
        }

        string normalized = builder.ToString().Trim('.');
        if (normalized.Length == 0)
        {
            normalized = fallback;
        }

        if (normalized.Length <= maximumLength)
        {
            return normalized;
        }

        string hash = StableHash(value, 10);
        return $"{normalized[..(maximumLength - hash.Length - 1)]}.{hash}";
    }

    private static string StableHash(string value, int characters)
    {
        byte[] bytes = Encoding.BigEndianUnicode.GetBytes(value);
        return Convert.ToHexString(SHA256.HashData(bytes))[..characters].ToLowerInvariant();
    }
}
