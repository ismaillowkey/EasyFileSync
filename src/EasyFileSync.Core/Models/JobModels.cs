using System.Text.Json.Serialization;

namespace EasyFileSync.Core.Models;

public enum EndpointKind
{
    Local = 0,
    Smb = 1,
    Ftp = 2,
}

public enum JobDirection
{
    TwoWay = 0,
    LeftToRight = 1,
    RightToLeft = 2,
}

public enum ConflictPolicy
{
    Skip = 0,
    NewerWins = 1,
    LeftWins = 2,
    RightWins = 3,
}

public enum JobRunState
{
    Idle,
    Waiting,
    Analyzing,
    Syncing,
    Stopped,
    Error,
    Done,
}

public enum JobResult
{
    None,
    NoChanges,
    Synced,
    Conflicts,
    Errors,
    Stopped,
}

public enum ItemAction
{
    None,
    CopyToRight,
    CopyToLeft,
    DeleteRight,
    DeleteLeft,
    CreateFolderRight,
    CreateFolderLeft,
    Conflict,
    Skip,
}

public enum ItemStatus
{
    Equal,
    NewLeft,
    NewRight,
    ChangedLeft,
    ChangedRight,
    DeletedLeft,
    DeletedRight,
    Conflict,
    Copying,
    Copied,
    Deleting,
    Deleted,
    Error,
}

public sealed class EndpointConfig
{
    public EndpointKind Kind { get; set; } = EndpointKind.Local;

    /// <summary>Local folder, UNC path, or remote folder on FTP.</summary>
    public string Path { get; set; } = "";

    public string Host { get; set; } = "";
    public int Port { get; set; } = 21;
    public string User { get; set; } = "";

    /// <summary>DPAPI-protected password. This is what gets serialized.</summary>
    public string PasswordProtected { get; set; } = "";

    [JsonIgnore]
    public string Password
    {
        get => EasyFileSync.Core.Storage.SecretProtector.Unprotect(PasswordProtected);
        set => PasswordProtected = EasyFileSync.Core.Storage.SecretProtector.Protect(value ?? "");
    }

    public string Domain { get; set; } = "";
    public bool UseTls { get; set; }
    public bool Passive { get; set; } = true;

    [JsonIgnore]
    public string DisplayPath => Kind switch
    {
        EndpointKind.Ftp => $"ftp://{Host}{(Port is 21 or 0 ? "" : ":" + Port)}{NormalizeRemote(Path)}",
        _ => Path,
    };

    public EndpointConfig Clone() => new()
    {
        Kind = Kind,
        Path = Path,
        Host = Host,
        Port = Port,
        User = User,
        Password = Password,
        Domain = Domain,
        UseTls = UseTls,
        Passive = Passive,
    };

    public static string NormalizeRemote(string path)
    {
        var p = (path ?? "").Replace('\\', '/').Trim();
        if (!p.StartsWith('/')) p = "/" + p;
        while (p.Contains("//")) p = p.Replace("//", "/");
        return p;
    }

    public string Describe() => Kind switch
    {
        EndpointKind.Ftp => $"FTP {Host}{NormalizeRemote(Path)}",
        EndpointKind.Smb => $"SMB {Path}",
        _ => Path,
    };
}

public enum CompareMode
{
    SizeAndTimestamp = 0,
    SizeOnly = 1,
    Checksum = 2,
}

public sealed class FilterOptions
{
    public List<string> Include { get; set; } = new();
    public List<string> Exclude { get; set; } = new();
    public bool ExcludeHidden { get; set; } = true;
    public bool ExcludeSystem { get; set; } = true;
    public bool ExcludeTemp { get; set; } = true;
    public bool ExcludeEmptyFolders { get; set; }

    public FilterOptions Clone() => new()
    {
        Include = new List<string>(Include),
        Exclude = new List<string>(Exclude),
        ExcludeHidden = ExcludeHidden,
        ExcludeSystem = ExcludeSystem,
        ExcludeTemp = ExcludeTemp,
        ExcludeEmptyFolders = ExcludeEmptyFolders,
    };
}

public sealed class AutoOptions
{
    public bool Enabled { get; set; }
    public bool OnFileChange { get; set; }
    public int FileChangeDelaySec { get; set; } = 10;
    public bool OnConnect { get; set; }
    public bool OnStart { get; set; }
    public int StartDelaySec { get; set; } = 30;
    public bool OnTimer { get; set; }
    public int TimerHours { get; set; }
    public int TimerMinutes { get; set; } = 30;
    public bool OnClock { get; set; }
    public int ClockHour { get; set; } = 2;
    public int ClockMinute { get; set; } = 0;
    public bool OnLogoff { get; set; }
    public bool LimitChanges { get; set; }
    public int LimitChangesPercent { get; set; } = 50;
    public bool Unattended { get; set; }

    [JsonIgnore]
    public bool AnyEnabled
    {
        get
        {
            if (!Enabled)
            {
                return false;
            }

            if (OnFileChange || OnConnect || OnStart || OnTimer || OnClock || OnLogoff)
            {
                return true;
            }

            return false;
        }
    }

    [JsonIgnore]
    public TimeSpan Interval
    {
        get
        {
            var span = TimeSpan.FromHours(Math.Max(0, TimerHours)) + TimeSpan.FromMinutes(Math.Max(0, TimerMinutes));
            if (span <= TimeSpan.Zero)
            {
                return TimeSpan.FromMinutes(30);
            }
            return span;
        }
    }

    public AutoOptions Clone() => (AutoOptions)MemberwiseClone();
}

public sealed class SyncOptions
{
    public bool PropagateDeletions { get; set; } = true;
    public bool CreateFolderIfNotFound { get; set; } = true;
    public bool FastNoState1Way { get; set; }
    public bool IgnoreChangesOnDestSide { get; set; }
    public bool MoveModeDeleteSource { get; set; }
    public bool DeleteEmptyFolders { get; set; }
    public bool SaveBackups { get; set; } = true;
    public int BackupRetentionDays { get; set; } = 30;
    public bool CopyCreateTime { get; set; }
    public bool CopyAttributes { get; set; } = true;
    public bool DetectMovesAndRenames { get; set; } = true;
    public CompareMode CompareMode { get; set; } = CompareMode.SizeAndTimestamp;
    public bool CompareAllByChecksum { get; set; }
    public bool UseChecksums { get; set; }
    public int ParallelTransfers { get; set; } = 3;
    public int RetryCount { get; set; } = 2;
    public int WaitForLocksMinutes { get; set; } = 1;
    public bool ShutdownAfterRun { get; set; }
    public ConflictPolicy ConflictPolicy { get; set; } = ConflictPolicy.Skip;

    public SyncOptions Clone() => (SyncOptions)MemberwiseClone();
}

public sealed class JobConfig
{
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public JobDirection Direction { get; set; } = JobDirection.TwoWay;
    public EndpointConfig Left { get; set; } = new();
    public EndpointConfig Right { get; set; } = new();
    public FilterOptions Filter { get; set; } = new();
    public AutoOptions Auto { get; set; } = new();
    public SyncOptions Options { get; set; } = new();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastRunUtc { get; set; }
    public JobResult LastResult { get; set; } = JobResult.None;
    public string LastStatus { get; set; } = "";

    [JsonIgnore]
    public string DirectionLabel => Direction switch
    {
        JobDirection.LeftToRight => "Backup Left to Right (1-way)",
        JobDirection.RightToLeft => "Backup Right to Left (1-way)",
        _ => "Synchronize 2-Way",
    };

    public JobConfig Clone() => new()
    {
        Name = Name,
        Group = Group,
        Direction = Direction,
        Left = Left.Clone(),
        Right = Right.Clone(),
        Filter = Filter.Clone(),
        Auto = Auto.Clone(),
        Options = Options.Clone(),
        CreatedUtc = CreatedUtc,
        LastRunUtc = LastRunUtc,
        LastResult = LastResult,
        LastStatus = LastStatus,
    };
}

public sealed record FileEntry(string RelativePath, bool IsDirectory, long Size, DateTime ModifiedUtc, bool IsHidden = false, bool IsSystem = false)
{
    [JsonIgnore]
    public string Name
    {
        get
        {
            var idx = RelativePath.LastIndexOf('/');
            return idx < 0 ? RelativePath : RelativePath[(idx + 1)..];
        }
    }

    [JsonIgnore]
    public int Depth => RelativePath.Count(c => c == '/');
}

public sealed class SideSnapshot
{
    public Dictionary<string, FileEntry> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<FileEntry> Files => Entries.Values.Where(e => !e.IsDirectory);
    public IEnumerable<FileEntry> Folders => Entries.Values.Where(e => e.IsDirectory);
    public long TotalBytes => Files.Sum(f => f.Size);
    public int TotalFiles => Files.Count();

    public FileEntry? Get(string relativePath) => Entries.TryGetValue(relativePath, out var e) ? e : null;

    public void Put(FileEntry entry) => Entries[entry.RelativePath] = entry;
}

public sealed class StateEntry
{
    public long LeftSize { get; set; }
    public DateTime LeftTimeUtc { get; set; }
    public long RightSize { get; set; }
    public DateTime RightTimeUtc { get; set; }
    public bool IsDirectory { get; set; }
}

public sealed class SyncItem
{
    public required string RelativePath { get; init; }
    public required bool IsDirectory { get; init; }
    public FileEntry? Left { get; init; }
    public FileEntry? Right { get; init; }
    public ItemAction Action { get; set; } = ItemAction.None;
    public ItemStatus Status { get; set; } = ItemStatus.Equal;
    public string Detail { get; set; } = "";
    public bool Resolved { get; set; }

    [JsonIgnore] public long LeftSize => Left?.Size ?? -1;
    [JsonIgnore] public long RightSize => Right?.Size ?? -1;
    [JsonIgnore] public DateTime? LeftTime => Left?.ModifiedUtc;
    [JsonIgnore] public DateTime? RightTime => Right?.ModifiedUtc;
    [JsonIgnore] public string Name
    {
        get
        {
            var idx = RelativePath.LastIndexOf('/');
            return idx < 0 ? RelativePath : RelativePath[(idx + 1)..];
        }
    }
}

public sealed class AnalysisResult
{
    public List<SyncItem> Items { get; } = new();
    public SideSnapshot Left { get; set; } = new();
    public SideSnapshot Right { get; set; } = new();
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime FinishedUtc { get; set; }

    public int ChangedCount => Items.Count(i => i.Action is not ItemAction.None and not ItemAction.Skip);
    public int ConflictCount => Items.Count(i => i.Action == ItemAction.Conflict);
    public int CopyCount => Items.Count(i => i.Action is ItemAction.CopyToRight or ItemAction.CopyToLeft);
    public int DeleteCount => Items.Count(i => i.Action is ItemAction.DeleteLeft or ItemAction.DeleteRight);
    public long BytesToCopy => Items.Where(i => i.Action is ItemAction.CopyToRight or ItemAction.CopyToLeft)
        .Sum(i => i.Action == ItemAction.CopyToRight ? i.Left?.Size ?? 0 : i.Right?.Size ?? 0);

    public bool HasChanges => ChangedCount > 0;
}

public sealed class LogLine
{
    public DateTime Time { get; init; } = DateTime.Now;
    public LogLevel Level { get; init; } = LogLevel.Info;
    public string Text { get; init; } = "";
}

public enum LogLevel
{
    Debug,
    Info,
    Error,
}
