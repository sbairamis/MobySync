using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.DependencyInjection;
using MobySync.Models;

namespace MobySync.Helpers;

public class DockerHelper
{
    public const string SelfUpdaterContainerName = "mobysync-self-updater";

    private readonly IDockerClient _dockerClient;
    private readonly ILogger<DockerHelper> _logger;
    private readonly CredentialsHelper _credentialsHelper;
    private readonly RegistryHelper _registryHelper;
    private readonly bool _pruneImages;
    private readonly bool _selfUpdate;
    private readonly string _selfHostname;
    private readonly HashSet<string> _excludedContainers;
    private readonly ImageTagResolver _tagResolver;
    private readonly DependencyGraphBuilder _graphBuilder;
    private readonly ContainerHealthVerifier _healthVerifier;
    private readonly ContainerSwapper _containerSwapper;

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
            : 30;

        var defaultHealthTimeoutSeconds = int.TryParse(Environment.GetEnvironmentVariable("HEALTHCHECK_TIMEOUT_SECONDS"), out var parsedHealthTimeout) && parsedHealthTimeout > 0
            ? parsedHealthTimeout
            : 60;

        var resolvedStabilityWindow = stabilityWindow ?? TimeSpan.FromSeconds(defaultStabilitySeconds);
        var resolvedHealthCheckTimeout = healthCheckTimeout ?? TimeSpan.FromSeconds(defaultHealthTimeoutSeconds);
        var resolvedPollInterval = pollInterval ?? TimeSpan.FromSeconds(1);

        _selfHostname = selfHostname ?? Environment.MachineName;

        _pruneImages = bool.TryParse(Environment.GetEnvironmentVariable("PRUNE_IMAGES"), out var pruneImagesParsed)
            && pruneImagesParsed;

        _selfUpdate = !bool.TryParse(Environment.GetEnvironmentVariable("SELF_UPDATE"), out var selfUpdateParsed)
            || selfUpdateParsed;

        var excludedRaw = Environment.GetEnvironmentVariable("EXCLUDED_CONTAINERS") ?? string.Empty;

        _excludedContainers = excludedRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (_excludedContainers.Count > 0)
            _logger.LogInformation("Configured exclusions: {Excluded}", string.Join(", ", _excludedContainers));

        _tagResolver = new ImageTagResolver(_logger);
        _graphBuilder = new DependencyGraphBuilder();
        _healthVerifier = new ContainerHealthVerifier(
            _dockerClient,
            _logger,
            resolvedStabilityWindow,
            resolvedHealthCheckTimeout,
            resolvedPollInterval);
        _containerSwapper = new ContainerSwapper(_dockerClient, _healthVerifier, _logger);
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

            var (containersGroups, cyclicGroups) = _graphBuilder.GroupDependentContainersSafely(monitoredContainers);

            foreach (var (cyclicContainers, errorMessage) in cyclicGroups)
            {
                _logger.LogError("Skipping dependency group due to circular dependency: {Error}", errorMessage);
                foreach (var container in cyclicContainers)
                {
                    updateSummary.Skipped.Add(new ContainerUpdateResult
                    {
                        ContainerName = container.Names.First().Replace("/", ""),
                        ImageName = ImageTagResolver.FetchBaseImageName(container.Image),
                        ErrorMessage = errorMessage,
                        Success = false
                    });
                }
            }

            foreach (var containerGroup in containersGroups)
            {
                var outdatedContainers = await PullGroupImages(containerGroup.Value, updateSummary);

                if (outdatedContainers is null || !outdatedContainers.Any())
                    continue;

                _logger.LogInformation("Group update execution order: {Order}",
                    string.Join(" -> ", outdatedContainers.Select(container => container.Names.First().Replace("/", ""))));

                await _containerSwapper.ReplaceContainerGroupAsync(outdatedContainers, _tagResolver.ResolveTargetTag, updateSummary);
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

    public async Task<bool> IsMonitoredContainerRunning(string containerName)
    {
        var monitoredContainers = await GetMonitoredContainers();
        return monitoredContainers is not null && monitoredContainers.Any(c =>
            string.Equals(c.Names.First().TrimStart('/'), containerName, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<UpdateCheckSummary> CheckForAvailableUpdates()
    {
        var checkSummary = new UpdateCheckSummary();
        var startTime = DateTime.Now;

        try
        {
            var monitoredContainers = await GetMonitoredContainers();

            if (monitoredContainers is null)
                return checkSummary;

            foreach (var container in monitoredContainers)
            {
                var baseImageName = ImageTagResolver.FetchBaseImageName(container.Image);
                var containerName = container.Names.First().Replace("/", "");
                var targetTag = _tagResolver.ResolveTargetTag(container, baseImageName);

                if (ImageTagResolver.IsPinnedVersionTag(targetTag))
                {
                    checkSummary.Skipped.Add(new ContainerUpdateResult
                    {
                        ContainerName = containerName,
                        ImageName = baseImageName,
                        ErrorMessage = $"Pinned version tag `{targetTag}` — skipping auto-update",
                        Success = false
                    });
                    continue;
                }

                if (await IsAutoRemoveContainerAsync(container.ID))
                {
                    _logger.LogInformation("Container {Container} has AutoRemove (--rm) enabled, skipping", containerName);
                    checkSummary.Skipped.Add(new ContainerUpdateResult
                    {
                        ContainerName = containerName,
                        ImageName = baseImageName,
                        ErrorMessage = "Container has AutoRemove (--rm) enabled — cannot safely backup or roll back",
                        Success = false
                    });
                    continue;
                }

                ImageInspectResponse runningImageInfo;
                try
                {
                    runningImageInfo = await _dockerClient.Images.InspectImageAsync(container.ImageID);
                    if (runningImageInfo.RepoDigests == null || !runningImageInfo.RepoDigests.Any())
                    {
                        checkSummary.Skipped.Add(new ContainerUpdateResult
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
                    _logger.LogError(ex, "Dry-run check failed to inspect image for {Container}", containerName);
                    checkSummary.Errors.Add(new ContainerUpdateResult
                    {
                        ContainerName = containerName,
                        ImageName = baseImageName,
                        ErrorMessage = $"Image inspect failed: {ex.Message}",
                        Success = false
                    });
                    continue;
                }

                var authCredentials = await _credentialsHelper.FetchCredentials(baseImageName);

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

                    _logger.LogError(checkEx, "Dry-run registry check failed for {Image}:{Tag}", baseImageName, targetTag);
                    checkSummary.Errors.Add(new ContainerUpdateResult
                    {
                        ContainerName = containerName,
                        ImageName = baseImageName,
                        ErrorMessage = checkMsg,
                        Success = false
                    });
                    continue;
                }

                var runningDigests = ExtractSha256Digests(runningImageInfo.RepoDigests);

                if (remoteDigest == null || runningDigests.Contains(remoteDigest))
                {
                    checkSummary.UpToDate.Add(containerName);
                    continue;
                }

                checkSummary.UpdatesAvailable.Add(new ContainerCheckResult
                {
                    ContainerName = containerName,
                    ImageName = baseImageName,
                    TargetTag = targetTag,
                    CurrentDigest = runningDigests.FirstOrDefault() ?? string.Empty,
                    RemoteDigest = remoteDigest
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An unexpected error occurred during dry-run update check.");
        }
        finally
        {
            checkSummary.TotalDuration = DateTime.Now - startTime;
        }

        return checkSummary;
    }

    public async Task<UpdateSummary> CheckForImageUpdatesForContainer(string containerName, bool includeDependencies = true)
    {
        var updateSummary = new UpdateSummary();
        var startTime = DateTime.Now;

        try
        {
            var monitoredContainers = await GetMonitoredContainers();

            if (monitoredContainers is null)
                return updateSummary;

            var targetContainer = monitoredContainers.FirstOrDefault(c =>
                string.Equals(c.Names.First().TrimStart('/'), containerName, StringComparison.OrdinalIgnoreCase));

            if (targetContainer is null)
            {
                _logger.LogWarning("Targeted update skipped: monitored running container '{Container}' was not found", containerName);
                return updateSummary;
            }

            IList<ContainerListResponse> targetGroup;
            if (includeDependencies)
            {
                var (containersGroups, cyclicGroups) = _graphBuilder.GroupDependentContainersSafely(monitoredContainers);
                var cyclicMatch = cyclicGroups.FirstOrDefault(g => g.Containers.Any(c => c.ID == targetContainer.ID));
                if (cyclicMatch.Containers is not null)
                {
                    _logger.LogError("Skipping targeted update for {Container} due to circular dependency: {Error}", containerName, cyclicMatch.ErrorMessage);
                    foreach (var container in cyclicMatch.Containers)
                    {
                        updateSummary.Skipped.Add(new ContainerUpdateResult
                        {
                            ContainerName = container.Names.First().Replace("/", ""),
                            ImageName = ImageTagResolver.FetchBaseImageName(container.Image),
                            ErrorMessage = cyclicMatch.ErrorMessage,
                            Success = false
                        });
                    }
                    return updateSummary;
                }

                targetGroup = containersGroups.Values.First(group => group.Any(c => c.ID == targetContainer.ID));
            }
            else
            {
                targetGroup = [targetContainer];
            }

            var outdatedContainers = await PullGroupImages(targetGroup, updateSummary);

            if (outdatedContainers is not null && outdatedContainers.Any())
            {
                _logger.LogInformation("Targeted group update execution order: {Order}",
                    string.Join(" -> ", outdatedContainers.Select(container => container.Names.First().Replace("/", ""))));

                await _containerSwapper.ReplaceContainerGroupAsync(outdatedContainers, _tagResolver.ResolveTargetTag, updateSummary);

                if (_pruneImages)
                {
                    await _dockerClient.Images.PruneImagesAsync(new ImagesPruneParameters());
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An unexpected error occurred while trying to run targeted update for {Container}.", containerName);
        }
        finally
        {
            updateSummary.TotalDuration = DateTime.Now - startTime;

            _logger.LogInformation("Targeted update cycle for {Container} finished in {Duration}. Successes: {Success}, Rollbacks: {Rollback}, Failed Pulls: {Failed}",
                containerName, updateSummary.TotalDuration.ToString(@"hh\:mm\:ss"), updateSummary.Successes.Count, updateSummary.Rollbacks.Count, updateSummary.FailedPulls.Count);
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

            var baseImageName = ImageTagResolver.FetchBaseImageName(self.Image);
            var targetTag = _tagResolver.ResolveTargetTag(self, baseImageName);

            if (ImageTagResolver.IsPinnedVersionTag(targetTag))
            {
                _logger.LogInformation("Self container {Container} uses pinned version tag ({Tag}), skipping self-update", selfName, targetTag);
                return null;
            }

            if (await IsAutoRemoveContainerAsync(self.ID))
            {
                _logger.LogInformation("Self container {Container} has AutoRemove (--rm) enabled, skipping self-update", selfName);
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

            var runningDigests = ExtractSha256Digests(runningImageInfo.RepoDigests);

            if (runningDigests.Contains(remoteDigest))
            {
                _logger.LogInformation("Self container {Container} is already running the latest version", selfName);
                return null;
            }

            _logger.LogInformation("New image available for self ({Image}:{Tag}) — pulling before spawning self-updater", baseImageName, targetTag);

            await PullImageWithAnonymousFallbackAsync(baseImageName, targetTag, authCredentials);
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
        var inheritedPrefixes = new[]
        {
            "DISCORD_WEBHOOK_URL=",
            "WEBHOOK_URL=",
            "TZ=",
            "PRUNE_IMAGES=",
            "STABILITY_WINDOW_SECONDS=",
            "HEALTHCHECK_TIMEOUT_SECONDS="
        };

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

    public Task<UpdateSummary> ExecuteSelfUpdateSwap(string targetContainerId, string? targetImageOverride = null) =>
        _containerSwapper.ExecuteSelfUpdateSwapAsync(targetContainerId, targetImageOverride, _pruneImages);

    private async Task<IList<ContainerListResponse>?> PullGroupImages(IList<ContainerListResponse> monitoredContainers, UpdateSummary summary)
    {
        var outdatedContainers = new List<ContainerListResponse>();

        foreach (var container in monitoredContainers)
        {
            var baseImageName = ImageTagResolver.FetchBaseImageName(container.Image);
            var containerName = container.Names.First().Replace("/", "");
            var targetTag = _tagResolver.ResolveTargetTag(container, baseImageName);

            if (ImageTagResolver.IsPinnedVersionTag(targetTag))
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

            if (await IsAutoRemoveContainerAsync(container.ID))
            {
                _logger.LogInformation("Container {Container} has AutoRemove (--rm) enabled, skipping", containerName);
                summary.Skipped.Add(new ContainerUpdateResult
                {
                    ContainerName = containerName,
                    ImageName = baseImageName,
                    ErrorMessage = "Container has AutoRemove (--rm) enabled — cannot safely backup or roll back",
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

            var runningDigests = ExtractSha256Digests(runningImageInfo.RepoDigests);

            if (runningDigests.Contains(remoteDigest))
            {
                _logger.LogInformation("Container {Container} is already running the latest version", containerName);
                summary.UpToDate.Add(containerName);
                continue;
            }

            _logger.LogInformation("New image available for {Image}:{Tag} — pulling", baseImageName, targetTag);

            try
            {
                await PullImageWithAnonymousFallbackAsync(baseImageName, targetTag, authCredentials);
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

    private async Task PullImageWithAnonymousFallbackAsync(string baseImageName, string targetTag, AuthConfig authCredentials)
    {
        try
        {
            var progress = new PullProgressReporter();
            await _dockerClient.Images.CreateImageAsync(new ImagesCreateParameters
            {
                FromImage = baseImageName,
                Tag = targetTag
            }, authCredentials, progress);
            progress.ThrowIfFailed(baseImageName, targetTag);
        }
        catch (Exception pullEx) when (IsAuthError(pullEx))
        {
            // Daemon may be using its own stale cached credentials; retry with an empty AuthConfig
            // so the daemon has no server address hint to look up its own credential store.
            _logger.LogWarning("Pull with credentials failed for {Image} — retrying anonymously", baseImageName);
            var retryProgress = new PullProgressReporter();
            await _dockerClient.Images.CreateImageAsync(new ImagesCreateParameters
            {
                FromImage = baseImageName,
                Tag = targetTag
            }, new AuthConfig(), retryProgress);
            retryProgress.ThrowIfFailed(baseImageName, targetTag);
        }
    }

    private sealed class PullProgressReporter : IProgress<JSONMessage>
    {
        public string? ErrorMessage { get; private set; }

        public void Report(JSONMessage value)
        {
            if (!string.IsNullOrWhiteSpace(value.Error?.Message))
                ErrorMessage = value.Error.Message;
        }

        public void ThrowIfFailed(string baseImageName, string targetTag)
        {
            if (!string.IsNullOrWhiteSpace(ErrorMessage))
            {
                throw new InvalidOperationException(
                    $"Docker stream error while pulling {baseImageName}:{targetTag}: {ErrorMessage}");
            }
        }
    }

    private async Task<bool> IsAutoRemoveContainerAsync(string containerId)
    {
        var inspect = await _dockerClient.Containers.InspectContainerAsync(containerId);
        return inspect.HostConfig?.AutoRemove == true;
    }

    private static HashSet<string> ExtractSha256Digests(IEnumerable<string> repoDigests) =>
        repoDigests
            .Where(d => d.Contains('@'))
            .Select(d => d.Split('@')[1])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool IsAuthError(Exception ex) =>
        ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("access denied", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("forbidden", StringComparison.OrdinalIgnoreCase);
}
