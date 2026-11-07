using EasyFileSync.Core.Models;

namespace EasyFileSync.Core.Storage;

/// <summary>
/// Abstraction over one side of a job: a local folder, an SMB share or an FTP server.
/// All paths are relative to the endpoint root and use '/' as separator.
/// </summary>
public interface IStorageEndpoint : IAsyncDisposable
{
    EndpointKind Kind { get; }
    string DisplayName { get; }

    /// <summary>Opens the endpoint and validates that the root folder is reachable.</summary>
    Task ConnectAsync(CancellationToken ct = default);

    Task<SideSnapshot> EnumerateAsync(FilterMatcher filter, IProgress<string>? progress, CancellationToken ct = default);

    Task<bool> ExistsAsync(string relativePath, CancellationToken ct = default);

    Task CreateDirectoryAsync(string relativePath, CancellationToken ct = default);

    Task DeleteFileAsync(string relativePath, CancellationToken ct = default);

    Task DeleteDirectoryAsync(string relativePath, CancellationToken ct = default);

    /// <summary>True when the folder exists and contains no entries.</summary>
    Task<bool> IsEmptyDirectoryAsync(string relativePath, CancellationToken ct = default);

    /// <summary>Lists one level (folders, files) for the folder picker.</summary>
    Task<(IReadOnlyList<string> Directories, IReadOnlyList<string> Files)> ListAsync(
        string relativePath = "", CancellationToken ct = default);

    /// <summary>Moves/renames a file inside this endpoint (used for backups).</summary>
    Task MoveAsync(string fromRelativePath, string toRelativePath, CancellationToken ct = default);

    Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct = default);

    Task WriteFileAsync(string relativePath, Stream content, long length, DateTime modifiedUtc, CancellationToken ct = default);

    Task CopyToAsync(string relativePath, IStorageEndpoint target, string targetRelativePath, DateTime modifiedUtc, CancellationToken ct = default);

    Task<long> GetFreeSpaceAsync(CancellationToken ct = default);
}
