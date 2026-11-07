using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Controls;
using Avalonia.Threading;
using EasyFileSync.App.Views;
using EasyFileSync.Core.Engine;
using EasyFileSync.Core.Models;

namespace EasyFileSync.App.WebUi;

/// <summary>
/// Translates web-UI messages into engine calls and pushes state back to the page.
/// </summary>
public sealed class WebBridge
{
    private static readonly JsonSerializerOptions JsonOut = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly JobStore _store;
    private readonly SyncEngine _engine;
    private readonly AutoRunner? _auto;
    private readonly JobLogWriter? _logWriter;
    private readonly Func<WebViewHost?> _host;
    private readonly Func<Window?>? _window;

    private JobConfig? _selected;
    private SyncPlan? _lastPlan;
    private CancellationTokenSource? _cts;

    public WebBridge(JobStore store, SyncEngine engine, Func<WebViewHost?> host, AutoRunner? auto = null, JobLogWriter? logWriter = null, Func<Window?>? window = null)
    {
        _store = store;
        _engine = engine;
        _auto = auto;
        _logWriter = logWriter;
        _host = host;
        _window = window;
        _engine.ProgressChanged += OnProgress;

        _engine.StatusChanged += OnStatusChanged;
        _engine.LogAppended += OnLogAppended;
    }

    private WebViewHost? Host => _host();

    public void OnMessage(string json)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try { Handle(json); }
            catch (Exception ex) { Toast("Error: " + ex.Message); }
        });
    }

    private void Handle(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("type", out var typeProp)) return;
        var type = typeProp.GetString() ?? "";
        var payload = root.TryGetProperty("payload", out var p) ? p : default;

        switch (type)
        {
            case "ready":
                PushAll();
                try { _auto?.Reload(); } catch { }
                break;

            case "refresh":
                PushAll();
                break;

            case "selectJob":
                if (payload.ValueKind == JsonValueKind.Object &&
                    payload.TryGetProperty("name", out var nameEl))
                {
                    SelectJob(nameEl.GetString() ?? "");
                }
                break;

            case "newJob":
                BeginNewJob();
                break;

            case "openOptions":
                OpenOptions(payload);
                break;

            case "browseSide":
                if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("side", out var bs))
                {
                    var sideName = bs.GetString();
                    if (!string.IsNullOrEmpty(sideName))
                    {
                        _ = BrowseEndpointAsync(sideName);
                    }
                    else
                    {
                        _ = BrowseEndpointAsync("left");
                    }
                }
                break;

            case "saveJobOptions":
                SaveJobOptions(payload);
                break;

            case "deleteJob":
                DeleteSelected();
                break;

            case "saveJob":
                SaveJob(payload);
                break;

            case "analyze":
                _ = RunAsync(analyzeOnly: true);
                break;

            case "sync":
                _ = RunAsync(analyzeOnly: false);
                break;

            case "stop":
                _cts?.Cancel();
                break;

            case "__removed":
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "EasyFileSync-js.log"),
                        $"[{DateTime.Now:HH:mm:ss}] {type}: {payload.GetRawText()}\n");
                }
                catch { }
                break;

            case "listLocal":
                ListLocal(payload);
                break;

            case "setEndpointPath":
                SetEndpointPath(payload);
                break;

            case "browseEndpoint":
                if (payload.ValueKind == JsonValueKind.Object
                    && payload.TryGetProperty("side", out var epSide))
                {
                    _ = BrowseEndpointAsync(epSide.GetString() ?? "left");
                }
                break;

            case "browseFolder":
                if (payload.ValueKind == JsonValueKind.Object
                    && payload.TryGetProperty("side", out var sideEl))
                {
                    _ = BrowseFolderAsync(sideEl.GetString() ?? "left");
                }
                break;

            case "ftpBrowse":
                _ = FtpBrowseAsync(payload);
                break;

            case "createJob":
                CreateJob(payload);
                break;

            case "toggleItem":
                if (payload.ValueKind == JsonValueKind.Object
                    && payload.TryGetProperty("index", out var idxEl)
                    && idxEl.TryGetInt32(out var idx))
                {
                    ToggleItem(idx);
                }
                break;

            case "toggleAutorun":
                if (_auto is not null)
                {
                    _auto.SetSuspended(!_auto.IsSuspended);
                    Send("autorun", new { suspended = _auto.IsSuspended });
                }
                break;

            case "jsDebug":
            case "jsError":
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "EasyFileSync-js.log"),
                        $"[{DateTime.Now:HH:mm:ss}] {type}: {payload.GetRawText()}\n");
                }
                catch { }
                break;


        }
    }

    /* ---------------- state push ---------------- */

    private void PushAll()
    {
        if (_selected is null)
        {
            _selected = _store.Jobs.OrderBy(j => j.Name).FirstOrDefault();
        }

        PushUserInfo();
        PushJobs();
        PushSelected();
    }

    private void PushUserInfo()
    {
        var text = $"{Environment.MachineName} / {Environment.UserName}";
        Send("userInfo", new { text });
    }

    private void PushJobs()
    {
        var jobs = _store.Jobs
            .OrderBy(j => j.Name)
            .Select(j => new
            {
                name = j.Name,
                directionIcon = j.Direction switch
                {
                    JobDirection.LeftToRight => "→",
                    JobDirection.RightToLeft => "←",
                    _ => "⇄",
                },
                pathLine = $"{ShortPath(j.Left)}  {Arrow(j.Direction)}  {ShortPath(j.Right)}",
                statusText = string.IsNullOrEmpty(j.LastStatus)
                    ? (j.LastRunUtc == default ? "Click Analyze to start" : j.LastResult.ToString())
                    : j.LastStatus,
                lastRunText = j.LastRunUtc == default ? "" : j.LastRunUtc.ToLocalTime().ToString("MM-dd HH:mm"),
            })
            .ToList();

        Send("jobs", new { jobs });
    }

    private static string Arrow(JobDirection d) => d switch
    {
        JobDirection.LeftToRight => "→",
        JobDirection.RightToLeft => "←",
        _ => "⇄",
    };

    private static string ShortPath(EndpointConfig ep)
    {
        var p = ep.Kind == EndpointKind.Ftp
            ? $"ftp://{ep.Host}{EndpointConfig.NormalizeRemote(ep.Path)}"
            : ep.Path;
        if (p.Length > 42) p = p[..39] + "…";
        return p;
    }

    private void PushSelected()
    {
        if (_selected is null)
        {
            Send("selected", null);
            return;
        }

        Send("selected", new
        {
            name = _selected.Name,
            leftPath = DisplayPath(_selected.Left),
            rightPath = DisplayPath(_selected.Right),
            leftFreeText = FreeSpace(_selected.Left),
            rightFreeText = FreeSpace(_selected.Right),
            directionIconPath = _selected.Direction switch
            {
                JobDirection.LeftToRight => "svg/dirltor.svg",
                JobDirection.RightToLeft => "svg/dirrtol.svg",
                _ => "svg/dir2way.svg",
            },
            leftIconPath = KindIcon(_selected.Left.Kind),
            rightIconPath = KindIcon(_selected.Right.Kind),
        });

        Send("space", new
        {
            leftPercent = 8,
            rightPercent = 8,
            leftFreeText = FreeSpace(_selected.Left),
            rightFreeText = FreeSpace(_selected.Right),
        });
    }

    private static string KindIcon(EndpointKind kind) => kind switch
    {
        EndpointKind.Ftp => "svg/fs_ftp.svg",
        EndpointKind.Smb => "svg/fs_smb.svg",
        _ => "svg/fs_local.svg",
    };

    private static string DisplayPath(EndpointConfig ep) => ep.Kind switch
    {
        EndpointKind.Ftp => $"ftp://{ep.Host}{(ep.Port is 21 or 0 ? "" : ":" + ep.Port)}{EndpointConfig.NormalizeRemote(ep.Path)}",
        _ => ep.Path,
    };

    private static string FreeSpace(EndpointConfig ep)
    {
        try
        {
            if (ep.Kind == EndpointKind.Ftp) return "";
            var root = Path.GetPathRoot(ep.Path);
            if (string.IsNullOrEmpty(root)) return "";
            var drive = new DriveInfo(root);
            if (!drive.IsReady) return "";
            return $"Free: {drive.AvailableFreeSpace / 1024.0 / 1024 / 1024:0.##} GB  " +
                   $"Total: {drive.TotalSize / 1024.0 / 1024 / 1024:0.##} GB";
        }
        catch { return ""; }
    }

    /* ---------------- selection & jobs ---------------- */

    private void SelectJob(string name)
    {
        _selected = _store.Find(name);
        _lastPlan = null;
        Send("plan", new { rows = Array.Empty<object>() });
        Send("logClear", new { });
        PushSelected();
        Send("busy", new { busy = false });
    }

    private async void BeginNewJob()
    {
        if (_cts is not null)
        {
            return;
        }

        // Cari nama default job berikutnya yang belum terpakai (Job 1, Job 2, dst)
        var baseName = "Job";
        var candidateName = "Job 1";
        for (var i = 1; i < 1000; i++)
        {
            candidateName = $"{baseName} {i}";
            if (!_store.NameExists(candidateName))
            {
                break;
            }
        }

        Window? ownerWindow = null;
        if (_window is not null)
        {
            ownerWindow = _window();
        }

        if (ownerWindow is not null)
        {
            // Buka dialog New Job menggunakan native Avalonia Window murni
            var dialog = new NewJobWindow(candidateName, _store);
            var result = await dialog.ShowDialog<NewJobResult?>(ownerWindow);

            if (result is not null)
            {
                var newJob = new JobConfig
                {
                    Name = result.Name,
                    Direction = result.Direction,
                    Left = new EndpointConfig { Kind = EndpointKind.Local, Path = "" },
                    Right = new EndpointConfig { Kind = EndpointKind.Local, Path = "" }
                };

                _store.Add(newJob);
                _selected = _store.Find(result.Name);
                _lastPlan = null;

                Send("modalClose", new { });
                Send("plan", new { rows = Array.Empty<object>() });
                PushAll();
                Toast($"Job '{result.Name}' created.");
            }
            return;
        }

        // Fallback jika owner window tidak tersedia
        OpenEditor(null, true);
    }

    private void OpenOptions(JsonElement payload = default)
    {
        if (_selected is null || _cts is not null) return;

        string? side = null;
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("side", out var s)
            && s.ValueKind == JsonValueKind.String)
        {
            side = s.GetString();
        }

        OpenEditor(_selected, false, side);
    }

    private void OpenEditor(JobConfig? job, bool isNew, string? focusSide = null)
    {
        object dto = job is null
            ? new
            {
                name = SuggestName(),
                direction = 0,
                leftKind = 0, leftPath = "", leftHost = "", leftPort = 21, leftUser = "", leftTls = false,
                rightKind = 0, rightPath = "", rightHost = "", rightPort = 21, rightUser = "", rightTls = false,
                excludeHidden = true, excludeSystem = true, excludeTemp = true,
                propagateDeletes = true, include = "", exclude = "",
            }
            : new
            {
                name = job.Name,
                direction = (int)job.Direction,
                leftKind = (int)job.Left.Kind,
                leftPath = job.Left.Path,
                leftHost = job.Left.Host,
                leftPort = job.Left.Port,
                leftUser = job.Left.User,
                leftTls = job.Left.UseTls,
                rightKind = (int)job.Right.Kind,
                rightPath = job.Right.Path,
                rightHost = job.Right.Host,
                rightPort = job.Right.Port,
                rightUser = job.Right.User,
                rightTls = job.Right.UseTls,
                excludeHidden = job.Filter.ExcludeHidden,
                excludeSystem = job.Filter.ExcludeSystem,
                excludeTemp = job.Filter.ExcludeTemp,
                propagateDeletes = job.Options.PropagateDeletions,
                include = string.Join("\n", job.Filter.Include),
                exclude = string.Join("\n", job.Filter.Exclude),
            };

        SendRaw(new { type = "editor", isNew, focusSide, job = dto });
    }

    private string SuggestName()
    {
        var name = "My Sync";
        var i = 2;
        while (_store.NameExists(name)) name = $"My Sync ({i++})";
        return name;
    }

    private void SaveJob(JsonElement p)
    {
        string Str(string key) => p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        int Int(string key) => p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        bool Bool(string key) => p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;

        var isNew = Bool("isNew");
        var original = Str("originalName");
        var name = Str("name").Trim();
        if (name.Length == 0) return;

        var job = isNew ? new JobConfig() : _store.Find(original);
        if (job is null) return;

        if (isNew && _store.NameExists(name)) { Toast("Nama job sudah dipakai."); return; }
        if (!isNew && !string.Equals(name, original, StringComparison.OrdinalIgnoreCase) && _store.NameExists(name))
        {
            Toast("Nama job sudah dipakai.");
            return;
        }

        job.Name = name;
        job.Direction = (JobDirection)Int("direction");

        job.Left.Kind = (EndpointKind)Int("leftKind");
        job.Left.Path = Str("leftPath");
        job.Left.Host = Str("leftHost");
        job.Left.Port = Int("leftPort") is 0 ? 21 : Int("leftPort");
        job.Left.User = Str("leftUser");
        job.Left.Password = Str("leftPassword");
        job.Left.UseTls = Bool("leftTls");

        job.Right.Kind = (EndpointKind)Int("rightKind");
        job.Right.Path = Str("rightPath");
        job.Right.Host = Str("rightHost");
        job.Right.Port = Int("rightPort") is 0 ? 21 : Int("rightPort");
        job.Right.User = Str("rightUser");
        job.Right.Password = Str("rightPassword");
        job.Right.UseTls = Bool("rightTls");

        job.Filter.ExcludeHidden = Bool("excludeHidden");
        job.Filter.ExcludeSystem = Bool("excludeSystem");
        job.Filter.ExcludeTemp = Bool("excludeTemp");
        job.Filter.Include = SplitLines(Str("include"));
        job.Filter.Exclude = SplitLines(Str("exclude"));
        job.Options.PropagateDeletions = Bool("propagateDeletes");

        if (isNew) _store.Add(job);
        else _store.Update(job);

        _selected = _store.Find(name);
        _lastPlan = null;
        Send("modalClose", new { });
        Send("plan", new { rows = Array.Empty<object>() });
        PushAll();
        Toast(isNew ? $"Job '{name}' dibuat." : "Job disimpan.");
    }

    private static List<string> SplitLines(string text) =>
        text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private void DeleteSelected()
    {
        if (_selected is null) return;
        var name = _selected.Name;
        _store.Remove(name);
        _selected = _store.Jobs.OrderBy(j => j.Name).Select(j => j).FirstOrDefault();
        _lastPlan = null;
        Send("plan", new { rows = Array.Empty<object>() });
        PushAll();
        Toast($"Job '{name}' dihapus.");
    }

    /* ---------------- analyze / sync ---------------- */

    private async Task RunAsync(bool analyzeOnly)
    {
        if (_selected is null || _cts is not null) return;

        var job = _selected;
        var cts = new CancellationTokenSource();
        _cts = cts;

        Send("busy", new { busy = true });
        Send("logClear", new { });

        try
        {
            var status = new JobRunStatus { JobName = job.Name };
            status.State = JobRunState.Analyzing;
            Send("summary", new { summary = "Analyzing..." });

            var plan = await Task.Run(() => _engine.AnalyzePlan(job, status, null, cts.Token), cts.Token);
            _lastPlan = plan;

            Send("plan", new { rows = BuildRows(plan) });
            Send("summary", new
            {
                summary = plan.HasChanges
                    ? $"Analyze: {plan.ChangedCount} changes, {SyncEngine.FormatBytes(plan.BytesToCopy)} to copy"
                    : "Analyze: no changes",
            });

            if (analyzeOnly)
            {
                Send("busy", new { busy = false });
                return;
            }

            Send("summary", new { summary = "Syncing..." });
            var result = await Task.Run(() => _engine.RunAsync(plan, job, status, null, cts.Token), cts.Token);

            Send("summary", new { summary = result.Message });
            PushJobs();
            Toast(result.Message);

            // Keep the executed rows visible, marked with the outcome (like GoodSync does).
            Send("plan", new { rows = BuildRows(plan, result) });
        }
        catch (OperationCanceledException)
        {
            Send("summary", new { summary = "Stopped." });
            Toast("Dihentikan.");
        }
        catch (Exception ex)
        {
            Send("summary", new { summary = ex.Message });
            Toast("Error: " + ex.Message);
            AppendLog(new LogEntry { Level = LogLevel.Error, Message = ex.Message });
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            Send("busy", new { busy = false });
            PushJobs();
        }
    }

    private static List<object> BuildRows(SyncPlan plan, SyncResult? result = null)
    {
        var rows = new List<object>(plan.Actions.Count);

        foreach (var a in plan.Actions)
        {
            var isFolder = a.Left?.IsDirectory == true || a.Right?.IsDirectory == true;

            rows.Add(new
            {
                name = a.Name,
                path = a.RelativePath,
                depth = a.RelativePath.Count(c => c == '/'),
                isFolder,
                leftSize = a.Left is null ? "" : (isFolder ? "" : SyncEngine.FormatBytes(a.Left.Size)),
                rightSize = a.Right is null ? "" : (isFolder ? "" : SyncEngine.FormatBytes(a.Right.Size)),
                leftDate = Fmt(a.Left?.ModifiedUtc),
                rightDate = Fmt(a.Right?.ModifiedUtc),
                leftAction = a.Kind is ActionKind.CopyLeftToRight or ActionKind.DeleteLeft ? a.DirectionGlyph : "",
                rightAction = a.Kind is ActionKind.CopyRightToLeft or ActionKind.DeleteRight ? a.DirectionGlyph : "",
                centerAction = a.Kind == ActionKind.Conflict ? "≠" : "",
                leftActionClass = a.Kind is ActionKind.CopyLeftToRight or ActionKind.DeleteLeft
                    ? (a.Kind == ActionKind.DeleteLeft ? "act_delete" : "act_to_right") : "",
                rightActionClass = a.Kind is ActionKind.CopyRightToLeft or ActionKind.DeleteRight
                    ? (a.Kind == ActionKind.DeleteRight ? "act_delete" : "act_to_left") : "",
                selected = a.Selected,
                canToggle = a.IsWork,
                statusText = a.Kind is ActionKind.Skip ? "Up to date"
                    : (result is not null && result.Errors == 0 ? "Synced" : a.Label),
                rowClass = RowClass(a.Kind, result),
            });
        }

        return rows;
    }

    private static string Fmt(DateTime? utc) =>
        utc is null || utc.Value == default ? "" : utc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    private static string RowClass(ActionKind kind, SyncResult? result)
    {
        if (kind == ActionKind.Conflict) return "st_conflict";
        if (kind is ActionKind.CopyLeftToRight or ActionKind.CopyRightToLeft or ActionKind.DeleteLeft or ActionKind.DeleteRight)
            return result is null ? "st_changed" : "st_synced";
        return "";
    }

    /* ---------------- engine events ---------------- */

    private void OnStatusChanged(JobRunStatus status)
    {
        if (_selected is null || !string.Equals(status.JobName, _selected.Name, StringComparison.OrdinalIgnoreCase)) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (status.State == JobRunState.Syncing)
            {
                Send("progress", new { currentItem = status.CurrentItem, percent = status.Percent });
            }
        });
    }

    private void OnLogAppended(string jobName, LogEntry entry)
    {
        if (_selected is null || !string.Equals(jobName, _selected.Name, StringComparison.OrdinalIgnoreCase)) return;
        Dispatcher.UIThread.Post(() => AppendLog(entry));
    }

    private void AppendLog(LogEntry entry)
    {
        try { _logWriter?.Append(_selected?.Name ?? "", entry); } catch { }
        SendRaw(new
        {
            type = "log",
            payload = new
            {
                time = entry.Timestamp.ToString("HH:mm:ss"),
                level = entry.Level.ToString().ToLowerInvariant(),
                text = entry.Message,
            },
        });
    }

    private async Task DumpDomAsync()
    {
        try
        {
            var host = Host;
            if (host is null) return;
            var js = "JSON.stringify({rows:window.__state_rows||null,body:document.getElementById(\x27job_tree_body\x27).innerHTML.slice(0,800),err:window.__lastError||null})";
            var res = await host.EvalAsync(js);
            var file = Path.Combine(Path.GetTempPath(), "EasyFileSync-dom.json");
            await File.WriteAllTextAsync(file, res ?? "null");
        }
        catch (Exception ex)
        {
            try { await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "EasyFileSync-dom.json"), "ERR " + ex); } catch { }
        }
    }

    /* ---------------- per-item selection ---------------- */

    private void ToggleItem(int index)
    {
        if (_lastPlan is null) return;
        var work = _lastPlan.Actions.Where(a => a.IsWork).ToList();
        if (index < 0 || index >= work.Count) return;

        work[index].Selected = !work[index].Selected;
        Send("plan", new { rows = BuildRows(_lastPlan) });
        Send("summary", new
        {
            summary = _lastPlan.HasChanges
                ? $"{_lastPlan.ChangedCount} changes, {SyncEngine.FormatBytes(_lastPlan.BytesToCopy)} to copy"
                : "No changes",
        });
    }

    private void OnProgress(string jobName, SyncProgress p)
    {
        if (_selected is null || !string.Equals(jobName, _selected.Name, StringComparison.OrdinalIgnoreCase)) return;

        Dispatcher.UIThread.Post(() =>
        {
            Send("progress", new
            {
                percent = Math.Round(p.Fraction * 100, 1),
                currentItem = p.CurrentFile,
                bytesDone = p.BytesDone,
                bytesTotal = p.BytesTotal,
                speed = SyncEngine.FormatBytes((long)p.BytesPerSecond) + "/s",
                eta = p.Eta is null ? "" : $"ETA {p.Eta.Value:hh\\:mm\\:ss}",
                items = $"{p.ItemsDone}/{p.ItemsTotal}",
            });
        });
    }

    /* ---------------- background auto-run ---------------- */

    private readonly HashSet<string> _autoRunning = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _autoLock = new();

    public async Task RunJobInBackgroundAsync(string jobName)
    {
        var job = _store.Find(jobName);
        if (job is null) return;
        if (_engine.IsRunning(jobName)) return;

        lock (_autoLock)
        {
            if (!_autoRunning.Add(jobName)) return;
        }

        try
        {
            var status = new JobRunStatus { JobName = job.Name };
            var plan = await Task.Run(() => _engine.AnalyzePlan(job, status)).ConfigureAwait(false);

            if (plan.HasChanges)
            {
                await Task.Run(() => _engine.RunAsync(plan, job, status, null)).ConfigureAwait(false);
                PushJobs();
                if (_selected is not null && string.Equals(_selected.Name, job.Name, StringComparison.OrdinalIgnoreCase))
                {
                    _lastPlan = plan;
                    Send("plan", new { rows = BuildRows(plan) });
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog(new LogEntry { Level = LogLevel.Error, Message = "AutoRun: " + ex.Message });
        }
        finally
        {
            lock (_autoLock) { _autoRunning.Remove(jobName); }
        }
    }

    /* ---------------- folder browsing ---------------- */

    private async Task BrowseFolderAsync(string side)
    {
        try
        {
            var owner = _host() is { } h && Avalonia.Controls.TopLevel.GetTopLevel(h) is Avalonia.Controls.Window w
                ? w : null;

            var picker = new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = side == "left" ? "Choose LEFT folder" : "Choose RIGHT folder",
                AllowMultiple = false,
            };

            if (owner is null)
            {
                Toast("Folder browser only works from the main window.");
                return;
            }

            var folders = await owner.StorageProvider.OpenFolderPickerAsync(picker);
            if (folders.Count == 0) return;

            var path = folders[0].Path.LocalPath;
            if (string.IsNullOrEmpty(path)) return;

            Send("browseResult", new { side, path });
        }
        catch (Exception ex)
        {
            Toast("Browse failed: " + ex.Message);
        }
    }

    /// <summary>Lists a directory (local or FTP) so the UI can show a picker.</summary>
    private async Task FtpBrowseAsync(JsonElement p)
    {
        try
        {
            string Str(string k) => p.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            int Int(string k) => p.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

            var kind = (EndpointKind)Int("kind");
            var path = Str("path");
            var host = Str("host");
            var user = Str("user");
            var pass = Str("password");
            var port = Int("port") is 0 ? 21 : Int("port");
            var tls = p.TryGetProperty("tls", out var t) && t.ValueKind == JsonValueKind.True;

            var dirs = new List<string>();
            var files = new List<string>();

            if (kind == EndpointKind.Ftp)
            {
                if (string.IsNullOrWhiteSpace(host))
                {
                    Send("browseListing", new { error = "Host is empty", dirs, files });
                    return;
                }

                var cfg = new EndpointConfig
                {
                    Kind = EndpointKind.Ftp,
                    Host = host,
                    Port = port,
                    User = user,
                    Password = pass,
                    UseTls = tls,
                    Path = EndpointConfig.NormalizeRemote(path),
                };

                await using var ep = new EasyFileSync.Core.Storage.FtpStorageEndpoint(cfg);
                await ep.ConnectAsync().ConfigureAwait(false);
                var (d, f) = await ep.ListAsync().ConfigureAwait(false);
                dirs.AddRange(d);
                files.AddRange(f);
            }
            else if (kind == EndpointKind.Smb && string.IsNullOrWhiteSpace(path))
            {
                // Offer drives and typed UNC roots as starting points.
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (!drive.IsReady) continue;
                    dirs.Add(drive.Name.TrimEnd('\\'));
                }

                Send("browseListing", new { kind = (int)kind, path = "", dirs, files = Array.Empty<string>() });
                return;
            }
            else
            {
                var local = string.IsNullOrWhiteSpace(path)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                    : path;

                if (!Directory.Exists(local))
                {
                    Send("browseListing", new { error = "Folder does not exist: " + local, dirs, files });
                    return;
                }

                foreach (var d in Directory.EnumerateDirectories(local))
                {
                    var info = new DirectoryInfo(d);
                    if ((info.Attributes & FileAttributes.Hidden) != 0) continue;
                    dirs.Add(info.Name);
                }
                foreach (var f in Directory.EnumerateFiles(local)) files.Add(Path.GetFileName(f));
            }

            dirs.Sort(StringComparer.OrdinalIgnoreCase);
            files.Sort(StringComparer.OrdinalIgnoreCase);

            Send("browseListing", new { kind = (int)kind, path, dirs, files });
        }
        catch (Exception ex)
        {
            Send("browseListing", new { error = ex.Message, dirs = Array.Empty<string>(), files = Array.Empty<string>() });
        }
    }

    /* ---------------- create job (name + direction) ---------------- */

    private void CreateJob(JsonElement p)
    {
        string Str(string k) => p.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        int Int(string k) => p.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

        var name = Str("name").Trim();
        if (name.Length == 0) return;

        if (_store.NameExists(name))
        {
            Toast($"Job '{name}' sudah ada.");
            return;
        }

        var job = new JobConfig
        {
            Name = name,
            Direction = (JobDirection)Int("direction"),
            Left = new EndpointConfig { Kind = EndpointKind.Local, Path = "" },
            Right = new EndpointConfig { Kind = EndpointKind.Local, Path = "" },
        };

        _store.Add(job);
        _selected = _store.Find(name);
        _lastPlan = null;

        Send("modalClose", new { });
        Send("plan", new { rows = Array.Empty<object>() });
        PushAll();
        Toast($"Job '{name}' dibuat. Klik kotak kiri/kanan untuk memilih folder.");
    }

    /// <summary>Picks a folder for one side and saves it straight into the job.</summary>
    private async Task BrowseEndpointAsync(string side)
    {
        try
        {
            if (_selected is null) return;

            var host = _host();
            var owner = host is not null ? Avalonia.Controls.TopLevel.GetTopLevel(host) as Avalonia.Controls.Window : null;
            if (owner is null)
            {
                Toast("Cannot open the folder picker.");
                return;
            }

            var isLeft = string.Equals(side, "left", StringComparison.OrdinalIgnoreCase);
            var ep = isLeft ? _selected.Left : _selected.Right;

            // FTP/SMB endpoints keep their own credentials: only local folders use the OS picker.
            if (ep.Kind != EndpointKind.Local)
            {
                Send("openOptions", new { });
                Toast("Edit the " + (isLeft ? "LEFT" : "RIGHT") + " path in Job Options.");
                return;
            }

            var folders = await owner.StorageProvider.OpenFolderPickerAsync(
                new Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = isLeft ? "Select LEFT folder" : "Select RIGHT folder",
                    AllowMultiple = false,
                });

            if (folders.Count == 0) return;

            var path = folders[0].Path.LocalPath;
            if (string.IsNullOrWhiteSpace(path)) return;

            ep.Path = path;
            _store.Update(_selected);
            _lastPlan = null;

            Send("plan", new { rows = Array.Empty<object>() });
            PushAll();
            Toast((isLeft ? "Left" : "Right") + " folder set: " + path);
        }
        catch (Exception ex)
        {
            Toast("Browse failed: " + ex.Message);
        }
    }

    /* ---------------- in-app folder browser (This Computer) ---------------- */

    private void ListLocal(JsonElement p)
    {
        try
        {
            var path = p.TryGetProperty("path", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

            var dirs = new List<object>();
            var files = new List<string>();

            // Empty path => drive list ("This Computer").
            if (string.IsNullOrWhiteSpace(path))
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (!drive.IsReady) continue;
                    dirs.Add(new
                    {
                        name = drive.Name.TrimEnd('\\'),
                        label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                            ? drive.Name.TrimEnd('\\')
                            : drive.VolumeLabel,
                        full = drive.Name,
                        isDrive = true,
                    });
                }

                Send("localListing", new { path = "", parent = "", dirs, files = Array.Empty<string>() });
                return;
            }

            if (!Directory.Exists(path))
            {
                Send("localListing", new { error = "Folder not found: " + path, path, dirs = Array.Empty<object>(), files = Array.Empty<string>() });
                return;
            }

            var di = new DirectoryInfo(path);

            foreach (var d in di.GetDirectories())
            {
                if ((d.Attributes & FileAttributes.Hidden) != 0) continue;
                if ((d.Attributes & FileAttributes.System) != 0) continue;

                dirs.Add(new
                {
                    name = d.Name,
                    label = d.Name,
                    full = d.FullName,
                    isDrive = false,
                });
            }

            foreach (var f in di.GetFiles())
            {
                if ((f.Attributes & FileAttributes.Hidden) != 0) continue;
                files.Add(f.Name);
            }

            dirs = dirs.OrderBy(x => (string)x.GetType().GetProperty("name")!.GetValue(x)!, StringComparer.OrdinalIgnoreCase).ToList();
            files.Sort(StringComparer.OrdinalIgnoreCase);

            var parent = di.Parent?.FullName ?? "";

            Send("localListing", new
            {
                path = di.FullName,
                parent,
                root = di.Root.FullName,
                dirs,
                files,
            });
        }
        catch (Exception ex)
        {
            Send("localListing", new { error = ex.Message, dirs = Array.Empty<object>(), files = Array.Empty<string>() });
        }
    }

    private void SetEndpointPath(JsonElement p)
    {
        if (_selected is null) return;

        var side = p.TryGetProperty("side", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "left" : "left";
        var path = p.TryGetProperty("path", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        if (string.IsNullOrWhiteSpace(path)) return;

        var isLeft = string.Equals(side, "left", StringComparison.OrdinalIgnoreCase);
        var ep = isLeft ? _selected.Left : _selected.Right;

        ep.Kind = EndpointKind.Local;
        ep.Path = path;

        _store.Update(_selected);
        _lastPlan = null;

        Send("modalClose", new { });
        Send("plan", new { rows = Array.Empty<object>() });
        PushAll();
        Toast((isLeft ? "Left" : "Right") + " folder: " + path);
    }

    /// <summary>Saves the paths/filters edited in the Job Options dialog.</summary>
    private void SaveJobOptions(JsonElement p)
    {
        if (_selected is null) return;

        string Str(string k) => p.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        bool Bool(string k) => p.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;

        var left = Str("leftPath").Trim();
        var right = Str("rightPath").Trim();

        _selected.Left.Kind = EndpointKind.Local;
        _selected.Left.Path = left;

        _selected.Right.Kind = EndpointKind.Local;
        _selected.Right.Path = right;

        _selected.Options.PropagateDeletions = Bool("propagateDeletes");
        _selected.Filter.ExcludeHidden = Bool("excludeHidden");
        _selected.Filter.ExcludeSystem = Bool("excludeSystem");
        _selected.Filter.ExcludeTemp = Bool("excludeTemp");
        _selected.Filter.Include = SplitLines(Str("include"));
        _selected.Filter.Exclude = SplitLines(Str("exclude"));

        _store.Update(_selected);
        _lastPlan = null;

        Send("modalClose", new { });
        Send("plan", new { rows = Array.Empty<object>() });
        PushAll();
        Toast("Job options saved.");
    }

    /* ---------------- transport ---------------- */

    private void Send(string type, object? payload)
    {
        SendRaw(new { type, payload });
    }

    private void SendRaw(object message)
    {
        try
        {
            var host = Host;
            if (host is null) return;
            var json = JsonSerializer.Serialize(message, JsonOut);
            host.PostJson(json);
            if (json.Contains("\"type\":\"plan\""))
            {
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "EasyFileSync-send.log"),
                        $"[{DateTime.Now:HH:mm:ss}] plan len={json.Length} head={json[..Math.Min(200, json.Length)]}\n");
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "EasyFileSync-send.log"),
                    $"[{DateTime.Now:HH:mm:ss}] {ex.GetType().Name}: {ex.Message}\n");
            }
            catch { }
        }
    }

    private void Toast(string text) => Send("toast", new { text });
}