using System.Reflection;
using System.Runtime.InteropServices;

namespace JaxI18n.NativeInterop;

internal static class NativeCoreLibraryResolver
{
    private static readonly Lazy<bool> Initialization = new(
        Initialize,
        LazyThreadSafetyMode.ExecutionAndPublication);

    internal static void EnsureInitialized()
    {
        _ = Initialization.Value;
    }

    private static bool Initialize()
    {
        NativeLibrary.SetDllImportResolver(
            typeof(NativeCoreLibraryResolver).Assembly,
            ResolveLibrary);
        return true;
    }

    private static nint ResolveLibrary(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, NativeMethods.LibraryName, StringComparison.Ordinal))
        {
            return nint.Zero;
        }

        var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var runtimeIdentifier = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.X86 => "win-x86",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException(
                $"Unsupported process architecture: {RuntimeInformation.ProcessArchitecture}.")
        };

        var candidates = new[]
        {
            Path.Combine(baseDirectory, "jax_i18n_core.dll"),
            Path.Combine(baseDirectory, "runtimes", runtimeIdentifier, "native", "jax_i18n_core.dll")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
            {
                return handle;
            }
        }

        throw new DllNotFoundException(
            $"The native core was not found in an application-owned trusted location for '{runtimeIdentifier}'. " +
            "Default DLL probing and PATH fallback are intentionally disabled.");
    }
}
