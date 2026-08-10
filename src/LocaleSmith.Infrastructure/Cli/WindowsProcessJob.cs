using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace LocaleSmith.Infrastructure.Cli;

[SupportedOSPlatform("windows")]
internal sealed class WindowsProcessJob : IDisposable
{
    private const uint JobObjectLimitProcessTime = 0x00000002;
    private const uint JobObjectLimitActiveProcess = 0x00000008;
    private const uint JobObjectLimitProcessMemory = 0x00000100;
    private const uint JobObjectLimitJobMemory = 0x00000200;
    private const uint JobObjectLimitDieOnUnhandledException = 0x00000400;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint JobObjectCpuRateControlEnable = 0x00000001;
    private const uint JobObjectCpuRateControlHardCap = 0x00000004;
    private const uint AllUiRestrictions = 0x000000FF;
    private const int JobObjectBasicUiRestrictionsClass = 4;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const int JobObjectCpuRateControlInformationClass = 15;
    private const uint MaximumActiveProcesses = 16;
    private const ulong MaximumProcessMemoryBytes = 512UL * 1024 * 1024;
    private const ulong MaximumJobMemoryBytes = 1024UL * 1024 * 1024;
    private const uint MaximumCpuRate = 5000;
    private const uint TerminationExitCode = 0xC000013A;

    private readonly SafeFileHandle _handle;
    private bool _disposed;

    private WindowsProcessJob(SafeFileHandle handle) => _handle = handle;

    public static WindowsProcessJob Create(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Job timeout must be positive.");
        }

        var handle = new SafeFileHandle(CreateJobObject(nint.Zero, null), ownsHandle: true);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to create a restricted CLI job object.");
        }

        var job = new WindowsProcessJob(handle);
        try
        {
            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    PerProcessUserTimeLimit = timeout.Ticks,
                    LimitFlags = JobObjectLimitProcessTime |
                        JobObjectLimitActiveProcess |
                        JobObjectLimitProcessMemory |
                        JobObjectLimitJobMemory |
                        JobObjectLimitDieOnUnhandledException |
                        JobObjectLimitKillOnJobClose,
                    ActiveProcessLimit = MaximumActiveProcesses
                },
                ProcessMemoryLimit = checked((nuint)MaximumProcessMemoryBytes),
                JobMemoryLimit = checked((nuint)MaximumJobMemoryBytes)
            };
            SetInformation(
                handle,
                JobObjectExtendedLimitInformationClass,
                limits,
                "Failed to configure CLI job process, memory, and kill-on-close limits.");

            var cpu = new JobObjectCpuRateControlInformation
            {
                ControlFlags = JobObjectCpuRateControlEnable | JobObjectCpuRateControlHardCap,
                CpuRate = MaximumCpuRate
            };
            SetInformation(
                handle,
                JobObjectCpuRateControlInformationClass,
                cpu,
                "Failed to configure the CLI job CPU hard cap.");

            var ui = new JobObjectBasicUiRestrictions { UiRestrictionsClass = AllUiRestrictions };
            SetInformation(
                handle,
                JobObjectBasicUiRestrictionsClass,
                ui,
                "Failed to configure the CLI job UI restrictions.");

            return job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    public void Assign(SafeProcessHandle process)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(process);
        if (!AssignProcessToJobObject(_handle, process))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Failed to constrain the suspended CLI process in its preconfigured job object.");
        }
    }

    public void Terminate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!TerminateJobObject(_handle, TerminationExitCode))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to terminate the restricted CLI job.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _handle.Dispose();
        _disposed = true;
    }

    private static void SetInformation<T>(
        SafeFileHandle job,
        int informationClass,
        T information,
        string errorMessage)
        where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(information, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(job, informationClass, buffer, checked((uint)size)))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), errorMessage);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectCpuRateControlInformation
    {
        public uint ControlFlags;
        public uint CpuRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicUiRestrictions
    {
        public uint UiRestrictionsClass;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObject(nint jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        int informationClass,
        nint information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
}
