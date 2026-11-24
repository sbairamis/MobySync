namespace MobySync.Models;

public class StatusResponse
{
    public string State { get; init; } = "idle";
    public DateTime? NextScheduledRunUtc { get; init; }
    public string? Timezone { get; init; }
    public DateTime? LastRunUtc { get; init; }
    public UpdateSummary? LastSummary { get; init; }
    public IReadOnlyList<UpdateHistoryEntry> History { get; init; } = [];
}

public class UpdateHistoryEntry
{
    public DateTime TimestampUtc { get; init; }
    public string Trigger { get; init; } = string.Empty;
    public UpdateSummary Summary { get; init; } = new();
}

public enum TargetedTriggerStatus
{
    Started,
    AlreadyRunning,
    ContainerNotFound
}
