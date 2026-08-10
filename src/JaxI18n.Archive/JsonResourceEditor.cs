using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JaxI18n.Archive;

internal static class JsonResourceEditor
{
    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private static readonly JsonSerializerOptions IndentedOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    public static IReadOnlyList<(string Key, string Value)> ReadLanguageEntries(string text)
    {
        JsonNode? root = JsonNode.Parse(text);
        if (root is not JsonObject rootObject)
        {
            throw new InvalidDataException("A Minecraft JSON language file must contain a JSON object at its root.");
        }

        var entries = new List<(string Key, string Value)>();
        foreach ((string key, JsonNode? value) in rootObject)
        {
            if (value is JsonValue jsonValue && jsonValue.TryGetValue(out string? stringValue))
            {
                entries.Add((key, stringValue));
            }
        }

        return entries;
    }

    public static IReadOnlyList<(string Pointer, string Value)> ReadMcmetaDisplayEntries(
        string text,
        string archivePath)
    {
        JsonNode? root = JsonNode.Parse(text);
        if (root is null)
        {
            throw new InvalidDataException("The JSON resource is empty.");
        }

        var leaves = new List<(string Pointer, string Value)>();
        if (!string.Equals(archivePath, "pack.mcmeta", StringComparison.OrdinalIgnoreCase) ||
            root is not JsonObject rootObject ||
            rootObject["pack"] is not JsonObject pack ||
            pack["description"] is not { } description)
        {
            return leaves;
        }

        CollectTextComponent(description, "/pack/description", leaves);
        return leaves;
    }

    public static byte[] UpdateLanguage(
        string baseText,
        IReadOnlyDictionary<string, string> translations)
    {
        JsonNode? root = JsonNode.Parse(baseText);
        if (root is not JsonObject rootObject)
        {
            throw new InvalidDataException("A Minecraft JSON language file must contain a JSON object at its root.");
        }

        foreach ((string key, string value) in translations)
        {
            rootObject[key] = value;
        }

        return Serialize(rootObject, baseText);
    }

    public static byte[] UpdatePointers(
        string baseText,
        IReadOnlyDictionary<string, string> translations)
    {
        JsonNode? root = JsonNode.Parse(baseText);
        if (root is null)
        {
            throw new InvalidDataException("The JSON resource is empty.");
        }

        foreach ((string pointer, string value) in translations)
        {
            if (pointer.Length == 0)
            {
                if (translations.Count != 1)
                {
                    throw new InvalidDataException("A root JSON string cannot be patched with additional pointers.");
                }

                root = JsonValue.Create(value)
                    ?? throw new InvalidDataException("The translated root JSON string could not be created.");
                continue;
            }

            SetPointer(root, pointer, value);
        }

        return Serialize(root, baseText);
    }

    private static void CollectTextComponent(
        JsonNode node,
        string pointer,
        ICollection<(string Pointer, string Value)> leaves)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                foreach (string displayProperty in new[] { "text", "fallback" })
                {
                    if (jsonObject[displayProperty] is JsonValue valueNode &&
                        valueNode.TryGetValue(out string? value))
                    {
                        leaves.Add(($"{pointer}/{displayProperty}", value));
                    }
                }

                if (jsonObject["extra"] is JsonArray extra)
                {
                    for (int index = 0; index < extra.Count; index++)
                    {
                        if (extra[index] is { } child)
                        {
                            CollectTextComponent(child, $"{pointer}/extra/{index}", leaves);
                        }
                    }
                }

                break;
            case JsonArray jsonArray:
                for (int index = 0; index < jsonArray.Count; index++)
                {
                    JsonNode? child = jsonArray[index];
                    if (child is not null)
                    {
                        CollectTextComponent(child, $"{pointer}/{index}", leaves);
                    }
                }

                break;
            case JsonValue jsonValue when jsonValue.TryGetValue(out string? value):
                leaves.Add((pointer, value));
                break;
        }
    }

    private static void SetPointer(JsonNode root, string pointer, string value)
    {
        if (string.IsNullOrEmpty(pointer) || pointer[0] != '/')
        {
            throw new InvalidDataException($"Invalid JSON pointer '{pointer}'.");
        }

        string[] segments = pointer[1..]
            .Split('/')
            .Select(UnescapePointerSegment)
            .ToArray();
        JsonNode current = root;
        for (int index = 0; index < segments.Length - 1; index++)
        {
            current = current switch
            {
                JsonObject jsonObject when jsonObject[segments[index]] is { } child => child,
                JsonArray jsonArray when int.TryParse(segments[index], out int arrayIndex) &&
                    arrayIndex >= 0 && arrayIndex < jsonArray.Count && jsonArray[arrayIndex] is { } child => child,
                _ => throw new InvalidDataException($"JSON pointer '{pointer}' does not identify an existing value.")
            };
        }

        string final = segments[^1];
        switch (current)
        {
            case JsonObject jsonObject when jsonObject.ContainsKey(final):
                jsonObject[final] = value;
                break;
            case JsonArray jsonArray when int.TryParse(final, out int arrayIndex) &&
                arrayIndex >= 0 && arrayIndex < jsonArray.Count:
                jsonArray[arrayIndex] = value;
                break;
            default:
                throw new InvalidDataException($"JSON pointer '{pointer}' does not identify an existing value.");
        }
    }

    private static byte[] Serialize(JsonNode root, string originalText)
    {
        bool indented = originalText.Contains('\n', StringComparison.Ordinal);
        string json = root.ToJsonString(indented ? IndentedOptions : CompactOptions);
        if (originalText.EndsWith('\n'))
        {
            json += Environment.NewLine;
        }

        return System.Text.Encoding.UTF8.GetBytes(json);
    }

    private static string UnescapePointerSegment(string segment) =>
        segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
}
