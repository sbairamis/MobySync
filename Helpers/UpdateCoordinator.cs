using MobySync.Interfaces;

namespace MobySync.Helpers;

public class UpdateCoordinator(DockerHelper dockerHelper, IEnumerable<INotificationHelper> notificationHelpers, ILogger<UpdateCoordinator> logger)
{
    private readonly SemaphoreSlim _updateProcessLock = new(1, 1);
    
    private async Task StartUpdateCycle()
    {
        logger.LogInformation("Update cycle starting");

        foreach (var helper in notificationHelpers.Where(h => h.IsConfigured))
        {
            try { await helper.SendUpdateStarted(); }
            catch (Exception ex) { logger.LogError(ex, "Failed to send update-started notification via {Provider}", helper.ProviderName); }
        }

        var summary = await dockerHelper.CheckForImageUpdates();

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
                await StartUpdateCycle();
            }
            finally
            {
                _updateProcessLock.Release();
            }
        });
        
        return true;
    }
    
    public async Task ExecuteScheduledUpdate()
    {
        await _updateProcessLock.WaitAsync(); 
        
        try
        {
            await StartUpdateCycle();
        }
        finally
        {
            _updateProcessLock.Release();
        }
    }
}