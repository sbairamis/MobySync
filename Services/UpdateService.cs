using MobySync.Helpers;

namespace MobySync.Services;

public class UpdateService(UpdateCoordinator updateCoordinator, ILogger<UpdateService> logger) : BackgroundService
{
    private int _updateHour = 23;
    
    private int _updateMinute = 30;
    
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("UPDATE_HOUR"), out var hourParsed))
            _updateHour = hourParsed;
        else
            logger.LogInformation("UPDATE_HOUR variable is missing or invalid. Defaulting to {Hour}", _updateHour);

        if (int.TryParse(Environment.GetEnvironmentVariable("UPDATE_MINUTE"), out var minuteParsed))
            _updateMinute = minuteParsed;
        else
            logger.LogInformation("UPDATE_MINUTE variable is missing or invalid. Defaulting to {Minute}", _updateMinute);

        var timezone = TimeZoneInfo.Local;
        var tzId = Environment.GetEnvironmentVariable("TZ");

        if (!string.IsNullOrWhiteSpace(tzId))
        {
            try
            {
                timezone = TimeZoneInfo.FindSystemTimeZoneById(tzId);
                logger.LogInformation("Timezone set to {Timezone}", timezone.Id);
            }
            catch (TimeZoneNotFoundException)
            {
                logger.LogWarning("Timezone '{TZ}' was not recognized. Defaulting to container local time ({Local})", tzId, TimeZoneInfo.Local.Id);
            }
        }
        else
        {
            logger.LogWarning("TZ is not set. Defaulting to container local time ({Local})", TimeZoneInfo.Local.Id);
        }

        var nowInTz = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timezone);
        logger.LogInformation("Current time: {Time:yyyy-MM-dd HH:mm:ss} ({Timezone})", nowInTz, timezone.Id);
        logger.LogInformation("Daily updates scheduled at {Hour:D2}:{Minute:D2} ({Timezone})", _updateHour, _updateMinute, timezone.Id);

        await updateCoordinator.SendStartupNotifications();

        while (!cancellationToken.IsCancellationRequested)
        {
            nowInTz = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timezone);

            var scheduledInTz = new DateTime(nowInTz.Year, nowInTz.Month, nowInTz.Day, _updateHour, _updateMinute, 0, DateTimeKind.Unspecified);

            if (nowInTz >= scheduledInTz)
                scheduledInTz = scheduledInTz.AddDays(1);

            logger.LogInformation("Next update at {ScheduledTime:yyyy-MM-dd HH:mm} ({Timezone})", scheduledInTz, timezone.Id);

            var scheduledUtc = TimeZoneInfo.ConvertTimeToUtc(scheduledInTz, timezone);
            updateCoordinator.SetNextScheduledRun(scheduledUtc, timezone.Id);

            var delay = scheduledUtc - DateTime.UtcNow;

            if (delay < TimeSpan.Zero)
                delay = TimeSpan.Zero;

            await Task.Delay(delay, cancellationToken);

            await updateCoordinator.ExecuteScheduledUpdate();
        }
    }
}