using System.Text.RegularExpressions;
using MobySync.Models;
using Docker.DotNet;
using Docker.DotNet.Models;
using MobySync.Interfaces;

namespace MobySync.Helpers;

public class DockerHelper
{
    private readonly DockerClient _dockerClient;

    private readonly ILogger<DockerHelper> _logger;

    private readonly CredentialsHelper _credentialsHelper;

    private bool _pruneImages;

    private readonly HashSet<string> _excludedContainers;

    private readonly Dictionary<string, string> _stackTagOverrides;

    private readonly Dictionary<string, string> _imageTagOverrides;

    private readonly RegistryHelper _registryHelper;

    public DockerHelper(ILogger<DockerHelper> logger, CredentialsHelper credentialsHelper, RegistryHelper registryHelper)
    {
        var dockerSocketUri = new Uri("unix:///var/run/docker.sock");

        _dockerClient = new DockerClientBuilder()
            .WithEndpoint(dockerSocketUri)
            .Build();

        _logger = logger;

        _credentialsHelper = credentialsHelper;

        _registryHelper = registryHelper;

        _pruneImages = false;

        if (bool.TryParse(Environment.GetEnvironmentVariable("PRUNE_IMAGES"), out var pruneImagesParsed))
        {
            _pruneImages = pruneImagesParsed;
        }

        var excludedRaw = Environment.GetEnvironmentVariable("EXCLUDED_CONTAINERS") ?? string.Empty;

        _excludedContainers = excludedRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (_excludedContainers.Count > 0)
            _logger.LogInformation("Configured exclusions: {Excluded}", string.Join(", ", _excludedContainers));

        _stackTagOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _imageTagOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var overridesRaw = Environment.GetEnvironmentVariable("IMAGE_TAG_OVERRIDES") ?? string.Empty;

        foreach (var entry in overridesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(':');

            if (parts.Length == 3)
                _stackTagOverrides[$"{parts[0]}:{parts[1]}"] = parts[2];
            else if (parts.Length == 2)
                _imageTagOverrides[parts[0]] = parts[1];
        }

        if (_stackTagOverrides.Count > 0)
            _logger.LogInformation("Stack tag overrides: {Overrides}", string.Join(", ", _stackTagOverrides.Select(kv => $"{kv.Key} → {kv.Value}")));

        if (_imageTagOverrides.Count > 0)
            _logger.LogInformation("Image tag overrides: {Overrides}", string.Join(", ", _imageTagOverrides.Select(kv => $"{kv.Key} → {kv.Value}")));
    }
    
    public async Task<UpdateSummary> CheckForImageUpdates()
    {
        var updateSummary = new UpdateSummary();
        
        var startTime = DateTime.Now;

        try
        {
            var monitoredContainers = await GetMonitoredContainers();
        
            if (monitoredContainers is null)
                return updateSummary;
        
            var containersGroups = GroupDependentContainers(monitoredContainers);

            foreach (var containerGroup in containersGroups)
            {
                var outdatedContainers = await PullGroupImages(containerGroup.Value, updateSummary);

                if (outdatedContainers is null|| !outdatedContainers.Any())
                    continue;
                
                _logger.LogInformation("Group update execution order: {Order}", 
                    string.Join(" -> ", outdatedContainers.Select(container => container.Names.First().Replace("/", ""))));
        
                await ReplaceContainers(outdatedContainers, updateSummary);
            }

            if (_pruneImages)
            {
                await _dockerClient.Images.PruneImagesAsync(new ImagesPruneParameters());
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An unexpected error occurred while trying to update the monitored containers.");
        }
        finally
        {
            updateSummary.TotalDuration = DateTime.Now - startTime;
            
            _logger.LogInformation("Update cycle finished in {Duration}. Successes: {Success}, Rollbacks: {Rollback}, Failed Pulls: {Failed}", 
                updateSummary.TotalDuration.ToString(@"hh\:mm\:ss"), updateSummary.Successes.Count, updateSummary.Rollbacks.Count, updateSummary.FailedPulls.Count);
        }

        return updateSummary;
    }
    
    private async Task<List<ContainerListResponse>?> GetMonitoredContainers()
    {
        var allContainers = await _dockerClient.Containers.ListContainersAsync(new ContainersListParameters { All = true });

        var runningContainers = allContainers.Where(c => c.State == "running").ToList();

        // Auto-detect own container by matching the Docker-assigned hostname (= short container ID)
        var selfName = string.Empty;
        try
        {
            var hostname = Environment.MachineName;
            var self = runningContainers.FirstOrDefault(c => c.ID.StartsWith(hostname, StringComparison.OrdinalIgnoreCase));
            if (self is not null)
            {
                selfName = self.Names.First().TrimStart('/');
                _logger.LogInformation("Auto-excluding self: {Name}", selfName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not detect own container — skipping self-exclusion");
        }

        var excluded = new HashSet<string>(_excludedContainers, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(selfName))
            excluded.Add(selfName);

        var monitoredContainers = runningContainers
            .Where(c => !excluded.Contains(c.Names.First().TrimStart('/')))
            .ToList();

        if (excluded.Count > 0)
            _logger.LogInformation("Excluded containers: {Names}", string.Join(", ", excluded));

        if (!monitoredContainers.Any())
        {
            _logger.LogWarning("No running containers found to monitor");
            return null;
        }

        _logger.LogInformation("Found {Count} containers to monitor: {Names}",
            monitoredContainers.Count,
            string.Join(", ", monitoredContainers.Select(c => c.Names.First().TrimStart('/'))));

        return monitoredContainers;
    }
    
    private Dictionary<string, List<ContainerListResponse>> GroupDependentContainers(IList<ContainerListResponse> monitoredContainers)
    {
        var containerMap = monitoredContainers.ToDictionary(
            container => container.Names.First().Replace("/", ""),
            container => container);
        
        var adjacencyList = new Dictionary<string, HashSet<string>>();
        
        foreach (var name in containerMap.Keys)
        {
            adjacencyList[name] = new HashSet<string>();
        }
        
        foreach (var container in monitoredContainers)
        {
            var containerName = container.Names.First().Replace("/", "");
            
            if (container.Labels != null && container.Labels.TryGetValue("com.mobysync.depends-on", out var deps))
            {
                foreach (var dependency in deps.Split(',').Select(dependency => dependency.Trim()))
                {
                    if (containerMap.ContainsKey(dependency))
                    {
                        adjacencyList[containerName].Add(dependency);
                        
                        adjacencyList[dependency].Add(containerName);
                    }
                }
            }
        }

        var visited = new HashSet<string>();
        
        var groupedDictionary = new Dictionary<string, List<ContainerListResponse>>();
        
        var groupCounter = 1;

        foreach (var nodeName in containerMap.Keys)
        {
            if (!visited.Contains(nodeName))
            {
                var currentGroupNames = new List<string>();
            
                ExploreNode(nodeName, currentGroupNames);

                var containersGroup = currentGroupNames.Select(name => containerMap[name]).ToList();
                
                var sortedGroup = SortContainersByDependencies(containersGroup);
            
                groupedDictionary.Add($"Auto-Group-{groupCounter}", sortedGroup);
                
                groupCounter++;
            }
        }

        return groupedDictionary;
        
        void ExploreNode(string current, List<string> group)
        {
            visited.Add(current);
            
            group.Add(current);

            foreach (var neighbor in adjacencyList[current])
            {
                if (!visited.Contains(neighbor))
                {
                    ExploreNode(neighbor, group);
                }
            }
        }
    }
    
    private async Task<IList<ContainerListResponse>?> PullGroupImages(IList<ContainerListResponse> monitoredContainers, UpdateSummary summary)
    {
        var outdatedContainers = new List<ContainerListResponse>();

        foreach (var container in monitoredContainers)
        {
            var baseImageName = FetchBaseImageName(container.Image);
            var containerName = container.Names.First().Replace("/", "");
            var targetTag = ResolveTargetTag(container, baseImageName);

            if (IsPinnedVersionTag(targetTag))
            {
                _logger.LogInformation("Container {Container} uses pinned version tag ({Tag}), skipping", containerName, targetTag);
                summary.Skipped.Add(new ContainerUpdateResult
                {
                    ContainerName = containerName,
                    ImageName = baseImageName,
                    ErrorMessage = $"Pinned version tag `{targetTag}` — skipping auto-update",
                    Success = false
                });
                continue;
            }

            _logger.LogInformation("Processing {Container}: image='{RawImage}', base='{Base}', tag='{Tag}'",
                containerName, container.Image, baseImageName, targetTag);

            ImageInspectResponse runningImageInfo;
            try
            {
                // Locally built images have no registry digest — skip them rather than failing the group
                runningImageInfo = await _dockerClient.Images.InspectImageAsync(container.ImageID);
                if (runningImageInfo.RepoDigests == null || !runningImageInfo.RepoDigests.Any())
                {
                    _logger.LogInformation("Container {Container} uses a locally built image ({Image}), skipping", containerName, baseImageName);
                    summary.Skipped.Add(new ContainerUpdateResult
                    {
                        ContainerName = containerName,
                        ImageName = baseImageName,
                        ErrorMessage = "Locally built image — cannot check for updates",
                        Success = false
                    });
                    continue;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to inspect image for {Container}. Aborting group update.", containerName);
                summary.FailedPulls.Add(new ContainerUpdateResult
                {
                    ContainerName = containerName,
                    ImageName = baseImageName,
                    ErrorMessage = $"Image inspect failed: {ex.Message}",
                    Success = false
                });
                return null;
            }

            var authCredentials = await _credentialsHelper.FetchCredentials(baseImageName);

            // Check the registry directly — bypasses the Docker daemon's credential cache entirely
            string? remoteDigest;
            try
            {
                remoteDigest = await _registryHelper.GetManifestDigest(baseImageName, targetTag, authCredentials);
            }
            catch (Exception checkEx)
            {
                var checkMsg = IsAuthError(checkEx)
                    ? $"Registry auth failed for {baseImageName}:{targetTag} — credentials may be expired or image may be private"
                    : $"Registry check failed for {baseImageName}:{targetTag}: {checkEx.Message}";

                _logger.LogError(checkEx, "Registry check failed for {Image}:{Tag}. Aborting group update.", baseImageName, targetTag);
                summary.FailedPulls.Add(new ContainerUpdateResult
                {
                    ContainerName = containerName,
                    ImageName = baseImageName,
                    ErrorMessage = checkMsg,
                    Success = false
                });
                return null;
            }

            if (remoteDigest == null)
            {
                _logger.LogWarning("Could not retrieve remote digest for {Image}:{Tag} — treating as up to date", baseImageName, targetTag);
                summary.UpToDate.Add(containerName);
                continue;
            }

            // Extract the sha256:... portion from each stored RepoDigest entry (format: image@sha256:...)
            var runningDigests = runningImageInfo.RepoDigests
                .Where(d => d.Contains('@'))
                .Select(d => d.Split('@')[1])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (runningDigests.Contains(remoteDigest))
            {
                _logger.LogInformation("Container {Container} is already running the latest version", containerName);
                summary.UpToDate.Add(containerName);
                continue;
            }

            _logger.LogInformation("New image available for {Image}:{Tag} — pulling", baseImageName, targetTag);

            try
            {
                try
                {
                    await _dockerClient.Images.CreateImageAsync(new ImagesCreateParameters
                    {
                        FromImage = baseImageName,
                        Tag = targetTag
                    }, authCredentials, new Progress<JSONMessage>());
                }
                catch (Exception pullEx) when (IsAuthError(pullEx))
                {
                    // Daemon may be using its own stale cached credentials; retry with a fully empty AuthConfig
                    // so the daemon has no server address hint to look up its own credential store.
                    _logger.LogWarning("Pull with credentials failed for {Image} — retrying anonymously", baseImageName);
                    await _dockerClient.Images.CreateImageAsync(new ImagesCreateParameters
                    {
                        FromImage = baseImageName,
                        Tag = targetTag
                    }, new AuthConfig(), new Progress<JSONMessage>());
                }

                _logger.LogInformation("Successfully pulled new version of {Image}:{Tag}", baseImageName, targetTag);
                outdatedContainers.Add(container);
            }
            catch (Exception pullEx)
            {
                var pullMsg = IsAuthError(pullEx)
                    ? $"Pull failed for {baseImageName}:{targetTag} — daemon authentication error (tried both credentials and anonymous)"
                    : $"Pull failed for {baseImageName}:{targetTag}: {pullEx.Message}";

                _logger.LogError(pullEx, "Pull failed for {Image}:{Tag}. Aborting group update.", baseImageName, targetTag);
                summary.FailedPulls.Add(new ContainerUpdateResult
                {
                    ContainerName = containerName,
                    ImageName = baseImageName,
                    ErrorMessage = pullMsg,
                    Success = false
                });
                return null;
            }
        }

        return outdatedContainers;
    }
    
    private async Task ReplaceContainers(IList<ContainerListResponse> containersForReplace, UpdateSummary summary) 
    {
        // Rollbacks are executed in the opposite direction 
        var transactionLog = new Stack<ContainerCheckpoint>();
        
        foreach (var containerForUpdate in containersForReplace)
        {
            var containerConfiguration = await _dockerClient.Containers.InspectContainerAsync(containerForUpdate.ID);
            
            var baseImageName = FetchBaseImageName(containerForUpdate.Image);
            
            var targetTag = ResolveTargetTag(containerForUpdate, baseImageName);

            var newContainerId = string.Empty;
            
            var originalName = containerForUpdate.Names.First().Replace("/", "");

            var backupName = $"{originalName}-backup";
            
            try
            {
                var createParams = new CreateContainerParameters(containerConfiguration.Config)
                {
                    Image = $"{baseImageName}:{targetTag}",
                    HostConfig = containerConfiguration.HostConfig,
                    NetworkingConfig = new NetworkingConfig { EndpointsConfig = containerConfiguration.NetworkSettings.Networks },
                    Name = originalName
                };
                
                // Rename the target container for update, in order to avoid conflicts
                await _dockerClient.Containers.RenameContainerAsync(containerForUpdate.ID, new ContainerRenameParameters { NewName = backupName}, 
                    CancellationToken.None);
                
                // Stop the target container for update
                await _dockerClient.Containers.StopContainerAsync(containerForUpdate.ID, new ContainerStopParameters());

                var createdContainer = await _dockerClient.Containers.CreateContainerAsync(createParams);
                
                // Start the updated container
                var successfulContainerStart = await _dockerClient.Containers.StartContainerAsync(createdContainer.ID, new ContainerStartParameters());
                
                if (!successfulContainerStart)
                    throw new InvalidOperationException($"Failed to start container {originalName}");
                
                // Wait some time in order to check the state of the created container
                await Task.Delay(TimeSpan.FromSeconds(10));
            
                var createdContainerConfiguration = await _dockerClient.Containers.InspectContainerAsync(createdContainer.ID);
                
                newContainerId =  createdContainerConfiguration.ID;
                
                if (!createdContainerConfiguration.State.Running)
                {
                    throw new InvalidOperationException($"Container {originalName} crashed after starting, " +
                                                        $"exit Code: {createdContainerConfiguration.State.ExitCode}");
                }
                
                // Add entry for rollback in case of a failure
                transactionLog.Push(new ContainerCheckpoint
                {
                    OldId = containerConfiguration.ID,
                    NewId = newContainerId,
                    OriginalName = originalName,
                    BackupName = backupName
                });

                summary.Successes.Add(new ContainerUpdateResult
                {
                    ContainerName = originalName,
                    ImageName = baseImageName,
                    NewTag = targetTag,
                    Success = true
                });
                
                _logger.LogInformation("Successfully updated {Container} to {Tag}", originalName, targetTag);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "An unexpected error occurred while trying to replace container {Container}. " +
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
                    BackupName = backupName
                });

                await AttemptRollback(transactionLog);

                return;
            }
        }
        
        foreach (var checkpoint in transactionLog)
        {
            try
            {
                await _dockerClient.Containers.RemoveContainerAsync(checkpoint.OldId, new ContainerRemoveParameters { Force = true });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove backup container {BackupName}, manual intervention is needed", checkpoint.BackupName);
            }
        }
    }
    
    private async Task AttemptRollback(Stack<ContainerCheckpoint> transactionLog)
    {
        foreach (var checkpoint in transactionLog)
        {
            _logger.LogInformation("Starting rollback process for container: {Container}", checkpoint.OriginalName);
            
            try
            {
                if (!string.IsNullOrEmpty(checkpoint.NewId))
                {
                    _logger.LogInformation("Removing failed container instance");
                    
                    await _dockerClient.Containers.RemoveContainerAsync(checkpoint.NewId, new ContainerRemoveParameters { Force = true });
                }

                var oldContainerInspect = await _dockerClient.Containers.InspectContainerAsync(checkpoint.OldId);
                
                // We only rename it back if it currently has the backup name
                if (oldContainerInspect.Name.Replace("/", "") == checkpoint.BackupName)
                {
                    _logger.LogInformation("Restoring original name...");
                    
                    await _dockerClient.Containers.RenameContainerAsync(checkpoint.OldId, new ContainerRenameParameters { NewName = checkpoint.OriginalName }, CancellationToken.None);
                }
                
                await _dockerClient.Containers.StartContainerAsync(checkpoint.OldId, new ContainerStartParameters());
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "An unexpected error occurred while tying to rollback container: {Container}. " +
                                        "Manual intervention is needed. Backup container is {BackupName}", checkpoint.OriginalName, checkpoint.BackupName);
            }
        }
    }
    
    private List<ContainerListResponse> SortContainersByDependencies(IList<ContainerListResponse> containers)
    {
        // This list is sorted so that base dependencies update first, and dependent containers update lastly
        var sortedList = new List<ContainerListResponse>();
        
        // Set used for containers that have been processed
        var visited = new HashSet<string>();
        
        // Set used to detect circular dependencies
        var visiting = new HashSet<string>();

        var containerMap = containers.ToDictionary(
            container =>  container.Names.First().Replace("/", ""),
            container => container
        );

        foreach (var container in containers)
        {
            Visit(container);
        }

        return sortedList;

        void Visit(ContainerListResponse container)
        {
            var containerName =  container.Names.First().Replace("/", "");
            
            // Dependency has already been resolved
            if (visited.Contains(containerName)) 
                return; 
            
            // Circular dependency detection
            if (visiting.Contains(containerName)) 
                throw new InvalidOperationException($"An circular dependency has detected involving containers '{containerName}'"); // see to add which container is the conflict

            visiting.Add(containerName);

            // Traverse to the dependencies of the container.
            if (container.Labels.TryGetValue("com.mobysync.depends-on", out var dependenciesString))
            {
                var dependencies = dependenciesString.Split(',')
                    .Select(dependency => dependency.Trim());

                foreach (var dependency in dependencies)
                {
                    if (containerMap.TryGetValue(dependency, out var dependencyContainer))
                    {
                        Visit(dependencyContainer); 
                    }
                }
            }

            visiting.Remove(containerName);
            
            visited.Add(containerName);
            
            sortedList.Add(container); 
        }
    }
    
    private string ResolveTargetTag(ContainerListResponse container, string baseImageName)
    {
        if (container.Labels != null && container.Labels.TryGetValue("com.mobysync.target-tag", out var labelTag))
            return labelTag;

        if (container.Labels != null
            && container.Labels.TryGetValue("com.docker.compose.project", out var project)
            && container.Labels.TryGetValue("com.docker.compose.service", out var service)
            && _stackTagOverrides.TryGetValue($"{project}:{service}", out var stackTag))
            return stackTag;

        if (_imageTagOverrides.TryGetValue(baseImageName, out var imageTag))
            return imageTag;

        var detectedTag = ExtractTagFromImage(container.Image);
        if (detectedTag != null)
            return detectedTag;

        return "latest";
    }

    private static bool IsPinnedVersionTag(string tag) =>
        Regex.IsMatch(tag, @"^v?\d+(\.\d+)+$");

    private static bool IsAuthError(Exception ex) =>
        ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("access denied", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("forbidden", StringComparison.OrdinalIgnoreCase);

    private static string? ExtractTagFromImage(string imageFullPath)
    {
        var lastColonIndex = imageFullPath.LastIndexOf(':');
        var lastSlashIndex = imageFullPath.LastIndexOf('/');

        if (lastColonIndex > lastSlashIndex && lastColonIndex < imageFullPath.Length - 1)
            return imageFullPath.Substring(lastColonIndex + 1);

        return null;
    }

    private string FetchBaseImageName(string imageFullPath)
    {
        var lastColonIndex = imageFullPath.LastIndexOf(':');

        var lastSlashIndex = imageFullPath.LastIndexOf('/');

        if (lastColonIndex > lastSlashIndex)
        {
            return imageFullPath.Substring(0, lastColonIndex);
        }

        return imageFullPath;
    }
}