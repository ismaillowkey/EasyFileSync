using System.Collections.Concurrent;
using System.Security.Cryptography;
using EasyFileSync.Core.Models;
using EasyFileSync.Core.Storage;

namespace EasyFileSync.Core.Engine;

public sealed class SyncEngine
{
    private readonly JobStore _store;
    private readonly StateStore _state;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.OrdinalIgnoreCase);

    public event Action<JobRunStatus>? StatusChanged;
    public event Action<string, LogEntry>? LogAppended;
    public event Action<string, SyncProgress>? ProgressChanged;

    public SyncEngine(JobStore store, StateStore state)
    {
        _store = store;
        _state = state;
    }

    public bool IsRunning(string jobName) => _running.ContainsKey(jobName);

    public void Stop(string jobName)
    {
        if (_running.TryGetValue(jobName, out var cts))
        {
            try { cts.Cancel(); } catch { }
        }
    }

    /* ============================================================
     *  ANALYZE
     * ============================================================ */

    public SyncPlan AnalyzePlan(JobConfig job, JobRunStatus status, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (_running.ContainsKey(job.Name))
            throw new InvalidOperationException($"Job '{job.Name}' is already running.");

        var plan = new SyncPlan();
        var left = EndpointFactory.Create(job.Left);
        var right = EndpointFactory.Create(job.Right);

        try
        {
            status.State = JobRunState.Analyzing;
            status.Message = "Connecting…";
            Report(status);

            left.ConnectAsync(ct).GetAwaiter().GetResult();
            right.ConnectAsync(ct).GetAwaiter().GetResult();

            var filter = new FilterMatcher(job.Filter);

            progress?.Report("Scanning left side…");
            var leftProgress = new Progress<string>(rel =>
            {
                progress?.Report("left|" + rel);
            });
            plan.Left = left.EnumerateAsync(filter, leftProgress, ct).GetAwaiter().GetResult();

            progress?.Report("Scanning right side…");
            var rightProgress = new Progress<string>(rel =>
            {
                progress?.Report("right|" + rel);
            });
            plan.Right = right.EnumerateAsync(filter, rightProgress, ct).GetAwaiter().GetResult();

            progress?.Report("reading_state");
            var state = _state.Load(job.Name);

            progress?.Report("comparing");
            var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in plan.Left.Entries.Keys) paths.Add(k);
            foreach (var k in plan.Right.Entries.Keys) paths.Add(k);

            foreach (var rel in paths)
            {
                ct.ThrowIfCancellationRequested();
                var l = plan.Left.Get(rel);
                var r = plan.Right.Get(rel);
                plan.Actions.Add(Decide(job, rel, l, r, state, left, right, ct));
            }

            ApplyRenameDetection(job, plan, state);
            ApplyConflictPolicy(job, plan, left);

            status.ChangedCount = plan.ChangedCount;
            status.ConflictCount = plan.ConflictCount;
            status.BytesTotal = plan.BytesToCopy;
            status.State = JobRunState.Idle;
            status.Message = plan.HasChanges
                ? $"{plan.ChangedCount} changes"
                : "No changes";
            Report(status);

            plan.FinishedUtc = DateTime.UtcNow;
            return plan;
        }
        finally
        {
            left.DisposeAsync().AsTask().GetAwaiter().GetResult();
            right.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private SyncAction Decide(
        JobConfig job,
        string rel,
        FileEntry? l,
        FileEntry? r,
        Dictionary<string, StateEntry> state,
        IStorageEndpoint left,
        IStorageEndpoint right,
        CancellationToken ct)
    {
        var dir = job.Direction;
        var opt = job.Options;
        var known = state.TryGetValue(rel, out var prev);

        // =========================================================================
        // KASUS 1: File HANYA ADA DI KANAN (Left = null, Right != null)
        // =========================================================================
        if (l is null && r is not null)
        {
            if (dir == JobDirection.LeftToRight)
            {
                // Pada arah Left to Right:
                // Jika "Mirror to Right" (PropagateDeletions = true):
                // Kanan harus identik dengan kiri. File di kanan yang tidak ada di kiri WAJIB DIHAPUS.
                if (opt.PropagateDeletions)
                {
                    return new SyncAction { Kind = ActionKind.DeleteRight, RelativePath = rel, Right = r };
                }
                else
                {
                    // Jika "Update Right" (PropagateDeletions = false):
                    // Update HANYA menyalin file baru/lebih baru dari kiri ke kanan.
                    // File yatim (orphan) di sisi kanan TETAP DIPERTAHANKAN (TIDAK BOLEH DIHAPUS!).
                    return Skip(rel, l, r);
                }
            }
            else if (dir == JobDirection.RightToLeft)
            {
                // Pada arah Right to Left:
                // File ada di kanan (sumber) dan belum ada di kiri (tujuan).
                // Baik mode Update maupun Mirror akan menyalin file ini ke kiri.
                return new SyncAction { Kind = ActionKind.CopyRightToLeft, RelativePath = rel, Right = r };
            }
            else
            {
                // Pada arah Two-Way (Dua Arah):
                if (known && opt.PropagateDeletions)
                {
                    // File sebelumnya ada di kedua sisi tetapi dihapus di kiri -> teruskan penghapusan ke kanan
                    return new SyncAction { Kind = ActionKind.DeleteRight, RelativePath = rel, Right = r };
                }
                else
                {
                    // File baru dibuat di kanan -> salin ke kiri
                    return new SyncAction { Kind = ActionKind.CopyRightToLeft, RelativePath = rel, Right = r };
                }
            }
        }

        // =========================================================================
        // KASUS 2: File HANYA ADA DI KIRI (Left != null, Right = null)
        // =========================================================================
        if (l is not null && r is null)
        {
            if (dir == JobDirection.RightToLeft)
            {
                // Pada arah Right to Left:
                // Jika "Mirror to Left" (PropagateDeletions = true):
                // Kiri harus identik dengan kanan. File di kiri yang tidak ada di kanan WAJIB DIHAPUS.
                if (opt.PropagateDeletions)
                {
                    return new SyncAction { Kind = ActionKind.DeleteLeft, RelativePath = rel, Left = l };
                }
                else
                {
                    // Jika "Update Left" (PropagateDeletions = false):
                    // File yatim di sisi kiri TETAP DIPERTAHANKAN (TIDAK BOLEH DIHAPUS!).
                    return Skip(rel, l, r);
                }
            }
            else if (dir == JobDirection.LeftToRight)
            {
                // Pada arah Left to Right:
                // File ada di kiri (sumber) dan belum ada di kanan (tujuan).
                // Salin file ini dari kiri ke kanan.
                return new SyncAction { Kind = ActionKind.CopyLeftToRight, RelativePath = rel, Left = l };
            }
            else
            {
                // Pada arah Two-Way (Dua Arah):
                if (known && opt.PropagateDeletions)
                {
                    // File sebelumnya ada tetapi dihapus di kanan -> teruskan penghapusan ke kiri
                    return new SyncAction { Kind = ActionKind.DeleteLeft, RelativePath = rel, Left = l };
                }
                else
                {
                    // File baru dibuat di kiri -> salin ke kanan
                    return new SyncAction { Kind = ActionKind.CopyLeftToRight, RelativePath = rel, Left = l };
                }
            }
        }

        if (l is null || r is null)
        {
            return Skip(rel, l, r);
        }

        // =========================================================================
        // KASUS 3: File ADA DI KEDUA SISI (Left != null, Right != null)
        // =========================================================================
        if (l.IsDirectory != r.IsDirectory)
        {
            return new SyncAction { Kind = ActionKind.Conflict, RelativePath = rel, Left = l, Right = r };
        }

        if (l.IsDirectory)
        {
            return Skip(rel, l, r);
        }

        // Periksa apakah file identik berdasarkan mode perbandingan terpilih
        if (opt.CompareMode == CompareMode.SizeOnly)
        {
            if (l.Size == r.Size)
            {
                return Skip(rel, l, r);
            }
        }
        else if (opt.CompareMode == CompareMode.Checksum)
        {
            status_hash_check(rel);
            if (SameContent(left, right, rel, ct))
            {
                return Skip(rel, l, r);
            }
        }
        else
        {
            // Mode default: Ukuran sama DAN waktu modifikasi selisih <= 2 detik -> dianggap sama
            bool sameSize = (l.Size == r.Size);
            bool closeTime = Math.Abs((l.ModifiedUtc - r.ModifiedUtc).TotalSeconds) <= 2;

            if (sameSize && closeTime)
            {
                return Skip(rel, l, r);
            }

            // Jika ukuran sama tapi waktu berbeda dan diminta checksum
            bool useHash = opt.CompareAllByChecksum || (sameSize && !closeTime);
            if (useHash)
            {
                status_hash_check(rel);
                if (SameContent(left, right, rel, ct))
                {
                    return Skip(rel, l, r);
                }
            }
        }

        // --- File di kedua sisi BERBEDA ---

        if (dir == JobDirection.LeftToRight)
        {
            if (opt.PropagateDeletions)
            {
                // Mode "Mirror to Right":
                // Kanan dipaksa identik dengan kiri tanpa peduli apakah kanan lebih baru atau tidak.
                return new SyncAction { Kind = ActionKind.CopyLeftToRight, RelativePath = rel, Left = l, Right = r };
            }
            else
            {
                // Mode "Update Right":
                // HANYA timpa jika sisi Kiri LEBIH BARU daripada sisi Kanan!
                if (l.ModifiedUtc > r.ModifiedUtc)
                {
                    return new SyncAction { Kind = ActionKind.CopyLeftToRight, RelativePath = rel, Left = l, Right = r };
                }
                else if (r.ModifiedUtc > l.ModifiedUtc)
                {
                    // Sisi Kanan lebih baru, pada Update Right TIDAK BOLEH ditimpa dengan file lama!
                    return Skip(rel, l, r);
                }
                else
                {
                    // Waktu modifikasi identik tetapi ukuran/isi berbeda: salin dari kiri ke kanan
                    return new SyncAction { Kind = ActionKind.CopyLeftToRight, RelativePath = rel, Left = l, Right = r };
                }
            }
        }
        else if (dir == JobDirection.RightToLeft)
        {
            if (opt.PropagateDeletions)
            {
                // Mode "Mirror to Left":
                // Kiri dipaksa identik dengan kanan tanpa peduli apakah kiri lebih baru atau tidak.
                return new SyncAction { Kind = ActionKind.CopyRightToLeft, RelativePath = rel, Left = l, Right = r };
            }
            else
            {
                // Mode "Update Left":
                // HANYA timpa jika sisi Kanan LEBIH BARU daripada sisi Kiri!
                if (r.ModifiedUtc > l.ModifiedUtc)
                {
                    return new SyncAction { Kind = ActionKind.CopyRightToLeft, RelativePath = rel, Left = l, Right = r };
                }
                else if (l.ModifiedUtc > r.ModifiedUtc)
                {
                    // Sisi Kiri lebih baru, pada Update Left TIDAK BOLEH ditimpa dengan file lama!
                    return Skip(rel, l, r);
                }
                else
                {
                    // Waktu modifikasi identik tetapi ukuran/isi berbeda: salin dari kanan ke kiri
                    return new SyncAction { Kind = ActionKind.CopyRightToLeft, RelativePath = rel, Left = l, Right = r };
                }
            }
        }
        else
        {
            // Mode "Update Both" (Dua Arah / Two-Way):
            if (l.ModifiedUtc > r.ModifiedUtc)
            {
                // Kiri lebih baru -> salin ke kanan
                return new SyncAction { Kind = ActionKind.CopyLeftToRight, RelativePath = rel, Left = l, Right = r };
            }
            else if (r.ModifiedUtc > l.ModifiedUtc)
            {
                // Kanan lebih baru -> salin ke kiri
                return new SyncAction { Kind = ActionKind.CopyRightToLeft, RelativePath = rel, Left = l, Right = r };
            }
            else
            {
                // Konflik jika waktu sama tetapi isi berbeda
                return new SyncAction { Kind = ActionKind.Conflict, RelativePath = rel, Left = l, Right = r };
            }
        }
    }

    private void status_hash_check(string rel) { /* hook for per-file status */ }

    private static bool SameContent(IStorageEndpoint left, IStorageEndpoint right, string rel, CancellationToken ct)
    {
        try
        {
            using var a = left.OpenReadAsync(rel, ct).GetAwaiter().GetResult();
            using var b = right.OpenReadAsync(rel, ct).GetAwaiter().GetResult();
            return HashStream(a, ct) == HashStream(b, ct);
        }
        catch
        {
            return false;
        }
    }

    private static string HashStream(Stream s, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        var buf = new byte[81920];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.TransformBlock(buf, 0, n, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    /// <summary>Pairs a delete + a copy of identical size/time into a rename.</summary>
    private void ApplyRenameDetection(JobConfig job, SyncPlan plan, Dictionary<string, StateEntry> state)
    {
        if (!job.Options.DetectMovesAndRenames) return;

        var deletes = plan.Actions
            .Where(a => a.Selected && a.Kind is ActionKind.DeleteLeft or ActionKind.DeleteRight)
            .Where(a => a.Left?.IsDirectory != true && a.Right?.IsDirectory != true)
            .ToList();

        var copies = plan.Actions
            .Where(a => a.Selected && a.Kind is ActionKind.CopyLeftToRight or ActionKind.CopyRightToLeft)
            .Where(a => a.Left?.IsDirectory != true && a.Right?.IsDirectory != true)
            .ToList();

        foreach (var del in deletes)
        {
            // The side that still has the (now-renamed-away) file.
            var oldSide = del.Kind == ActionKind.DeleteLeft ? Side.Left : Side.Right;
            var gone = oldSide == Side.Left ? del.Left : del.Right;
            if (gone is null) continue;

            var match = copies.FirstOrDefault(c =>
            {
                var fresh = c.Kind == ActionKind.CopyLeftToRight ? c.Left : c.Right;
                return fresh is not null
                    && fresh.Size == gone.Size
                    && Math.Abs((fresh.ModifiedUtc - gone.ModifiedUtc).TotalSeconds) <= 2;
            });

            if (match is null) continue;

            // "delete old name + copy new name" becomes a single rename on that side.
            match.Kind = oldSide == Side.Left ? ActionKind.RenameOnLeft : ActionKind.RenameOnRight;
            match.OldRelativePath = del.RelativePath;
            del.Selected = false;
        }
    }

    private void ApplyConflictPolicy(JobConfig job, SyncPlan plan, IStorageEndpoint left)
    {
        var policy = job.Options.ConflictPolicy;
        if (policy == ConflictPolicy.Skip) return;

        foreach (var a in plan.Actions.Where(x => x.Kind == ActionKind.Conflict))
        {
            var l = a.Left;
            var r = a.Right;

            var winner = policy switch
            {
                ConflictPolicy.LeftWins => Side.Left,
                ConflictPolicy.RightWins => Side.Right,
                ConflictPolicy.NewerWins => (l?.ModifiedUtc ?? DateTime.MinValue) >= (r?.ModifiedUtc ?? DateTime.MinValue)
                    ? Side.Left : Side.Right,
                _ => Side.Left,
            };

            a.Kind = winner == Side.Left ? ActionKind.CopyLeftToRight : ActionKind.CopyRightToLeft;
        }
    }

    private static SyncAction Skip(string rel, FileEntry? l, FileEntry? r) =>
        new() { Kind = ActionKind.Skip, RelativePath = rel, Left = l, Right = r };

    /* ============================================================
     *  EXECUTE
     * ============================================================ */

    public async Task<SyncResult> RunAsync(
        SyncPlan plan,
        JobConfig job,
        JobRunStatus status,
        IProgress<SyncProgress>? progress,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = new SyncResult();
        var log = new Action<LogLevel, string>((lvl, msg) =>
        {
            var entry = new LogEntry { Level = lvl, Message = msg };
            result.Log.Add(entry);
            LogAppended?.Invoke(job.Name, entry);
        });

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running[job.Name] = cts;
        var token = cts.Token;

        var left = EndpointFactory.Create(job.Left);
        var right = EndpointFactory.Create(job.Right);

        try
        {
            status.State = JobRunState.Syncing;
            status.Message = "Connecting…";
            Report(status);

            await left.ConnectAsync(token).ConfigureAwait(false);
            await right.ConnectAsync(token).ConfigureAwait(false);

            log(LogLevel.Info, $"Sync started: {job.Name} ({job.Direction})");

            var work = plan.Actions.Where(a => a.Selected && a.IsWork).ToList();
            var totalBytes = work.Sum(a => a.Size);
            status.BytesTotal = totalBytes;
            long doneBytes = 0;
            var doneItems = 0;

            var swRate = System.Diagnostics.Stopwatch.StartNew();

            void PublishProgress(string item)
            {
                var elapsed = Math.Max(0.001, swRate.Elapsed.TotalSeconds);
                var rate = doneBytes / elapsed;
                status.BytesDone = doneBytes;
                status.BytesPerSecond = rate;
                status.CurrentItem = item;
                status.Percent = totalBytes > 0
                    ? Math.Clamp(doneBytes * 100.0 / totalBytes, 0, 100)
                    : (work.Count > 0 ? Math.Clamp(doneItems * 100.0 / work.Count, 0, 100) : 100);
                Report(status);

                var snap = new SyncProgress
                {
                    Phase = "Syncing",
                    CurrentFile = item,
                    ItemsDone = doneItems,
                    ItemsTotal = work.Count,
                    BytesDone = doneBytes,
                    BytesTotal = totalBytes,
                    BytesPerSecond = rate,
                };
                progress?.Report(snap);
                try { ProgressChanged?.Invoke(job.Name, snap); } catch { }
            }

            // --- 1. renames (no data transfer) ---
            foreach (var a in work.Where(x => x.IsRename))
            {
                token.ThrowIfCancellationRequested();
                PublishProgress(a.RelativePath);
                try
                {
                    var target = a.TargetSide == Side.Right ? right : left;
                    var oldRel = a.OldRelativePath ?? a.RelativePath;
                    var moved = false;

                    if (!string.Equals(oldRel, a.RelativePath, StringComparison.OrdinalIgnoreCase)
                        && await target.ExistsAsync(oldRel, token).ConfigureAwait(false))
                    {
                        if (job.Options.SaveBackups)
                            await BackupExistingAsync(target, a.RelativePath, job, token).ConfigureAwait(false);

                        if (await target.ExistsAsync(a.RelativePath, token).ConfigureAwait(false))
                            await target.DeleteFileAsync(a.RelativePath, token).ConfigureAwait(false);

                        await target.MoveAsync(oldRel, a.RelativePath, token).ConfigureAwait(false);
                        moved = true;
                    }

                    if (!moved)
                    {
                        // Rename could not be applied server-side: fall back to a copy.
                        if (job.Options.SaveBackups)
                            await BackupExistingAsync(target, a.RelativePath, job, token).ConfigureAwait(false);
                        await CopyOneAsync(a, job, left, right, token).ConfigureAwait(false);
                    }

                    result.Renamed++;
                    doneItems++;
                    doneBytes += a.Size;
                    log(LogLevel.Info, $"Rename {a.DirectionGlyph} {oldRel} -> {a.RelativePath}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    result.Errors++;
                    log(LogLevel.Error, $"Rename failed: {a.RelativePath}: {ex.Message}");
                }
            }

            // --- 2. deletions ---
            foreach (var a in work.Where(x => x.IsDelete))
            {
                token.ThrowIfCancellationRequested();
                PublishProgress(a.RelativePath);
                try
                {
                    var ep = a.Kind == ActionKind.DeleteLeft ? left : right;
                    var entry = a.Kind == ActionKind.DeleteLeft ? a.Left : a.Right;

                    if (job.Options.SaveBackups && entry?.IsDirectory != true)
                        await BackupExistingAsync(ep, a.RelativePath, job, token).ConfigureAwait(false);

                    if (entry?.IsDirectory == true)
                        await ep.DeleteDirectoryAsync(a.RelativePath, token).ConfigureAwait(false);
                    else
                        await ep.DeleteFileAsync(a.RelativePath, token).ConfigureAwait(false);

                    result.Deleted++;
                    doneItems++;
                    log(LogLevel.Info, $"Delete {a.DirectionGlyph} {a.RelativePath}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    result.Errors++;
                    log(LogLevel.Error, $"Delete failed: {a.RelativePath}: {ex.Message}");
                }
            }

            // --- 3. copies (directory creation first) ---
            var copies = work.Where(x => x.IsCopy).ToList();

            foreach (var a in copies.Where(x => x.Left?.IsDirectory == true || x.Right?.IsDirectory == true))
            {
                var target = a.TargetSide == Side.Right ? right : left;
                try
                {
                    await target.CreateDirectoryAsync(a.RelativePath, token).ConfigureAwait(false);
                    doneItems++;
                }
                catch (Exception ex)
                {
                    result.Errors++;
                    log(LogLevel.Error, $"Create folder failed: {a.RelativePath}: {ex.Message}");
                }
            }

            var semaphore = new SemaphoreSlim(Math.Clamp(job.Options.ParallelTransfers, 1, 10));
            var copiedCount = 0;
            var errorCount = 0;
            long movedBytes = 0;

            var tasks = copies
                .Where(a => a.Left?.IsDirectory != true && a.Right?.IsDirectory != true)
                .Select(async a =>
                {
                    await semaphore.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        PublishProgress(a.RelativePath);

                        var target = a.TargetSide == Side.Right ? right : left;

                        if (job.Options.SaveBackups)
                            await BackupExistingAsync(target, a.RelativePath, job, token).ConfigureAwait(false);

                        await CopyOneAsync(a, job, left, right, token).ConfigureAwait(false);

                        Interlocked.Increment(ref copiedCount);
                        Interlocked.Add(ref movedBytes, a.Size);
                        Interlocked.Increment(ref doneItems);
                        log(LogLevel.Info, $"Copy {a.DirectionGlyph} {a.RelativePath} ({FormatBytes(a.Size)})");

                        if (job.Options.MoveModeDeleteSource)
                        {
                            var src = a.SourceSide == Side.Left ? left : right;
                            try
                            {
                                await src.DeleteFileAsync(a.RelativePath, token).ConfigureAwait(false);
                                log(LogLevel.Info, $"Moved (source removed): {a.RelativePath}");
                            }
                            catch (Exception ex)
                            {
                                log(LogLevel.Error, $"Move cleanup failed: {a.RelativePath}: {ex.Message}");
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref errorCount);
                        log(LogLevel.Error, $"Copy failed: {a.RelativePath}: {ex.Message}");
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

            await Task.WhenAll(tasks).ConfigureAwait(false);
            semaphore.Dispose();

            result.Copied = copiedCount;
            result.Errors += errorCount;
            doneBytes = Interlocked.Read(ref movedBytes) + result.Renamed * 0;

            // --- 4. delete empty folders ---
            if (job.Options.DeleteEmptyFolders)
            {
                await RemoveEmptyFoldersAsync(left, plan.Left, log, token).ConfigureAwait(false);
                await RemoveEmptyFoldersAsync(right, plan.Right, log, token).ConfigureAwait(false);
            }

            status.Percent = 100;
            status.CurrentItem = "";
            sw.Stop();
            result.Elapsed = sw.Elapsed;
            result.BytesTransferred = doneBytes;

            SaveJournal(job, plan);

            result.Result = result.Errors > 0 ? JobResult.Errors
                : result.Copied + result.Deleted + result.Renamed > 0 ? JobResult.Synced
                : JobResult.NoChanges;

            result.Message = result.Errors > 0
                ? $"Completed with {result.Errors} error(s)"
                : result.Copied + result.Deleted + result.Renamed > 0
                    ? $"Copied {result.Copied}, deleted {result.Deleted}, renamed {result.Renamed}"
                    : "No changes";

            log(LogLevel.Info, $"Sync finished: {result.Message} in {result.Elapsed.TotalSeconds:F1}s");

            status.State = result.Errors > 0 ? JobRunState.Error : JobRunState.Done;
            status.Message = result.Message;
            Report(status);

            var stored = _store.Find(job.Name);
            if (stored is not null)
            {
                stored.LastRunUtc = DateTime.UtcNow;
                stored.LastResult = result.Result;
                stored.LastStatus = result.Message;
                _store.Update(stored);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            result.Result = JobResult.Stopped;
            result.Message = "Stopped by user";
            result.Elapsed = sw.Elapsed;
            status.State = JobRunState.Stopped;
            status.Message = result.Message;
            Report(status);
            log(LogLevel.Error, "Sync stopped by user");
            return result;
        }
        finally
        {
            _running.TryRemove(job.Name, out _);
            cts.Dispose();
            left.DisposeAsync().AsTask().GetAwaiter().GetResult();
            right.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private async Task CopyOneAsync(SyncAction a, JobConfig job, IStorageEndpoint left, IStorageEndpoint right, CancellationToken ct)
    {
        var source = a.SourceSide == Side.Left ? left : right;
        var target = a.TargetSide == Side.Right ? right : left;
        var mtime = a.Left?.ModifiedUtc ?? a.Right?.ModifiedUtc ?? default;

        await RetryAsync(async t =>
            await source.CopyToAsync(a.RelativePath, target, a.RelativePath, mtime, t).ConfigureAwait(false),
            job.Options.RetryCount, ct).ConfigureAwait(false);
    }

    private async Task BackupExistingAsync(IStorageEndpoint target, string rel, JobConfig job, CancellationToken ct)
    {
        if (!await target.ExistsAsync(rel, ct).ConfigureAwait(false)) return;
        if (job.Options.BackupRetentionDays <= 0) return;

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupRel = $"_EasyFileSync_/saved/{stamp}/{rel}";
        try
        {
            await target.MoveAsync(rel, backupRel, ct).ConfigureAwait(false);
        }
        catch
        {
            // Backup is best-effort; never block the sync because of it.
        }
    }

    private async Task RemoveEmptyFoldersAsync(
        IStorageEndpoint ep,
        SideSnapshot snapshot,
        Action<LogLevel, string> log,
        CancellationToken ct)
    {
        var dirs = snapshot.Folders
            .OrderByDescending(d => d.RelativePath.Length)
            .ToList();

        foreach (var d in dirs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await ep.IsEmptyDirectoryAsync(d.RelativePath, ct).ConfigureAwait(false))
                {
                    await ep.DeleteDirectoryAsync(d.RelativePath, ct).ConfigureAwait(false);
                    log(LogLevel.Info, $"Remove empty folder: {d.RelativePath}");
                }
            }
            catch { /* best effort */ }
        }
    }

    private void SaveJournal(JobConfig job, SyncPlan plan)
    {
        var state = new Dictionary<string, StateEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var a in plan.Actions)
        {
            if (!a.Selected) continue;

            // Deletions remove the entry; the file no longer exists on either side.
            if (a.IsDelete) continue;

            // Store the real per-side snapshot so the next run can tell
            // which side changed. Equal items must be journaled too, otherwise
            // two-way conflict detection has no baseline.
            var l = a.Left;
            var r = a.Right;

            if (a.Kind == ActionKind.Skip)
            {
                if (l is null || r is null) continue;
            }
            else if (a.IsCopy)
            {
                if (a.TargetSide == Side.Right) r = l;
                else l = r;
            }
            else if (a.IsRename)
            {
                if (a.TargetSide == Side.Right) r = l;
                else l = r;
            }
            else
            {
                continue;
            }

            if (l is null || r is null) continue;

            state[a.RelativePath] = new StateEntry
            {
                LeftSize = l.Size,
                LeftTimeUtc = l.ModifiedUtc,
                RightSize = r.Size,
                RightTimeUtc = r.ModifiedUtc,
                IsDirectory = l.IsDirectory,
            };
        }

        // Keep journal entries for untouched paths as well.
        var previous = _state.Load(job.Name);
        foreach (var kv in previous)
        {
            if (!state.ContainsKey(kv.Key)) state[kv.Key] = kv.Value;
        }

        _state.Save(job.Name, state);
    }

    private static async Task RetryAsync(Func<CancellationToken, Task> op, int retries, CancellationToken ct)
    {
        var attempts = 0;
        while (true)
        {
            try
            {
                await op(ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) when (attempts < Math.Max(0, retries))
            {
                attempts++;
                await Task.Delay(400 * attempts, ct).ConfigureAwait(false);
            }
        }
    }

    private void Report(JobRunStatus status)
    {
        try { StatusChanged?.Invoke(status.CloneStatus()); }
        catch { }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{v:0.##} {units[u]}";
    }
}
