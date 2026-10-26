namespace CleanYourPC.Core.Models;

public class BackupEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SnapshotVersion { get; set; } = "v1.0";
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string RuleId { get; set; } = string.Empty;
    public string RuleName { get; set; } = string.Empty;
    public RuleActionType ActionType { get; set; }
    public string TargetPath { get; set; } = string.Empty;
    public string ValueName { get; set; } = string.Empty;
    public string? OriginalValue { get; set; }
    public bool OriginalExisted { get; set; }
    public string NewValue { get; set; } = string.Empty;
    public string? BackupData { get; set; }
    public bool SupportsRestore { get; set; }
}

public class BackupSnapshot
{
    public string Version { get; set; } = "v1.0";
    public string SnapshotId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string Description { get; set; } = string.Empty;
    public string OSVersion { get; set; } = string.Empty;
    public List<BackupEntry> Entries { get; set; } = new();
    public int ItemCount => Entries.Count;
}

public class ExecutionItemResult
{
    public string RuleId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string BeforeValue { get; set; } = string.Empty;
    public string AfterValue { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool RequiresReboot { get; set; }
}

public class CleanReport
{
    public DateTime GeneratedAt { get; set; } = DateTime.Now;
    public string OSVersion { get; set; } = string.Empty;
    public bool RanAsAdmin { get; set; }
    public int TotalScannedItems { get; set; }
    public int IssuesFound { get; set; }
    public int SucceededCount { get; set; }
    public int FailedCount { get; set; }
    public int SkippedOrUnconfirmedCount { get; set; }
    public List<ExecutionItemResult> Results { get; set; } = new();
    public List<string> ManualReviewItems { get; set; } = new();
}