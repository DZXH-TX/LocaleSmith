using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;
using Microsoft.Win32.SafeHandles;

namespace LocaleSmith.Infrastructure.Security;

public sealed partial class WindowsCredentialSecretStore : ISecretStore
{
    internal const string DefaultTargetPrefix = "LocaleSmith";
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaximumCredentialBlobSize = 2560;
    private readonly string _targetPrefix;

    public WindowsCredentialSecretStore(string targetPrefix = DefaultTargetPrefix)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Credential Manager is only available on Windows.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(targetPrefix);
        _targetPrefix = targetPrefix.Trim().TrimEnd('/');
    }

    public ValueTask<SecretValue?> ResolveAsync(string reference, CancellationToken cancellationToken = default)
    {
        var target = GetTarget(reference);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CredRead(target, CredentialTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == ErrorNotFound)
            {
                return ValueTask.FromResult<SecretValue?>(null);
            }

            throw new Win32Exception(error, "Failed to read a credential from Windows Credential Manager.");
        }

        using var credentialHandle = new SafeCredentialHandle(pointer);
        var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
        if (credential.CredentialBlobSize == 0)
        {
            return ValueTask.FromResult<SecretValue?>(new SecretValue([]));
        }

        var bytes = new byte[checked((int)credential.CredentialBlobSize)];
        try
        {
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            var characters = Encoding.UTF8.GetChars(bytes);
            try
            {
                return ValueTask.FromResult<SecretValue?>(new SecretValue(characters));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(characters.AsSpan()));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public ValueTask SetAsync(
        string reference,
        ReadOnlyMemory<char> secret,
        CancellationToken cancellationToken = default)
    {
        var target = GetTarget(reference);
        cancellationToken.ThrowIfCancellationRequested();
        if (secret.IsEmpty)
        {
            throw new ArgumentException("Secrets cannot be empty.", nameof(secret));
        }

        var bytes = new byte[Encoding.UTF8.GetByteCount(secret.Span)];
        Encoding.UTF8.GetBytes(secret.Span, bytes);
        if (bytes.Length > MaximumCredentialBlobSize)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new ArgumentException(
                $"The UTF-8 encoded credential exceeds {MaximumCredentialBlobSize} bytes.",
                nameof(secret));
        }

        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = target,
                CredentialBlobSize = checked((uint)bytes.Length),
                CredentialBlob = blob,
                Persist = CredentialPersistLocalMachine,
                UserName = System.Environment.UserName
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    "Failed to write a credential to Windows Credential Manager.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            ZeroUnmanagedMemory(blob, bytes.Length);
            Marshal.FreeHGlobal(blob);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> DeleteAsync(string reference, CancellationToken cancellationToken = default)
    {
        var target = GetTarget(reference);
        cancellationToken.ThrowIfCancellationRequested();
        if (CredDelete(target, CredentialTypeGeneric, 0))
        {
            return ValueTask.FromResult(true);
        }

        var error = Marshal.GetLastPInvokeError();
        if (error == ErrorNotFound)
        {
            return ValueTask.FromResult(false);
        }

        throw new Win32Exception(error, "Failed to delete a credential from Windows Credential Manager.");
    }

    private string GetTarget(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        if (reference.Length > 200 || !CredentialReferencePattern().IsMatch(reference))
        {
            throw new ArgumentException(
                "Credential references may contain only letters, digits, '.', '_', ':', '/' and '-'.",
                nameof(reference));
        }

        return $"{_targetPrefix}/{reference}";
    }

    private static void ZeroUnmanagedMemory(nint pointer, int length)
    {
        for (var index = 0; index < length; index++)
        {
            Marshal.WriteByte(pointer, index, 0);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._:/-]+$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex CredentialReferencePattern();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        public nint Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }

    private sealed class SafeCredentialHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeCredentialHandle(nint handle) : base(true) => SetHandle(handle);

        protected override bool ReleaseHandle()
        {
            CredFree(handle);
            return true;
        }
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out nint credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint credential);
}
