using System.Buffers.Binary;
using System.Text;

namespace JaxI18n.Archive.ClassFile;

internal ref struct ClassFileReader
{
    private readonly ReadOnlySpan<byte> _bytes;

    public ClassFileReader(ReadOnlySpan<byte> bytes, int position = 0)
    {
        _bytes = bytes;
        Position = position;
    }

    public int Position { get; private set; }

    public int Remaining => _bytes.Length - Position;

    public byte ReadU1(string context)
    {
        EnsureAvailable(1, context);
        return _bytes[Position++];
    }

    public ushort ReadU2(string context)
    {
        EnsureAvailable(sizeof(ushort), context);
        ushort value = BinaryPrimitives.ReadUInt16BigEndian(_bytes[Position..]);
        Position += sizeof(ushort);
        return value;
    }

    public uint ReadU4(string context)
    {
        EnsureAvailable(sizeof(uint), context);
        uint value = BinaryPrimitives.ReadUInt32BigEndian(_bytes[Position..]);
        Position += sizeof(uint);
        return value;
    }

    public int ReadI4(string context) => unchecked((int)ReadU4(context));

    public short ReadI2(string context) => unchecked((short)ReadU2(context));

    public ReadOnlySpan<byte> ReadBytes(int length, string context)
    {
        if (length < 0)
        {
            throw new InvalidDataException($"Negative length while reading {context}.");
        }

        EnsureAvailable(length, context);
        ReadOnlySpan<byte> value = _bytes.Slice(Position, length);
        Position += length;
        return value;
    }

    public void Skip(int length, string context) => ReadBytes(length, context);

    private void EnsureAvailable(int length, string context)
    {
        if (length > Remaining)
        {
            throw new InvalidDataException($"Truncated class file while reading {context}.");
        }
    }
}

internal static class BigEndianWriter
{
    public static void WriteU2(Stream stream, int value)
    {
        if ((uint)value > ushort.MaxValue)
        {
            throw new InvalidDataException($"Value {value} does not fit in a class-file u2.");
        }

        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)value);
        stream.Write(bytes);
    }

    public static void WriteU4(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }
}

internal static class ModifiedUtf8
{
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        var characters = new char[bytes.Length];
        int output = 0;

        for (int index = 0; index < bytes.Length;)
        {
            byte first = bytes[index++];
            if (first is > 0 and <= 0x7f)
            {
                characters[output++] = (char)first;
                continue;
            }

            if ((first & 0xe0) == 0xc0)
            {
                if (index >= bytes.Length || (bytes[index] & 0xc0) != 0x80)
                {
                    throw new InvalidDataException("Malformed modified UTF-8 two-byte sequence.");
                }

                byte second = bytes[index++];
                int value = ((first & 0x1f) << 6) | (second & 0x3f);
                if (value == 0)
                {
                    if (first != 0xc0 || second != 0x80)
                    {
                        throw new InvalidDataException("Malformed modified UTF-8 NUL sequence.");
                    }
                }
                else if (value < 0x80)
                {
                    throw new InvalidDataException("Overlong modified UTF-8 two-byte sequence.");
                }

                characters[output++] = (char)value;
                continue;
            }

            if ((first & 0xf0) == 0xe0)
            {
                if (index + 1 >= bytes.Length ||
                    (bytes[index] & 0xc0) != 0x80 ||
                    (bytes[index + 1] & 0xc0) != 0x80)
                {
                    throw new InvalidDataException("Malformed modified UTF-8 three-byte sequence.");
                }

                byte second = bytes[index++];
                byte third = bytes[index++];
                int value = ((first & 0x0f) << 12) | ((second & 0x3f) << 6) | (third & 0x3f);
                if (value < 0x800)
                {
                    throw new InvalidDataException("Overlong modified UTF-8 three-byte sequence.");
                }

                characters[output++] = (char)value;
                continue;
            }

            throw new InvalidDataException("Malformed or unsupported modified UTF-8 sequence.");
        }

        return new string(characters, 0, output);
    }

    public static byte[] Encode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var output = new MemoryStream(Math.Min(value.Length * 3, ushort.MaxValue));
        foreach (char character in value)
        {
            if (character is > '\0' and <= '\u007f')
            {
                output.WriteByte((byte)character);
            }
            else if (character <= '\u07ff')
            {
                output.WriteByte((byte)(0xc0 | (character >> 6)));
                output.WriteByte((byte)(0x80 | (character & 0x3f)));
            }
            else
            {
                output.WriteByte((byte)(0xe0 | (character >> 12)));
                output.WriteByte((byte)(0x80 | ((character >> 6) & 0x3f)));
                output.WriteByte((byte)(0x80 | (character & 0x3f)));
            }

            if (output.Length > ushort.MaxValue)
            {
                throw new InvalidDataException("Modified UTF-8 constant exceeds 65,535 bytes.");
            }
        }

        return output.ToArray();
    }
}

internal enum ConstantPoolTag : byte
{
    Utf8 = 1,
    Integer = 3,
    Float = 4,
    Long = 5,
    Double = 6,
    Class = 7,
    String = 8,
    Fieldref = 9,
    Methodref = 10,
    InterfaceMethodref = 11,
    NameAndType = 12,
    MethodHandle = 15,
    MethodType = 16,
    Dynamic = 17,
    InvokeDynamic = 18,
    Module = 19,
    Package = 20
}

internal sealed record ConstantPoolEntry(ConstantPoolTag Tag, byte[] Payload);

internal sealed class ConstantPool
{
    private readonly List<ConstantPoolEntry?> _entries;

    private ConstantPool(List<ConstantPoolEntry?> entries)
    {
        _entries = entries;
    }

    public int Count => _entries.Count;

    public static ConstantPool Parse(ref ClassFileReader reader)
    {
        ushort count = reader.ReadU2("constant_pool_count");
        if (count == 0)
        {
            throw new InvalidDataException("constant_pool_count must be non-zero.");
        }

        var entries = new List<ConstantPoolEntry?>(count) { null };
        for (int index = 1; index < count; index++)
        {
            ConstantPoolTag tag = (ConstantPoolTag)reader.ReadU1($"constant_pool[{index}].tag");
            byte[] payload = ReadPayload(ref reader, tag, index);
            entries.Add(new ConstantPoolEntry(tag, payload));
            if (tag is ConstantPoolTag.Long or ConstantPoolTag.Double)
            {
                if (++index >= count)
                {
                    throw new InvalidDataException("A long or double constant cannot occupy the final pool slot.");
                }

                entries.Add(null);
            }
        }

        var pool = new ConstantPool(entries);
        pool.ValidateAllReferences();
        return pool;
    }

    public ConstantPoolEntry Get(int index, string context)
    {
        if (index <= 0 || index >= _entries.Count || _entries[index] is not { } entry)
        {
            throw new InvalidDataException($"Invalid constant-pool index {index} in {context}.");
        }

        return entry;
    }

    public string GetUtf8(int index, string context)
    {
        ConstantPoolEntry entry = Get(index, context);
        RequireTag(entry, ConstantPoolTag.Utf8, context);
        ushort length = ReadU2(entry.Payload, 0);
        if (entry.Payload.Length != length + sizeof(ushort))
        {
            throw new InvalidDataException($"Malformed UTF-8 constant in {context}.");
        }

        return ModifiedUtf8.Decode(entry.Payload.AsSpan(sizeof(ushort)));
    }

    public string GetClassName(int classIndex, string context)
    {
        ConstantPoolEntry entry = Get(classIndex, context);
        RequireTag(entry, ConstantPoolTag.Class, context);
        return GetUtf8(ReadU2(entry.Payload, 0), context);
    }

    public string GetString(int stringIndex, string context)
    {
        ConstantPoolEntry entry = Get(stringIndex, context);
        RequireTag(entry, ConstantPoolTag.String, context);
        return GetUtf8(ReadU2(entry.Payload, 0), context);
    }

    public bool IsTag(int index, params ConstantPoolTag[] allowed)
    {
        ConstantPoolEntry entry = Get(index, "bytecode operand");
        return allowed.Contains(entry.Tag);
    }

    public (string Owner, string Name, string Descriptor) ResolveMethodReference(
        int methodReferenceIndex,
        ConstantPoolTag requiredTag,
        string context)
    {
        ConstantPoolEntry method = Get(methodReferenceIndex, context);
        RequireTag(method, requiredTag, context);
        string owner = GetClassName(ReadU2(method.Payload, 0), context);
        ConstantPoolEntry nameAndType = Get(ReadU2(method.Payload, 2), context);
        RequireTag(nameAndType, ConstantPoolTag.NameAndType, context);
        string name = GetUtf8(ReadU2(nameAndType.Payload, 0), context);
        string descriptor = GetUtf8(ReadU2(nameAndType.Payload, 2), context);
        return (owner, name, descriptor);
    }

    public ushort AppendUtf8(string value)
    {
        byte[] encoded = ModifiedUtf8.Encode(value);
        var payload = new byte[sizeof(ushort) + encoded.Length];
        BinaryPrimitives.WriteUInt16BigEndian(payload, checked((ushort)encoded.Length));
        encoded.CopyTo(payload, sizeof(ushort));
        return Append(new ConstantPoolEntry(ConstantPoolTag.Utf8, payload));
    }

    public ushort AppendSingleIndex(ConstantPoolTag tag, ushort index)
    {
        var payload = new byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(payload, index);
        return Append(new ConstantPoolEntry(tag, payload));
    }

    public ushort AppendDoubleIndex(ConstantPoolTag tag, ushort first, ushort second)
    {
        var payload = new byte[sizeof(ushort) * 2];
        BinaryPrimitives.WriteUInt16BigEndian(payload, first);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(sizeof(ushort)), second);
        return Append(new ConstantPoolEntry(tag, payload));
    }

    public void WriteTo(Stream stream)
    {
        BigEndianWriter.WriteU2(stream, _entries.Count);
        for (int index = 1; index < _entries.Count; index++)
        {
            ConstantPoolEntry? entry = _entries[index];
            if (entry is null)
            {
                continue;
            }

            stream.WriteByte((byte)entry.Tag);
            stream.Write(entry.Payload);
        }
    }

    private static byte[] ReadPayload(ref ClassFileReader reader, ConstantPoolTag tag, int index)
    {
        string context = $"constant_pool[{index}]";
        return tag switch
        {
            ConstantPoolTag.Utf8 => ReadUtf8Payload(ref reader, context),
            ConstantPoolTag.Integer or ConstantPoolTag.Float or
                ConstantPoolTag.Fieldref or ConstantPoolTag.Methodref or
                ConstantPoolTag.InterfaceMethodref or ConstantPoolTag.NameAndType or
                ConstantPoolTag.Dynamic or ConstantPoolTag.InvokeDynamic =>
                reader.ReadBytes(4, context).ToArray(),
            ConstantPoolTag.Long or ConstantPoolTag.Double => reader.ReadBytes(8, context).ToArray(),
            ConstantPoolTag.Class or ConstantPoolTag.String or ConstantPoolTag.MethodType or
                ConstantPoolTag.Module or ConstantPoolTag.Package => reader.ReadBytes(2, context).ToArray(),
            ConstantPoolTag.MethodHandle => reader.ReadBytes(3, context).ToArray(),
            _ => throw new InvalidDataException($"Unknown constant-pool tag {(byte)tag} at index {index}.")
        };
    }

    private static byte[] ReadUtf8Payload(ref ClassFileReader reader, string context)
    {
        ushort length = reader.ReadU2($"{context}.length");
        ReadOnlySpan<byte> bytes = reader.ReadBytes(length, context);
        _ = ModifiedUtf8.Decode(bytes);
        var payload = new byte[sizeof(ushort) + length];
        BinaryPrimitives.WriteUInt16BigEndian(payload, length);
        bytes.CopyTo(payload.AsSpan(sizeof(ushort)));
        return payload;
    }

    private ushort Append(ConstantPoolEntry entry)
    {
        if (_entries.Count >= ushort.MaxValue)
        {
            throw new InvalidDataException("Rewritten constant pool would exceed the JVM u2 limit.");
        }

        ushort index = checked((ushort)_entries.Count);
        _entries.Add(entry);
        return index;
    }

    private void ValidateAllReferences()
    {
        for (int index = 1; index < _entries.Count; index++)
        {
            ConstantPoolEntry? entry = _entries[index];
            if (entry is null)
            {
                continue;
            }

            string context = $"constant_pool[{index}]";
            switch (entry.Tag)
            {
                case ConstantPoolTag.Utf8:
                case ConstantPoolTag.Integer:
                case ConstantPoolTag.Float:
                case ConstantPoolTag.Long:
                case ConstantPoolTag.Double:
                    break;
                case ConstantPoolTag.Class:
                case ConstantPoolTag.Module:
                case ConstantPoolTag.Package:
                    RequireReferencedTag(entry, 0, ConstantPoolTag.Utf8, context);
                    break;
                case ConstantPoolTag.String:
                    RequireReferencedTag(entry, 0, ConstantPoolTag.Utf8, context);
                    break;
                case ConstantPoolTag.Fieldref:
                case ConstantPoolTag.Methodref:
                case ConstantPoolTag.InterfaceMethodref:
                    RequireReferencedTag(entry, 0, ConstantPoolTag.Class, context);
                    RequireReferencedTag(entry, 2, ConstantPoolTag.NameAndType, context);
                    break;
                case ConstantPoolTag.NameAndType:
                    RequireReferencedTag(entry, 0, ConstantPoolTag.Utf8, context);
                    RequireReferencedTag(entry, 2, ConstantPoolTag.Utf8, context);
                    break;
                case ConstantPoolTag.MethodHandle:
                    ValidateMethodHandle(entry, context);
                    break;
                case ConstantPoolTag.MethodType:
                    RequireReferencedTag(entry, 0, ConstantPoolTag.Utf8, context);
                    break;
                case ConstantPoolTag.Dynamic:
                case ConstantPoolTag.InvokeDynamic:
                    RequireReferencedTag(entry, 2, ConstantPoolTag.NameAndType, context);
                    break;
                default:
                    throw new InvalidDataException($"Unknown constant-pool tag in {context}.");
            }
        }
    }

    private void ValidateMethodHandle(ConstantPoolEntry entry, string context)
    {
        int kind = entry.Payload[0];
        ConstantPoolEntry reference = Get(ReadU2(entry.Payload, 1), context);
        bool valid = kind switch
        {
            >= 1 and <= 4 => reference.Tag == ConstantPoolTag.Fieldref,
            5 or 8 => reference.Tag == ConstantPoolTag.Methodref,
            6 or 7 => reference.Tag is ConstantPoolTag.Methodref or ConstantPoolTag.InterfaceMethodref,
            9 => reference.Tag == ConstantPoolTag.InterfaceMethodref,
            _ => false
        };
        if (!valid)
        {
            throw new InvalidDataException($"Invalid method-handle reference in {context}.");
        }
    }

    private void RequireReferencedTag(
        ConstantPoolEntry source,
        int payloadOffset,
        ConstantPoolTag required,
        string context)
    {
        ConstantPoolEntry target = Get(ReadU2(source.Payload, payloadOffset), context);
        RequireTag(target, required, context);
    }

    private static ushort ReadU2(byte[] payload, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(offset, sizeof(ushort)));

    private static void RequireTag(ConstantPoolEntry entry, ConstantPoolTag required, string context)
    {
        if (entry.Tag != required)
        {
            throw new InvalidDataException(
                $"Expected {required} but found {entry.Tag} in {context}.");
        }
    }
}
