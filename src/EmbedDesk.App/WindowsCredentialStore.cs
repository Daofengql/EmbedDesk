using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace EmbedDesk.App;

internal sealed record StoredWindowsCredential(string UserName, string Secret);

/// <summary>Minimal wrapper for generic credentials owned by the current Windows user.</summary>
internal static class WindowsCredentialStore
{
    private const uint GenericCredential = 1;
    private const uint LocalMachinePersistence = 2;

    public static void Save(string target, string secret, string userName, string comment)
    {
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrEmpty(secret)) throw new ArgumentException("凭据目标和密钥不能为空。");
        var blob = Encoding.UTF8.GetBytes(secret);
        var handle = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, handle, blob.Length);
            var credential = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = target,
                UserName = userName,
                Comment = comment,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = handle,
                Persist = LocalMachinePersistence
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法保存凭据到 Windows 凭据管理器。");
        }
        finally
        {
            Marshal.Copy(new byte[blob.Length], 0, handle, blob.Length);
            Marshal.FreeHGlobal(handle);
            Array.Clear(blob);
        }
    }

    public static StoredWindowsCredential? Read(string target)
    {
        if (!CredRead(target, GenericCredential, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168) return null;
            throw new Win32Exception(error, "无法读取 Windows 凭据管理器中的凭据。");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlobSize > 64 * 1024) throw new InvalidDataException("凭据内容超过读取上限。");
            var secretBytes = new byte[checked((int)credential.CredentialBlobSize)];
            Marshal.Copy(credential.CredentialBlob, secretBytes, 0, secretBytes.Length);
            try { return new StoredWindowsCredential(credential.UserName ?? string.Empty, Encoding.UTF8.GetString(secretBytes)); }
            finally { Array.Clear(secretBytes); }
        }
        finally { CredFree(pointer); }
    }

    public static void Delete(string target)
    {
        if (!CredDelete(target, GenericCredential, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1168) throw new Win32Exception(error, "无法删除 Windows 凭据管理器中的凭据。");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref NativeCredential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll", SetLastError = false)] private static extern void CredFree(IntPtr buffer);
}
