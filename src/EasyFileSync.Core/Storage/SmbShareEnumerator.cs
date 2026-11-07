using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace EasyFileSync.Core.Storage;

public static class SmbShareEnumerator
{
    private const int ERROR_SUCCESS = 0;
    private const uint STYPE_DISKTREE = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHARE_INFO_1
    {
        public string shi1_netname;
        public uint shi1_type;
        public string shi1_remark;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int NetShareEnum(
        string? servername,
        int level,
        out IntPtr bufptr,
        int prefmaxlen,
        out int entriesread,
        out int totalentries,
        ref int resume_handle);

    [DllImport("netapi32.dll", SetLastError = true)]
    private static extern int NetApiBufferFree(IntPtr buffer);

    /// <summary>
    /// Mengambil daftar nama folder share yang dipublikasikan oleh server SMB/Windows Share.
    /// </summary>
    public static List<string> EnumerateShares(string serverName)
    {
        var result = new List<string>();

        if (string.IsNullOrWhiteSpace(serverName))
        {
            return result;
        }

        // Bersihkan awalan backslash atau slash dari nama server/IP
        string cleanServer = serverName.TrimStart('\\', '/').Trim();
        int slashIdx = cleanServer.IndexOfAny(new[] { '\\', '/' });
        if (slashIdx >= 0)
        {
            cleanServer = cleanServer.Substring(0, slashIdx);
        }

        IntPtr bufPtr = IntPtr.Zero;
        int entriesRead = 0;
        int totalEntries = 0;
        int resumeHandle = 0;

        try
        {
            int ret = NetShareEnum(cleanServer, 1, out bufPtr, -1, out entriesRead, out totalEntries, ref resumeHandle);
            if (ret == ERROR_SUCCESS && bufPtr != IntPtr.Zero)
            {
                int itemSize = Marshal.SizeOf<SHARE_INFO_1>();
                for (int i = 0; i < entriesRead; i++)
                {
                    IntPtr currentItem = IntPtr.Add(bufPtr, i * itemSize);
                    var info = Marshal.PtrToStructure<SHARE_INFO_1>(currentItem);

                    // Ambil hanya share bertipe DISK_TREE (folder/drive)
                    uint shareType = info.shi1_type & 0xFFFF;
                    if (shareType == STYPE_DISKTREE)
                    {
                        string shareName = info.shi1_netname;
                        if (!string.IsNullOrWhiteSpace(shareName))
                        {
                            // Tampilkan share reguler (abaikan share sistem internal seperti IPC$)
                            if (!string.Equals(shareName, "IPC$", StringComparison.OrdinalIgnoreCase))
                            {
                                result.Add(shareName);
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Tangkap exception jika netapi32 gagal dihubungi
        }
        finally
        {
            if (bufPtr != IntPtr.Zero)
            {
                NetApiBufferFree(bufPtr);
            }
        }

        return result;
    }
}

