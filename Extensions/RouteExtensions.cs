using MobySync.Helpers;
using MobySync.Models;

namespace MobySync.Extensions;

public static class RouteExtensions
{
    public static void MapUpdaterEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/status", (UpdateCoordinator updateCoordinator) =>
            Results.Ok(updateCoordinator.GetStatus()));

        endpoints.MapPost("/update/check", async (UpdateCoordinator updateCoordinator) =>
        {
            var checkSummary = await updateCoordinator.CheckForUpdates();
            return Results.Ok(checkSummary);
        });

        endpoints.MapPost("/update/trigger", async (UpdateCoordinator updateCoordinator) =>
        {
            var started = await updateCoordinator.TryStartManualUpdate(); 
            
            return started 
                ? Results.Accepted() 
                : Results.Conflict(new { status = "An update process is already running" });
        });

        endpoints.MapPost("/update/trigger/{containerName}", async (string containerName, UpdateCoordinator updateCoordinator, bool? includeDependencies) =>
        {
            var status = await updateCoordinator.TryStartTargetedUpdate(containerName, includeDependencies ?? true);

            return status switch
            {
                TargetedTriggerStatus.Started => Results.Accepted(value: new { status = "Targeted update started", container = containerName, includeDependencies = includeDependencies ?? true }),
                TargetedTriggerStatus.AlreadyRunning => Results.Conflict(new { status = "An update process is already running" }),
                TargetedTriggerStatus.ContainerNotFound => Results.NotFound(new { status = $"Running monitored container '{containerName}' was not found" }),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
            };
        });
    }
}
