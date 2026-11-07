using EasyFileSync.Core.Models;

namespace EasyFileSync.Core.Storage;

public sealed class LocalStorageEndpoint : IStorageEndpoint
{
    private readonly EndpointConfig _config;
    private readonly string _root;

    public LocalStorageEndpoint(EndpointConfig config)
    {
        _config = config;
        _root = config.Path ?? "";
    }

    public EndpointKind Kind => _config.Kind;
    public string DisplayName => _root;
    public string Root => _root;

    public Task ConnectAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_root))
            throw new StorageException("Folder path is empty.");

        if (_config.Kind == EndpointKind.Smb)
        {
            if (!SmbConnection.TryEnsure(_root, _config, out var err))
                throw new StorageException(err ?? $"Cannot connect to {_root}");
        }

        if (!Directory.Exists(_root))
            throw new StorageException($"Folder does not exist: {_root}");

        return Task.CompletedTask;
    }

    public Task<SideSnapshot> EnumerateAsync(FilterMatcher filter, IProgress<string>? progress, CancellationToken ct = default)
    {
        var snapshot = new SideSnapshot();
        var rootInfo = new DirectoryInfo(_root);
        if (!rootInfo.Exists) throw new StorageException($"Folder does not exist: {_root}");

        Walk(rootInfo, "", snapshot, filter, progress, ct);
        return Task.FromResult(snapshot);
    }

    private void Walk(DirectoryInfo dir, string relative, SideSnapshot snapshot, FilterMatcher filter, IProgress<string>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        FileSystemInfo[] children;
        try
        {
            children = dir.GetFileSystemInfos();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            return;
        }

        foreach (var child in children)
        {
            ct.ThrowIfCancellationRequested();

            var rel = string.IsNullOrEmpty(relative) ? child.Name : relative + "/" + child.Name;
            var isHidden = (child.Attributes & FileAttributes.Hidden) != 0;
            var isSystem = (child.Attributes & FileAttributes.System) != 0;

            if (child is DirectoryInfo sub)
            {
                var entry = new FileEntry(rel, true, 0, SafeTime(() => sub.LastWriteTimeUtc), isHidden, isSystem);
                if (filter.IsExcluded(rel, entry)) continue;

                snapshot.Put(entry);
                progress?.Report(rel);

                var isLink = (sub.Attributes & FileAttributes.ReparsePoint) != 0;
                if (!isLink) Walk(sub, rel, snapshot, filter, progress, ct);
            }
            else if (child is FileInfo file)
            {
                long size;
                DateTime modified;
                try
                {
                    size = file.Length;
                    modified = file.LastWriteTimeUtc;
                }
                catch (IOException)
                {
                    continue;
                }

                var entry = new FileEntry(rel, false, size, modified, isHidden, isSystem);
                if (filter.IsExcluded(rel, entry)) continue;

                snapshot.Put(entry);
                progress?.Report(rel);
            }
        }
    }

    private static DateTime SafeTime(Func<DateTime> getter)
    {
        try { return getter(); }
        catch { return default; }
    }

    private string Full(string relativePath)
    {
        var rel = (relativePath ?? "").Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        return rel.Length == 0 ? _root : Path.Combine(_root, rel);
    }

    public Task<bool> ExistsAsync(string relativePath, CancellationToken ct = default)
    {
        var path = Full(relativePath);
        return Task.FromResult(File.Exists(path) || Directory.Exists(path));
    }

    public Task CreateDirectoryAsync(string relativePath, CancellationToken ct = default)
    {
        var path = Full(relativePath);
        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        return Task.CompletedTask;
    }

    public Task DeleteFileAsync(string relativePath, CancellationToken ct = default)
    {
        var path = Full(relativePath);
        if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        return Task.CompletedTask;
    }

    public Task DeleteDirectoryAsync(string relativePath, CancellationToken ct = default)
    {
        var path = Full(relativePath);
        if (Directory.Exists(path)) Directory.Delete(path, true);
        return Task.CompletedTask;
    }

    public Task<bool> IsEmptyDirectoryAsync(string relativePath, CancellationToken ct = default)
    {
        var path = Full(relativePath);
        if (!Directory.Exists(path)) return Task.FromResult(false);
        try
        {
            return Task.FromResult(!Directory.EnumerateFileSystemEntries(path).Any());
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task<(IReadOnlyList<string> Directories, IReadOnlyList<string> Files)> ListAsync(
        string relativePath = "", CancellationToken ct = default)
    {
        var dirs = new List<string>();
        var files = new List<string>();

        var path = Full(relativePath);
        if (!Directory.Exists(path))
            throw new StorageException("Folder does not exist: " + path);

        foreach (var d in Directory.EnumerateDirectories(path))
        {
            var info = new DirectoryInfo(d);
            if ((info.Attributes & FileAttributes.Hidden) != 0) continue;
            dirs.Add(info.Name);
        }

        foreach (var f in Directory.EnumerateFiles(path)) files.Add(Path.GetFileName(f));

        dirs.Sort(StringComparer.OrdinalIgnoreCase);
        files.Sort(StringComparer.OrdinalIgnoreCase);

        return Task.FromResult(((IReadOnlyList<string>)dirs, (IReadOnlyList<string>)files));
    }

    public Task MoveAsync(string fromRelativePath, string toRelativePath, CancellationToken ct = default)
    {
        var src = Full(fromRelativePath);
        var dst = Full(toRelativePath);
        var dir = Path.GetDirectoryName(dst);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(dst)) File.Delete(dst);
        File.Move(src, dst, true);
        return Task.CompletedTask;
    }

    public Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct = default)
    {
        var path = Full(relativePath);
        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024, true);
        return Task.FromResult(stream);
    }

    public async Task WriteFileAsync(string relativePath, Stream content, long length, DateTime modifiedUtc, CancellationToken ct = default)
    {
        var path = Full(relativePath);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var temp = path + ".EasyFileSync-part";
        await using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
        {
            await content.CopyToAsync(fs, 128 * 1024, ct).ConfigureAwait(false);
        }

        if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }

        File.Move(temp, path);

        if (modifiedUtc != default)
        {
            try { File.SetLastWriteTimeUtc(path, modifiedUtc); } catch { }
        }
    }

    public async Task CopyToAsync(string relativePath, IStorageEndpoint target, string targetRelativePath, DateTime modifiedUtc, CancellationToken ct = default)
    {
        if (target is LocalStorageEndpoint local && Kind == EndpointKind.Local && local.Kind == EndpointKind.Local)
        {
            var src = Full(relativePath);
            var dst = local.Full(targetRelativePath);
            var dir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(src, dst, true);
            if (modifiedUtc != default)
            {
                try { File.SetLastWriteTimeUtc(dst, modifiedUtc); } catch { }
            }
            return;
        }

        await using var stream = await OpenReadAsync(relativePath, ct).ConfigureAwait(false);
        var length = stream.CanSeek ? stream.Length : 0;
        await target.WriteFileAsync(targetRelativePath, stream, length, modifiedUtc, ct).ConfigureAwait(false);
    }

    public Task<long> GetFreeSpaceAsync(CancellationToken ct = default)
    {
        try
        {
            var root = Path.GetPathRoot(_root);
            if (string.IsNullOrEmpty(root)) return Task.FromResult(0L);
            var drive = new DriveInfo(root);
            return Task.FromResult(drive.IsReady ? drive.AvailableFreeSpace : 0L);
        }
        catch
        {
            return Task.FromResult(0L);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
