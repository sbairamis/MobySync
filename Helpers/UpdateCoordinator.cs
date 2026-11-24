using MobySync.Interfaces;
using MobySync.Models;

namespace MobySync.Helpers;

public class UpdateCoordinator(DockerHelper dockerHelper, IEnumerable<INotificationHelper> notificationHelpers, ILogger<UpdateCoordinator> logger)
{
    private const int MaxHistoryEntries = 20;

    private readonly SemaphoreSlim _updateProcessLock = new(1, 1);
    private readonly object _statusLock = new();
    private readonly List<UpdateHistoryEntry> _history = [];
    private DateTime? _nextScheduledRunUtc;
    private string? _timezone;
    private DateTime? _lastRunUtc;
    private UpdateSummary? _lastSummary;

    public void SetNextScheduledRun(DateTime nextScheduledRunUtc, string timezone)
    {
        lock (_statusLock)
        {
            _nextScheduledRunUtc = nextScheduledRunUtc;
            _timezone = timezone;
        }
    }

    public StatusResponse GetStatus()
    {
        lock (_statusLock)
        {
            return new StatusResponse
            {
                State = _updateProcessLock.CurrentCount == 0 ? "running" : "idle",
                NextScheduledRunUtc = _nextScheduledRunUtc,
                Timezone = _timezone,
                LastRunUtc = _lastRunUtc,
                LastSummary = _lastSummary,
                History = _history.ToList()
            };
        }
    }

    public Task<UpdateCheckSummary> CheckForUpdates() => dockerHelper.CheckForAvailableUpdates();

    private void RecordRunSummary(UpdateSummary summary, string trigger)
    {
        var completedAt = DateTime.UtcNow;

        lock (_statusLock)
        {
            _lastRunUtc = completedAt;
            _lastSummary = summary;
            _history.Insert(0, new UpdateHistoryEntry
            {
                TimestampUtc = completedAt,
                Trigger = trigger,
                Summary = summary
            });

            if (_history.Count > MaxHistoryEntries)
            {
                _history.RemoveAt(_history.Count - 1);
            }
        }
    }

    private async Task<UpdateSummary> StartUpdateCycle(string trigger = "scheduled")
    {
        logger.LogInformation("Update cycle starting (trigger: {Trigger})", trigger);

        foreach (var helper in notificationHelpers.Where(h => h.IsConfigured))
        {
            try { await helper.SendUpdateStarted(); }
            catch (Exception ex) { logger.LogError(ex, "Failed to send update-started notification via {Provider}", helper.ProviderName); }
        }

        var summary = await dockerHelper.CheckForImageUpdates();
        RecordRunSummary(summary, trigger);

        foreach (var notificationHelper in notificationHelpers.Where(helper => helper.IsConfigured))
        {
            try
            {
                await notificationHelper.SendSummary(summary);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An unexpected error occurred while trying to send a notification via provider: " +
                                    "{Provider}", notificationHelper.ProviderName);
            }
        }

        var selfUpdateFailureSummary = await dockerHelper.CheckAndTriggerSelfUpdate();

        if (selfUpdateFailureSummary is not null)
        {
            foreach (var notificationHelper in notificationHelpers.Where(helper => helper.IsConfigured))
            {
                try
                {
                    await notificationHelper.SendSummary(selfUpdateFailureSummary);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to send self-update failure notification via {Provider}",
                        notificationHelper.ProviderName);
                }
            }
        }

        return summary;
    }

    private async Task<UpdateSummary> StartTargetedUpdateCycle(string containerName, bool includeDependencies)
    {
        logger.LogInformation("Targeted update cycle starting for {Container} (includeDependencies={IncludeDependencies})",
            containerName, includeDependencies);

        foreach (var helper in notificationHelpers.Where(h => h.IsConfigured))
        {
            try { await helper.SendUpdateStarted(); }
            catch (Exception ex) { logger.LogError(ex, "Failed to send update-started notification via {Provider}", helper.ProviderName); }
        }

        var summary = await dockerHelper.CheckForImageUpdatesForContainer(containerName, includeDependencies);
        RecordRunSummary(summary, $"targeted:{containerName}");

        foreach (var notificationHelper in notificationHelpers.Where(helper => helper.IsConfigured))
        {
            try
            {
                await notificationHelper.SendSummary(summary);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to send targeted update summary notification via {Provider}",
                    notificationHelper.ProviderName);
            }
        }

        return summary;
    }

    public async Task ExecuteSelfUpdate(string targetContainerId, string? targetImage = null)
    {
        logger.LogInformation("Executing self-update helper mode for target container {ContainerId}", targetContainerId);

        var summary = await dockerHelper.ExecuteSelfUpdateSwap(targetContainerId, targetImage);

        foreach (var notificationHelper in notificationHelpers.Where(helper => helper.IsConfigured))
        {
            try
            {
                await notificationHelper.SendSummary(summary);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to send self-update summary notification via {Provider}",
                    notificationHelper.ProviderName);
            }
        }
    }

    public async Task SendStartupNotifications()
    {
        foreach (var helper in notificationHelpers.Where(h => h.IsConfigured))
        {
            try
            {
                await helper.SendStartupTest();
                logger.LogInformation("Startup test sent via {Provider}", helper.ProviderName);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to send startup test via {Provider}", helper.ProviderName);
            }
        }
    }

    public async Task<bool> TryStartManualUpdate()
    {
        if (!await _updateProcessLock.WaitAsync(0))
        {
            return false;
        }
        
        _ = Task.Run(async () =>
        {
            try
            {
                await StartUpdateCycle("manual");
            }
            finally
            {
                _updateProcessLock.Release();
            }
        });
        
        return true;
    }

    public async Task<TargetedTriggerStatus> TryStartTargetedUpdate(string containerName, bool includeDependencies = true)
    {
        if (!await _updateProcessLock.WaitAsync(0))
        {
            return TargetedTriggerStatus.AlreadyRunning;
        }

        bool exists;
        try
        {
            exists = await dockerHelper.IsMonitoredContainerRunning(containerName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to verify monitored container {ContainerName} for targeted update", containerName);
            _updateProcessLock.Release();
            return TargetedTriggerStatus.ContainerNotFound;
        }

        if (!exists)
        {
            _updateProcessLock.Release();
            return TargetedTriggerStatus.ContainerNotFound;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await StartTargetedUpdateCycle(containerName, includeDependencies);
            }
            finally
            {
                _updateProcessLock.Release();
            }
        });

        return TargetedTriggerStatus.Started;
    }

    public async Task<UpdateSummary?> ExecuteTargetedUpdate(string containerName, bool includeDependencies = true)
    {
        await _updateProcessLock.WaitAsync();

        try
        {
            return await StartTargetedUpdateCycle(containerName, includeDependencies);
        }
        finally
        {
            _updateProcessLock.Release();
        }
    }
    
    public async Task ExecuteScheduledUpdate()
    {
        await _updateProcessLock.WaitAsync(); 
        
        try
        {
            await StartUpdateCycle("scheduled");
        }
        finally
        {
            _updateProcessLock.Release();
        }
    }
}