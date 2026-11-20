using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using TapoCam.Core;

namespace TapoCam.App.Services;

internal sealed class CredentialStore : ISecretStore
{
    private const uint Generic = 1;
    private const uint LocalMachine = 2;
    private const int NotFound = 1168;

    public string? Get(Guid cameraId)
    {
        if (!CredRead(Target(cameraId), Generic, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == NotFound)
                return null;
            throw new Win32Exception(error, "Não foi possível ler a senha.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            return credential.BlobSize == 0 ? "" : Marshal.PtrToStringUni(credential.Blob, (int)credential.BlobSize / 2);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public void Set(Guid cameraId, string password)
    {
        var bytes = Encoding.Unicode.GetBytes(password);
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var credential = new Credential
            {
                Type = Generic, TargetName = Target(cameraId), Persist = LocalMachine,
                BlobSize = (uint)bytes.Length, Blob = pointer, UserName = "TapoCam"
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Não foi possível salvar a senha. Tente de novo.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            Marshal.FreeHGlobal(pointer);
        }
    }

    public void Delete(Guid cameraId)
    {
        if (!CredDelete(Target(cameraId), Generic, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != NotFound)
                throw new Win32Exception(error, "Não foi possível excluir a senha.");
        }
    }

    private static string Target(Guid cameraId) => $"TapoCam:{cameraId:D}";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public nint Blob;
        public uint Persist, AttributeCount;
        public nint Attributes;
        public string? TargetAlias, UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out nint credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint credential);
}
