using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MobySync.Helpers;

namespace MobySync.Tests;

[Collection("SequentialEnvTests")]
public class SelfUpdateTests
{
    private const string SelfId = "a1b2c3d4e5f6111122223333";
    private const string SelfHostname = "a1b2c3d4e5f6";
    private const string SelfImage = "ghcr.io/sbairamis/mobysync:latest";
    private const string SelfBaseImage = "ghcr.io/sbairamis/mobysync";
    private const string SelfImageId = "sha256:self-image-old";

    private static SimulatedContainer CreateDefaultSelfContainer(string image = SelfImage) => new()
    {
        Id = SelfId,
        Name = "mobysync",
        Image = image,
        ImageId = SelfImageId,
        Running = true,
        Hostname = SelfHostname,
        Env =
        [
            "API_KEY=secret",
            "DISCORD_WEBHOOK_URL=https://discord.example/webhook",
            "WEBHOOK_URL=https://generic.example/webhook",
            "TZ=Europe/Athens"
        ],
        HostConfig = new HostConfig
        {
            Binds = ["/var/run/docker.sock:/var/run/docker.sock"],
            NetworkMode = "bridge"
        }
    };

    [Fact]
    public async Task CheckAndTriggerSelfUpdate_WhenNewImageAvailable_PullsImageAndSpawnsEphemeralUpdater()
    {
        Environment.SetEnvironmentVariable("SELF_UPDATE", "true");
        Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", "");

        var state = new SimulatedDockerState();
        state.Containers[SelfId] = CreateDefaultSelfContainer();
        state.ImageDigests[SelfImageId] = [$"{SelfBaseImage}@sha256:old-digest-111"];

        var registry = new FakeRegistryHelper(new Dictionary<string, string?>
        {
            [SelfImage] = "sha256:new-digest-222"
        });

        var helper = new DockerHelper(
            NullLogger<DockerHelper>.Instance,
            new FakeCredentialsHelper(),
            registry,
            state.CreateClient(),
            TimeSpan.FromMilliseconds(5),
            SelfHostname);

        var failureSummary = await helper.CheckAndTriggerSelfUpdate();

        Assert.Null(failureSummary);
        Assert.Single(state.PulledImages);
        Assert.Equal((SelfBaseImage, "latest"), state.PulledImages[0]);

        var updater = state.Containers.Values.SingleOrDefault(c => c.Name == DockerHelper.SelfUpdaterContainerName);
        Assert.NotNull(updater);
        Assert.True(updater.Running);
        Assert.Equal(SelfImage, updater.Image);
        Assert.True(updater.HostConfig.AutoRemove);
        Assert.Contains("/var/run/docker.sock:/var/run/docker.sock", updater.HostConfig.Binds);
        Assert.Contains($"MOBYSYNC_SELF_UPDATE_TARGET={SelfId}", updater.Env);
        Assert.Contains($"MOBYSYNC_SELF_TARGET_IMAGE={SelfImage}", updater.Env);
        Assert.Contains("DISCORD_WEBHOOK_URL=https://discord.example/webhook", updater.Env);
        Assert.Contains("WEBHOOK_URL=https://generic.example/webhook", updater.Env);
        Assert.Contains("TZ=Europe/Athens", updater.Env);
    }

    [Fact]
    public async Task CheckAndTriggerSelfUpdate_WhenAlreadyUpToDate_DoesNotPullOrSpawnUpdater()
    {
        Environment.SetEnvironmentVariable("SELF_UPDATE", "true");
        Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", "");

        var state = new SimulatedDockerState();
        state.Containers[SelfId] = CreateDefaultSelfContainer();
        state.ImageDigests[SelfImageId] = [$"{SelfBaseImage}@sha256:same-digest"];

        var registry = new FakeRegistryHelper(new Dictionary<string, string?>
        {
            [SelfImage] = "sha256:same-digest"
        });

        var helper = new DockerHelper(
            NullLogger<DockerHelper>.Instance,
            new FakeCredentialsHelper(),
            registry,
            state.CreateClient(),
            TimeSpan.FromMilliseconds(5),
            SelfHostname);

        var failureSummary = await helper.CheckAndTriggerSelfUpdate();

        Assert.Null(failureSummary);
        Assert.Empty(state.PulledImages);
        Assert.DoesNotContain(state.Containers.Values, c => c.Name == DockerHelper.SelfUpdaterContainerName);
    }

    [Fact]
    public async Task CheckAndTriggerSelfUpdate_WhenDisabledViaEnvVar_SkipsSelfUpdate()
    {
        Environment.SetEnvironmentVariable("SELF_UPDATE", "false");
        Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", "");

        try
        {
            var state = new SimulatedDockerState();
            state.Containers[SelfId] = CreateDefaultSelfContainer();
            state.ImageDigests[SelfImageId] = [$"{SelfBaseImage}@sha256:old-digest"];

            var registry = new FakeRegistryHelper(new Dictionary<string, string?>
            {
                [SelfImage] = "sha256:new-digest"
            });

            var helper = new DockerHelper(
                NullLogger<DockerHelper>.Instance,
                new FakeCredentialsHelper(),
                registry,
                state.CreateClient(),
                TimeSpan.FromMilliseconds(5),
                SelfHostname);

            var failureSummary = await helper.CheckAndTriggerSelfUpdate();

            Assert.Null(failureSummary);
            Assert.Empty(state.PulledImages);
            Assert.DoesNotContain(state.Containers.Values, c => c.Name == DockerHelper.SelfUpdaterContainerName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SELF_UPDATE", "true");
        }
    }

    [Fact]
    public async Task CheckAndTriggerSelfUpdate_WhenSelfInExcludedContainers_SkipsSelfUpdate()
    {
        Environment.SetEnvironmentVariable("SELF_UPDATE", "true");
        Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", "mobysync");

        try
        {
            var state = new SimulatedDockerState();
            state.Containers[SelfId] = CreateDefaultSelfContainer();
            state.ImageDigests[SelfImageId] = [$"{SelfBaseImage}@sha256:old-digest"];

            var registry = new FakeRegistryHelper(new Dictionary<string, string?>
            {
                [SelfImage] = "sha256:new-digest"
            });

            var helper = new DockerHelper(
                NullLogger<DockerHelper>.Instance,
                new FakeCredentialsHelper(),
                registry,
                state.CreateClient(),
                TimeSpan.FromMilliseconds(5),
                SelfHostname);

            var failureSummary = await helper.CheckAndTriggerSelfUpdate();

            Assert.Null(failureSummary);
            Assert.Empty(state.PulledImages);
            Assert.DoesNotContain(state.Containers.Values, c => c.Name == DockerHelper.SelfUpdaterContainerName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", "");
        }
    }

    [Fact]
    public async Task CheckAndTriggerSelfUpdate_WhenPinnedVersionTag_SkipsSelfUpdate()
    {
        Environment.SetEnvironmentVariable("SELF_UPDATE", "true");
        Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", "");

        var state = new SimulatedDockerState();
        state.Containers[SelfId] = CreateDefaultSelfContainer($"{SelfBaseImage}:v1.0.4");
        state.ImageDigests[SelfImageId] = [$"{SelfBaseImage}@sha256:old-digest"];

        var registry = new FakeRegistryHelper(new Dictionary<string, string?>
        {
            [$"{SelfBaseImage}:v1.0.4"] = "sha256:new-digest"
        });

        var helper = new DockerHelper(
            NullLogger<DockerHelper>.Instance,
            new FakeCredentialsHelper(),
            registry,
            state.CreateClient(),
            TimeSpan.FromMilliseconds(5),
            SelfHostname);

        var failureSummary = await helper.CheckAndTriggerSelfUpdate();

        Assert.Null(failureSummary);
        Assert.Empty(state.PulledImages);
        Assert.DoesNotContain(state.Containers.Values, c => c.Name == DockerHelper.SelfUpdaterContainerName);
    }

    [Fact]
    public async Task ExecuteSelfUpdateSwap_WhenNewContainerHealthy_ReplacesSelfAndRemovesBackup()
    {
        Environment.SetEnvironmentVariable("PRUNE_IMAGES", "true");

        try
        {
            var state = new SimulatedDockerState();
            state.Containers[SelfId] = CreateDefaultSelfContainer();

            var helper = new DockerHelper(
                NullLogger<DockerHelper>.Instance,
                new FakeCredentialsHelper(),
                new FakeRegistryHelper(new Dictionary<string, string?>()),
                state.CreateClient(),
                TimeSpan.FromMilliseconds(5),
                "helper-container-hostname");

            var summary = await helper.ExecuteSelfUpdateSwap(SelfId, SelfImage);

            Assert.Single(summary.Successes);
            Assert.Empty(summary.Rollbacks);
            Assert.Empty(summary.FailedPulls);
            Assert.Equal("mobysync", summary.Successes[0].ContainerName);
            Assert.Equal(SelfBaseImage, summary.Successes[0].ImageName);
            Assert.Equal("latest", summary.Successes[0].NewTag);

            // Old container backup should be removed
            Assert.False(state.Containers.ContainsKey(SelfId));
            Assert.DoesNotContain(state.Containers.Values, c => c.Name == "mobysync-backup");

            // New container should be running with the original name 'mobysync' and its own fresh short-ID hostname
            var replacement = Assert.Single(state.Containers.Values, c => c.Name == "mobysync");
            Assert.True(replacement.Running);
            Assert.NotEqual(SelfId, replacement.Id);
            Assert.StartsWith(replacement.Hostname, replacement.Id);
            Assert.True(state.PruneCalled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PRUNE_IMAGES", "false");
        }
    }

    [Fact]
    public async Task ExecuteSelfUpdateSwap_WhenNewContainerCrashes_RollsBackToOriginalContainer()
    {
        var state = new SimulatedDockerState();
        state.Containers[SelfId] = CreateDefaultSelfContainer();

        // Simulate the newly created container crashing immediately after starting
        state.OnStartContainerCrash = container => container.Id != SelfId;

        var helper = new DockerHelper(
            NullLogger<DockerHelper>.Instance,
            new FakeCredentialsHelper(),
            new FakeRegistryHelper(new Dictionary<string, string?>()),
            state.CreateClient(),
            TimeSpan.FromMilliseconds(5),
            "helper-container-hostname");

        var summary = await helper.ExecuteSelfUpdateSwap(SelfId, SelfImage);

        Assert.Empty(summary.Successes);
        Assert.Single(summary.Rollbacks);
        Assert.Equal("mobysync", summary.Rollbacks[0].ContainerName);
        Assert.Contains("crashed after starting", summary.Rollbacks[0].ErrorMessage);

        // Original container must be restored back to 'mobysync' and running
        Assert.True(state.Containers.ContainsKey(SelfId));
        var restored = state.Containers[SelfId];
        Assert.Equal("mobysync", restored.Name);
        Assert.True(restored.Running);

        // Failed replacement container must have been removed
        Assert.Single(state.Containers);
    }

    [Fact]
    public async Task UpdateCoordinator_FullCycleAndSelfUpdateMode_UpdatesOtherContainersFirstThenSelfUpdates()
    {
        Environment.SetEnvironmentVariable("SELF_UPDATE", "true");
        Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", "");

        var state = new SimulatedDockerState();
        state.Containers[SelfId] = CreateDefaultSelfContainer();
        state.ImageDigests[SelfImageId] = [$"{SelfBaseImage}@sha256:self-old"];

        const string appId = "b2c3d4e5f6a1999988887777";
        const string appImageId = "sha256:nginx-old";
        state.Containers[appId] = new SimulatedContainer
        {
            Id = appId,
            Name = "web-app",
            Image = "nginx:latest",
            ImageId = appImageId,
            Running = true,
            Hostname = appId[..12]
        };
        state.ImageDigests[appImageId] = ["nginx@sha256:nginx-old-digest"];

        var registry = new FakeRegistryHelper(new Dictionary<string, string?>
        {
            ["nginx:latest"] = "sha256:nginx-new-digest",
            [SelfImage] = "sha256:self-new"
        });

        var dockerHelper = new DockerHelper(
            NullLogger<DockerHelper>.Instance,
            new FakeCredentialsHelper(),
            registry,
            state.CreateClient(),
            TimeSpan.FromMilliseconds(5),
            SelfHostname);

        var notificationHelper = new FakeNotificationHelper();
        var coordinator = new UpdateCoordinator(
            dockerHelper,
            [notificationHelper],
            NullLogger<UpdateCoordinator>.Instance);

        // Phase 1: Execute scheduled update cycle inside current MobySync
        await coordinator.ExecuteScheduledUpdate();

        Assert.Equal(1, notificationHelper.UpdateStartedCount);
        Assert.Single(notificationHelper.SentSummaries);
        Assert.Single(notificationHelper.SentSummaries[0].Successes);
        Assert.Equal("web-app", notificationHelper.SentSummaries[0].Successes[0].ContainerName);

        // Self-updater helper container should now be spawned and running
        var spawnedHelper = state.Containers.Values.SingleOrDefault(c => c.Name == DockerHelper.SelfUpdaterContainerName);
        Assert.NotNull(spawnedHelper);
        Assert.True(spawnedHelper.Running);

        // Phase 2: Simulate the spawned helper container executing ExecuteSelfUpdate
        await coordinator.ExecuteSelfUpdate(SelfId, SelfImage);

        Assert.Equal(2, notificationHelper.SentSummaries.Count);
        var selfUpdateSummary = notificationHelper.SentSummaries[1];
        Assert.Single(selfUpdateSummary.Successes);
        Assert.Equal("mobysync", selfUpdateSummary.Successes[0].ContainerName);

        // Verify new mobysync container replaced the old one
        var newSelf = Assert.Single(state.Containers.Values, c => c.Name == "mobysync");
        Assert.NotEqual(SelfId, newSelf.Id);
        Assert.True(newSelf.Running);
    }

    [Fact]
    public async Task CheckAndTriggerSelfUpdate_WhenStaleHelperContainerExists_RemovesStaleHelperBeforeSpawningNewOne()
    {
        Environment.SetEnvironmentVariable("SELF_UPDATE", "true");
        Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", "");

        var state = new SimulatedDockerState();
        state.Containers[SelfId] = CreateDefaultSelfContainer();
        state.ImageDigests[SelfImageId] = [$"{SelfBaseImage}@sha256:old-digest"];

        const string staleHelperId = "stale-helper-id-000011112222";
        state.Containers[staleHelperId] = new SimulatedContainer
        {
            Id = staleHelperId,
            Name = DockerHelper.SelfUpdaterContainerName,
            Image = SelfImage,
            Running = false
        };

        var registry = new FakeRegistryHelper(new Dictionary<string, string?>
        {
            [SelfImage] = "sha256:new-digest"
        });

        var helper = new DockerHelper(
            NullLogger<DockerHelper>.Instance,
            new FakeCredentialsHelper(),
            registry,
            state.CreateClient(),
            TimeSpan.FromMilliseconds(5),
            SelfHostname);

        await helper.CheckAndTriggerSelfUpdate();

        Assert.False(state.Containers.ContainsKey(staleHelperId));
        var newHelper = Assert.Single(state.Containers.Values, c => c.Name == DockerHelper.SelfUpdaterContainerName);
        Assert.NotEqual(staleHelperId, newHelper.Id);
        Assert.True(newHelper.Running);
    }

    [Fact]
    public async Task CheckAndTriggerSelfUpdate_WhenPullFails_ReturnsFailureSummaryWithoutSpawningHelper()
    {
        Environment.SetEnvironmentVariable("SELF_UPDATE", "true");
        Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", "");

        var state = new SimulatedDockerState();
        state.Containers[SelfId] = CreateDefaultSelfContainer();
        state.ImageDigests[SelfImageId] = [$"{SelfBaseImage}@sha256:old-digest"];
        state.OnPullImage = _ => throw new InvalidOperationException("network timeout while pulling");

        var registry = new FakeRegistryHelper(new Dictionary<string, string?>
        {
            [SelfImage] = "sha256:new-digest"
        });

        var helper = new DockerHelper(
            NullLogger<DockerHelper>.Instance,
            new FakeCredentialsHelper(),
            registry,
            state.CreateClient(),
            TimeSpan.FromMilliseconds(5),
            SelfHostname);

        var failureSummary = await helper.CheckAndTriggerSelfUpdate();

        Assert.NotNull(failureSummary);
        Assert.Single(failureSummary.FailedPulls);
        Assert.Contains("network timeout while pulling", failureSummary.FailedPulls[0].ErrorMessage);
        Assert.DoesNotContain(state.Containers.Values, c => c.Name == DockerHelper.SelfUpdaterContainerName);
    }

    [Fact]
    public async Task ExecuteSelfUpdateSwap_WhenStaleBackupExists_RemovesStaleBackupBeforeSwapping()
    {
        var state = new SimulatedDockerState();
        state.Containers[SelfId] = CreateDefaultSelfContainer();

        const string staleBackupId = "stale-backup-id-999900001111";
        state.Containers[staleBackupId] = new SimulatedContainer
        {
            Id = staleBackupId,
            Name = "mobysync-backup",
            Image = SelfImage,
            Running = false
        };

        var helper = new DockerHelper(
            NullLogger<DockerHelper>.Instance,
            new FakeCredentialsHelper(),
            new FakeRegistryHelper(new Dictionary<string, string?>()),
            state.CreateClient(),
            TimeSpan.FromMilliseconds(5),
            "helper-container-hostname");

        var summary = await helper.ExecuteSelfUpdateSwap(SelfId, SelfImage);

        Assert.Single(summary.Successes);
        Assert.False(state.Containers.ContainsKey(staleBackupId));
        Assert.False(state.Containers.ContainsKey(SelfId));
        var replacement = Assert.Single(state.Containers.Values);
        Assert.Equal("mobysync", replacement.Name);
        Assert.True(replacement.Running);
    }

    [Fact]
    public async Task CheckAndTriggerSelfUpdate_RespectsTargetTagLabel()
    {
        Environment.SetEnvironmentVariable("SELF_UPDATE", "true");
        Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", "");

        var state = new SimulatedDockerState();
        var selfContainer = CreateDefaultSelfContainer();
        selfContainer.Labels["com.mobysync.target-tag"] = "beta";
        state.Containers[SelfId] = selfContainer;
        state.ImageDigests[SelfImageId] = [$"{SelfBaseImage}@sha256:old-digest"];

        var registry = new FakeRegistryHelper(new Dictionary<string, string?>
        {
            [$"{SelfBaseImage}:beta"] = "sha256:new-beta-digest"
        });

        var helper = new DockerHelper(
            NullLogger<DockerHelper>.Instance,
            new FakeCredentialsHelper(),
            registry,
            state.CreateClient(),
            TimeSpan.FromMilliseconds(5),
            SelfHostname);

        await helper.CheckAndTriggerSelfUpdate();

        Assert.Single(state.PulledImages);
        Assert.Equal((SelfBaseImage, "beta"), state.PulledImages[0]);

        var updater = Assert.Single(state.Containers.Values, c => c.Name == DockerHelper.SelfUpdaterContainerName);
        Assert.Equal($"{SelfBaseImage}:beta", updater.Image);
        Assert.Contains($"MOBYSYNC_SELF_TARGET_IMAGE={SelfBaseImage}:beta", updater.Env);
    }

    [Fact]
    public async Task ExecuteSelfUpdateSwap_WhenContainerHasHealthcheckAndBecomesHealthy_SucceedsOnceHealthy()
    {
        var state = new SimulatedDockerState();
        state.Containers[SelfId] = CreateDefaultSelfContainer();

        // Simulate the replacement container starting with Health.Status = "starting" and transitioning to "healthy" on the 2nd inspect
        state.OnInspectContainer = c =>
        {
            if (c.Id != SelfId)
            {
                c.HealthStatus = c.InspectCount < 2 ? "starting" : "healthy";
            }
        };

        var helper = new DockerHelper(
            NullLogger<DockerHelper>.Instance,
            new FakeCredentialsHelper(),
            new FakeRegistryHelper(new Dictionary<string, string?>()),
            state.CreateClient(),
            stabilityWindow: TimeSpan.FromSeconds(5),
            selfHostname: "helper-container-hostname",
            healthCheckTimeout: TimeSpan.FromSeconds(2),
            pollInterval: TimeSpan.FromMilliseconds(5));

        var summary = await helper.ExecuteSelfUpdateSwap(SelfId, SelfImage);

        Assert.Single(summary.Successes);
        Assert.Empty(summary.Rollbacks);
        var replacement = Assert.Single(state.Containers.Values);
        Assert.Equal("healthy", replacement.HealthStatus);
    }

    [Fact]
    public async Task ExecuteSelfUpdateSwap_WhenContainerHasHealthcheckAndReportsUnhealthy_RollsBackWithDiagnosticOutput()
    {
        var state = new SimulatedDockerState();
        state.Containers[SelfId] = CreateDefaultSelfContainer();

        // Simulate a container that stays Running=true, but its Docker HEALTHCHECK reports "unhealthy"
        state.OnInspectContainer = c =>
        {
            if (c.Id != SelfId)
            {
                c.HealthStatus = c.InspectCount < 2 ? "starting" : "unhealthy";
                c.HealthLogOutput = "HTTP 500 Internal Server Error on /health";
            }
        };

        var helper = new DockerHelper(
            NullLogger<DockerHelper>.Instance,
            new FakeCredentialsHelper(),
            new FakeRegistryHelper(new Dictionary<string, string?>()),
            state.CreateClient(),
            stabilityWindow: TimeSpan.FromSeconds(5),
            selfHostname: "helper-container-hostname",
            healthCheckTimeout: TimeSpan.FromSeconds(2),
            pollInterval: TimeSpan.FromMilliseconds(5));

        var summary = await helper.ExecuteSelfUpdateSwap(SelfId, SelfImage);

        Assert.Empty(summary.Successes);
        Assert.Single(summary.Rollbacks);
        Assert.Contains("unhealthy", summary.Rollbacks[0].ErrorMessage);
        Assert.Contains("HTTP 500 Internal Server Error on /health", summary.Rollbacks[0].ErrorMessage);

        // Original container must be restored and running
        Assert.True(state.Containers.ContainsKey(SelfId));
        Assert.Equal("mobysync", state.Containers[SelfId].Name);
        Assert.True(state.Containers[SelfId].Running);
    }

    [Fact]
    public async Task ExecuteSelfUpdateSwap_WhenContainerWithoutHealthcheckCrashLoopsWithRestartPolicy_DetectsRestartAndRollsBack()
    {
        var state = new SimulatedDockerState();
        state.Containers[SelfId] = CreateDefaultSelfContainer();

        // Simulate a container without a HEALTHCHECK that has 'restart: unless-stopped':
        // It stays Running=true when inspected, but RestartCount increments (and PID changes) on the 2nd poll!
        state.OnInspectContainer = c =>
        {
            if (c.Id != SelfId && c.InspectCount >= 2)
            {
                c.RestartCount = 1;
                c.Pid = 2048;
                c.ExitCode = 1;
            }
        };

        var helper = new DockerHelper(
            NullLogger<DockerHelper>.Instance,
            new FakeCredentialsHelper(),
            new FakeRegistryHelper(new Dictionary<string, string?>()),
            state.CreateClient(),
            stabilityWindow: TimeSpan.FromMilliseconds(50),
            selfHostname: "helper-container-hostname",
            healthCheckTimeout: TimeSpan.FromSeconds(1),
            pollInterval: TimeSpan.FromMilliseconds(5));

        var summary = await helper.ExecuteSelfUpdateSwap(SelfId, SelfImage);

        Assert.Empty(summary.Successes);
        Assert.Single(summary.Rollbacks);
        Assert.Contains("restarted by restart policy", summary.Rollbacks[0].ErrorMessage);

        // Original container must be restored and running
        Assert.True(state.Containers.ContainsKey(SelfId));
        Assert.Equal("mobysync", state.Containers[SelfId].Name);
        Assert.True(state.Containers[SelfId].Running);
    }

    [Fact]
    public async Task ExecuteSelfUpdateSwap_WhenContainerAttachedToMultipleNetworks_PreservesAllNetworksAndSanitizesAliases()
    {
        var state = new SimulatedDockerState();
        var selfContainer = CreateDefaultSelfContainer();
        selfContainer.HostConfig.NetworkMode = "frontend";
        selfContainer.Networks = new Dictionary<string, EndpointSettings>
        {
            ["frontend"] = new()
            {
                NetworkID = "net-frontend-id",
                Aliases = ["mobysync", SelfHostname]
            },
            ["backend"] = new()
            {
                NetworkID = "net-backend-id",
                Aliases = ["mobysync-internal", SelfHostname],
                IPAMConfig = new EndpointIPAMConfig { IPv4Address = "172.20.0.10" }
            },
            ["monitoring"] = new()
            {
                NetworkID = "net-monitoring-id",
                Aliases = ["metrics", SelfHostname]
            }
        };
        state.Containers[SelfId] = selfContainer;

        var helper = new DockerHelper(
            NullLogger<DockerHelper>.Instance,
            new FakeCredentialsHelper(),
            new FakeRegistryHelper(new Dictionary<string, string?>()),
            state.CreateClient(),
            stabilityWindow: TimeSpan.FromMilliseconds(10),
            selfHostname: "helper-container-hostname",
            healthCheckTimeout: TimeSpan.FromSeconds(1),
            pollInterval: TimeSpan.FromMilliseconds(5));

        var summary = await helper.ExecuteSelfUpdateSwap(SelfId, SelfImage);

        Assert.Single(summary.Successes);

        // Primary network ('frontend') was attached via CreateContainerParameters, with old short-ID alias stripped
        var replacement = Assert.Single(state.Containers.Values);
        Assert.True(replacement.Networks.ContainsKey("frontend"));
        Assert.Contains("mobysync", replacement.Networks["frontend"].Aliases);
        Assert.DoesNotContain(SelfHostname, replacement.Networks["frontend"].Aliases);

        // Both secondary networks ('backend' and 'monitoring') were explicitly connected via Networks.ConnectNetworkAsync
        Assert.Equal(2, state.ConnectedNetworks.Count);
        var backendConn = Assert.Single(state.ConnectedNetworks, n => n.NetworkIdOrName == "net-backend-id");
        Assert.Equal(replacement.Id, backendConn.ContainerId);
        Assert.Contains("mobysync-internal", backendConn.Endpoint!.Aliases);
        Assert.DoesNotContain(SelfHostname, backendConn.Endpoint.Aliases);
        Assert.Equal("172.20.0.10", backendConn.Endpoint.IPAMConfig.IPv4Address);

        var monitoringConn = Assert.Single(state.ConnectedNetworks, n => n.NetworkIdOrName == "net-monitoring-id");
        Assert.Equal(replacement.Id, monitoringConn.ContainerId);
        Assert.Contains("metrics", monitoringConn.Endpoint!.Aliases);
        Assert.DoesNotContain(SelfHostname, monitoringConn.Endpoint.Aliases);
    }
}
