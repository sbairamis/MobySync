using System.Text.RegularExpressions;
using MobySync.Models;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.DependencyInjection;
using MobySync.Interfaces;

namespace MobySync.Helpers;

public class DockerHelper
{
    public const string SelfUpdaterContainerName = "mobysync-self-updater";

    private readonly IDockerClient _dockerClient;

    private readonly ILogger<DockerHelper> _logger;

    private readonly CredentialsHelper _credentialsHelper;

    private bool _pruneImages;

    private readonly bool _selfUpdate;

    private readonly TimeSpan _stabilityWindow;

    private readonly TimeSpan _healthCheckTimeout;

    private readonly TimeSpan _pollInterval;

    private readonly string _selfHostname;

    private readonly HashSet<string> _excludedContainers;

    private readonly Dictionary<string, string> _stackTagOverrides;

    private readonly Dictionary<string, string> _imageTagOverrides;

    private readonly RegistryHelper _registryHelper;

    [ActivatorUtilitiesConstructor]
    public DockerHelper(ILogger<DockerHelper> logger, CredentialsHelper credentialsHelper, RegistryHelper registryHelper)
        : this(logger, credentialsHelper, registryHelper, null, null, null, null, null)
    {
    }

    public DockerHelper(
        ILogger<DockerHelper> logger,
        CredentialsHelper credentialsHelper,
        RegistryHelper registryHelper,
        IDockerClient? dockerClient,
        TimeSpan? stabilityWindow = null,
        string? selfHostname = null,
        TimeSpan? healthCheckTimeout = null,
        TimeSpan? pollInterval = null)
    {
        if (dockerClient is not null)
        {
            _dockerClient = dockerClient;
        }
        else
        {
            var dockerSocketUri = new Uri("unix:///var/run/docker.sock");

            _dockerClient = new DockerClientBuilder()
                .WithEndpoint(dockerSocketUri)
                .Build();
        }

        _logger = logger;

        _credentialsHelper = credentialsHelper;

        _registryHelper = registryHelper;

        var defaultStabilitySeconds = int.TryParse(Environment.GetEnvironmentVariable("STABILITY_WINDOW_SECONDS"), out var parsedStability) && parsedStability > 0
            ? parsedStability
            : 10;

        var defaultHealthTimeoutSeconds = int.TryParse(Environment.GetEnvironmentVariable("HEALTHCHECK_TIMEOUT_SECONDS"), out var parsedHealthTimeout) && parsedHealthTimeout > 0
            ? parsedHealthTimeout
            : 60;

        _stabilityWindow = stabilityWindow ?? TimeSpan.FromSeconds(defaultStabilitySeconds);

        _healthCheckTimeout = healthCheckTimeout ?? TimeSpan.FromSeconds(defaultHealthTimeoutSeconds);

        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);

        _selfHostname = selfHostname ?? Environment.MachineName;

        _pruneImages = false;

        if (bool.TryParse(Environment.GetEnvironmentVariable("PRUNE_IMAGES"), out var pruneImagesParsed))
        {
            _pruneImages = pruneImagesParsed;
        }

        _selfUpdate = true;

        if (bool.TryParse(Environment.GetEnvironmentVariable("SELF_UPDATE"), out var selfUpdateParsed))
        {
            _selfUpdate = selfUpdateParsed;
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
            var self = FindSelfContainer(runningContainers);
            if (self is not null)
            {
                selfName = self.Names.First().TrimStart('/');
                _logger.LogInformation("Auto-excluding self from regular batch: {Name}", selfName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not detect own container — skipping self-exclusion");
        }

        var excluded = new HashSet<string>(_excludedContainers, StringComparer.OrdinalIgnoreCase)
        {
            SelfUpdaterContainerName
        };
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

    private ContainerListResponse? FindSelfContainer(IEnumerable<ContainerListResponse> runningContainers)
    {
        if (string.IsNullOrWhiteSpace(_selfHostname))
            return null;

        return runningContainers.FirstOrDefault(c => c.ID.StartsWith(_selfHostname, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<UpdateSummary?> CheckAndTriggerSelfUpdate()
    {
        if (!_selfUpdate)
        {
            _logger.LogInformation("Self-update is disabled (SELF_UPDATE=false)");
            return null;
        }

        try
        {
            var allContainers = await _dockerClient.Containers.ListContainersAsync(new ContainersListParameters { All = true });
            var runningContainers = allContainers.Where(c => c.State == "running").ToList();
            var self = FindSelfContainer(runningContainers);

            if (self is null)
            {
                _logger.LogWarning("Could not detect own container — skipping self-update check");
                return null;
            }

            var selfName = self.Names.First().TrimStart('/');

            if (_excludedContainers.Contains(selfName))
            {
                _logger.LogInformation("Self container {Name} is in EXCLUDED_CONTAINERS — skipping self-update", selfName);
                return null;
            }

            var baseImageName = FetchBaseImageName(self.Image);
            var targetTag = ResolveTargetTag(self, baseImageName);

            if (IsPinnedVersionTag(targetTag))
            {
                _logger.LogInformation("Self container {Container} uses pinned version tag ({Tag}), skipping self-update", selfName, targetTag);
                return null;
            }

            var runningImageInfo = await _dockerClient.Images.InspectImageAsync(self.ImageID);
            if (runningImageInfo.RepoDigests == null || !runningImageInfo.RepoDigests.Any())
            {
                _logger.LogInformation("Self container {Container} uses a locally built image ({Image}), skipping self-update", selfName, baseImageName);
                return null;
            }

            var authCredentials = await _credentialsHelper.FetchCredentials(baseImageName);
            var remoteDigest = await _registryHelper.GetManifestDigest(baseImageName, targetTag, authCredentials);

            if (remoteDigest == null)
            {
                _logger.LogWarning("Could not retrieve remote digest for self ({Image}:{Tag}) — treating as up to date", baseImageName, targetTag);
                return null;
            }

            var runningDigests = runningImageInfo.RepoDigests
                .Where(d => d.Contains('@'))
                .Select(d => d.Split('@')[1])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (runningDigests.Contains(remoteDigest))
            {
                _logger.LogInformation("Self container {Container} is already running the latest version", selfName);
                return null;
            }

            _logger.LogInformation("New image available for self ({Image}:{Tag}) — pulling before spawning self-updater", baseImageName, targetTag);

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
                _logger.LogWarning("Self image pull with credentials failed for {Image} — retrying anonymously", baseImageName);
                await _dockerClient.Images.CreateImageAsync(new ImagesCreateParameters
                {
                    FromImage = baseImageName,
                    Tag = targetTag
                }, new AuthConfig(), new Progress<JSONMessage>());
            }

            await SpawnSelfUpdaterContainer(self, allContainers, baseImageName, targetTag);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check or trigger self-update");
            var failureSummary = new UpdateSummary();
            failureSummary.FailedPulls.Add(new ContainerUpdateResult
            {
                ContainerName = "mobysync (self-update)",
                ImageName = "mobysync",
                ErrorMessage = $"Self-update trigger failed: {ex.Message}",
                Success = false
            });
            return failureSummary;
        }
    }

    private async Task SpawnSelfUpdaterContainer(
        ContainerListResponse self,
        IList<ContainerListResponse> allContainers,
        string baseImageName,
        string targetTag)
    {
        var existingHelper = allContainers.FirstOrDefault(c =>
            c.Names.Any(n => string.Equals(n.TrimStart('/'), SelfUpdaterContainerName, StringComparison.OrdinalIgnoreCase)));

        if (existingHelper is not null)
        {
            _logger.LogInformation("Removing existing self-updater container {Id}", existingHelper.ID);
            await _dockerClient.Containers.RemoveContainerAsync(existingHelper.ID, new ContainerRemoveParameters { Force = true });
        }

        var selfInspect = await _dockerClient.Containers.InspectContainerAsync(self.ID);
        var fullImage = $"{baseImageName}:{targetTag}";

        var envList = new List<string>();
        var inheritedPrefixes = new[] { "DISCORD_WEBHOOK_URL=", "WEBHOOK_URL=", "TZ=", "PRUNE_IMAGES=" };

        if (selfInspect.Config?.Env is not null)
        {
            foreach (var envEntry in selfInspect.Config.Env)
            {
                if (inheritedPrefixes.Any(prefix => envEntry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    envList.Add(envEntry);
                }
            }
        }

        envList.Add($"MOBYSYNC_SELF_UPDATE_TARGET={self.ID}");
        envList.Add($"MOBYSYNC_SELF_TARGET_IMAGE={fullImage}");

        var binds = selfInspect.HostConfig?.Binds?
            .Where(b => b.Contains("docker.sock", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (binds is null || binds.Count == 0)
        {
            binds = ["/var/run/docker.sock:/var/run/docker.sock"];
        }

        var hostConfig = new HostConfig
        {
            AutoRemove = true,
            Binds = binds
        };

        if (selfInspect.HostConfig?.Mounts is not null && selfInspect.HostConfig.Mounts.Any())
        {
            var socketMounts = selfInspect.HostConfig.Mounts
                .Where(m => (m.Source != null && m.Source.Contains("docker.sock", StringComparison.OrdinalIgnoreCase))
                         || (m.Target != null && m.Target.Contains("docker.sock", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (socketMounts.Count > 0)
                hostConfig.Mounts = socketMounts;
        }

        if (!string.IsNullOrWhiteSpace(selfInspect.HostConfig?.NetworkMode)
            && !selfInspect.HostConfig.NetworkMode.StartsWith("container:", StringComparison.OrdinalIgnoreCase))
        {
            hostConfig.NetworkMode = selfInspect.HostConfig.NetworkMode;
        }

        var createParams = new CreateContainerParameters
        {
            Name = SelfUpdaterContainerName,
            Image = fullImage,
            Env = envList,
            HostConfig = hostConfig
        };

        var createdHelper = await _dockerClient.Containers.CreateContainerAsync(createParams);
        var started = await _dockerClient.Containers.StartContainerAsync(createdHelper.ID, new ContainerStartParameters());

        if (!started)
            throw new InvalidOperationException($"Failed to start self-updater helper container {SelfUpdaterContainerName}");

        _logger.LogInformation("Started self-updater helper container {HelperName} ({HelperId}) targeting {SelfId}",
            SelfUpdaterContainerName, createdHelper.ID, self.ID);
    }

    public async Task<UpdateSummary> ExecuteSelfUpdateSwap(string targetContainerId, string? targetImageOverride = null)
    {
        var summary = new UpdateSummary();
        var startTime = DateTime.Now;
        var transactionLog = new Stack<ContainerCheckpoint>();

        ContainerInspectResponse containerConfiguration;
        try
        {
            containerConfiguration = await _dockerClient.Containers.InspectContainerAsync(targetContainerId);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to inspect target container {ContainerId} for self-update", targetContainerId);
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

        var baseImageName = FetchBaseImageName(fullImage);
        var targetTag = ExtractTagFromImage(fullImage) ?? "latest";
        var newContainerId = string.Empty;

        try
        {
            // Clean up any leftover backup container from a previous interrupted run
            var allContainers = await _dockerClient.Containers.ListContainersAsync(new ContainersListParameters { All = true });
            var staleBackup = allContainers.FirstOrDefault(c =>
                c.ID != containerConfiguration.ID &&
                c.Names.Any(n => string.Equals(n.TrimStart('/'), backupName, StringComparison.OrdinalIgnoreCase)));

            if (staleBackup is not null)
            {
                await _dockerClient.Containers.RemoveContainerAsync(staleBackup.ID, new ContainerRemoveParameters { Force = true });
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

            await _dockerClient.Containers.RenameContainerAsync(
                containerConfiguration.ID,
                new ContainerRenameParameters { NewName = backupName },
                CancellationToken.None);

            await _dockerClient.Containers.StopContainerAsync(containerConfiguration.ID, new ContainerStopParameters());

            var createdContainer = await _dockerClient.Containers.CreateContainerAsync(createParams);
            newContainerId = createdContainer.ID;

            await AttachSecondaryNetworksAsync(createdContainer.ID, originalName, secondaryNetworks);

            var successfulContainerStart = await _dockerClient.Containers.StartContainerAsync(createdContainer.ID, new ContainerStartParameters());
            if (!successfulContainerStart)
                throw new InvalidOperationException($"Failed to start replacement container {originalName}");

            await VerifyContainerStartupAsync(createdContainer.ID, originalName);

            try
            {
                await _dockerClient.Containers.RemoveContainerAsync(containerConfiguration.ID, new ContainerRemoveParameters { Force = true });
            }
            catch (Exception removeEx)
            {
                _logger.LogWarning(removeEx, "Failed to remove backup container {BackupName}, manual intervention is needed", backupName);
            }

            if (_pruneImages)
            {
                await _dockerClient.Images.PruneImagesAsync(new ImagesPruneParameters());
            }

            summary.Successes.Add(new ContainerUpdateResult
            {
                ContainerName = originalName,
                ImageName = baseImageName,
                NewTag = targetTag,
                Success = true
            });

            _logger.LogInformation("Successfully self-updated {Container} to {Tag}", originalName, targetTag);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Self-update failed while replacing {Container}. Rolling back to previous container.", originalName);

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
                if (containerConfiguration.Config is not null
                    && !string.IsNullOrEmpty(containerConfiguration.Config.Hostname)
                    && containerConfiguration.ID.StartsWith(containerConfiguration.Config.Hostname, StringComparison.OrdinalIgnoreCase))
                {
                    containerConfiguration.Config.Hostname = string.Empty;
                }

                var (primaryNetworkingConfig, secondaryNetworks) = SplitPrimaryAndSecondaryNetworks(containerConfiguration);

                var createParams = new CreateContainerParameters(containerConfiguration.Config)
                {
                    Image = $"{baseImageName}:{targetTag}",
                    HostConfig = containerConfiguration.HostConfig,
                    NetworkingConfig = primaryNetworkingConfig,
                    Name = originalName
                };
                
                // Rename the target container for update, in order to avoid conflicts
                await _dockerClient.Containers.RenameContainerAsync(containerForUpdate.ID, new ContainerRenameParameters { NewName = backupName}, 
                    CancellationToken.None);
                
                // Stop the target container for update
                await _dockerClient.Containers.StopContainerAsync(containerForUpdate.ID, new ContainerStopParameters());

                var createdContainer = await _dockerClient.Containers.CreateContainerAsync(createParams);
                newContainerId = createdContainer.ID;

                await AttachSecondaryNetworksAsync(createdContainer.ID, originalName, secondaryNetworks);
                
                // Start the updated container
                var successfulContainerStart = await _dockerClient.Containers.StartContainerAsync(createdContainer.ID, new ContainerStartParameters());
                
                if (!successfulContainerStart)
                    throw new InvalidOperationException($"Failed to start container {originalName}");
                
                await VerifyContainerStartupAsync(createdContainer.ID, originalName);
                
                // Add entry for rollback in case of a failure
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
                await _dockerClient.Containers.RemoveContainerAsync(checkpoint.OldId, new ContainerRemoveParameters { Force = true });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove backup container {BackupName}, manual intervention is needed", checkpoint.BackupName);
            }
        }
    }

    private async Task VerifyContainerStartupAsync(string containerId, string containerName)
    {
        var initialInspect = await _dockerClient.Containers.InspectContainerAsync(containerId);

        EnsureProcessRunningAndNotRestarted(initialInspect, containerName, initialPid: 0, initialStartedAt: null);

        var hasHealthcheck = initialInspect.State?.Health is not null
            && !string.IsNullOrWhiteSpace(initialInspect.State.Health.Status)
            && !string.Equals(initialInspect.State.Health.Status, "none", StringComparison.OrdinalIgnoreCase);

        if (hasHealthcheck)
        {
            _logger.LogInformation("Container {Container} defines a Docker HEALTHCHECK — monitoring health status (timeout: {Timeout}s)",
                containerName, _healthCheckTimeout.TotalSeconds);

            await VerifyWithDockerHealthcheckAsync(containerId, containerName, initialInspect);
        }
        else
        {
            _logger.LogInformation("Container {Container} has no Docker HEALTHCHECK — monitoring process stability (PID/restarts) for {Window}s",
                containerName, _stabilityWindow.TotalSeconds);

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
                _logger.LogInformation("Container {Container} reported healthy after {Elapsed:F1}s",
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

            if (stopwatch.Elapsed >= _healthCheckTimeout)
            {
                throw new InvalidOperationException(
                    $"Container {containerName} healthcheck timed out after {_healthCheckTimeout.TotalSeconds:F0}s (last status: '{healthStatus}')");
            }

            await Task.Delay(_pollInterval);
            currentInspect = await _dockerClient.Containers.InspectContainerAsync(containerId);
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

        while (stopwatch.Elapsed < _stabilityWindow)
        {
            var remaining = _stabilityWindow - stopwatch.Elapsed;
            var delay = remaining < _pollInterval ? remaining : _pollInterval;

            if (delay > TimeSpan.Zero)
                await Task.Delay(delay);

            var currentInspect = await _dockerClient.Containers.InspectContainerAsync(containerId);
            EnsureProcessRunningAndNotRestarted(currentInspect, containerName, initialPid, initialStartedAt);
        }
    }

    private static void EnsureProcessRunningAndNotRestarted(
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

                if (checkpoint.OriginalNetworks is not null && checkpoint.OriginalNetworks.Count > 1)
                {
                    var currentNetworks = oldContainerInspect.NetworkSettings?.Networks;
                    foreach (var (networkName, endpoint) in checkpoint.OriginalNetworks)
                    {
                        if (currentNetworks is null || !currentNetworks.ContainsKey(networkName))
                        {
                            var networkTarget = !string.IsNullOrWhiteSpace(endpoint.NetworkID)
                                ? endpoint.NetworkID
                                : networkName;

                            await _dockerClient.Networks.ConnectNetworkAsync(networkTarget, new NetworkConnectParameters
                            {
                                Container = checkpoint.OldId,
                                EndpointConfig = SanitizeEndpointSettings(endpoint, checkpoint.OldId)
                            });
                        }
                    }
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

    private static (NetworkingConfig PrimaryNetworkingConfig, List<(string NetworkName, EndpointSettings Endpoint)> SecondaryNetworks)
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

            _logger.LogInformation("Connecting container {Container} to additional network {Network}", containerName, networkName);

            await _dockerClient.Networks.ConnectNetworkAsync(networkTarget, new NetworkConnectParameters
            {
                Container = containerId,
                EndpointConfig = endpoint
            });
        }
    }

    private static EndpointSettings SanitizeEndpointSettings(EndpointSettings source, string oldContainerId)
    {
        var oldShortId = !string.IsNullOrEmpty(oldContainerId) && oldContainerId.Length >= 12
            ? oldContainerId[..12]
            : oldContainerId;

        var sanitizedAliases = source.Aliases?
            .Where(a => !string.Equals(a, oldShortId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return new EndpointSettings
        {
            IPAMConfig = source.IPAMConfig,
            Links = source.Links,
            Aliases = sanitizedAliases,
            NetworkID = source.NetworkID,
            DriverOpts = source.DriverOpts
        };
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