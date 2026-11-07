using EasyFileSync.Core.Models;

namespace EasyFileSync.Core.Engine;

/// <summary>
/// Runs jobs automatically: on start, on a timer, and when watched folders change.
/// </summary>
public sealed class AutoRunner : IDisposable
{
    private readonly JobStore _store;
    private readonly SyncEngine _engine;
    private readonly object _sync = new();

    private readonly List<JobTimer> _timers = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Dictionary<string, DateTime> _lastRun = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _pendingChange = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _connectJobStatus = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Timer _tick;

    private volatile bool _suspended;
    private bool _disposed;

    public event Action<string>? JobTriggered;
    public event Action<string, string>? Note;

    public bool IsSuspended => _suspended;

    public AutoRunner(JobStore store, SyncEngine engine)
    {
        _store = store;
        _engine = engine;
        _tick = new System.Threading.Timer(Tick, null, 5000, 5000);
    }

    public void SetSuspended(bool suspended)
    {
        _suspended = suspended;
        Note?.Invoke("autorun", suspended ? "AutoRun suspended" : "AutoRun resumed");
    }

    /// <summary>(Re)builds timers and watchers from the current job list.</summary>
    public void Reload()
    {
        lock (_sync)
        {
            StopAll();

            foreach (var job in _store.Jobs)
            {
                if (!job.Auto.AnyEnabled) continue;

                if (job.Auto.OnTimer)
                {
                    _timers.Add(new JobTimer
                    {
                        JobName = job.Name,
                        Interval = job.Auto.Interval,
                        DueUtc = DateTime.UtcNow + job.Auto.Interval,
                    });
                }

                if (job.Auto.OnClock)
                {
                    // Hitung jadwal harian berdasarkan jam dan menit lokal
                    var nowLocal = DateTime.Now;
                    int safeHour = Math.Clamp(job.Auto.ClockHour, 0, 23);
                    int safeMinute = Math.Clamp(job.Auto.ClockMinute, 0, 59);

                    var scheduledToday = new DateTime(
                        nowLocal.Year,
                        nowLocal.Month,
                        nowLocal.Day,
                        safeHour,
                        safeMinute,
                        0
                    );

                    DateTime nextScheduleLocal;
                    if (scheduledToday > nowLocal)
                    {
                        nextScheduleLocal = scheduledToday;
                    }
                    else
                    {
                        // Jika waktu hari ini sudah lewat, jadwalkan untuk besok
                        nextScheduleLocal = scheduledToday.AddDays(1);
                    }

                    _timers.Add(new JobTimer
                    {
                        JobName = job.Name,
                        Interval = TimeSpan.FromDays(1),
                        DueUtc = nextScheduleLocal.ToUniversalTime(),
                        IsDailyClock = true,
                        ClockHour = safeHour,
                        ClockMinute = safeMinute
                    });
                }

                if (job.Auto.OnStart)
                {
                    _timers.Add(new JobTimer
                    {
                        JobName = job.Name,
                        Interval = TimeSpan.Zero,
                        DueUtc = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(5, job.Auto.StartDelaySec)),
                        OneShot = true,
                    });
                }

                if (job.Auto.OnFileChange)
                {
                    AttachWatcher(job);
                }

                if (job.Auto.OnConnect)
                {
                    // Catat status ketersediaan awal endpoint
                    _connectJobStatus[job.Name] = IsJobEndpointsAvailable(job);
                }
            }
        }
    }

    private void AttachWatcher(JobConfig job)
    {
        foreach (var ep in new[] { job.Left, job.Right })
        {
            if (ep.Kind == EndpointKind.Ftp) continue;
            if (string.IsNullOrWhiteSpace(ep.Path) || !Directory.Exists(ep.Path)) continue;

            try
            {
                var watcher = new FileSystemWatcher(ep.Path)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                  NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 32 * 1024,
                };

                FileSystemEventHandler onChange = (_, _) => MarkChanged(job.Name, job.Auto.FileChangeDelaySec);
                RenamedEventHandler onRename = (_, _) => MarkChanged(job.Name, job.Auto.FileChangeDelaySec);

                watcher.Created += onChange;
                watcher.Changed += onChange;
                watcher.Deleted += onChange;
                watcher.Renamed += onRename;
                watcher.EnableRaisingEvents = true;

                _watchers.Add(watcher);
            }
            catch (Exception ex)
            {
                Note?.Invoke(job.Name, "Watcher failed: " + ex.Message);
            }
        }
    }

    private void MarkChanged(string jobName, int delaySec)
    {
        if (_suspended) return;
        lock (_sync)
        {
            _pendingChange[jobName] = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(2, delaySec));
        }
    }

    private void Tick(object? state)
    {
        if (_disposed || _suspended) return;

        try
        {
            var due = new List<string>();

            lock (_sync)
            {
                var now = DateTime.UtcNow;

                foreach (var t in _timers.ToList())
                {
                    if (t.DueUtc > now) continue;

                    due.Add(t.JobName);

                    if (t.OneShot || t.Interval <= TimeSpan.Zero)
                    {
                        _timers.Remove(t);
                    }
                    else if (t.IsDailyClock)
                    {
                        // Jadwalkan untuk hari berikutnya di jam dan menit yang sama
                        var localNow = DateTime.Now;
                        var nextDaily = new DateTime(localNow.Year, localNow.Month, localNow.Day, t.ClockHour, t.ClockMinute, 0).AddDays(1);
                        t.DueUtc = nextDaily.ToUniversalTime();
                    }
                    else
                    {
                        t.DueUtc = now + t.Interval;
                    }
                }

                foreach (var kv in _pendingChange.ToList())
                {
                    if (kv.Value > now) continue;

                    _pendingChange.Remove(kv.Key);

                    // Debounce: skip if it just ran.
                    if (_lastRun.TryGetValue(kv.Key, out var last) && (now - last).TotalSeconds < 5) continue;

                    due.Add(kv.Key);
                }

                // Periksa trigger OnConnect (koneksi drive / perangkat)
                foreach (var job in _store.Jobs)
                {
                    if (job.Auto.AnyEnabled && job.Auto.OnConnect)
                    {
                        bool currentAvailable = IsJobEndpointsAvailable(job);
                        if (_connectJobStatus.TryGetValue(job.Name, out bool previousAvailable))
                        {
                            // Jika sebelumnya tidak terhubung lalu sekarang terhubung
                            if (!previousAvailable && currentAvailable)
                            {
                                due.Add(job.Name);
                            }
                        }
                        _connectJobStatus[job.Name] = currentAvailable;
                    }
                }
            }

            foreach (var name in due.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (_engine.IsRunning(name)) continue;

                var job = _store.Find(name);
                if (job is null) continue;

                _lastRun[name] = DateTime.UtcNow;
                JobTriggered?.Invoke(name);
            }
        }
        catch
        {
            // Never let the scheduler die.
        }
    }

    private bool IsJobEndpointsAvailable(JobConfig job)
    {
        // Periksa apakah folder lokal Left dapat diakses
        if (job.Left.Kind == EndpointKind.Local)
        {
            if (!string.IsNullOrWhiteSpace(job.Left.Path))
            {
                if (!Directory.Exists(job.Left.Path))
                {
                    return false;
                }
            }
        }

        // Periksa apakah folder lokal Right dapat diakses
        if (job.Right.Kind == EndpointKind.Local)
        {
            if (!string.IsNullOrWhiteSpace(job.Right.Path))
            {
                if (!Directory.Exists(job.Right.Path))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private void StopAll()
    {
        _timers.Clear();

        foreach (var w in _watchers)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
        }

        _watchers.Clear();
        _pendingChange.Clear();
        _connectJobStatus.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_sync) StopAll();
        try { _tick.Dispose(); } catch { }
    }

    private sealed class JobTimer
    {
        public required string JobName { get; init; }
        public required TimeSpan Interval { get; init; }
        public DateTime DueUtc { get; set; }
        public bool OneShot { get; init; }
        public bool IsDailyClock { get; init; }
        public int ClockHour { get; init; }
        public int ClockMinute { get; init; }
    }
}

/// <summary>Writes job logs to %APPDATA%\EasyFileSync\logs\&lt;job&gt;.log with size-based rotation.</summary>
public sealed class JobLogWriter : IDisposable
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private readonly AppPaths _paths;
    private readonly object _sync = new();

    public JobLogWriter(AppPaths paths) => _paths = paths;

    public void Append(string jobName, LogEntry entry)
    {
        try
        {
            var safe = string.Concat(jobName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var file = Path.Combine(_paths.LogDir, safe + ".log");

            lock (_sync)
            {
                if (File.Exists(file) && new FileInfo(file).Length > MaxBytes)
                {
                    var old = file + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(file, old);
                }

                File.AppendAllText(file,
                    $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss} [{entry.Level}] {entry.Message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break a sync.
        }
    }

    public void Dispose() { }
}
