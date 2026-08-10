using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LocaleSmith.Infrastructure.Cli;

/// <summary>
/// Starts a CLI process with a low-integrity restricted token on a private desktop and places the
/// still-suspended process into a preconfigured kill-on-close job before its first instruction runs.
/// This constrains token privileges, process-tree lifetime and common UI attacks. It is deliberately
/// not described as filesystem isolation: an allowed executable may still read files permitted by
/// the restricted user's ACLs outside the policy-selected working directory, access the network, or
/// launch other low-integrity child images inside the same job.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsRestrictedProcessLauncher : IRestrictedProcessLauncher
{
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustDefault = 0x0080;
    private const uint DisableMaxPrivilege = 0x00000001;
    private const uint LuaToken = 0x00000004;
    private const int TokenIntegrityLevel = 25;
    private const uint SeGroupIntegrity = 0x00000020;
    private const uint SeGroupIntegrityEnabled = 0x00000040;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateNoWindow = 0x08000000;
    private const uint HandleFlagInherit = 0x00000001;
    private const nuint ProcThreadAttributeHandleList = 0x00020002;
    private const uint DesktopAllAccess = 0x000F01FF;
    private const int UoiName = 2;
    private const uint StartupFailureExitCode = 0xC0000142;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const uint Infinite = 0xFFFFFFFF;
    private const int ErrorInsufficientBuffer = 122;
    private const int MaximumCommandLineCharacters = 32767;
    private const string LowIntegritySid = "S-1-16-4096";

    public static WindowsRestrictedProcessLauncher Instance { get; } = new();

    private WindowsRestrictedProcessLauncher()
    {
    }

    public IRestrictedChildProcess Start(RestrictedProcessStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Restricted CLI process creation is Windows-only and has no unrestricted fallback.");
        }

        var executable = Path.GetFullPath(request.ExecutablePath);
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
        {
            throw new FileNotFoundException(
                "The policy-resolved absolute CLI executable no longer exists. Startup failed closed.",
                executable);
        }

        var workingDirectory = Path.GetFullPath(request.WorkingDirectory);
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The policy-approved CLI working directory no longer exists: '{workingDirectory}'.");
        }

        var commandLine = BuildCommandLine(executable, request.Arguments);
        var environmentBlock = BuildEnvironmentBlock(request.Environment);
        WindowsProcessJob? job = null;
        SafeAccessTokenHandle? processToken = null;
        SafeDesktopHandle? desktop = null;
        AnonymousPipePair? standardOutput = null;
        AnonymousPipePair? standardError = null;
        AnonymousPipePair? standardInput = null;
        ProcThreadAttributeList? attributeList = null;
        SafeHGlobalHandle? environment = null;
        SafeProcessHandle? process = null;
        SafeFileHandle? primaryThread = null;
        try
        {
            job = WindowsProcessJob.Create(request.Timeout);
            processToken = CreateLowIntegrityRestrictedToken();
            // Microsoft explicitly warns that a restricted-token process must not share the default
            // desktop with unrestricted applications because of SendMessage/PostMessage shatter attacks:
            // https://learn.microsoft.com/windows/win32/api/securitybaseapi/nf-securitybaseapi-createrestrictedtoken
            var desktopName = $"LocaleSmithCli_{Guid.NewGuid():N}";
            desktop = CreatePrivateLowIntegrityDesktop(desktopName);
            var windowStationName = GetCurrentWindowStationName();

            standardOutput = AnonymousPipePair.Create(childReads: false);
            standardError = AnonymousPipePair.Create(childReads: false);
            standardInput = AnonymousPipePair.Create(childReads: true);
            attributeList = ProcThreadAttributeList.Create(
                standardInput.ChildHandle,
                standardOutput.ChildHandle,
                standardError.ChildHandle);
            environment = SafeHGlobalHandle.FromString(environmentBlock);

            var startupInfo = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = checked((uint)Marshal.SizeOf<StartupInfoEx>()),
                    Desktop = $"{windowStationName}\\{desktopName}",
                    Flags = StartfUseStdHandles,
                    StandardInput = standardInput.ChildHandle.DangerousGetHandle(),
                    StandardOutput = standardOutput.ChildHandle.DangerousGetHandle(),
                    StandardError = standardError.ChildHandle.DangerousGetHandle()
                },
                AttributeList = attributeList.DangerousGetHandle()
            };
            var creationFlags = CreateSuspended |
                CreateNoWindow |
                CreateUnicodeEnvironment |
                ExtendedStartupInfoPresent;
            if (!CreateProcessAsUser(
                    processToken,
                    executable,
                    commandLine,
                    nint.Zero,
                    nint.Zero,
                    inheritHandles: true,
                    creationFlags,
                    environment.DangerousGetHandle(),
                    workingDirectory,
                    ref startupInfo,
                    out var processInformation))
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    "CreateProcessAsUserW refused the low-integrity restricted CLI process.");
            }

            process = new SafeProcessHandle(processInformation.Process, ownsHandle: true);
            primaryThread = new SafeFileHandle(processInformation.Thread, ownsHandle: true);
            job.Assign(process);

            // A newly created suspended thread must have exactly one suspend count. Any other result
            // is treated as an unsafe state and the process is killed rather than allowed to continue.
            var previousSuspendCount = ResumeThread(primaryThread);
            if (previousSuspendCount != 1)
            {
                var error = previousSuspendCount == uint.MaxValue ? Marshal.GetLastPInvokeError() : 0;
                throw new Win32Exception(
                    error,
                    $"ResumeThread returned unexpected suspend count {previousSuspendCount}; startup failed closed.");
            }

            primaryThread.Dispose();
            primaryThread = null;
            standardInput.ParentHandle.Dispose();
            standardInput.ChildHandle.Dispose();
            standardOutput.ChildHandle.Dispose();
            standardError.ChildHandle.Dispose();

            var child = WindowsRestrictedChildProcess.Create(
                process,
                job,
                desktop,
                standardOutput.DetachParentHandle(),
                standardError.DetachParentHandle());
            process = null;
            job = null;
            desktop = null;
            return child;
        }
        catch (Exception startupException)
        {
            var cleanupException = TerminateFailedStartup(process, job);
            if (cleanupException is not null)
            {
                throw new AggregateException(
                    "Restricted process startup failed and the suspended process required emergency cleanup.",
                    startupException,
                    cleanupException);
            }

            throw;
        }
        finally
        {
            primaryThread?.Dispose();
            process?.Dispose();
            attributeList?.Dispose();
            standardInput?.Dispose();
            standardError?.Dispose();
            standardOutput?.Dispose();
            environment?.Dispose();
            desktop?.Dispose();
            processToken?.Dispose();
            job?.Dispose();
        }
    }

    private static SafeAccessTokenHandle CreateLowIntegrityRestrictedToken()
    {
        if (!OpenProcessToken(
                GetCurrentProcess(),
                TokenAssignPrimary | TokenDuplicate | TokenQuery | TokenAdjustDefault,
                out var currentToken))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to open the current process token.");
        }

        using (currentToken)
        {
            if (!CreateRestrictedToken(
                    currentToken,
                    DisableMaxPrivilege | LuaToken,
                    0,
                    nint.Zero,
                    0,
                    nint.Zero,
                    0,
                    nint.Zero,
                    out var restrictedToken))
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    "Failed to create the DISABLE_MAX_PRIVILEGE/LUA restricted token.");
            }

            try
            {
                using var integritySid = ConvertStringSid(LowIntegritySid);
                var mandatoryLabel = new TokenMandatoryLabel
                {
                    Label = new SidAndAttributes
                    {
                        Sid = integritySid.DangerousGetHandle(),
                        Attributes = SeGroupIntegrity | SeGroupIntegrityEnabled
                    }
                };
                var informationLength = checked(
                    (uint)Marshal.SizeOf<TokenMandatoryLabel>() + GetLengthSid(integritySid.DangerousGetHandle()));
                if (!SetTokenInformation(
                        restrictedToken,
                        TokenIntegrityLevel,
                        ref mandatoryLabel,
                        informationLength))
                {
                    throw new Win32Exception(
                        Marshal.GetLastPInvokeError(),
                        "Failed to set the restricted token integrity level to Low (S-1-16-4096).");
                }

                return restrictedToken;
            }
            catch
            {
                restrictedToken.Dispose();
                throw;
            }
        }
    }

    private static SafeDesktopHandle CreatePrivateLowIntegrityDesktop(string desktopName)
    {
        var userSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The current Windows token does not contain a user SID.");
        var sddl = $"O:{userSid}G:{userSid}D:(A;;GA;;;{userSid})S:(ML;;NW;;;LW)";
        using var securityDescriptor = ConvertStringSecurityDescriptor(sddl);
        var attributes = new SecurityAttributes
        {
            Length = checked((uint)Marshal.SizeOf<SecurityAttributes>()),
            SecurityDescriptor = securityDescriptor.DangerousGetHandle(),
            InheritHandle = false
        };
        var desktop = CreateDesktop(
            desktopName,
            nint.Zero,
            nint.Zero,
            0,
            DesktopAllAccess,
            ref attributes);
        if (desktop.IsInvalid)
        {
            desktop.Dispose();
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Failed to create the private low-integrity CLI desktop.");
        }

        return desktop;
    }

    private static string GetCurrentWindowStationName()
    {
        var windowStation = GetProcessWindowStation();
        if (windowStation == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to obtain the current window station.");
        }

        _ = GetUserObjectInformation(windowStation, UoiName, nint.Zero, 0, out var requiredBytes);
        var error = Marshal.GetLastPInvokeError();
        if (requiredBytes == 0 || error != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(error, "Failed to size the current window station name.");
        }

        var buffer = Marshal.AllocHGlobal(checked((int)requiredBytes));
        try
        {
            if (!GetUserObjectInformation(windowStation, UoiName, buffer, requiredBytes, out _))
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    "Failed to read the current window station name.");
            }

            return Marshal.PtrToStringUni(buffer)
                ?? throw new InvalidOperationException("The current window station name was empty.");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static char[] BuildCommandLine(string executable, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder();
        AppendWindowsArgument(builder, executable);
        foreach (var argument in arguments)
        {
            builder.Append(' ');
            AppendWindowsArgument(builder, argument);
        }

        if (builder.Length >= MaximumCommandLineCharacters)
        {
            throw new ArgumentException(
                $"The Windows command line must be shorter than {MaximumCommandLineCharacters} characters.",
                nameof(arguments));
        }

        builder.Append('\0');
        return builder.ToString().ToCharArray();
    }

    private static void AppendWindowsArgument(StringBuilder builder, string argument)
    {
        if (argument.Contains('\0'))
        {
            throw new ArgumentException("Windows command-line arguments cannot contain NUL.", nameof(argument));
        }

        if (argument.Length != 0 && argument.All(static value => !char.IsWhiteSpace(value) && value != '"'))
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');
        var backslashCount = 0;
        foreach (var value in argument)
        {
            if (value == '\\')
            {
                backslashCount++;
                continue;
            }

            if (value == '"')
            {
                builder.Append('\\', checked((backslashCount * 2) + 1));
                builder.Append('"');
                backslashCount = 0;
                continue;
            }

            builder.Append('\\', backslashCount);
            backslashCount = 0;
            builder.Append(value);
        }

        builder.Append('\\', checked(backslashCount * 2));
        builder.Append('"');
    }

    private static string BuildEnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var entries = new List<string>(environment.Count);
        foreach (var pair in environment.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Contains('=') || pair.Key.Contains('\0'))
            {
                throw new ArgumentException("Environment variable names must be nonempty and cannot contain '=' or NUL.");
            }

            if (pair.Value.Contains('\0'))
            {
                throw new ArgumentException("Environment variable values cannot contain NUL.");
            }

            entries.Add($"{pair.Key}={pair.Value}");
        }

        return string.Join('\0', entries) + "\0\0";
    }

    private static Exception? TerminateFailedStartup(SafeProcessHandle? process, WindowsProcessJob? job)
    {
        if (process is null || process.IsInvalid || process.IsClosed)
        {
            return null;
        }

        Exception? terminationException = null;
        if (!TerminateProcess(process, StartupFailureExitCode))
        {
            var waitResult = WaitForSingleObject(process, 0);
            if (waitResult != WaitObject0)
            {
                terminationException = new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    "Failed to terminate the suspended restricted process after startup failure.");
            }
        }

        try
        {
            job?.Dispose();
        }
        catch (Exception exception)
        {
            terminationException = terminationException is null
                ? exception
                : new AggregateException(terminationException, exception);
        }

        var finalWait = WaitForSingleObject(process, 5000);
        if (finalWait != WaitObject0)
        {
            var waitException = new Win32Exception(
                finalWait == uint.MaxValue ? Marshal.GetLastPInvokeError() : 0,
                "The failed restricted process did not terminate within the emergency cleanup interval.");
            terminationException = terminationException is null
                ? waitException
                : new AggregateException(terminationException, waitException);
        }

        return terminationException;
    }

    private static SafeLocalAllocHandle ConvertStringSid(string sid)
    {
        if (!ConvertStringSidToSid(sid, out var handle))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Failed to create Windows SID '{sid}'.");
        }

        return handle;
    }

    private static SafeLocalAllocHandle ConvertStringSecurityDescriptor(string sddl)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                sddl,
                1,
                out var handle,
                out _))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Failed to create the private desktop security descriptor.");
        }

        return handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public nint SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public nint Reserved2;
        public nint StandardInput;
        public nint StandardOutput;
        public nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    private sealed class AnonymousPipePair : IDisposable
    {
        private SafeFileHandle? _parentHandle;

        private AnonymousPipePair(SafeFileHandle parentHandle, SafeFileHandle childHandle)
        {
            _parentHandle = parentHandle;
            ChildHandle = childHandle;
        }

        public SafeFileHandle ParentHandle =>
            _parentHandle ?? throw new ObjectDisposedException(nameof(AnonymousPipePair));

        public SafeFileHandle ChildHandle { get; }

        public static AnonymousPipePair Create(bool childReads)
        {
            var attributes = new SecurityAttributes
            {
                Length = checked((uint)Marshal.SizeOf<SecurityAttributes>()),
                InheritHandle = true
            };
            if (!CreatePipe(out var read, out var write, ref attributes, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to create a restricted CLI pipe.");
            }

            var parent = childReads ? write : read;
            var child = childReads ? read : write;
            if (!SetHandleInformation(parent, HandleFlagInherit, 0))
            {
                var error = Marshal.GetLastPInvokeError();
                read.Dispose();
                write.Dispose();
                throw new Win32Exception(error, "Failed to make the parent CLI pipe handle non-inheritable.");
            }

            return new AnonymousPipePair(parent, child);
        }

        public SafeFileHandle DetachParentHandle()
        {
            var handle = ParentHandle;
            _parentHandle = null;
            return handle;
        }

        public void Dispose()
        {
            _parentHandle?.Dispose();
            _parentHandle = null;
            ChildHandle.Dispose();
        }
    }

    private sealed class ProcThreadAttributeList : IDisposable
    {
        private nint _handle;
        private bool _initialized;

        private ProcThreadAttributeList(nint handle) => _handle = handle;

        public nint DangerousGetHandle() => _handle != nint.Zero
            ? _handle
            : throw new ObjectDisposedException(nameof(ProcThreadAttributeList));

        public static ProcThreadAttributeList Create(params SafeFileHandle[] inheritedHandles)
        {
            nuint size = 0;
            var initialResult = InitializeProcThreadAttributeList(nint.Zero, 1, 0, ref size);
            var sizingError = Marshal.GetLastPInvokeError();
            if (initialResult || size == 0 || sizingError != ErrorInsufficientBuffer)
            {
                throw new Win32Exception(
                    sizingError,
                    "Failed to size the restricted process attribute list.");
            }

            var buffer = Marshal.AllocHGlobal(checked((nint)size));
            var list = new ProcThreadAttributeList(buffer);
            try
            {
                if (!InitializeProcThreadAttributeList(buffer, 1, 0, ref size))
                {
                    throw new Win32Exception(
                        Marshal.GetLastPInvokeError(),
                        "Failed to initialize the restricted process attribute list.");
                }

                list._initialized = true;

                var values = inheritedHandles.Select(static handle => handle.DangerousGetHandle()).ToArray();
                var valueSize = checked((nuint)(values.Length * nint.Size));
                var valueBuffer = Marshal.AllocHGlobal(checked((nint)valueSize));
                try
                {
                    Marshal.Copy(values, 0, valueBuffer, values.Length);
                    if (!UpdateProcThreadAttribute(
                            buffer,
                            0,
                            ProcThreadAttributeHandleList,
                            valueBuffer,
                            valueSize,
                            nint.Zero,
                            nint.Zero))
                    {
                        throw new Win32Exception(
                            Marshal.GetLastPInvokeError(),
                            "Failed to restrict child handle inheritance to the three standard pipes.");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(valueBuffer);
                }

                return list;
            }
            catch
            {
                list.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            var handle = Interlocked.Exchange(ref _handle, nint.Zero);
            if (handle == nint.Zero)
            {
                return;
            }

            if (_initialized)
            {
                DeleteProcThreadAttributeList(handle);
                _initialized = false;
            }

            Marshal.FreeHGlobal(handle);
        }
    }

    private sealed class SafeHGlobalHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeHGlobalHandle() : base(ownsHandle: true)
        {
        }

        public static SafeHGlobalHandle FromString(string value)
        {
            var result = new SafeHGlobalHandle();
            result.SetHandle(Marshal.StringToHGlobalUni(value));
            return result;
        }

        protected override bool ReleaseHandle()
        {
            Marshal.FreeHGlobal(handle);
            return true;
        }
    }

    private sealed class SafeLocalAllocHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeLocalAllocHandle() : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => LocalFree(handle) == nint.Zero;
    }

    private sealed class SafeDesktopHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeDesktopHandle() : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseDesktop(handle);
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        nint process,
        uint desiredAccess,
        out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateRestrictedToken(
        SafeAccessTokenHandle existingToken,
        uint flags,
        uint disableSidCount,
        nint sidsToDisable,
        uint deletePrivilegeCount,
        nint privilegesToDelete,
        uint restrictedSidCount,
        nint sidsToRestrict,
        out SafeAccessTokenHandle newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetTokenInformation(
        SafeAccessTokenHandle token,
        int informationClass,
        ref TokenMandatoryLabel information,
        uint informationLength);

    [DllImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(
        SafeAccessTokenHandle token,
        string applicationName,
        [In, Out] char[] commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSidToSidW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSidToSid(string stringSid, out SafeLocalAllocHandle sid);

    [DllImport(
        "advapi32.dll",
        EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        uint stringSdRevision,
        out SafeLocalAllocHandle securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("advapi32.dll")]
    private static extern uint GetLengthSid(nint sid);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);

    [DllImport("user32.dll", EntryPoint = "CreateDesktopW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeDesktopHandle CreateDesktop(
        string desktop,
        nint device,
        nint deviceMode,
        uint flags,
        uint desiredAccess,
        ref SecurityAttributes attributes);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(nint desktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetProcessWindowStation();

    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(
        nint handle,
        int index,
        nint information,
        uint informationLength,
        out uint needed);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(
        out SafeFileHandle readPipe,
        out SafeFileHandle writePipe,
        ref SecurityAttributes pipeAttributes,
        uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        nint attributeList,
        int attributeCount,
        uint flags,
        ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        nint attributeList,
        uint flags,
        nuint attribute,
        nint value,
        nuint size,
        nint previousValue,
        nint returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint attributeList);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    private sealed class WindowsRestrictedChildProcess : IRestrictedChildProcess
    {
        private readonly SafeProcessHandle _process;
        private readonly WindowsProcessJob _job;
        private readonly SafeDesktopHandle _desktop;
        private bool _jobTerminated;
        private bool _disposed;

        private WindowsRestrictedChildProcess(
            SafeProcessHandle process,
            WindowsProcessJob job,
            SafeDesktopHandle desktop,
            StreamReader standardOutput,
            StreamReader standardError)
        {
            _process = process;
            _job = job;
            _desktop = desktop;
            StandardOutput = standardOutput;
            StandardError = standardError;
        }

        public StreamReader StandardOutput { get; }

        public StreamReader StandardError { get; }

        public static WindowsRestrictedChildProcess Create(
            SafeProcessHandle process,
            WindowsProcessJob job,
            SafeDesktopHandle desktop,
            SafeFileHandle standardOutput,
            SafeFileHandle standardError)
        {
            StreamReader? outputReader = null;
            try
            {
                outputReader = CreateReader(standardOutput);
                var errorReader = CreateReader(standardError);
                return new WindowsRestrictedChildProcess(process, job, desktop, outputReader, errorReader);
            }
            catch
            {
                outputReader?.Dispose();
                throw;
            }
        }

        public int? ExitCode
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (WaitForSingleObject(_process, 0) != WaitObject0)
                {
                    return null;
                }

                if (!GetExitCodeProcess(_process, out var exitCode))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to read CLI process exit code.");
                }

                return unchecked((int)exitCode);
            }
        }

        public async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var waitHandle = new ProcessWaitHandle(_process);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = ThreadPool.RegisterWaitForSingleObject(
                waitHandle,
                static (state, _) => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
                completion,
                Timeout.Infinite,
                executeOnlyOnce: true);
            using var cancellationRegistration = cancellationToken.Register(
                static state =>
                {
                    var pair = ((TaskCompletionSource<bool> Completion, CancellationToken Token))state!;
                    pair.Completion.TrySetCanceled(pair.Token);
                },
                (completion, cancellationToken));
            try
            {
                await completion.Task.ConfigureAwait(false);
            }
            finally
            {
                _ = registration.Unregister(null);
            }
        }

        public void TerminateTree()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_jobTerminated)
            {
                return;
            }

            _job.Terminate();
            _jobTerminated = true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            // Kill-on-close is the final fail-closed backstop if an explicit termination call failed.
            _job.Dispose();
            SafeDisposeReader(StandardOutput);
            SafeDisposeReader(StandardError);
            _process.Dispose();
            _desktop.Dispose();
            _disposed = true;
        }

        private static StreamReader CreateReader(SafeFileHandle handle)
        {
            try
            {
                return new StreamReader(
                    new FileStream(handle, FileAccess.Read, bufferSize: 4096, isAsync: false),
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        private static void SafeDisposeReader(StreamReader value)
        {
            try
            {
                value.Dispose();
            }
            catch (IOException)
            {
                // The job is already closed and all descendants are dead; a broken pipe during cleanup is benign.
            }
        }
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        private readonly SafeProcessHandle _process;
        private int _referenceHeld;

        public ProcessWaitHandle(SafeProcessHandle process)
        {
            _process = process;
            var addedReference = false;
            process.DangerousAddRef(ref addedReference);
            _referenceHeld = addedReference ? 1 : 0;
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
        }

        protected override void Dispose(bool explicitDisposing)
        {
            base.Dispose(explicitDisposing);
            if (Interlocked.Exchange(ref _referenceHeld, 0) != 0)
            {
                _process.DangerousRelease();
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
}
