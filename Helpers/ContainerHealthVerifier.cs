using Docker.DotNet;
using Docker.DotNet.Models;

namespace MobySync.Helpers;

public class ContainerHealthVerifier(
    IDockerClient dockerClient,
    ILogger logger,
    TimeSpan stabilityWindow,
    TimeSpan healthCheckTimeout,
    TimeSpan pollInterval)
{
    public async Task VerifyContainerStartupAsync(string containerId, string containerName)
    {
        var initialInspect = await dockerClient.Containers.InspectContainerAsync(containerId);

        EnsureProcessRunningAndNotRestarted(initialInspect, containerName, initialPid: 0, initialStartedAt: null);

        var hasHealthcheck = initialInspect.State?.Health is not null
            && !string.IsNullOrWhiteSpace(initialInspect.State.Health.Status)
            && !string.Equals(initialInspect.State.Health.Status, "none", StringComparison.OrdinalIgnoreCase);

        if (hasHealthcheck)
        {
            logger.LogInformation("Container {Container} defines a Docker HEALTHCHECK — monitoring health status (timeout: {Timeout}s)",
                containerName, healthCheckTimeout.TotalSeconds);

            await VerifyWithDockerHealthcheckAsync(containerId, containerName, initialInspect);
        }
        else
        {
            logger.LogInformation("Container {Container} has no Docker HEALTHCHECK — monitoring process stability (PID/restarts) for {Window}s",
                containerName, stabilityWindow.TotalSeconds);

            await VerifyProcessStabilityAsync(containerId, containerName, initialInspect);
        }
    }

    private async Task VerifyWithDockerHealthcheckAsync(
        string containerId,
        string containerName,
        ContainerInspectResponse currentInspect)
    {
        var initialPid = currentInspect.State?.Pid ?? 0;
        var initialStartedAt = currentInspect.State?.StartedAt;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        while (true)
        {
            EnsureProcessRunningAndNotRestarted(currentInspect, containerName, initialPid, initialStartedAt);

            var healthStatus = currentInspect.State?.Health?.Status;

            if (string.Equals(healthStatus, "healthy", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("Container {Container} reported healthy after {Elapsed:F1}s",
                    containerName, stopwatch.Elapsed.TotalSeconds);
                return;
            }

            if (string.Equals(healthStatus, "unhealthy", StringComparison.OrdinalIgnoreCase))
            {
                var lastLog = currentInspect.State?.Health?.Log?.LastOrDefault()?.Output?.Trim();
                var detail = string.IsNullOrWhiteSpace(lastLog) ? string.Empty : $" (last output: {lastLog})";
                throw new InvalidOperationException(
                    $"Container {containerName} reported unhealthy status{detail}");
            }

            if (stopwatch.Elapsed >= healthCheckTimeout)
            {
                throw new InvalidOperationException(
                    $"Container {containerName} healthcheck timed out after {healthCheckTimeout.TotalSeconds:F0}s (last status: '{healthStatus}')");
            }

            await Task.Delay(pollInterval);
            currentInspect = await dockerClient.Containers.InspectContainerAsync(containerId);
        }
    }

    private async Task VerifyProcessStabilityAsync(
        string containerId,
        string containerName,
        ContainerInspectResponse initialInspect)
    {
        var initialPid = initialInspect.State?.Pid ?? 0;
        var initialStartedAt = initialInspect.State?.StartedAt;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        while (stopwatch.Elapsed < stabilityWindow)
        {
            var remaining = stabilityWindow - stopwatch.Elapsed;
            var delay = remaining < pollInterval ? remaining : pollInterval;

            if (delay > TimeSpan.Zero)
                await Task.Delay(delay);

            var currentInspect = await dockerClient.Containers.InspectContainerAsync(containerId);
            EnsureProcessRunningAndNotRestarted(currentInspect, containerName, initialPid, initialStartedAt);
        }
    }

    public static void EnsureProcessRunningAndNotRestarted(
        ContainerInspectResponse inspect,
        string containerName,
        long initialPid,
        string? initialStartedAt)
    {
        var state = inspect.State;
        if (state is null)
            throw new InvalidOperationException($"Container {containerName} returned null state during inspection");

        if (state.OOMKilled)
            throw new InvalidOperationException($"Container {containerName} was OOM-killed (exit code: {state.ExitCode})");

        if (state.Dead)
            throw new InvalidOperationException($"Container {containerName} entered 'dead' state (exit code: {state.ExitCode})");

        if (state.Restarting)
            throw new InvalidOperationException($"Container {containerName} crashed and is restarting (exit code: {state.ExitCode}, restart count: {inspect.RestartCount})");

        if (!state.Running)
            throw new InvalidOperationException($"Container {containerName} crashed after starting, exit Code: {state.ExitCode}");

        if (inspect.RestartCount > 0)
            throw new InvalidOperationException($"Container {containerName} crashed and was restarted by restart policy (restart count: {inspect.RestartCount}, last exit code: {state.ExitCode})");

        if (initialPid > 0 && state.Pid > 0 && state.Pid != initialPid)
            throw new InvalidOperationException($"Container {containerName} main process restarted unexpectedly (PID changed from {initialPid} to {state.Pid})");

        if (!string.IsNullOrWhiteSpace(initialStartedAt)
            && !string.IsNullOrWhiteSpace(state.StartedAt)
            && !string.Equals(initialStartedAt, state.StartedAt, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Container {containerName} restarted unexpectedly (StartedAt changed from {initialStartedAt} to {state.StartedAt})");
        }
    }
}
