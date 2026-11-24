namespace MobySync.Models;

public class UpdateCheckSummary
{
    public DateTime CheckedAtUtc { get; init; } = DateTime.UtcNow;
    public TimeSpan TotalDuration { get; set; }
    public List<ContainerCheckResult> UpdatesAvailable { get; } = [];
    public List<string> UpToDate { get; } = [];
    public List<ContainerUpdateResult> Skipped { get; } = [];
    public List<ContainerUpdateResult> Errors { get; } = [];
    public bool HasUpdates => UpdatesAvailable.Count > 0;
}

public class ContainerCheckResult
{
    public string ContainerName { get; init; } = string.Empty;
    public string ImageName { get; init; } = string.Empty;
    public string TargetTag { get; init; } = string.Empty;
    public string CurrentDigest { get; init; } = string.Empty;
    public string RemoteDigest { get; init; } = string.Empty;
}
