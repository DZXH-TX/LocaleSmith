using System.Text.Json.Serialization;

namespace JaxI18n.Application.Services;

internal sealed record PromptTranslationItem(
    string Id,
    string RelativePath,
    string? Key,
    string Source);

internal sealed record PromptTranslationEnvelope(
    string TargetLanguage,
    IReadOnlyList<string> Styles,
    IReadOnlyList<PromptTranslationItem> Entries);

internal sealed class ModelTranslationEnvelope
{
    public List<ModelTranslationItem>? Translations { get; init; }
}

internal sealed class ModelTranslationItem
{
    public string? Id { get; init; }

    public string? Formal { get; init; }

    public string? Informal { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(PromptTranslationEnvelope))]
[JsonSerializable(typeof(ModelTranslationEnvelope))]
internal sealed partial class TranslationJsonContext : JsonSerializerContext
{
}
