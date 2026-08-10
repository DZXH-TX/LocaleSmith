using System.Buffers.Binary;

namespace JaxI18n.Archive.ClassFile;

internal sealed record ParsedExceptionHandler(
    int StartOffset,
    int EndOffset,
    int HandlerOffset);

internal sealed record ParsedCode(
    int CodeOffsetInTail,
    byte[] Code,
    JavaCodeAnalysis Analysis,
    IReadOnlyList<ParsedExceptionHandler> ExceptionHandlers)
{
    public IReadOnlySet<int> ProtectedControlFlowPoints { get; } = ExceptionHandlers
        .SelectMany(static handler => new[]
        {
            handler.StartOffset,
            handler.EndOffset,
            handler.HandlerOffset
        })
        .ToHashSet();
}

internal sealed record ParsedMethod(
    string Name,
    string Descriptor,
    ParsedCode? Code);

internal sealed class ParsedClassFile
{
    private const uint Magic = 0xcafebabe;
    private const int MaxClassFileSize = 32 * 1024 * 1024;
    private const int MaxAttributeSize = 16 * 1024 * 1024;
    private const int MaxMembers = 16_384;
    private const int MaxAttributes = 4_096;
    private const int MaxExceptionHandlers = 8_192;

    private ParsedClassFile(
        byte[] header,
        ConstantPool constantPool,
        byte[] tail,
        string className,
        IReadOnlyList<ParsedMethod> methods)
    {
        Header = header;
        ConstantPool = constantPool;
        Tail = tail;
        ClassName = className;
        Methods = methods;
    }

    public byte[] Header { get; }

    public ConstantPool ConstantPool { get; }

    public byte[] Tail { get; }

    public string ClassName { get; }

    public IReadOnlyList<ParsedMethod> Methods { get; }

    public static ParsedClassFile Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 10 or > MaxClassFileSize)
        {
            throw new InvalidDataException(
                $"Class file length must be between 10 and {MaxClassFileSize} bytes.");
        }

        var reader = new ClassFileReader(bytes);
        if (reader.ReadU4("magic") != Magic)
        {
            throw new InvalidDataException("Input does not contain a Java class-file magic value.");
        }

        ushort minorVersion = reader.ReadU2("minor_version");
        ushort majorVersion = reader.ReadU2("major_version");
        if (majorVersion is < 45 or > 70)
        {
            throw new InvalidDataException($"Unsupported Java class-file version {majorVersion}.{minorVersion}.");
        }

        ConstantPool pool = ConstantPool.Parse(ref reader);
        int tailStart = reader.Position;
        byte[] header = bytes[..8].ToArray();
        byte[] tail = bytes[tailStart..].ToArray();
        ParseTail(tail, pool, out string className, out IReadOnlyList<ParsedMethod> methods);
        return new ParsedClassFile(header, pool, tail, className, methods);
    }

    public byte[] Rebuild(byte[] rewrittenTail)
    {
        ArgumentNullException.ThrowIfNull(rewrittenTail);
        using var output = new MemoryStream(
            checked(Header.Length + ConstantPool.Count * 8 + rewrittenTail.Length));
        output.Write(Header);
        ConstantPool.WriteTo(output);
        output.Write(rewrittenTail);
        if (output.Length > MaxClassFileSize)
        {
            throw new InvalidDataException("Rebuilt class file exceeds the configured safety limit.");
        }

        return output.ToArray();
    }

    private static void ParseTail(
        byte[] tail,
        ConstantPool pool,
        out string className,
        out IReadOnlyList<ParsedMethod> methods)
    {
        var reader = new ClassFileReader(tail);
        _ = reader.ReadU2("access_flags");
        ushort thisClassIndex = reader.ReadU2("this_class");
        ushort superClassIndex = reader.ReadU2("super_class");
        className = pool.GetClassName(thisClassIndex, "this_class");
        if (superClassIndex != 0)
        {
            _ = pool.GetClassName(superClassIndex, "super_class");
        }

        ushort interfaceCount = reader.ReadU2("interfaces_count");
        RequireCount(interfaceCount, MaxMembers, "interfaces_count");
        for (int index = 0; index < interfaceCount; index++)
        {
            _ = pool.GetClassName(reader.ReadU2($"interfaces[{index}]"), $"interfaces[{index}]");
        }

        ushort fieldCount = reader.ReadU2("fields_count");
        RequireCount(fieldCount, MaxMembers, "fields_count");
        var fieldSignatures = new HashSet<(string Name, string Descriptor)>();
        for (int index = 0; index < fieldCount; index++)
        {
            (string name, string descriptor) = ParseField(ref reader, pool, index);
            if (!fieldSignatures.Add((name, descriptor)))
            {
                throw new InvalidDataException($"Duplicate field {name}:{descriptor}.");
            }
        }

        ushort methodCount = reader.ReadU2("methods_count");
        RequireCount(methodCount, MaxMembers, "methods_count");
        var parsedMethods = new List<ParsedMethod>(methodCount);
        var methodSignatures = new HashSet<(string Name, string Descriptor)>();
        for (int index = 0; index < methodCount; index++)
        {
            ParsedMethod method = ParseMethod(ref reader, pool, index);
            if (!methodSignatures.Add((method.Name, method.Descriptor)))
            {
                throw new InvalidDataException($"Duplicate method {method.Name}{method.Descriptor}.");
            }

            parsedMethods.Add(method);
        }

        ParseAttributes(ref reader, pool, "class", allowCode: false, out _);
        if (reader.Remaining != 0)
        {
            throw new InvalidDataException("Trailing bytes remain after the class-file structure.");
        }

        methods = parsedMethods.AsReadOnly();
    }

    private static (string Name, string Descriptor) ParseField(
        ref ClassFileReader reader,
        ConstantPool pool,
        int fieldIndex)
    {
        _ = reader.ReadU2($"fields[{fieldIndex}].access_flags");
        string name = pool.GetUtf8(
            reader.ReadU2($"fields[{fieldIndex}].name_index"),
            $"fields[{fieldIndex}].name");
        string descriptor = pool.GetUtf8(
            reader.ReadU2($"fields[{fieldIndex}].descriptor_index"),
            $"fields[{fieldIndex}].descriptor");
        if (name.Length == 0 || !JavaDescriptorValidator.IsFieldDescriptor(descriptor))
        {
            throw new InvalidDataException($"Malformed field metadata at index {fieldIndex}.");
        }

        ParseAttributes(ref reader, pool, $"fields[{fieldIndex}]", allowCode: false, out _);
        return (name, descriptor);
    }

    private static ParsedMethod ParseMethod(
        ref ClassFileReader reader,
        ConstantPool pool,
        int methodIndex)
    {
        ushort accessFlags = reader.ReadU2($"methods[{methodIndex}].access_flags");
        string name = pool.GetUtf8(
            reader.ReadU2($"methods[{methodIndex}].name_index"),
            $"methods[{methodIndex}].name");
        string descriptor = pool.GetUtf8(
            reader.ReadU2($"methods[{methodIndex}].descriptor_index"),
            $"methods[{methodIndex}].descriptor");
        if (name.Length == 0 || !JavaDescriptorValidator.IsMethodDescriptor(descriptor))
        {
            throw new InvalidDataException($"Malformed method metadata at index {methodIndex}.");
        }

        ParseAttributes(
            ref reader,
            pool,
            $"methods[{methodIndex}]",
            allowCode: true,
            out ParsedCode? code);
        bool mustNotHaveCode = (accessFlags & (0x0100 | 0x0400)) != 0;
        if (mustNotHaveCode == (code is not null))
        {
            throw new InvalidDataException(
                $"Method {name}{descriptor} has an invalid Code attribute presence for its access flags.");
        }

        if (name == "<clinit>" && descriptor != "()V")
        {
            throw new InvalidDataException("Class initializer must have descriptor ()V.");
        }

        if (name == "<init>" && !descriptor.EndsWith(")V", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Instance initializer must return void.");
        }

        return new ParsedMethod(name, descriptor, code);
    }

    private static void ParseAttributes(
        ref ClassFileReader reader,
        ConstantPool pool,
        string owner,
        bool allowCode,
        out ParsedCode? code)
    {
        ushort count = reader.ReadU2($"{owner}.attributes_count");
        RequireCount(count, MaxAttributes, $"{owner}.attributes_count");
        code = null;
        for (int index = 0; index < count; index++)
        {
            string context = $"{owner}.attributes[{index}]";
            string name = pool.GetUtf8(reader.ReadU2($"{context}.name_index"), context);
            int length = ReadBoundedLength(ref reader, $"{context}.length");
            int bodyOffset = reader.Position;
            ReadOnlySpan<byte> body = reader.ReadBytes(length, context);
            if (!string.Equals(name, "Code", StringComparison.Ordinal))
            {
                continue;
            }

            if (!allowCode)
            {
                throw new InvalidDataException($"Code attribute is not valid on {owner}.");
            }

            if (code is not null)
            {
                throw new InvalidDataException($"Duplicate Code attribute on {owner}.");
            }

            code = ParseCodeAttribute(body, bodyOffset, pool, context);
        }
    }

    private static ParsedCode ParseCodeAttribute(
        ReadOnlySpan<byte> body,
        int bodyOffsetInTail,
        ConstantPool pool,
        string context)
    {
        var reader = new ClassFileReader(body);
        _ = reader.ReadU2($"{context}.max_stack");
        _ = reader.ReadU2($"{context}.max_locals");
        uint rawCodeLength = reader.ReadU4($"{context}.code_length");
        if (rawCodeLength is 0 or > ushort.MaxValue)
        {
            throw new InvalidDataException("Code attribute exceeds the JVM code-length limit.");
        }

        int codeLength = checked((int)rawCodeLength);
        int codeOffsetInTail = checked(bodyOffsetInTail + reader.Position);
        byte[] codeBytes = reader.ReadBytes(codeLength, $"{context}.code").ToArray();
        JavaCodeAnalysis analysis = JavaBytecodeDecoder.Decode(codeBytes, pool);

        ushort exceptionCount = reader.ReadU2($"{context}.exception_table_length");
        RequireCount(exceptionCount, MaxExceptionHandlers, $"{context}.exception_table_length");
        var handlers = new List<ParsedExceptionHandler>(exceptionCount);
        for (int index = 0; index < exceptionCount; index++)
        {
            int start = reader.ReadU2($"{context}.exception_table[{index}].start_pc");
            int end = reader.ReadU2($"{context}.exception_table[{index}].end_pc");
            int handler = reader.ReadU2($"{context}.exception_table[{index}].handler_pc");
            ushort catchType = reader.ReadU2($"{context}.exception_table[{index}].catch_type");
            if (start >= end || end > codeLength || handler >= codeLength ||
                !analysis.Boundaries.Contains(start) ||
                !analysis.Boundaries.Contains(end) ||
                !analysis.Boundaries.Contains(handler))
            {
                throw new InvalidDataException(
                    $"Exception table entry {index} does not land on valid instruction boundaries.");
            }

            if (catchType != 0)
            {
                _ = pool.GetClassName(catchType, $"{context}.exception_table[{index}].catch_type");
            }

            handlers.Add(new ParsedExceptionHandler(start, end, handler));
        }

        ParseAttributes(ref reader, pool, $"{context}.code", allowCode: false, out _);
        if (reader.Remaining != 0)
        {
            throw new InvalidDataException($"Code attribute {context} has trailing bytes.");
        }

        return new ParsedCode(codeOffsetInTail, codeBytes, analysis, handlers.AsReadOnly());
    }

    private static int ReadBoundedLength(ref ClassFileReader reader, string context)
    {
        uint value = reader.ReadU4(context);
        if (value > MaxAttributeSize || value > reader.Remaining)
        {
            throw new InvalidDataException($"Attribute {context} exceeds the configured safety limit.");
        }

        return checked((int)value);
    }

    private static void RequireCount(int value, int maximum, string context)
    {
        if (value > maximum)
        {
            throw new InvalidDataException($"{context} exceeds the configured safety limit {maximum}.");
        }
    }
}

internal static class JavaDescriptorValidator
{
    public static bool IsFieldDescriptor(string descriptor)
    {
        int offset = 0;
        return ParseFieldType(descriptor, ref offset, allowVoid: false) && offset == descriptor.Length;
    }

    public static bool IsMethodDescriptor(string descriptor)
    {
        if (descriptor.Length < 3 || descriptor[0] != '(')
        {
            return false;
        }

        int offset = 1;
        int parameterSlots = 0;
        while (offset < descriptor.Length && descriptor[offset] != ')')
        {
            int before = offset;
            if (!ParseFieldType(descriptor, ref offset, allowVoid: false))
            {
                return false;
            }

            parameterSlots += descriptor[before] is 'J' or 'D' ? 2 : 1;
            if (parameterSlots > 255)
            {
                return false;
            }
        }

        if (offset >= descriptor.Length || descriptor[offset++] != ')')
        {
            return false;
        }

        return ParseFieldType(descriptor, ref offset, allowVoid: true) && offset == descriptor.Length;
    }

    private static bool ParseFieldType(string value, ref int offset, bool allowVoid)
    {
        if (offset >= value.Length)
        {
            return false;
        }

        char type = value[offset++];
        if (type is 'B' or 'C' or 'D' or 'F' or 'I' or 'J' or 'S' or 'Z')
        {
            return true;
        }

        if (type == 'V')
        {
            return allowVoid;
        }

        if (type == 'L')
        {
            int start = offset;
            int semicolon = value.IndexOf(';', offset);
            if (semicolon <= start)
            {
                return false;
            }

            for (int index = start; index < semicolon; index++)
            {
                if (value[index] is '.' or '[' or ';')
                {
                    return false;
                }
            }

            offset = semicolon + 1;
            return true;
        }

        if (type != '[')
        {
            return false;
        }

        int dimensions = 1;
        while (offset < value.Length && value[offset] == '[')
        {
            dimensions++;
            offset++;
        }

        return dimensions <= 255 && ParseFieldType(value, ref offset, allowVoid: false);
    }
}
