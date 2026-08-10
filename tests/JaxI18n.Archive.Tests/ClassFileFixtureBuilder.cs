using System.Buffers.Binary;
using System.Text;

namespace JaxI18n.Archive.Tests;

internal enum ClassFileFixtureKind
{
    Safe,
    SharedConstantPool,
    NonAdjacent,
    WrongMethod,
    BranchTargetsInvocation,
    ExceptionBoundaryAtInvocation,
    InterfaceMethodReference,
    BranchTargetsTranslatable,
    BranchTargetsInstructionInterior,
    UnknownOpcode,
    LdcWide
}

/// <summary>
/// Builds small class files directly from bytes. No Java compiler or external
/// fixture is required, so Archive integration tests can reuse it.
/// </summary>
internal static class ClassFileFixtureBuilder
{
    private const string ComponentOwner = "net/minecraft/network/chat/Component";
    private const string ComponentDescriptor =
        "(Ljava/lang/String;)Lnet/minecraft/network/chat/MutableComponent;";

    public static byte[] CreateSafeLiteralClass(string literal = "Hello world") =>
        Create(ClassFileFixtureKind.Safe, literal);

    public static byte[] Create(
        ClassFileFixtureKind kind = ClassFileFixtureKind.Safe,
        string literal = "Hello world")
    {
        using var output = new MemoryStream();
        WriteU4(output, 0xcafebabe);
        WriteU2(output, 0);
        WriteU2(output, 61);

        // Indices are intentionally stable: #9 is the shared String and #15
        // is the original Methodref. LdcWide pads the pool without changing
        // either index.
        int paddingConstants = kind == ClassFileFixtureKind.LdcWide ? 250 : 0;
        WriteU2(output, 16 + paddingConstants);
        WriteUtf8(output, "example/Test");              // #1
        WriteSingleIndex(output, 7, 1);                  // #2 Class
        WriteUtf8(output, "java/lang/Object");          // #3
        WriteSingleIndex(output, 7, 3);                  // #4 Class
        WriteUtf8(output, "run");                       // #5
        WriteUtf8(output, "()V");                       // #6
        WriteUtf8(output, "Code");                      // #7
        WriteUtf8(output, literal);                      // #8
        WriteSingleIndex(output, 8, 8);                  // #9 String
        WriteUtf8(output, ComponentOwner);               // #10
        WriteSingleIndex(output, 7, 10);                 // #11 Class
        WriteUtf8(
            output,
            kind is ClassFileFixtureKind.WrongMethod or ClassFileFixtureKind.BranchTargetsTranslatable
                ? "translatable"
                : "literal"); // #12
        WriteUtf8(output, ComponentDescriptor);          // #13
        WriteDoubleIndex(output, 12, 12, 13);            // #14 NameAndType
        WriteDoubleIndex(
            output,
            kind == ClassFileFixtureKind.InterfaceMethodReference ? (byte)11 : (byte)10,
            11,
            14);                                          // #15 Methodref/InterfaceMethodref
        for (int index = 0; index < paddingConstants; index++)
        {
            output.WriteByte(3); // CONSTANT_Integer
            WriteU4(output, checked((uint)index));
        }

        byte[] code = CreateCode(kind);
        WriteU2(output, 0x0021); // public + super
        WriteU2(output, 2);      // this_class
        WriteU2(output, 4);      // super_class
        WriteU2(output, 0);      // interfaces_count
        WriteU2(output, 0);      // fields_count
        WriteU2(output, 1);      // methods_count
        WriteU2(output, 0x0009); // public static
        WriteU2(output, 5);      // run
        WriteU2(output, 6);      // ()V
        WriteU2(output, 1);      // attributes_count
        WriteU2(output, 7);      // Code

        int exceptionCount = kind == ClassFileFixtureKind.ExceptionBoundaryAtInvocation ? 1 : 0;
        int codeAttributeLength = checked(12 + code.Length + (exceptionCount * 8));
        WriteU4(output, checked((uint)codeAttributeLength));
        WriteU2(output, 1); // max_stack
        WriteU2(output, 0); // max_locals
        WriteU4(output, checked((uint)code.Length));
        output.Write(code);
        WriteU2(output, exceptionCount);
        if (exceptionCount == 1)
        {
            WriteU2(output, 0); // start_pc
            WriteU2(output, 2); // end_pc at invokestatic (unsafe boundary)
            WriteU2(output, 5); // handler_pc at pop
            WriteU2(output, 0); // catch all
        }

        WriteU2(output, 0); // Code attributes_count
        WriteU2(output, 0); // class attributes_count
        return output.ToArray();
    }

    private static byte[] CreateCode(ClassFileFixtureKind kind) => kind switch
    {
        ClassFileFixtureKind.Safe or
            ClassFileFixtureKind.WrongMethod or
            ClassFileFixtureKind.ExceptionBoundaryAtInvocation or
            ClassFileFixtureKind.InterfaceMethodReference =>
            new byte[] { 0x12, 0x09, 0xb8, 0x00, 0x0f, 0x57, 0xb1 },
        ClassFileFixtureKind.SharedConstantPool =>
            new byte[]
            {
                0x12, 0x09, 0xb8, 0x00, 0x0f, 0x57,
                0x12, 0x09, 0xb8, 0x00, 0x0f, 0x57,
                0xb1
            },
        ClassFileFixtureKind.NonAdjacent =>
            new byte[] { 0x12, 0x09, 0x00, 0xb8, 0x00, 0x0f, 0x57, 0xb1 },
        ClassFileFixtureKind.BranchTargetsInvocation or
            ClassFileFixtureKind.BranchTargetsTranslatable =>
            new byte[]
            {
                0x12, 0x09,             // 0: ldc
                0xb8, 0x00, 0x0f,       // 2: invokestatic
                0x57,                   // 5: pop
                0xa7, 0xff, 0xfc,       // 6: goto 2
                0xb1                    // 9: return
            },
        ClassFileFixtureKind.BranchTargetsInstructionInterior =>
            new byte[]
            {
                0x12, 0x09,
                0xb8, 0x00, 0x0f,
                0x57,
                0xa7, 0xff, 0xfd,       // 6: goto 3 (middle of invokestatic)
                0xb1
            },
        ClassFileFixtureKind.UnknownOpcode =>
            new byte[] { 0x12, 0x09, 0xcb, 0xb8, 0x00, 0x0f, 0x57, 0xb1 },
        ClassFileFixtureKind.LdcWide =>
            new byte[] { 0x13, 0x00, 0x09, 0xb8, 0x00, 0x0f, 0x57, 0xb1 },
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static void WriteUtf8(Stream output, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        output.WriteByte(1);
        WriteU2(output, bytes.Length);
        output.Write(bytes);
    }

    private static void WriteSingleIndex(Stream output, byte tag, int index)
    {
        output.WriteByte(tag);
        WriteU2(output, index);
    }

    private static void WriteDoubleIndex(Stream output, byte tag, int first, int second)
    {
        output.WriteByte(tag);
        WriteU2(output, first);
        WriteU2(output, second);
    }

    private static void WriteU2(Stream output, int value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, checked((ushort)value));
        output.Write(bytes);
    }

    private static void WriteU4(Stream output, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        output.Write(bytes);
    }
}
