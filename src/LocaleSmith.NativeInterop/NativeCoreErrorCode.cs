namespace LocaleSmith.NativeInterop;

public enum NativeCoreErrorCode
{
    Ok = 0,
    NullPointer = 1,
    InvalidUtf8 = 2,
    Io = 3,
    InvalidArchive = 4,
    LimitExceeded = 5,
    UnsafeArchivePath = 6,
    Serialization = 7,
    Panic = 8,
    InvalidArgument = 9
}
