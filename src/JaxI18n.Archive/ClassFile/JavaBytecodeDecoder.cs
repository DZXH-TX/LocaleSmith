using System.Buffers.Binary;

namespace JaxI18n.Archive.ClassFile;

internal sealed record JavaInstruction(
    int Offset,
    byte Opcode,
    int Length,
    int? ConstantPoolIndex,
    IReadOnlyList<int> BranchTargets)
{
    public string Mnemonic => Opcode switch
    {
        0x12 => "ldc",
        0x13 => "ldc_w",
        0xb8 => "invokestatic",
        _ => $"opcode_0x{Opcode:x2}"
    };
}

internal sealed record JavaCodeAnalysis(
    IReadOnlyList<JavaInstruction> Instructions,
    IReadOnlyDictionary<int, JavaInstruction> InstructionsByOffset,
    IReadOnlySet<int> BranchTargets,
    IReadOnlySet<int> Boundaries);

internal static class JavaBytecodeDecoder
{
    private const int MaxSwitchEntries = 65_536;

    public static JavaCodeAnalysis Decode(ReadOnlySpan<byte> code, ConstantPool constantPool)
    {
        if (code.Length is 0 or > ushort.MaxValue)
        {
            throw new InvalidDataException("Code attribute length must be between 1 and 65,535 bytes.");
        }

        var instructions = new List<JavaInstruction>();
        var byOffset = new Dictionary<int, JavaInstruction>();
        var boundaries = new HashSet<int> { code.Length };
        var allTargets = new HashSet<int>();

        for (int offset = 0; offset < code.Length;)
        {
            JavaInstruction instruction = DecodeInstruction(code, offset, constantPool);
            instructions.Add(instruction);
            byOffset.Add(offset, instruction);
            boundaries.Add(offset);
            foreach (int target in instruction.BranchTargets)
            {
                allTargets.Add(target);
            }

            offset = checked(offset + instruction.Length);
        }

        foreach (int target in allTargets)
        {
            if (target < 0 || target >= code.Length || !boundaries.Contains(target))
            {
                throw new InvalidDataException(
                    $"Branch or switch target {target} is not an instruction boundary.");
            }
        }

        return new JavaCodeAnalysis(instructions, byOffset, allTargets, boundaries);
    }

    private static JavaInstruction DecodeInstruction(
        ReadOnlySpan<byte> code,
        int offset,
        ConstantPool pool)
    {
        byte opcode = code[offset];
        if (opcode is 0xaa or 0xab)
        {
            return DecodeSwitch(code, offset, opcode);
        }

        if (opcode == 0xc4)
        {
            return DecodeWide(code, offset);
        }

        int length = GetFixedLength(opcode);
        EnsureInstructionAvailable(code, offset, length);
        int? poolIndex = null;
        int[] targets = Array.Empty<int>();

        switch (opcode)
        {
            case 0x12:
                poolIndex = code[offset + 1];
                RequirePoolTag(
                    pool,
                    poolIndex.Value,
                    opcode,
                    ConstantPoolTag.Integer,
                    ConstantPoolTag.Float,
                    ConstantPoolTag.Class,
                    ConstantPoolTag.String,
                    ConstantPoolTag.MethodHandle,
                    ConstantPoolTag.MethodType,
                    ConstantPoolTag.Dynamic);
                break;
            case 0x13:
                poolIndex = ReadU2(code, offset + 1);
                RequirePoolTag(
                    pool,
                    poolIndex.Value,
                    opcode,
                    ConstantPoolTag.Integer,
                    ConstantPoolTag.Float,
                    ConstantPoolTag.Class,
                    ConstantPoolTag.String,
                    ConstantPoolTag.MethodHandle,
                    ConstantPoolTag.MethodType,
                    ConstantPoolTag.Dynamic);
                break;
            case 0x14:
                poolIndex = ReadU2(code, offset + 1);
                RequirePoolTag(
                    pool,
                    poolIndex.Value,
                    opcode,
                    ConstantPoolTag.Long,
                    ConstantPoolTag.Double,
                    ConstantPoolTag.Dynamic);
                break;
            case >= 0x99 and <= 0xa8:
            case 0xc6:
            case 0xc7:
                targets = new[] { CheckedTarget(offset, ReadI2(code, offset + 1)) };
                break;
            case 0xc8:
            case 0xc9:
                targets = new[] { CheckedTarget(offset, ReadI4(code, offset + 1)) };
                break;
            case >= 0xb2 and <= 0xb5:
                poolIndex = ReadU2(code, offset + 1);
                RequirePoolTag(pool, poolIndex.Value, opcode, ConstantPoolTag.Fieldref);
                break;
            case 0xb6:
                poolIndex = ReadU2(code, offset + 1);
                RequirePoolTag(pool, poolIndex.Value, opcode, ConstantPoolTag.Methodref);
                break;
            case 0xb7:
            case 0xb8:
                poolIndex = ReadU2(code, offset + 1);
                RequirePoolTag(
                    pool,
                    poolIndex.Value,
                    opcode,
                    ConstantPoolTag.Methodref,
                    ConstantPoolTag.InterfaceMethodref);
                break;
            case 0xb9:
                poolIndex = ReadU2(code, offset + 1);
                RequirePoolTag(pool, poolIndex.Value, opcode, ConstantPoolTag.InterfaceMethodref);
                if (code[offset + 3] == 0 || code[offset + 4] != 0)
                {
                    throw new InvalidDataException("Malformed invokeinterface operands.");
                }

                break;
            case 0xba:
                poolIndex = ReadU2(code, offset + 1);
                RequirePoolTag(pool, poolIndex.Value, opcode, ConstantPoolTag.InvokeDynamic);
                if (code[offset + 3] != 0 || code[offset + 4] != 0)
                {
                    throw new InvalidDataException("Malformed invokedynamic operands.");
                }

                break;
            case 0xbb:
            case 0xbd:
            case 0xc0:
            case 0xc1:
                poolIndex = ReadU2(code, offset + 1);
                RequirePoolTag(pool, poolIndex.Value, opcode, ConstantPoolTag.Class);
                break;
            case 0xbc:
                if (code[offset + 1] is < 4 or > 11)
                {
                    throw new InvalidDataException("Invalid newarray primitive type.");
                }

                break;
            case 0xc5:
                poolIndex = ReadU2(code, offset + 1);
                RequirePoolTag(pool, poolIndex.Value, opcode, ConstantPoolTag.Class);
                if (code[offset + 3] == 0)
                {
                    throw new InvalidDataException("multianewarray dimensions must be non-zero.");
                }

                break;
        }

        return new JavaInstruction(offset, opcode, length, poolIndex, targets);
    }

    private static JavaInstruction DecodeSwitch(ReadOnlySpan<byte> code, int offset, byte opcode)
    {
        int padding = (4 - ((offset + 1) & 3)) & 3;
        int cursor = checked(offset + 1 + padding);
        int fixedOperandLength = opcode == 0xaa ? 12 : 8;
        EnsureInstructionAvailable(code, offset, checked(1 + padding + fixedOperandLength));
        for (int index = offset + 1; index < cursor; index++)
        {
            if (code[index] != 0)
            {
                throw new InvalidDataException("Switch padding bytes must be zero.");
            }
        }

        int defaultOffset = ReadI4(code, cursor);
        cursor += sizeof(int);
        var targets = new List<int> { CheckedTarget(offset, defaultOffset) };

        if (opcode == 0xaa)
        {
            int low = ReadI4(code, cursor);
            int high = ReadI4(code, cursor + sizeof(int));
            cursor += sizeof(int) * 2;
            long count = (long)high - low + 1;
            if (count is < 0 or > MaxSwitchEntries)
            {
                throw new InvalidDataException("Invalid or excessive tableswitch range.");
            }

            EnsureInstructionAvailable(code, offset, checked(cursor - offset + (int)count * sizeof(int)));
            for (int index = 0; index < count; index++)
            {
                targets.Add(CheckedTarget(offset, ReadI4(code, cursor)));
                cursor += sizeof(int);
            }
        }
        else
        {
            int pairs = ReadI4(code, cursor);
            cursor += sizeof(int);
            if (pairs is < 0 or > MaxSwitchEntries)
            {
                throw new InvalidDataException("Invalid or excessive lookupswitch pair count.");
            }

            EnsureInstructionAvailable(code, offset, checked(cursor - offset + pairs * sizeof(int) * 2));
            int? previousMatch = null;
            for (int index = 0; index < pairs; index++)
            {
                int match = ReadI4(code, cursor);
                int branchOffset = ReadI4(code, cursor + sizeof(int));
                cursor += sizeof(int) * 2;
                if (previousMatch is not null && match <= previousMatch.Value)
                {
                    throw new InvalidDataException("lookupswitch match keys must be strictly increasing.");
                }

                previousMatch = match;
                targets.Add(CheckedTarget(offset, branchOffset));
            }
        }

        return new JavaInstruction(offset, opcode, cursor - offset, null, targets);
    }

    private static JavaInstruction DecodeWide(ReadOnlySpan<byte> code, int offset)
    {
        EnsureInstructionAvailable(code, offset, 2);
        byte modifiedOpcode = code[offset + 1];
        int length = modifiedOpcode switch
        {
            0x15 or 0x16 or 0x17 or 0x18 or 0x19 or
                0x36 or 0x37 or 0x38 or 0x39 or 0x3a or 0xa9 => 4,
            0x84 => 6,
            _ => throw new InvalidDataException(
                $"Invalid opcode 0x{modifiedOpcode:x2} following wide at offset {offset}.")
        };
        EnsureInstructionAvailable(code, offset, length);
        return new JavaInstruction(offset, 0xc4, length, null, Array.Empty<int>());
    }

    private static int GetFixedLength(byte opcode) => opcode switch
    {
        <= 0x0f => 1,
        0x10 => 2,
        0x11 => 3,
        0x12 => 2,
        0x13 or 0x14 => 3,
        >= 0x15 and <= 0x19 => 2,
        >= 0x1a and <= 0x35 => 1,
        >= 0x36 and <= 0x3a => 2,
        >= 0x3b and <= 0x83 => 1,
        0x84 => 3,
        >= 0x85 and <= 0x98 => 1,
        >= 0x99 and <= 0xa8 => 3,
        0xa9 => 2,
        >= 0xac and <= 0xb1 => 1,
        >= 0xb2 and <= 0xb8 => 3,
        0xb9 or 0xba => 5,
        0xbb => 3,
        0xbc => 2,
        0xbd => 3,
        0xbe or 0xbf => 1,
        0xc0 or 0xc1 => 3,
        0xc2 or 0xc3 => 1,
        0xc5 => 4,
        0xc6 or 0xc7 => 3,
        0xc8 or 0xc9 => 5,
        _ => throw new InvalidDataException($"Unknown or reserved JVM opcode 0x{opcode:x2}.")
    };

    private static void RequirePoolTag(
        ConstantPool pool,
        int index,
        byte opcode,
        params ConstantPoolTag[] tags)
    {
        if (!pool.IsTag(index, tags))
        {
            throw new InvalidDataException(
                $"Invalid constant-pool tag for opcode 0x{opcode:x2} at index {index}.");
        }
    }

    private static int ReadU2(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);

    private static short ReadI2(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadInt16BigEndian(bytes[offset..]);

    private static int ReadI4(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadInt32BigEndian(bytes[offset..]);

    private static int CheckedTarget(int instructionOffset, long relativeOffset)
    {
        long target = instructionOffset + relativeOffset;
        if (target is < int.MinValue or > int.MaxValue)
        {
            throw new InvalidDataException("Branch target overflows a class-file code offset.");
        }

        return (int)target;
    }

    private static void EnsureInstructionAvailable(ReadOnlySpan<byte> code, int offset, int length)
    {
        if (length <= 0 || offset < 0 || offset > code.Length - length)
        {
            throw new InvalidDataException($"Truncated instruction at bytecode offset {offset}.");
        }
    }
}
