using System.Security.Authentication;
using EasyFileSync.Core.Models;
using FluentFTP;

namespace EasyFileSync.Core.Storage;

public sealed class FtpStorageEndpoint : IStorageEndpoint
{
    private readonly EndpointConfig _config;
    private AsyncFtpClient? _client;
    private string _root = "/";

    public FtpStorageEndpoint(EndpointConfig config)
    {
        _config = config;
        _root = EndpointConfig.NormalizeRemote(config.Path);
    }

    public EndpointKind Kind => EndpointKind.Ftp;
    public string DisplayName => _config.DisplayPath;

    private AsyncFtpClient Client => _client ?? throw new StorageException("FTP connection is not open.");

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var client = new AsyncFtpClient(
            _config.Host,
            _config.User,
            _config.Password,
            _config.Port <= 0 ? 21 : _config.Port);

        client.Config.DataConnectionType = _config.Passive
            ? FtpDataConnectionType.AutoPassive
            : FtpDataConnectionType.AutoActive;
        client.Config.EncryptionMode = _config.UseTls ? FtpEncryptionMode.Explicit : FtpEncryptionMode.None;
        client.Config.ValidateAnyCertificate = true;
        client.Config.SocketKeepAlive = true;
        client.Config.ConnectTimeout = 20000;
        client.Config.ReadTimeout = 30000;
        client.Config.DataConnectionConnectTimeout = 20000;
        client.Config.DataConnectionReadTimeout = 30000;
        client.Config.RetryAttempts = 1;
        client.Config.LogToConsole = false;

        try
        {
            await client.Connect(ct).ConfigureAwait(false);
        }
        catch (AuthenticationException)
        {
            await SafeDispose(client).ConfigureAwait(false);
            throw new StorageException($"FTP login rejected for user '{_config.User}'.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await SafeDispose(client).ConfigureAwait(false);
            throw new StorageException($"Cannot connect to FTP server {_config.Host}:{_config.Port}: {ex.Message}", ex);
        }

        try
        {
            if (!await client.DirectoryExists(_root, ct).ConfigureAwait(false))
            {
                throw new StorageException($"Remote folder not found: {_root}");
            }
        }
        catch (StorageException)
        {
            await SafeDispose(client).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await SafeDispose(client).ConfigureAwait(false);
            throw new StorageException($"Cannot open remote folder {_root}: {ex.Message}", ex);
        }

        _client = client;
    }

    public async Task<SideSnapshot> EnumerateAsync(FilterMatcher filter, IProgress<string>? progress, CancellationToken ct = default)
    {
        var snapshot = new SideSnapshot();
        var items = await Client.GetListing(_root, FtpListOption.Recursive | FtpListOption.SizeModify | FtpListOption.AllFiles, ct)
            .ConfigureAwait(false);

        var rootPrefix = _root.TrimEnd('/');
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Type == FtpObjectType.Link) continue;
            if (item.Name is "." or "..") continue;

            var full = item.FullName.Replace('\\', '/');
            if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rel = full[rootPrefix.Length..].TrimStart('/');
            if (rel.Length == 0) continue;

            var isDir = item.Type == FtpObjectType.Directory;
            var modified = item.Modified == DateTime.MinValue ? DateTime.UnixEpoch : item.Modified.ToUniversalTime();

            var entry = new FileEntry(
                rel,
                isDir,
                isDir ? 0 : item.Size,
                modified,
                item.Name.StartsWith('.'));

            if (filter.IsExcluded(rel, entry)) continue;
            snapshot.Put(entry);
            progress?.Report(rel);
        }

        return snapshot;
    }

    public async Task<bool> ExistsAsync(string relativePath, CancellationToken ct = default)
    {
        var remote = Remote(relativePath);
        if (await Client.FileExists(remote, ct).ConfigureAwait(false)) return true;
        return await Client.DirectoryExists(remote, ct).ConfigureAwait(false);
    }

    public Task CreateDirectoryAsync(string relativePath, CancellationToken ct = default) =>
        Client.CreateDirectory(Remote(relativePath), ct);

    public Task DeleteFileAsync(string relativePath, CancellationToken ct = default) =>
        Client.DeleteFile(Remote(relativePath), ct);

    public Task DeleteDirectoryAsync(string relativePath, CancellationToken ct = default) =>
        Client.DeleteDirectory(Remote(relativePath), ct);

    public async Task<bool> IsEmptyDirectoryAsync(string relativePath, CancellationToken ct = default)
    {
        try
        {
            var remote = Remote(relativePath);
            if (!await Client.DirectoryExists(remote, ct).ConfigureAwait(false)) return false;

            var items = await Client.GetListing(remote, FtpListOption.Size, ct).ConfigureAwait(false);
            foreach (var item in items)
            {
                if (item.Name == "." || item.Name == "..") continue;
                return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<(IReadOnlyList<string> Directories, IReadOnlyList<string> Files)> ListAsync(
        string relativePath = "", CancellationToken ct = default)
    {
        var dirs = new List<string>();
        var files = new List<string>();

        var remote = string.IsNullOrWhiteSpace(relativePath)
            ? _root
            : Remote(relativePath);

        var items = await Client.GetListing(remote, FtpListOption.Size | FtpListOption.Modify, ct)
            .ConfigureAwait(false);

        foreach (var item in items)
        {
            if (item.Name == "." || item.Name == "..") continue;
            if (item.Type == FtpObjectType.Link) continue;

            if (item.Type == FtpObjectType.Directory) dirs.Add(item.Name);
            else files.Add(item.Name);
        }

        dirs.Sort(StringComparer.OrdinalIgnoreCase);
        files.Sort(StringComparer.OrdinalIgnoreCase);

        return (dirs, files);
    }

    public async Task MoveAsync(string fromRelativePath, string toRelativePath, CancellationToken ct = default)
    {
        var from = Remote(fromRelativePath);
        var to = Remote(toRelativePath);

        var cut = to.LastIndexOf('/');
        if (cut > 0)
        {
            await Client.CreateDirectory(to[..cut], ct).ConfigureAwait(false);
        }

        if (await Client.FileExists(to, ct).ConfigureAwait(false))
        {
            await Client.DeleteFile(to, ct).ConfigureAwait(false);
        }

        await Client.Rename(from, to, ct).ConfigureAwait(false);
    }

    public async Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct = default)
    {
        var temp = Path.Combine(Path.GetTempPath(), "EasyFileSync-ftp-" + Guid.NewGuid().ToString("N"));
        var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024, true);
        try
        {
            var ok = await Client.DownloadStream(fs, Remote(relativePath), token: ct).ConfigureAwait(false);
            if (!ok) throw new StorageException($"FTP download failed: {relativePath}");
            fs.Position = 0;
            return fs;
        }
        catch
        {
            await fs.DisposeAsync().ConfigureAwait(false);
            TryDelete(temp);
            throw;
        }
    }

    public async Task WriteFileAsync(string relativePath, Stream content, long length, DateTime modifiedUtc, CancellationToken ct = default)
    {
        var remote = Remote(relativePath);
        var status = await Client.UploadStream(content, remote, FtpRemoteExists.OverwriteInPlace, true, null, ct).ConfigureAwait(false);
        if (status is not (FtpStatus.Success or FtpStatus.Skipped))
        {
            throw new StorageException($"FTP upload failed ({status}): {relativePath}");
        }

        if (modifiedUtc != default)
        {
            try { await Client.SetModifiedTime(remote, modifiedUtc.ToLocalTime(), ct).ConfigureAwait(false); }
            catch { /* not supported by every server */ }
        }
    }

    public async Task CopyToAsync(string relativePath, IStorageEndpoint target, string targetRelativePath, DateTime modifiedUtc, CancellationToken ct = default)
    {
        await using var stream = await OpenReadAsync(relativePath, ct).ConfigureAwait(false);
        var length = stream.CanSeek ? stream.Length : 0;
        await target.WriteFileAsync(targetRelativePath, stream, length, modifiedUtc, ct).ConfigureAwait(false);
    }

    public Task<long> GetFreeSpaceAsync(CancellationToken ct = default) => Task.FromResult(0L);

    private string Remote(string relativePath)
    {
        var rel = (relativePath ?? "").Replace('\\', '/').Trim('/');
        var root = _root.TrimEnd('/');
        return rel.Length == 0 ? (root.Length == 0 ? "/" : root) : $"{root}/{rel}";
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static async Task SafeDispose(AsyncFtpClient client)
    {
        try { await client.Disconnect(CancellationToken.None).ConfigureAwait(false); } catch { }
        try { client.Dispose(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is null) return;
        try { await _client.Disconnect(CancellationToken.None).ConfigureAwait(false); } catch { }
        try { _client.Dispose(); } catch { }
        _client = null;
    }
}