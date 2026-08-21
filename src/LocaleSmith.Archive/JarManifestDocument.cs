using System.Text;

namespace LocaleSmith.Archive;

internal sealed class JarManifestDocument
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly List<ManifestSection> _sections;

    private JarManifestDocument(List<ManifestSection> sections)
    {
        _sections = sections;
    }

    public bool ContainsSignatureClaims => _sections
        .SelectMany(static section => section.Attributes)
        .Any(static attribute => IsSignatureAttribute(attribute.Name));

    public static bool MayContainSignatureClaims(ReadOnlySpan<byte> bytes)
    {
        string ascii = Encoding.ASCII.GetString(bytes)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        foreach (string line in ascii.Split('\n'))
        {
            if (line.Length == 0 || line[0] == ' ')
            {
                continue;
            }

            int separator = line.IndexOf(':');
            if (separator > 0 && IsSignatureAttribute(line[..separator]))
            {
                return true;
            }
        }

        return false;
    }

    public static JarManifestDocument Parse(ReadOnlySpan<byte> bytes)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("JAR manifest is not valid UTF-8.", exception);
        }

        if (text.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidDataException("JAR manifest contains a null character.");
        }

        text = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        var sections = new List<ManifestSection>();
        var attributes = new List<ManifestAttribute>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in text.Split('\n'))
        {
            if (line.Length == 0)
            {
                AddSectionIfNotEmpty(sections, attributes);
                attributes = new List<ManifestAttribute>();
                names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            if (line[0] == ' ')
            {
                if (attributes.Count == 0)
                {
                    throw new InvalidDataException("JAR manifest continuation line has no preceding attribute.");
                }

                ManifestAttribute previous = attributes[^1];
                attributes[^1] = previous with { Value = previous.Value + line[1..] };
                continue;
            }

            int separator = line.IndexOf(':');
            if (separator <= 0 || separator + 1 >= line.Length || line[separator + 1] != ' ')
            {
                throw new InvalidDataException($"JAR manifest contains a malformed attribute line: '{line}'.");
            }

            string name = line[..separator];
            ValidateAttributeName(name);
            if (!names.Add(name))
            {
                throw new InvalidDataException($"JAR manifest section repeats attribute '{name}'.");
            }

            attributes.Add(new ManifestAttribute(name, line[(separator + 2)..]));
        }

        AddSectionIfNotEmpty(sections, attributes);
        if (sections.Count == 0)
        {
            throw new InvalidDataException("JAR manifest contains no attributes.");
        }

        if (!sections[0].Attributes.Any(static attribute =>
                string.Equals(attribute.Name, "Manifest-Version", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("JAR manifest main section has no Manifest-Version attribute.");
        }

        for (int index = 1; index < sections.Count; index++)
        {
            if (sections[index].Attributes.Count == 0 ||
                !string.Equals(sections[index].Attributes[0].Name, "Name", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(sections[index].Attributes[0].Value))
            {
                throw new InvalidDataException($"JAR manifest entry section {index} must start with a non-empty Name attribute.");
            }
        }

        return new JarManifestDocument(sections);
    }

    public byte[] CreateUnsignedCopy()
    {
        var sanitized = new List<ManifestSection>(_sections.Count);
        for (int index = 0; index < _sections.Count; index++)
        {
            List<ManifestAttribute> attributes = _sections[index].Attributes
                .Where(static attribute => !IsSignatureAttribute(attribute.Name))
                .ToList();
            if (index > 0 && attributes.Count == 1 &&
                string.Equals(attributes[0].Name, "Name", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (attributes.Count > 0)
            {
                sanitized.Add(new ManifestSection(attributes));
            }
        }

        using var output = new MemoryStream();
        foreach (ManifestSection section in sanitized)
        {
            foreach (ManifestAttribute attribute in section.Attributes)
            {
                WriteWrappedAttribute(output, attribute);
            }

            output.Write("\r\n"u8);
        }

        return output.ToArray();
    }

    private static bool IsSignatureAttribute(string name) =>
        name.Equals("Signature-Version", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Magic", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Digest-Algorithms", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("-Digest", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("Digest", StringComparison.OrdinalIgnoreCase);

    private static void AddSectionIfNotEmpty(
        ICollection<ManifestSection> sections,
        List<ManifestAttribute> attributes)
    {
        if (attributes.Count > 0)
        {
            sections.Add(new ManifestSection(attributes));
        }
    }

    private static void ValidateAttributeName(string name)
    {
        if (name.Length is 0 or > 70 || !char.IsAsciiLetterOrDigit(name[0]) ||
            name.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
        {
            throw new InvalidDataException($"JAR manifest attribute name is invalid: '{name}'.");
        }
    }

    private static void WriteWrappedAttribute(Stream output, ManifestAttribute attribute)
    {
        string prefix = $"{attribute.Name}: ";
        int prefixBytes = StrictUtf8.GetByteCount(prefix);
        if (prefixBytes >= 72)
        {
            throw new InvalidDataException($"JAR manifest attribute name is too long: '{attribute.Name}'.");
        }

        int offset = 0;
        bool first = true;
        do
        {
            string linePrefix = first ? prefix : " ";
            int maximumValueBytes = 72 - StrictUtf8.GetByteCount(linePrefix);
            int length = TakeUtf16Length(attribute.Value.AsSpan(offset), maximumValueBytes);
            if (length == 0 && offset < attribute.Value.Length)
            {
                throw new InvalidDataException(
                    $"JAR manifest attribute '{attribute.Name}' contains a character too large to wrap safely.");
            }

            string line = string.Concat(
                linePrefix.AsSpan(),
                attribute.Value.AsSpan(offset, length),
                "\r\n".AsSpan());
            byte[] lineBytes = StrictUtf8.GetBytes(line);
            output.Write(lineBytes);
            offset += length;
            first = false;
        }
        while (offset < attribute.Value.Length);
    }

    private static int TakeUtf16Length(ReadOnlySpan<char> value, int maximumUtf8Bytes)
    {
        int utf16Length = 0;
        int utf8Length = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (utf8Length + rune.Utf8SequenceLength > maximumUtf8Bytes)
            {
                break;
            }

            utf8Length += rune.Utf8SequenceLength;
            utf16Length += rune.Utf16SequenceLength;
        }

        return utf16Length;
    }

    private sealed record ManifestSection(List<ManifestAttribute> Attributes);

    private sealed record ManifestAttribute(string Name, string Value);
}
