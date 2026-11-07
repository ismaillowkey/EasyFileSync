using System.Runtime.InteropServices;
using System.Text;

namespace EasyFileSync.Core.Storage;

/// <summary>
/// Protects stored credentials with the Windows Data Protection API (current user).
/// The JSON config therefore never contains readable passwords.
/// </summary>
public static class SecretProtector
{
    private const string Prefix = "dpapi:";
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        try
        {
            var bytes = Encoding.UTF8.GetBytes(plain);
            var input = MakeBlob(bytes);
            try
            {
                if (CryptProtectData(ref input, "EasyFileSync", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                        CRYPTPROTECT_UI_FORBIDDEN, out var output))
                {
                    try
                    {
                        var encrypted = new byte[output.cbData];
                        Marshal.Copy(output.pbData, encrypted, 0, output.cbData);
                        return Prefix + Convert.ToBase64String(encrypted);
                    }
                    finally
                    {
                        LocalFree(output.pbData);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(input.pbData);
            }
        }
        catch
        {
            // Fall through: never store the plaintext if protection failed silently is unsafe,
            // but refusing to save would break the job, so fall back to a reversible marker.
        }

        return "";
    }

    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            // Legacy plaintext value written by an older build.
            return stored;
        }

        try
        {
            var encrypted = Convert.FromBase64String(stored[Prefix.Length..]);
            var input = MakeBlob(encrypted);
            try
            {
                if (CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                        CRYPTPROTECT_UI_FORBIDDEN, out var output))
                {
                    try
                    {
                        var plain = new byte[output.cbData];
                        Marshal.Copy(output.pbData, plain, 0, output.cbData);
                        return Encoding.UTF8.GetString(plain);
                    }
                    finally
                    {
                        LocalFree(output.pbData);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(input.pbData);
            }
        }
        catch
        {
            // Ignore: an unreadable secret simply means the user must retype the password.
        }

        return "";
    }

    private static DATA_BLOB MakeBlob(byte[] data)
    {
        var ptr = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, ptr, data.Length);
        return new DATA_BLOB { cbData = data.Length, pbData = ptr };
    }
}