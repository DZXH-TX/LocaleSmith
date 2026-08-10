namespace JaxI18n.Core.Models;

public sealed record AvailableModelInfo(
    string Name,
    string? Digest,
    long? SizeBytes,
    DateTimeOffset? ModifiedAt,
    string? Family,
    string? ParameterSize,
    string? QuantizationLevel);
