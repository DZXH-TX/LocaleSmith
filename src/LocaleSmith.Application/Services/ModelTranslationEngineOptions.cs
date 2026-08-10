namespace LocaleSmith.Application.Services;

public sealed record ModelTranslationEngineOptions
{
    public int MaxEntriesPerRequest { get; init; } = 40;

    public int MaxSourceCharactersPerRequest { get; init; } = 12_000;

    public int MaxOutputTokens { get; init; } = 8_000;

    internal void Validate()
    {
        if (MaxEntriesPerRequest <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxEntriesPerRequest));
        }

        if (MaxSourceCharactersPerRequest <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxSourceCharactersPerRequest));
        }

        if (MaxOutputTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxOutputTokens));
        }
    }
}
