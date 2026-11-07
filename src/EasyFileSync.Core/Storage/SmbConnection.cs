using System.Runtime.InteropServices;

namespace EasyFileSync.Core.Storage;

/// <summary>
/// Maps SMB/UNC shares using the Windows network provider (mpr.dll).
/// This gives a network path the same semantics as a local folder, including
/// credentials supplied by the user instead of the logged-on session.
/// </summary>
public static class SmbConnection
{
    private const int RESOURCETYPE_DISK = 0x00000001;
    private const int CONNECT_UPDATE_PROFILE = 0x00000001;
    private const int ERROR_ALREADY_ASSIGNED = 85;
    private const int ERROR_SESSION_CREDENTIAL_CONFLICT = 1219;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NETRESOURCE
    {
        public int dwScope;
        public int dwType;
        public int dwDisplayType;
        public int dwUsage;
        public string? lpLocalName;
        public string? lpRemoteName;
        public string? lpComment;
        public string? lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WNetAddConnection2W(ref NETRESOURCE netResource, string? password, string? username, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WNetCancelConnection2W(string name, int flags, bool force);

    public static bool IsUnc(string path) => path.StartsWith(@"\\", StringComparison.Ordinal);

    public static string ShareRoot(string uncPath)
    {
        if (!IsUnc(uncPath)) return uncPath;
        var parts = uncPath.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return uncPath;
        return $@"\\{parts[0]}\{parts[1]}";
    }

    /// <summary>
    /// Ensures the share is reachable, mapping it with the supplied credentials when needed.
    /// </summary>
    public static bool TryEnsure(string uncPath, Models.EndpointConfig config, out string? error)
    {
        error = null;
        if (!IsUnc(uncPath))
        {
            error = "SMB path must start with \\\\server\\share";
            return false;
        }

        var root = ShareRoot(uncPath);
        if (Directory.Exists(root)) return true;

        if (string.IsNullOrWhiteSpace(config.User))
        {
            error = $"Cannot access {root}. Provide SMB user name and password, or connect the share in Windows first.";
            return false;
        }

        var user = string.IsNullOrWhiteSpace(config.Domain) ? config.User : $"{config.Domain}\\{config.User}";

        var resource = new NETRESOURCE
        {
            dwType = RESOURCETYPE_DISK,
            lpRemoteName = root,
            lpLocalName = null,
            lpProvider = null,
        };

        var rc = WNetAddConnection2W(ref resource, config.Password, user, CONNECT_UPDATE_PROFILE);
        if (rc == 0) return true;

        if (rc is ERROR_ALREADY_ASSIGNED or ERROR_SESSION_CREDENTIAL_CONFLICT && Directory.Exists(root))
        {
            return true;
        }

        error = rc switch
        {
            5 => $"Access denied to {root} for user '{user}'.",
            53 => $"Network path not found: {root}",
            67 => $"Network name not found: {root}",
            86 => $"Invalid SMB password for user '{user}'.",
            1219 => $"A connection to {root} already exists with different credentials. Disconnect it first.",
            1326 => $"Logon failure for user '{user}'.",
            _ => $"Cannot map {root} (Win32 error {rc}).",
        };
        return false;
    }

    public static void Disconnect(string uncPath)
    {
        try { WNetCancelConnection2W(ShareRoot(uncPath), 0, true); }
        catch { /* best effort */ }
    }
}
