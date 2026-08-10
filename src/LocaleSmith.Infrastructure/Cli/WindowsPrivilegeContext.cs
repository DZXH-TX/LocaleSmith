using System.Runtime.InteropServices;
using LocaleSmith.Core.Abstractions;

namespace LocaleSmith.Infrastructure.Cli;

public sealed class WindowsPrivilegeContext : IPrivilegeContext
{
    private const uint TokenQuery = 0x0008;
    private const int TokenElevation = 20;

    public bool IsElevated
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token))
            {
                return true;
            }

            try
            {
                var elevation = new TokenElevationInfo();
                var size = Marshal.SizeOf<TokenElevationInfo>();
                return !GetTokenInformation(token, TokenElevation, ref elevation, size, out _) || elevation.TokenIsElevated != 0;
            }
            finally
            {
                CloseHandle(token);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevationInfo
    {
        public uint TokenIsElevated;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        nint tokenHandle,
        int tokenInformationClass,
        ref TokenElevationInfo tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
