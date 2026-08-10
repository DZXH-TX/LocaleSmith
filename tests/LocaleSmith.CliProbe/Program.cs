using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace LocaleSmith.CliProbe;

internal static class Program
{
    private const int TokenIntegrityLevel = 25;
    private const uint TokenQuery = 0x0008;
    private const int UoiName = 2;
    private const int ErrorInsufficientBuffer = 122;

    public static int Main(string[] arguments)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("windows-only");
            return 2;
        }

        return RunWindows(arguments);
    }

    [SupportedOSPlatform("windows")]
    private static int RunWindows(string[] arguments)
    {
        var mode = arguments.FirstOrDefault() ?? "inspect";
        switch (mode)
        {
            case "inspect":
                if (!IsProcessInJob(GetCurrentProcess(), nint.Zero, out var inJob))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "IsProcessInJob failed.");
                }

                Console.WriteLine(
                    $"integrity={GetIntegritySid()};inJob={inJob};desktop={GetCurrentDesktopName()}");
                return 0;
            case "echo":
                Console.WriteLine(string.Join('|', arguments.Skip(1)));
                return 0;
            case "spawn-tree":
                return SpawnTree();
            case "hold":
                Thread.Sleep(Timeout.Infinite);
                return 0;
            default:
                Console.Error.WriteLine($"unknown-mode={mode}");
                return 3;
        }
    }

    [SupportedOSPlatform("windows")]
    private static int SpawnTree()
    {
        var executable = System.Environment.ProcessPath
            ?? throw new InvalidOperationException("The probe executable path is unavailable.");
        using var child = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            ArgumentList = { "hold" }
        }) ?? throw new InvalidOperationException("The probe child process did not start.");
        Console.WriteLine($"childPid={child.Id}");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    [SupportedOSPlatform("windows")]
    private static string GetIntegritySid()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "OpenProcessToken failed.");
        }

        using (token)
        {
            _ = GetTokenInformation(token, TokenIntegrityLevel, nint.Zero, 0, out var requiredBytes);
            if (requiredBytes == 0 || Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Integrity buffer sizing failed.");
            }

            var buffer = Marshal.AllocHGlobal(checked((int)requiredBytes));
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, requiredBytes, out _))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Integrity query failed.");
                }

                var label = Marshal.PtrToStructure<TokenMandatoryLabel>(buffer);
                if (!ConvertSidToStringSid(label.Label.Sid, out var sid))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "SID conversion failed.");
                }

                using (sid)
                {
                    return Marshal.PtrToStringUni(sid.DangerousGetHandle())
                        ?? throw new InvalidOperationException("Integrity SID was empty.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static string GetCurrentDesktopName()
    {
        var desktop = GetThreadDesktop(GetCurrentThreadId());
        if (desktop == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetThreadDesktop failed.");
        }

        _ = GetUserObjectInformation(desktop, UoiName, nint.Zero, 0, out var requiredBytes);
        if (requiredBytes == 0 || Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Desktop name buffer sizing failed.");
        }

        var buffer = Marshal.AllocHGlobal(checked((int)requiredBytes));
        try
        {
            if (!GetUserObjectInformation(desktop, UoiName, buffer, requiredBytes, out _))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Desktop name query failed.");
            }

            return Marshal.PtrToStringUni(buffer)
                ?? throw new InvalidOperationException("Desktop name was empty.");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public nint Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenMandatoryLabel
    {
        public SidAndAttributes Label;
    }

    private sealed class SafeLocalAllocHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeLocalAllocHandle() : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => LocalFree(handle) == nint.Zero;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        nint process,
        uint desiredAccess,
        out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle token,
        int tokenInformationClass,
        nint tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [DllImport("advapi32.dll", EntryPoint = "ConvertSidToStringSidW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSidToStringSid(nint sid, out SafeLocalAllocHandle stringSid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(
        nint process,
        nint job,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetThreadDesktop(uint threadId);

    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(
        nint handle,
        int index,
        nint information,
        uint informationLength,
        out uint needed);
}
