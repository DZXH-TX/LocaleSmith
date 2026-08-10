using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;

namespace LocaleSmith.Infrastructure.Cli;

public sealed class SafeCliCommandPolicy : ICliCommandPolicy, ICliSandboxRootManager, IDisposable
{
    public static IReadOnlyList<string> AbsoluteBlacklistPatterns { get; } = Array.AsReadOnly<string>(
    [
        @"::",
        // Product policy requires any occurrence of "Format" to be an absolute denial,
        // including option names and concatenated/obfuscated variants.
        @"(?i)format",
        @"(?i)\b(?:rd|rmdir)\b(?=[^\r\n]*(?:/s|-recurse)\b)(?=[^\r\n]*(?:/q|-force)\b)",
        @"(?i)\bdel(?:ete)?\b(?=[^\r\n]*/f\b)(?=[^\r\n]*/s\b)",
        @"(?i)\bremove-item\b(?=[^\r\n]*-recurse\b)(?=[^\r\n]*-force\b)",
        @"(?i)>\s*nul:?\b",
        @"(?i)(?:-|/)(?:e|ec|enc|encodedcommand|encodedarguments)\b",
        @"(?i)\b(?:invoke-expression|iex|start-process\b[^\r\n]*-verb\s+runas|runas|gsudo|sudo)\b"
    ]);

    private static readonly Regex[] AbsoluteBlacklist = AbsoluteBlacklistPatterns
        .Select(static pattern => new Regex(
            pattern,
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100)))
        .ToArray();
    private static readonly Regex ShellSyntax = new(
        @"[\r\n;&|<>`] | \$\(",
        RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex ProtectedEnvironmentPath = new(
        @"(?i)%(?:windir|systemroot|programfiles(?:\(x86\))?)%",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex EnvironmentExpansion = new(
        @"%[A-Za-z_][A-Za-z0-9_()]*%|\$(?:env:|\{env:)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex ParentTraversal = new(
        @"(?:^|[\\/])\.\.(?:[\\/]|$)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex WindowsRootedPathFragment = new(
        @"(?i)(?:[a-z]:[\\/]|\\\\)[^""'\r\n]*",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100));
    private static readonly HashSet<string> HighRiskInterpreters = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd",
        "powershell",
        "pwsh",
        "wscript",
        "cscript",
        "mshta",
        "rundll32",
        "regsvr32",
        "wmic"
    };

    private readonly ReaderWriterLockSlim _lock = new();
    private readonly HashSet<string> _allowedExecutables = new(PathComparer);
    private readonly HashSet<string> _sandboxRoots = new(PathComparer);
    private readonly string _temporaryRoot;
    private readonly TimeSpan _maximumTimeout;

    public SafeCliCommandPolicy(
        IEnumerable<string> allowedExecutables,
        IEnumerable<string>? additionalSandboxRoots = null,
        string? temporaryRoot = null,
        TimeSpan? maximumTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(allowedExecutables);
        _maximumTimeout = maximumTimeout ?? TimeSpan.FromSeconds(30);
        if (_maximumTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTimeout), "Maximum timeout must be positive.");
        }

        _temporaryRoot = NormalizeSandboxRoot(temporaryRoot ?? Path.GetTempPath());
        ReplaceSandboxRoots(additionalSandboxRoots ?? []);
        ReplaceAllowedExecutables(allowedExecutables);
    }

    public IReadOnlySet<string> AllowedExecutables
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return new ReadOnlySet<string>(new HashSet<string>(_allowedExecutables, PathComparer));
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    public IReadOnlySet<string> SandboxRoots
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return new ReadOnlySet<string>(new HashSet<string>(_sandboxRoots, PathComparer));
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    public void ReplaceSandboxRoots(IEnumerable<string> sandboxRoots)
    {
        ArgumentNullException.ThrowIfNull(sandboxRoots);
        var normalized = sandboxRoots
            .Select(NormalizeSandboxRoot)
            .Prepend(_temporaryRoot)
            .Distinct(PathComparer)
            .ToArray();
        _lock.EnterWriteLock();
        try
        {
            _sandboxRoots.Clear();
            _sandboxRoots.UnionWith(normalized);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void ReplaceAllowedExecutables(IEnumerable<string> executables)
    {
        ArgumentNullException.ThrowIfNull(executables);
        var normalized = executables.Select(ResolveExecutablePath).ToArray();
        _lock.EnterWriteLock();
        try
        {
            _allowedExecutables.Clear();
            _allowedExecutables.UnionWith(normalized);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public bool AddAllowedExecutable(string executable)
    {
        var normalized = ResolveExecutablePath(executable);
        _lock.EnterWriteLock();
        try
        {
            return _allowedExecutables.Add(normalized);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public bool RemoveAllowedExecutable(string executable)
    {
        var normalized = ResolveExecutablePath(executable);
        _lock.EnterWriteLock();
        try
        {
            return _allowedExecutables.Remove(normalized);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public CliPolicyDecision Evaluate(CliCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!TryResolveExecutablePath(command.Executable, out var executable))
        {
            return CliPolicyDecision.Deny(
                CliPolicyViolation.ExecutableNotAllowed,
                "The executable could not be resolved to an existing trusted path.");
        }

        if (HighRiskInterpreters.Contains(Path.GetFileNameWithoutExtension(executable)))
        {
            return CliPolicyDecision.Deny(
                CliPolicyViolation.InterpreterNotAllowed,
                "Shell and living-off-the-land interpreters require a separate AST-validating low-integrity broker and cannot run through the direct CLI path.");
        }

        _lock.EnterReadLock();
        try
        {
            if (!_allowedExecutables.Contains(executable))
            {
                return CliPolicyDecision.Deny(
                    CliPolicyViolation.ExecutableNotAllowed,
                    $"Executable '{Path.GetFileName(command.Executable)}' is not on the dynamic allowlist.");
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }

        if (command.Timeout > _maximumTimeout)
        {
            return CliPolicyDecision.Deny(
                CliPolicyViolation.TimeoutTooLong,
                $"The command timeout exceeds the {_maximumTimeout.TotalSeconds:0}-second maximum.");
        }

        var unredacted = command.ToDisplayString(redactSensitiveValues: false);
        foreach (var blacklist in AbsoluteBlacklist)
        {
            if (blacklist.IsMatch(unredacted))
            {
                return CliPolicyDecision.Deny(
                    CliPolicyViolation.AbsoluteBlacklistMatch,
                    $"The command matches absolute blacklist rule '{blacklist}'.");
            }
        }

        if (ShellSyntax.IsMatch(unredacted))
        {
            return CliPolicyDecision.Deny(
                CliPolicyViolation.ShellSyntaxNotAllowed,
                "Shell chaining, redirection, command substitution and multiline syntax are not allowed.");
        }

        var argumentText = string.Join(' ', command.Arguments).Replace('/', '\\');
        if (ProtectedEnvironmentPath.IsMatch(argumentText))
        {
            return CliPolicyDecision.Deny(
                CliPolicyViolation.ProtectedPathAccess,
                "Arguments may not access Windows or Program Files directories.");
        }

        foreach (var argument in command.Arguments)
        {
            if (EnvironmentExpansion.IsMatch(argument))
            {
                return CliPolicyDecision.Deny(
                    CliPolicyViolation.EnvironmentExpansionNotAllowed,
                    "Environment-variable expansion is not allowed in executable arguments.");
            }

            if (ParentTraversal.IsMatch(argument.Replace('\\', '/')))
            {
                return CliPolicyDecision.Deny(
                    CliPolicyViolation.PathArgumentOutsideSandbox,
                    "Parent-directory traversal is not allowed in executable arguments.");
            }
        }

        if (command.HasSensitiveArguments)
        {
            return CliPolicyDecision.Deny(
                CliPolicyViolation.SensitiveArgumentNotAllowed,
                "Credential-like values cannot be supplied through a model-authored CLI command.");
        }

        if (!Directory.Exists(command.WorkingDirectory))
        {
            return CliPolicyDecision.Deny(
                CliPolicyViolation.WorkingDirectoryMissing,
                "The working directory does not exist.");
        }

        var workingDirectory = CanonicalizeExistingDirectory(command.WorkingDirectory);
        var protectedRoots = GetProtectedRoots().ToArray();
        if (protectedRoots.Any(root => IsWithin(workingDirectory, root)))
        {
            return CliPolicyDecision.Deny(
                CliPolicyViolation.ProtectedPathAccess,
                "The working directory may not be Windows or Program Files.");
        }

        string[] configuredSandboxRoots;
        _lock.EnterReadLock();
        try
        {
            configuredSandboxRoots = _sandboxRoots.ToArray();
        }
        finally
        {
            _lock.ExitReadLock();
        }

        var availableSandboxRoots = configuredSandboxRoots
            .Where(Directory.Exists)
            .Select(CanonicalizeExistingDirectory)
            .ToArray();
        if (!availableSandboxRoots.Any(root => IsWithin(workingDirectory, root)))
        {
            return CliPolicyDecision.Deny(
                CliPolicyViolation.WorkingDirectoryOutsideSandbox,
                "The working directory must be inside the temporary directory or a configured sandbox.");
        }

        string[] pathCandidates;
        try
        {
            pathCandidates = command.Arguments
                .SelectMany(argument => ExtractPathCandidates(argument, workingDirectory))
                .ToArray();
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException)
        {
            return CliPolicyDecision.Deny(
                CliPolicyViolation.PathArgumentOutsideSandbox,
                "A path argument could not be safely normalized.");
        }

        foreach (var candidate in pathCandidates)
        {
            string canonicalCandidate;
            try
            {
                canonicalCandidate = CanonicalizePotentialPath(candidate);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return CliPolicyDecision.Deny(
                    CliPolicyViolation.PathArgumentOutsideSandbox,
                    "An absolute path argument could not be safely normalized.");
            }

            if (protectedRoots.Any(root => IsWithin(canonicalCandidate, root)))
            {
                return CliPolicyDecision.Deny(
                    CliPolicyViolation.ProtectedPathAccess,
                    "Arguments may not access Windows or Program Files directories.");
            }

            if (!availableSandboxRoots.Any(root => IsWithin(canonicalCandidate, root)))
            {
                return CliPolicyDecision.Deny(
                    CliPolicyViolation.PathArgumentOutsideSandbox,
                    "Absolute path arguments must remain inside an approved sandbox.");
            }
        }

        return CliPolicyDecision.Permit(executable);
    }

    public void Dispose() => _lock.Dispose();

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static bool TryResolveExecutablePath(string executable, out string resolved)
    {
        try
        {
            resolved = ResolveExecutablePath(executable);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FileNotFoundException or NotSupportedException or PathTooLongException)
        {
            resolved = string.Empty;
            return false;
        }
    }

    private static string ResolveExecutablePath(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        var trimmed = executable.Trim();
        if (trimmed.IndexOfAny(['\r', '\n', '"', '\'', ';', '&', '|', '<', '>', '`']) >= 0)
        {
            throw new ArgumentException("Executable names cannot contain shell syntax.", nameof(executable));
        }

        if (Path.IsPathFullyQualified(trimmed) ||
            trimmed.Contains(Path.DirectorySeparatorChar) ||
            trimmed.Contains(Path.AltDirectorySeparatorChar))
        {
            return ResolveExistingFile(trimmed);
        }

        var pathValue = System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = OperatingSystem.IsWindows()
            ? (System.Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : new[] { string.Empty };
        if (Path.HasExtension(trimmed))
        {
            extensions = new[] { string.Empty };
        }

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var cleanDirectory = directory.Trim().Trim('"');
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(cleanDirectory, trimmed + extension.ToLowerInvariant());
                if (File.Exists(candidate))
                {
                    return ResolveExistingFile(candidate);
                }

                candidate = Path.Combine(cleanDirectory, trimmed + extension.ToUpperInvariant());
                if (File.Exists(candidate))
                {
                    return ResolveExistingFile(candidate);
                }
            }
        }

        throw new FileNotFoundException("The executable was not found on PATH.", trimmed);
    }

    private static string ResolveExistingFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The executable does not exist.", fullPath);
        }

        var info = new FileInfo(fullPath);
        return Path.GetFullPath(
            info.LinkTarget is null
                ? info.FullName
                : info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName);
    }

    private static string NormalizeSandboxRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var fullPath = Path.GetFullPath(root);
        return Directory.Exists(fullPath) ? CanonicalizeExistingDirectory(fullPath) : fullPath;
    }

    private static IEnumerable<string> GetProtectedRoots()
    {
        var candidates = new[]
        {
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows),
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles),
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86)
        };

        return candidates
            .Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(Path.GetFullPath)
            .Distinct(PathComparer);
    }

    private static HashSet<string> ExtractPathCandidates(string argument, string workingDirectory)
    {
        var candidates = new HashSet<string>(PathComparer);
        var trimmed = argument.Trim().Trim('"', '\'');
        if (trimmed.StartsWith('@'))
        {
            trimmed = trimmed[1..];
        }

        AddPotentialPath(trimmed, workingDirectory, candidates);
        var separator = trimmed.IndexOf('=');
        if (separator >= 0 && separator + 1 < trimmed.Length)
        {
            AddPotentialPath(
                trimmed[(separator + 1)..].Trim('"', '\''),
                workingDirectory,
                candidates);
        }

        var optionSeparator = trimmed.IndexOf(':');
        if (optionSeparator > 1 &&
            optionSeparator + 1 < trimmed.Length &&
            (trimmed.StartsWith('-') || trimmed.StartsWith('/')))
        {
            AddPotentialPath(
                trimmed[(optionSeparator + 1)..].Trim('"', '\''),
                workingDirectory,
                candidates);
        }

        foreach (Match match in WindowsRootedPathFragment.Matches(trimmed))
        {
            AddPotentialPath(match.Value.Trim(), workingDirectory, candidates);
        }

        return candidates;
    }

    private static void AddPotentialPath(
        string candidate,
        string workingDirectory,
        HashSet<string> paths)
    {
        candidate = candidate.Trim().Trim('"', '\'');
        if (candidate.Length == 0)
        {
            return;
        }

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile)
            {
                paths.Add(uri.LocalPath);
            }

            return;
        }

        if (Path.IsPathFullyQualified(candidate))
        {
            paths.Add(candidate);
            return;
        }

        // On Windows, paths such as /Windows/System32 are rooted on the current drive but
        // are not fully qualified. Resolve them before the slash-option check so they cannot
        // masquerade as command switches and escape the approved working-directory root.
        if (Path.IsPathRooted(candidate))
        {
            paths.Add(Path.GetFullPath(candidate, workingDirectory));
            return;
        }

        if (candidate.StartsWith('-') || candidate.StartsWith('/'))
        {
            return;
        }

        bool looksLikePath =
            candidate.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 ||
            candidate.StartsWith('.') ||
            Path.HasExtension(candidate);
        if (!looksLikePath)
        {
            string combinedSimpleName = Path.GetFullPath(candidate, workingDirectory);
            looksLikePath = File.Exists(combinedSimpleName) || Directory.Exists(combinedSimpleName);
        }

        if (looksLikePath)
        {
            paths.Add(Path.GetFullPath(candidate, workingDirectory));
        }
    }

    private static string CanonicalizePotentialPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath))
        {
            return CanonicalizeExistingDirectory(fullPath);
        }

        if (File.Exists(fullPath))
        {
            var existingFileParent = Path.GetDirectoryName(fullPath)
                ?? throw new InvalidOperationException("An existing file path did not have a parent directory.");
            var resolvedFileParent = CanonicalizeExistingDirectory(existingFileParent);
            var info = new FileInfo(Path.Combine(resolvedFileParent, Path.GetFileName(fullPath)));
            if (info.LinkTarget is not null)
            {
                return Path.GetFullPath(info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName);
            }

            return Path.GetFullPath(info.FullName);
        }

        var parent = Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
        {
            parent = Path.GetDirectoryName(parent);
        }

        if (string.IsNullOrEmpty(parent))
        {
            return fullPath;
        }

        var canonicalParent = CanonicalizeExistingDirectory(parent);
        var relative = Path.GetRelativePath(parent, fullPath);
        return Path.GetFullPath(Path.Combine(canonicalParent, relative));
    }

    private static string CanonicalizeExistingDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)!;
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(current, segment);
            var info = new DirectoryInfo(candidate);
            current = info.LinkTarget is null
                ? info.FullName
                : info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName;
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }

    private static bool IsWithin(string candidate, string root)
    {
        candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return PathComparer.Equals(candidate, root) ||
               candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }
}
