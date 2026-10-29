using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
namespace HardwareLens.Windows;

/// <summary>Checks a file's embedded digital signature with WinVerifyTrust (cache only, no revocation or network lookups).</summary>
public static class Authenticode
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct WINTRUST_FILE_INFO { public uint cbStruct; public nint pcwszFilePath; public nint hFile; public nint pgKnownSubject; }
    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_DATA
    {
        public uint cbStruct; public nint pPolicyCallbackData, pSIPClientData; public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice; public nint pFile;
        public uint dwStateAction; public nint hWVTStateData, pwszURLReference; public uint dwProvFlags, dwUIContext; public nint pSignatureSettings;
    }
    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)] static extern int WinVerifyTrust(nint hwnd, ref Guid action, ref WINTRUST_DATA data);

    public sealed record Result(string Status, string? Publisher, bool Valid);

    public static Result Check(string path)
    {
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
        nint pPath = Marshal.StringToHGlobalUni(path); nint pFile = 0;
        try
        {
            var fi = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = pPath };
            pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>()); Marshal.StructureToPtr(fi, pFile, false);
            var d = new WINTRUST_DATA { cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(), dwUIChoice = 2, fdwRevocationChecks = 0, dwUnionChoice = 1, pFile = pFile, dwStateAction = 0, dwProvFlags = 0x1000 | 0x10 };
            int rc = WinVerifyTrust(0, ref action, ref d);
            string? pub = null;
            try { using var c = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)); pub = c.GetNameInfo(X509NameType.SimpleName, false); } catch { }
            return rc switch
            {
                0 => new Result("Digitally signed and the signature checks out", pub, true),
                unchecked((int)0x800B0100) => new Result("No embedded signature (Windows system files are often signed through a catalog instead)", null, false),
                unchecked((int)0x800B0101) or unchecked((int)0x800B0104) => new Result("Signed, but the certificate has expired or the signature does not verify", pub, false),
                unchecked((int)0x80096010) => new Result("Signature present but the file has been altered after signing", pub, false),
                _ => new Result("Signature could not be verified (code 0x" + rc.ToString("X8") + ")", pub, false)
            };
        }
        catch (Exception e) { return new Result("Signature check failed: " + e.Message, null, false); }
        finally { Marshal.FreeHGlobal(pPath); if (pFile != 0) Marshal.FreeHGlobal(pFile); }
    }
}
