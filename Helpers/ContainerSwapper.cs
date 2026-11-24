using Docker.DotNet;
using Docker.DotNet.Models;
using MobySync.Models;

namespace MobySync.Helpers;

public class ContainerSwapper(
    IDockerClient dockerClient,
    ContainerHealthVerifier healthVerifier,
    ILogger logger)
{
    public async Task ReplaceContainerGroupAsync(
        IList<ContainerListResponse> containersForReplace,
        Func<ContainerListResponse, string, string> resolveTargetTag,
        UpdateSummary summary)
    {
        // Rollbacks are executed in the opposite direction
        var transactionLog = new Stack<ContainerCheckpoint>();

        foreach (var containerForUpdate in containersForReplace)
        {
            var containerConfiguration = await dockerClient.Containers.InspectContainerAsync(containerForUpdate.ID);
            var baseImageName = ImageTagResolver.FetchBaseImageName(containerForUpdate.Image);
            var targetTag = resolveTargetTag(containerForUpdate, baseImageName);
            var newContainerId = string.Empty;
            var originalName = containerForUpdate.Names.First().Replace("/", "");
            var backupName = $"{originalName}-backup";

            try
            {
                await SwapSingleContainerAsync(
                    containerConfiguration,
                    originalName,
                    backupName,
                    baseImageName,
                    targetTag,
                    id => newContainerId = id);

                transactionLog.Push(new ContainerCheckpoint
                {
                    OldId = containerConfiguration.ID,
                    NewId = newContainerId,
                    OriginalName = originalName,
                    BackupName = backupName,
                    OriginalNetworks = containerConfiguration.NetworkSettings?.Networks
                });

                summary.Successes.Add(new ContainerUpdateResult
                {
                    ContainerName = originalName,
                    ImageName = baseImageName,
                    NewTag = targetTag,
                    Success = true
                });

                logger.LogInformation("Successfully updated {Container} to {Tag}", originalName, targetTag);
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "An unexpected error occurred while trying to replace container {Container}. " +
                                       "Reverting changes of previously updated containers", originalName);

                summary.Rollbacks.Add(new ContainerUpdateResult
                {
                    ContainerName = originalName,
                    ImageName = baseImageName,
                    ErrorMessage = ex.Message,
                    Success = false
                });

                transactionLog.Push(new ContainerCheckpoint
                {
                    OldId = containerConfiguration.ID,
                    NewId = newContainerId,
                    OriginalName = originalName,
                    BackupName = backupName,
                    OriginalNetworks = containerConfiguration.NetworkSettings?.Networks
                });

                await AttemptRollback(transactionLog);
                return;
            }
        }

        foreach (var checkpoint in transactionLog)
        {
            try
            {
                await dockerClient.Containers.RemoveContainerAsync(checkpoint.OldId, new ContainerRemoveParameters { Force = true });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to remove backup container {BackupName}, manual intervention is needed", checkpoint.BackupName);
            }
        }
    }

    public async Task<UpdateSummary> ExecuteSelfUpdateSwapAsync(
        string targetContainerId,
        string? targetImageOverride,
        bool pruneImages)
    {
        var summary = new UpdateSummary();
        var startTime = DateTime.Now;
        var transactionLog = new Stack<ContainerCheckpoint>();

        ContainerInspectResponse containerConfiguration;
        try
        {
            containerConfiguration = await dockerClient.Containers.InspectContainerAsync(targetContainerId);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Failed to inspect target container {ContainerId} for self-update", targetContainerId);
            summary.FailedPulls.Add(new ContainerUpdateResult
            {
                ContainerName = targetContainerId,
                ImageName = targetImageOverride ?? "mobysync",
                ErrorMessage = $"Inspect target failed: {ex.Message}",
                Success = false
            });
            summary.TotalDuration = DateTime.Now - startTime;
            return summary;
        }

        var originalName = containerConfiguration.Name.TrimStart('/');
        var backupName = $"{originalName}-backup";
        var fullImage = !string.IsNullOrWhiteSpace(targetImageOverride)
            ? targetImageOverride
            : (containerConfiguration.Config?.Image ?? "mobysync:latest");

        var baseImageName = ImageTagResolver.FetchBaseImageName(fullImage);
        var targetTag = ImageTagResolver.ExtractTagFromImage(fullImage) ?? "latest";
        var newContainerId = string.Empty;

        try
        {
            await SwapSingleContainerAsync(
                containerConfiguration,
                originalName,
                backupName,
                baseImageName,
                targetTag,
                id => newContainerId = id);

            try
            {
                await dockerClient.Containers.RemoveContainerAsync(containerConfiguration.ID, new ContainerRemoveParameters { Force = true });
            }
            catch (Exception removeEx)
            {
                logger.LogWarning(removeEx, "Failed to remove backup container {BackupName}, manual intervention is needed", backupName);
            }

            if (pruneImages)
            {
                await dockerClient.Images.PruneImagesAsync(new ImagesPruneParameters());
            }

            summary.Successes.Add(new ContainerUpdateResult
            {
                ContainerName = originalName,
                ImageName = baseImageName,
                NewTag = targetTag,
                Success = true
            });

            logger.LogInformation("Successfully self-updated {Container} to {Tag}", originalName, targetTag);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Self-update failed while replacing {Container}. Rolling back to previous container.", originalName);

            summary.Rollbacks.Add(new ContainerUpdateResult
            {
                ContainerName = originalName,
                ImageName = baseImageName,
                ErrorMessage = ex.Message,
                Success = false
            });

            transactionLog.Push(new ContainerCheckpoint
            {
                OldId = containerConfiguration.ID,
                NewId = newContainerId,
                OriginalName = originalName,
                BackupName = backupName,
                OriginalNetworks = containerConfiguration.NetworkSettings?.Networks
            });

            await AttemptRollback(transactionLog);
        }
        finally
        {
            summary.TotalDuration = DateTime.Now - startTime;
        }

        return summary;
    }

    private async Task SwapSingleContainerAsync(
        ContainerInspectResponse containerConfiguration,
        string originalName,
        string backupName,
        string baseImageName,
        string targetTag,
        Action<string> onCreated)
    {
        // Clean up any leftover backup container from a previous interrupted run
        var allContainers = await dockerClient.Containers.ListContainersAsync(new ContainersListParameters { All = true });
        var staleBackup = allContainers.FirstOrDefault(c =>
            c.ID != containerConfiguration.ID &&
            c.Names.Any(n => string.Equals(n.TrimStart('/'), backupName, StringComparison.OrdinalIgnoreCase)));

        if (staleBackup is not null)
        {
            await dockerClient.Containers.RemoveContainerAsync(staleBackup.ID, new ContainerRemoveParameters { Force = true });
        }

        // Clear Docker-generated short-ID hostname so the new container gets its own short ID as hostname
        if (containerConfiguration.Config is not null
            && !string.IsNullOrEmpty(containerConfiguration.Config.Hostname)
            && containerConfiguration.ID.StartsWith(containerConfiguration.Config.Hostname, StringComparison.OrdinalIgnoreCase))
        {
            containerConfiguration.Config.Hostname = string.Empty;
        }

        var (primaryNetworkingConfig, secondaryNetworks) = SplitPrimaryAndSecondaryNetworks(containerConfiguration);

        var createParams = containerConfiguration.Config is not null
            ? new CreateContainerParameters(containerConfiguration.Config)
            : new CreateContainerParameters();

        createParams.Image = $"{baseImageName}:{targetTag}";
        createParams.HostConfig = containerConfiguration.HostConfig;
        createParams.NetworkingConfig = primaryNetworkingConfig;
        createParams.Name = originalName;

        // Rename and stop the old container to avoid name/port conflicts
        await dockerClient.Containers.RenameContainerAsync(
            containerConfiguration.ID,
            new ContainerRenameParameters { NewName = backupName },
            CancellationToken.None);

        await dockerClient.Containers.StopContainerAsync(containerConfiguration.ID, new ContainerStopParameters());

        var createdContainer = await dockerClient.Containers.CreateContainerAsync(createParams);
        onCreated(createdContainer.ID);

        await AttachSecondaryNetworksAsync(createdContainer.ID, originalName, secondaryNetworks);

        var successfulContainerStart = await dockerClient.Containers.StartContainerAsync(createdContainer.ID, new ContainerStartParameters());
        if (!successfulContainerStart)
            throw new InvalidOperationException($"Failed to start container {originalName}");

        await healthVerifier.VerifyContainerStartupAsync(createdContainer.ID, originalName);
    }

    public async Task AttemptRollback(Stack<ContainerCheckpoint> transactionLog)
    {
        foreach (var checkpoint in transactionLog)
        {
            logger.LogInformation("Starting rollback process for container: {Container}", checkpoint.OriginalName);

            try
            {
                if (!string.IsNullOrEmpty(checkpoint.NewId))
                {
                    try
                    {
                        logger.LogInformation("Removing failed container instance");
                        await dockerClient.Containers.RemoveContainerAsync(checkpoint.NewId, new ContainerRemoveParameters { Force = true });
                    }
                    catch (Exception removeEx)
                    {
                        logger.LogWarning(removeEx, "Failed to remove failed container {ContainerId} during rollback of {Container}", checkpoint.NewId, checkpoint.OriginalName);
                    }
                }

                var oldContainerInspect = await dockerClient.Containers.InspectContainerAsync(checkpoint.OldId);

                // Only rename it back if it currently holds the backup name
                if (oldContainerInspect.Name.Replace("/", "") == checkpoint.BackupName)
                {
                    try
                    {
                        logger.LogInformation("Restoring original name...");
                        await dockerClient.Containers.RenameContainerAsync(checkpoint.OldId, new ContainerRenameParameters { NewName = checkpoint.OriginalName }, CancellationToken.None);
                    }
                    catch (Exception renameEx)
                    {
                        logger.LogError(renameEx, "Failed to rename backup container {BackupName} back to {OriginalName}; starting backup container directly", checkpoint.BackupName, checkpoint.OriginalName);
                    }
                }

                if (checkpoint.OriginalNetworks is not null && checkpoint.OriginalNetworks.Count > 1)
                {
                    try
                    {
                        var currentNetworks = oldContainerInspect.NetworkSettings?.Networks;
                        foreach (var (networkName, endpoint) in checkpoint.OriginalNetworks)
                        {
                            if (currentNetworks is null || !currentNetworks.ContainsKey(networkName))
                            {
                                var networkTarget = !string.IsNullOrWhiteSpace(endpoint.NetworkID)
                                    ? endpoint.NetworkID
                                    : networkName;

                                await dockerClient.Networks.ConnectNetworkAsync(networkTarget, new NetworkConnectParameters
                                {
                                    Container = checkpoint.OldId,
                                    EndpointConfig = SanitizeEndpointSettings(endpoint, checkpoint.OldId)
                                });
                            }
                        }
                    }
                    catch (Exception netEx)
                    {
                        logger.LogWarning(netEx, "Failed to restore all secondary networks for {Container} during rollback", checkpoint.OriginalName);
                    }
                }

                await dockerClient.Containers.StartContainerAsync(checkpoint.OldId, new ContainerStartParameters());
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "An unexpected error occurred while trying to rollback container: {Container}. " +
                                       "Manual intervention is needed. Backup container is {BackupName}", checkpoint.OriginalName, checkpoint.BackupName);
            }
        }
    }

    private async Task AttachSecondaryNetworksAsync(
        string containerId,
        string containerName,
        IReadOnlyList<(string NetworkName, EndpointSettings Endpoint)> secondaryNetworks)
    {
        foreach (var (networkName, endpoint) in secondaryNetworks)
        {
            var networkTarget = !string.IsNullOrWhiteSpace(endpoint.NetworkID)
                ? endpoint.NetworkID
                : networkName;

            logger.LogInformation("Connecting container {Container} to additional network {Network}", containerName, networkName);

            await dockerClient.Networks.ConnectNetworkAsync(networkTarget, new NetworkConnectParameters
            {
                Container = containerId,
                EndpointConfig = endpoint
            });
        }
    }

    public static (NetworkingConfig PrimaryNetworkingConfig, List<(string NetworkName, EndpointSettings Endpoint)> SecondaryNetworks)
        SplitPrimaryAndSecondaryNetworks(ContainerInspectResponse containerConfiguration)
    {
        var allNetworks = containerConfiguration.NetworkSettings?.Networks;
        if (allNetworks is null || allNetworks.Count == 0)
        {
            return (new NetworkingConfig(), []);
        }

        var networkMode = containerConfiguration.HostConfig?.NetworkMode;
        var primaryNetworkName = !string.IsNullOrWhiteSpace(networkMode) && allNetworks.ContainsKey(networkMode)
            ? networkMode
            : allNetworks.Keys.First();

        var primaryEndpoint = SanitizeEndpointSettings(allNetworks[primaryNetworkName], containerConfiguration.ID);
        var primaryNetworkingConfig = new NetworkingConfig
        {
            EndpointsConfig = new Dictionary<string, EndpointSettings>
            {
                [primaryNetworkName] = primaryEndpoint
            }
        };

        var secondaryNetworks = allNetworks
            .Where(kvp => !string.Equals(kvp.Key, primaryNetworkName, StringComparison.Ordinal))
            .Select(kvp => (NetworkName: kvp.Key, Endpoint: SanitizeEndpointSettings(kvp.Value, containerConfiguration.ID)))
            .ToList();

        return (primaryNetworkingConfig, secondaryNetworks);
    }

    public static EndpointSettings SanitizeEndpointSettings(EndpointSettings source, string oldContainerId)
    {
        var oldShortId = !string.IsNullOrEmpty(oldContainerId) && oldContainerId.Length >= 12
            ? oldContainerId[..12]
            : oldContainerId;

        var sanitizedAliases = source.Aliases?
            .Where(a => !string.Equals(a, oldShortId, StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];

        return new EndpointSettings
        {
            IPAMConfig = source.IPAMConfig,
            Links = source.Links,
            Aliases = sanitizedAliases,
            NetworkID = source.NetworkID,
            DriverOpts = source.DriverOpts
        };
    }
}
