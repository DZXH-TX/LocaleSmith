namespace JaxI18n.Application;

public sealed class TranslationContractException : Exception
{
    public TranslationContractException(string message)
        : base(message)
    {
    }

    public TranslationContractException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
