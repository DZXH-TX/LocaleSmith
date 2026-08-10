using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using JaxI18n.Core.Abstractions;
using JaxI18n.Core.Models;

namespace JaxI18n.Infrastructure.Environment;

public sealed partial class TerminalEnvironmentDetector : ITerminalEnvironmentDetector
{
    public static IReadOnlySet<string> DefaultEnvironmentAllowlist { get; } = new HashSet<string>(
        [
            "OS", "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER", "NUMBER_OF_PROCESSORS",
            "SystemRoot", "WINDIR", "ComSpec", "PATH", "PATHEXT", "TEMP", "TMP",
            "DOTNET_ROOT", "JAVA_HOME"
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _environmentAllowlist;
    private readonly IReadOnlyDictionary<string, string?>? _environmentOverride;
    private readonly string? _workingDirectoryOverride;
    private readonly TerminalShellKind? _shellOverride;
    private readonly string? _shellVersionOverride;

    public TerminalEnvironmentDetector(
        IEnumerable<string>? environmentAllowlist = null,
        IReadOnlyDictionary<string, string?>? environmentOverride = null,
        string? workingDirectoryOverride = null,
        TerminalShellKind? shellOverride = null,
        string? shellVersionOverride = null)
    {
        _environmentAllowlist = new HashSet<string>(
            environmentAllowlist ?? DefaultEnvironmentAllowlist,
            StringComparer.OrdinalIgnoreCase);
        _environmentOverride = environmentOverride;
        _workingDirectoryOverride = workingDirectoryOverride;
        _shellOverride = shellOverride;
        _shellVersionOverride = shellVersionOverride;
    }

    public ValueTask<TerminalEnvironmentContext> DetectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var environment = CaptureEnvironment();
        var (shell, shellVersion) = _shellOverride is { } explicitShell
            ? (explicitShell, _shellVersionOverride)
            : DetectShell();
        var context = new TerminalEnvironmentContext(
            RuntimeInformation.OSDescription,
            System.Environment.OSVersion.VersionString,
            shell,
            shellVersion,
            Path.GetFullPath(_workingDirectoryOverride ?? System.Environment.CurrentDirectory),
            environment);
        return ValueTask.FromResult(context);
    }

    private SortedDictionary<string, string> CaptureEnvironment()
    {
        IEnumerable<KeyValuePair<string, string?>> values;
        if (_environmentOverride is not null)
        {
            values = _environmentOverride;
        }
        else
        {
            values = System.Environment.GetEnvironmentVariables()
                .Cast<System.Collections.DictionaryEntry>()
                .Select(static item => new KeyValuePair<string, string?>(item.Key?.ToString() ?? string.Empty, item.Value?.ToString()));
        }

        var safe = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values)
        {
            if (!_environmentAllowlist.Contains(pair.Key) ||
                SensitiveVariableName().IsMatch(pair.Key) ||
                string.IsNullOrEmpty(pair.Value))
            {
                continue;
            }

            safe[pair.Key] = SanitizeValue(pair.Value);
        }

        return safe;
    }

    private static string SanitizeValue(string value)
    {
        const int maximumLength = 2048;
        var length = Math.Min(value.Length, maximumLength);
        return string.Create(length, value, static (destination, source) =>
        {
            for (var index = 0; index < destination.Length; index++)
            {
                var character = source[index];
                destination[index] = char.IsControl(character) ? ' ' : character;
            }
        });
    }

    private static (TerminalShellKind Shell, string? Version) DetectShell()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (TerminalShellKind.Unknown, null);
        }

        foreach (var ancestor in EnumerateProcessAncestors())
        {
            var shell = ancestor.ExecutableName.ToLowerInvariant() switch
            {
                "pwsh.exe" or "pwsh" => TerminalShellKind.PowerShellCore,
                "powershell.exe" or "powershell" => TerminalShellKind.WindowsPowerShell,
                "cmd.exe" or "cmd" => TerminalShellKind.CommandPrompt,
                _ => TerminalShellKind.Unknown
            };
            if (shell != TerminalShellKind.Unknown)
            {
                return (shell, TryGetProcessVersion(ancestor.ProcessId));
            }
        }

        var distributionChannel = System.Environment.GetEnvironmentVariable("POWERSHELL_DISTRIBUTION_CHANNEL");
        if (!string.IsNullOrWhiteSpace(distributionChannel))
        {
            return (TerminalShellKind.PowerShellCore, null);
        }

        var psModulePath = System.Environment.GetEnvironmentVariable("PSModulePath");
        if (!string.IsNullOrWhiteSpace(psModulePath))
        {
            return (TerminalShellKind.WindowsPowerShell, "5.1");
        }

        return string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("ComSpec"))
            ? (TerminalShellKind.Unknown, null)
            : (TerminalShellKind.CommandPrompt, TryGetFileVersion(System.Environment.GetEnvironmentVariable("ComSpec")!));
    }

    private static IEnumerable<ProcessEntry> EnumerateProcessAncestors()
    {
        var snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
        if (snapshot == new nint(-1))
        {
            yield break;
        }

        try
        {
            var entries = new Dictionary<uint, ProcessEntry>();
            var native = new NativeProcessEntry { Size = checked((uint)Marshal.SizeOf<NativeProcessEntry>()) };
            if (Process32First(snapshot, ref native))
            {
                do
                {
                    entries[native.ProcessId] = new ProcessEntry(native.ProcessId, native.ParentProcessId, native.ExecutableFile);
                    native.Size = checked((uint)Marshal.SizeOf<NativeProcessEntry>());
                }
                while (Process32Next(snapshot, ref native));
            }

            using var currentProcess = Process.GetCurrentProcess();
            var currentId = checked((uint)currentProcess.Id);
            for (var depth = 0; depth < 32 && entries.TryGetValue(currentId, out var current); depth++)
            {
                if (!entries.TryGetValue(current.ParentProcessId, out var parent) || parent.ProcessId == currentId)
                {
                    yield break;
                }

                yield return parent;
                currentId = parent.ProcessId;
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private static string? TryGetProcessVersion(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            return process.MainModule?.FileVersionInfo.ProductVersion;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string? TryGetFileVersion(string path)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(path).ProductVersion;
        }
        catch (Exception exception) when (exception is ArgumentException or FileNotFoundException)
        {
            return null;
        }
    }

    [GeneratedRegex(
        "(?i)(?:secret|token|password|credential|cookie|private.?key|api.?key|access.?key|auth|connection.?string)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SensitiveVariableName();

    private sealed record ProcessEntry(uint ProcessId, uint ParentProcessId, string ExecutableName);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId;
        public uint ThreadCount;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(nint snapshot, ref NativeProcessEntry entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(nint snapshot, ref NativeProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
