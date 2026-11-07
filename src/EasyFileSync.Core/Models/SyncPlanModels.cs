namespace EasyFileSync.Core.Models;

public enum ActionKind
{
    None = 0,
    CopyLeftToRight = 1,
    CopyRightToLeft = 2,
    DeleteLeft = 3,
    DeleteRight = 4,
    CreateDirectoryLeft = 5,
    CreateDirectoryRight = 6,
    DeleteDirectoryLeft = 7,
    DeleteDirectoryRight = 8,
    Conflict = 9,
    Skip = 10,
    RenameOnLeft = 11,
    RenameOnRight = 12,
}

public enum Side
{
    Left = 0,
    Right = 1,
}

public sealed class SyncAction
{
    public ActionKind Kind { get; set; }
    public string RelativePath { get; init; } = "";
    public FileEntry? Left { get; init; }
    public FileEntry? Right { get; init; }

    /// <summary>For rename actions: the path the item had before.</summary>
    public string? OldRelativePath { get; set; }

    /// <summary>User can tick items off in the UI; unticked actions are skipped.</summary>
    public bool Selected { get; set; } = true;

    public long Size => Kind == ActionKind.CopyLeftToRight ? Left?.Size ?? 0
        : Kind == ActionKind.CopyRightToLeft ? Right?.Size ?? 0 : 0;

    public Side SourceSide => Kind switch
    {
        ActionKind.CopyRightToLeft => Side.Right,
        ActionKind.RenameOnLeft => Side.Right,
        _ => Side.Left,
    };

    public Side TargetSide => Kind switch
    {
        ActionKind.CopyLeftToRight => Side.Right,
        ActionKind.RenameOnRight => Side.Right,
        ActionKind.RenameOnLeft => Side.Left,
        _ => Side.Left,
    };

    /// <summary>For rename actions: which side still holds the old name.</summary>
    public Side? OldPathSide => Kind switch
    {
        ActionKind.RenameOnRight => Side.Right,
        ActionKind.RenameOnLeft => Side.Left,
        _ => null,
    };

    public bool IsCopy => Kind is ActionKind.CopyLeftToRight or ActionKind.CopyRightToLeft;
    public bool IsDelete => Kind is ActionKind.DeleteLeft or ActionKind.DeleteRight;
    public bool IsRename => Kind is ActionKind.RenameOnLeft or ActionKind.RenameOnRight;
    public bool IsConflict => Kind == ActionKind.Conflict;
    public bool IsWork => IsCopy || IsDelete || IsRename;

    public string DirectionGlyph => Kind switch
    {
        ActionKind.CopyLeftToRight => "\u2192",
        ActionKind.CopyRightToLeft => "\u2190",
        ActionKind.DeleteLeft => "\u2190",
        ActionKind.DeleteRight => "\u2192",
        ActionKind.RenameOnLeft => "\u21ba",
        ActionKind.RenameOnRight => "\u21bb",
        ActionKind.Conflict => "\u2260",
        ActionKind.Skip => "=",
        _ => "",
    };

    public string Label => Kind switch
    {
        ActionKind.CopyLeftToRight => "Copy to right",
        ActionKind.CopyRightToLeft => "Copy to left",
        ActionKind.DeleteLeft => "Delete on left",
        ActionKind.DeleteRight => "Delete on right",
        ActionKind.RenameOnLeft => "Rename on left",
        ActionKind.RenameOnRight => "Rename on right",
        ActionKind.Conflict => "Conflict",
        ActionKind.Skip => "Skip",
        _ => "",
    };

    public string Name
    {
        get
        {
            var idx = RelativePath.LastIndexOf('/');
            return idx < 0 ? RelativePath : RelativePath[(idx + 1)..];
        }
    }
}

public sealed class SyncPlan
{
    public List<SyncAction> Actions { get; } = new();
    public SideSnapshot Left { get; set; } = new();
    public SideSnapshot Right { get; set; } = new();
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime FinishedUtc { get; set; }

    public IEnumerable<SyncAction> SelectedActions => Actions.Where(a => a.Selected);

    public int CopyCount => SelectedActions.Count(a => a.IsCopy);
    public int DeleteCount => SelectedActions.Count(a => a.IsDelete);
    public int RenameCount => SelectedActions.Count(a => a.IsRename);
    public int ConflictCount => Actions.Count(a => a.IsConflict);
    public int SkipCount => Actions.Count(a => a.Kind == ActionKind.Skip);
    public int ChangedCount => CopyCount + DeleteCount + RenameCount + ConflictCount;
    public long BytesToCopy => SelectedActions.Sum(a => a.Size);
    public bool HasChanges => CopyCount > 0 || DeleteCount > 0 || RenameCount > 0;
}

public sealed class SyncProgress
{
    public string Phase { get; init; } = "";
    public string CurrentFile { get; init; } = "";
    public int ItemsDone { get; init; }
    public int ItemsTotal { get; init; }
    public long BytesDone { get; init; }
    public long BytesTotal { get; init; }
    public double BytesPerSecond { get; init; }

    public double Fraction => BytesTotal > 0
        ? Math.Clamp((double)BytesDone / BytesTotal, 0, 1)
        : ItemsTotal > 0 ? Math.Clamp((double)ItemsDone / ItemsTotal, 0, 1) : 0;

    public TimeSpan? Eta
    {
        get
        {
            if (BytesPerSecond <= 0 || BytesTotal <= 0) return null;
            var left = BytesTotal - BytesDone;
            if (left <= 0) return TimeSpan.Zero;
            return TimeSpan.FromSeconds(left / BytesPerSecond);
        }
    }
}

public sealed class LogEntry
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public LogLevel Level { get; init; } = LogLevel.Info;
    public string Message { get; init; } = "";

    public override string ToString() => $"{Timestamp:HH:mm:ss}  {Message}";
}

public sealed class SyncResult
{
    public JobResult Result { get; set; } = JobResult.None;
    public int Copied { get; set; }
    public int Deleted { get; set; }
    public int Renamed { get; set; }
    public int Skipped { get; set; }
    public int Conflicts { get; set; }
    public int Errors { get; set; }
    public long BytesTransferred { get; set; }
    public TimeSpan Elapsed { get; set; }
    public string Message { get; set; } = "";
    public List<LogEntry> Log { get; } = new();
}

public sealed class JobRunStatus
{
    public string JobName { get; set; } = "";
    public JobRunState State { get; set; } = JobRunState.Idle;
    public string Message { get; set; } = "";
    public string CurrentItem { get; set; } = "";
    public double Percent { get; set; }
    public long BytesDone { get; set; }
    public long BytesTotal { get; set; }
    public double BytesPerSecond { get; set; }
    public int ChangedCount { get; set; }
    public int ErrorCount { get; set; }
    public int ConflictCount { get; set; }
    public int Copied { get; set; }
    public int Deleted { get; set; }

    public bool IsBusy => State is JobRunState.Analyzing or JobRunState.Syncing;

    public JobRunStatus CloneStatus() => new()
    {
        JobName = JobName,
        State = State,
        Message = Message,
        CurrentItem = CurrentItem,
        Percent = Percent,
        BytesDone = BytesDone,
        BytesTotal = BytesTotal,
        BytesPerSecond = BytesPerSecond,
        ChangedCount = ChangedCount,
        ErrorCount = ErrorCount,
        ConflictCount = ConflictCount,
        Copied = Copied,
        Deleted = Deleted,
    };
}
