namespace LocaleSmith.NativeInterop;

public sealed class NativeCoreException : Exception
{
    public NativeCoreException(NativeCoreErrorCode errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public NativeCoreErrorCode ErrorCode { get; }
}
