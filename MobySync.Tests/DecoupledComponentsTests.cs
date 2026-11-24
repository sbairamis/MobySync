using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MobySync.Helpers;
using MobySync.Models;

namespace MobySync.Tests;

[Collection("SequentialEnvTests")]
public class DecoupledComponentsTests
{
    [Fact]
    public void ImageTagResolver_ResolvesOverridesExtractsTagsAndDetectsPinnedVersions()
    {
        var resolver = new ImageTagResolver(
            NullLogger.Instance,
            "mystack:web:canary,nginx:alpine");

        var labelContainer = new ContainerListResponse
        {
            Image = "nginx:edge",
            Labels = new Dictionary<string, string>
            {
                ["com.mobysync.target-tag"] = "custom-label",
                ["com.docker.compose.project"] = "mystack",
                ["com.docker.compose.service"] = "web"
            }
        };
        var stackContainer = new ContainerListResponse
        {
            Image = "nginx:edge",
            Labels = new Dictionary<string, string>
            {
                ["com.docker.compose.project"] = "mystack",
                ["com.docker.compose.service"] = "web"
            }
        };
        var imageOverrideContainer = new ContainerListResponse
        {
            Image = "nginx:mainline",
            Labels = new Dictionary<string, string>()
        };
        var fallbackContainer = new ContainerListResponse
        {
            Image = "localhost:5000/myrepo/app",
            Labels = new Dictionary<string, string>()
        };

        Assert.Equal("custom-label", resolver.ResolveTargetTag(labelContainer, "nginx"));
        Assert.Equal("canary", resolver.ResolveTargetTag(stackContainer, "nginx"));
        Assert.Equal("alpine", resolver.ResolveTargetTag(imageOverrideContainer, "nginx"));
        Assert.Equal("latest", resolver.ResolveTargetTag(fallbackContainer, "localhost:5000/myrepo/app"));

        Assert.Equal("localhost:5000/myrepo/app", ImageTagResolver.FetchBaseImageName("localhost:5000/myrepo/app:v2"));
        Assert.Equal("v2", ImageTagResolver.ExtractTagFromImage("localhost:5000/myrepo/app:v2"));
        Assert.Null(ImageTagResolver.ExtractTagFromImage("localhost:5000/myrepo/app"));

        Assert.True(ImageTagResolver.IsPinnedVersionTag("v1.2.3"));
        Assert.True(ImageTagResolver.IsPinnedVersionTag("16.4"));
        Assert.False(ImageTagResolver.IsPinnedVersionTag("latest"));
        Assert.False(ImageTagResolver.IsPinnedVersionTag("1.2-alpine"));
    }

    [Fact]
    public void DependencyGraphBuilder_GroupsConnectedComponentsSortsTopologicallyAndHandlesNullLabels()
    {
        var builder = new DependencyGraphBuilder();

        var containers = new List<ContainerListResponse>
        {
            new()
            {
                ID = "web-1",
                Names = ["/web"],
                Labels = new Dictionary<string, string> { ["com.mobysync.depends-on"] = "api" }
            },
            new()
            {
                ID = "standalone-1",
                Names = ["/standalone"],
                Labels = null!
            },
            new()
            {
                ID = "api-1",
                Names = ["/api"],
                Labels = new Dictionary<string, string> { ["com.mobysync.depends-on"] = "db" }
            },
            new()
            {
                ID = "db-1",
                Names = ["/db"],
                Labels = null!
            }
        };

        var groups = builder.GroupDependentContainers(containers);

        Assert.Equal(2, groups.Count);
        var stackGroup = groups.Values.Single(g => g.Count == 3);
        Assert.Equal(["db", "api", "web"], stackGroup.Select(c => c.Names[0].TrimStart('/')).ToList());

        var standaloneGroup = groups.Values.Single(g => g.Count == 1);
        Assert.Equal("standalone", standaloneGroup[0].Names[0].TrimStart('/'));
    }

    [Fact]
    public void DependencyGraphBuilder_WhenCircularDependencyExists_ThrowsInvalidOperationException()
    {
        var builder = new DependencyGraphBuilder();

        var containers = new List<ContainerListResponse>
        {
            new()
            {
                ID = "a-1",
                Names = ["/svc-a"],
                Labels = new Dictionary<string, string> { ["com.mobysync.depends-on"] = "svc-b" }
            },
            new()
            {
                ID = "b-1",
                Names = ["/svc-b"],
                Labels = new Dictionary<string, string> { ["com.mobysync.depends-on"] = "svc-a" }
            }
        };

        var ex = Assert.Throws<InvalidOperationException>(() => builder.GroupDependentContainers(containers));
        Assert.Contains("circular dependency", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ContainerHealthVerifier_DetectsOomKilledRestartingAndHealthcheckTimeouts()
    {
        var state = new SimulatedDockerState();
        var client = state.CreateClient();
        var verifier = new ContainerHealthVerifier(
            client,
            NullLogger.Instance,
            stabilityWindow: TimeSpan.FromMilliseconds(20),
            healthCheckTimeout: TimeSpan.FromMilliseconds(25),
            pollInterval: TimeSpan.FromMilliseconds(5));

        state.Containers["oom-id"] = new SimulatedContainer
        {
            Id = "oom-id",
            Name = "oom-app",
            Running = false,
            OOMKilled = true,
            ExitCode = 137
        };

        var oomEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            verifier.VerifyContainerStartupAsync("oom-id", "oom-app"));
        Assert.Contains("OOM-killed", oomEx.Message);

        state.Containers["restarting-id"] = new SimulatedContainer
        {
            Id = "restarting-id",
            Name = "restarting-app",
            Running = true,
            Restarting = true,
            RestartCount = 2,
            ExitCode = 1
        };

        var restartingEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            verifier.VerifyContainerStartupAsync("restarting-id", "restarting-app"));
        Assert.Contains("restarting", restartingEx.Message);

        state.Containers["timeout-id"] = new SimulatedContainer
        {
            Id = "timeout-id",
            Name = "slow-app",
            Running = true,
            HealthStatus = "starting"
        };

        var timeoutEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            verifier.VerifyContainerStartupAsync("timeout-id", "slow-app"));
        Assert.Contains("healthcheck timed out", timeoutEx.Message);
    }

    [Fact]
    public async Task ContainerSwapper_ReplaceContainerGroupAsync_CleansUpStaleBackupAndPreservesSecondaryNetworksAcrossSpanningContainers()
    {
        const string oldDbId = "db-1111222233334444";
        const string oldWebId = "web-1111222233334444";
        const string staleBackupId = "db-stale-backup-id";

        var state = new SimulatedDockerState();

        state.Containers[staleBackupId] = new SimulatedContainer
        {
            Id = staleBackupId,
            Name = "db-backup",
            Image = "postgres:latest",
            Running = false
        };

        state.Containers[oldDbId] = new SimulatedContainer
        {
            Id = oldDbId,
            Name = "db",
            Image = "postgres:latest",
            Running = true,
            Hostname = oldDbId[..12],
            HostConfig = new HostConfig { NetworkMode = "backend" },
            Networks = new Dictionary<string, EndpointSettings>
            {
                ["backend"] = new() { NetworkID = "net-backend", Aliases = ["db", oldDbId[..12]] },
                ["metrics"] = new() { NetworkID = "net-metrics", Aliases = ["db-metrics", oldDbId[..12]] }
            }
        };

        state.Containers[oldWebId] = new SimulatedContainer
        {
            Id = oldWebId,
            Name = "web",
            Image = "nginx:latest",
            Running = true,
            Hostname = oldWebId[..12],
            HostConfig = new HostConfig { NetworkMode = "frontend" },
            Networks = new Dictionary<string, EndpointSettings>
            {
                ["frontend"] = new() { NetworkID = "net-frontend", Aliases = ["web", oldWebId[..12]] },
                ["backend"] = new() { NetworkID = "net-backend", Aliases = ["web-api", oldWebId[..12]] }
            }
        };

        var client = state.CreateClient();
        var healthVerifier = new ContainerHealthVerifier(
            client,
            NullLogger.Instance,
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(2));
        var swapper = new ContainerSwapper(client, healthVerifier, NullLogger.Instance);
        var resolver = new ImageTagResolver(NullLogger.Instance, "");

        var containersToReplace = new List<ContainerListResponse>
        {
            new() { ID = oldDbId, Names = ["/db"], Image = "postgres:latest" },
            new() { ID = oldWebId, Names = ["/web"], Image = "nginx:latest" }
        };

        var summary = new UpdateSummary();
        await swapper.ReplaceContainerGroupAsync(containersToReplace, resolver.ResolveTargetTag, summary);

        Assert.Equal(2, summary.Successes.Count);
        Assert.Empty(summary.Rollbacks);

        // Stale backup and old containers must be removed
        Assert.False(state.Containers.ContainsKey(staleBackupId));
        Assert.False(state.Containers.ContainsKey(oldDbId));
        Assert.False(state.Containers.ContainsKey(oldWebId));

        var newDb = Assert.Single(state.Containers.Values, c => c.Name == "db");
        var newWeb = Assert.Single(state.Containers.Values, c => c.Name == "web");

        Assert.True(newDb.Running);
        Assert.True(newWeb.Running);
        Assert.True(newDb.Networks.ContainsKey("backend"));
        Assert.True(newDb.Networks.ContainsKey("net-metrics"));
        Assert.True(newWeb.Networks.ContainsKey("frontend"));
        Assert.True(newWeb.Networks.ContainsKey("net-backend"));

        Assert.Equal(2, state.ConnectedNetworks.Count);
        Assert.Contains(state.ConnectedNetworks, c => c.NetworkIdOrName == "net-metrics" && c.ContainerId == newDb.Id);
        Assert.Contains(state.ConnectedNetworks, c => c.NetworkIdOrName == "net-backend" && c.ContainerId == newWeb.Id);
    }

    [Fact]
    public async Task ContainerSwapper_AttemptRollback_WhenRenameBackToOriginalNameFails_StillStartsBackupContainer()
    {
        const string oldId = "app-orig-111122223333";

        var state = new SimulatedDockerState();
        state.Containers[oldId] = new SimulatedContainer
        {
            Id = oldId,
            Name = "app",
            Image = "nginx:latest",
            Running = true,
            Hostname = oldId[..12]
        };

        state.OnStartContainerCrash = c => c.Id != oldId;
        state.OnRenameContainer = (currentName, newName) =>
        {
            if (currentName == "app-backup" && newName == "app")
                throw new InvalidOperationException("name conflict during rollback rename");
        };

        var client = state.CreateClient();
        var healthVerifier = new ContainerHealthVerifier(
            client,
            NullLogger.Instance,
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(2));
        var swapper = new ContainerSwapper(client, healthVerifier, NullLogger.Instance);
        var resolver = new ImageTagResolver(NullLogger.Instance, "");

        var summary = new UpdateSummary();
        await swapper.ReplaceContainerGroupAsync(
            [new ContainerListResponse { ID = oldId, Names = ["/app"], Image = "nginx:latest" }],
            resolver.ResolveTargetTag,
            summary);

        Assert.Single(summary.Rollbacks);
        var restored = Assert.Single(state.Containers.Values);
        Assert.Equal(oldId, restored.Id);
        Assert.True(restored.Running);
        Assert.Equal("app-backup", restored.Name);
    }

    [Fact]
    public async Task ContainerSwapper_WhenStaleBackupContainerIsRunning_AbortsSwapWithoutRemovingRunningBackup()
    {
        const string oldDbId = "db-orig-222233334444";
        const string runningBackupId = "db-running-backup-id";

        var state = new SimulatedDockerState();
        state.Containers[oldDbId] = new SimulatedContainer
        {
            Id = oldDbId,
            Name = "db",
            Image = "postgres:latest",
            Running = true,
            Hostname = oldDbId[..12]
        };
        state.Containers[runningBackupId] = new SimulatedContainer
        {
            Id = runningBackupId,
            Name = "db-backup",
            Image = "postgres:latest",
            Running = true
        };

        var client = state.CreateClient();
        var healthVerifier = new ContainerHealthVerifier(
            client,
            NullLogger.Instance,
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(2));
        var swapper = new ContainerSwapper(client, healthVerifier, NullLogger.Instance);
        var resolver = new ImageTagResolver(NullLogger.Instance, "");

        var summary = new UpdateSummary();
        await swapper.ReplaceContainerGroupAsync(
            [new ContainerListResponse { ID = oldDbId, Names = ["/db"], Image = "postgres:latest" }],
            resolver.ResolveTargetTag,
            summary);

        Assert.Empty(summary.Successes);
        var rollback = Assert.Single(summary.Rollbacks);
        Assert.Contains("currently running", rollback.ErrorMessage);
        Assert.True(state.Containers.ContainsKey(runningBackupId));
        Assert.True(state.Containers[runningBackupId].Running);
        Assert.True(state.Containers[oldDbId].Running);
        Assert.DoesNotContain(state.OperationLog, op => op == "remove:db-backup");
        Assert.DoesNotContain(state.OperationLog, op => op == "start:db");
    }

    [Fact]
    public async Task ContainerSwapper_WhenSecondContainerInspectFails_RollsBackPreviouslyUpdatedGroupContainers()
    {
        const string oldDbId = "db-orig-333344445555";
        const string oldApiId = "api-orig-333344445555";

        var state = new SimulatedDockerState();
        state.Containers[oldDbId] = new SimulatedContainer
        {
            Id = oldDbId,
            Name = "db",
            Image = "postgres:latest",
            Running = true,
            Hostname = oldDbId[..12]
        };
        state.Containers[oldApiId] = new SimulatedContainer
        {
            Id = oldApiId,
            Name = "api",
            Image = "myorg/api:latest",
            Running = true,
            Hostname = oldApiId[..12]
        };

        state.OnInspectContainer = c =>
        {
            if (c.Id == oldApiId)
                throw new InvalidOperationException("api container inspect failed");
        };

        var client = state.CreateClient();
        var healthVerifier = new ContainerHealthVerifier(
            client,
            NullLogger.Instance,
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(2));
        var swapper = new ContainerSwapper(client, healthVerifier, NullLogger.Instance);
        var resolver = new ImageTagResolver(NullLogger.Instance, "");

        var summary = new UpdateSummary();
        await swapper.ReplaceContainerGroupAsync(
            [
                new ContainerListResponse { ID = oldDbId, Names = ["/db"], Image = "postgres:latest" },
                new ContainerListResponse { ID = oldApiId, Names = ["/api"], Image = "myorg/api:latest" }
            ],
            resolver.ResolveTargetTag,
            summary);

        Assert.Empty(summary.Successes);
        Assert.Equal(2, summary.Rollbacks.Count);
        Assert.Contains(summary.Rollbacks, r => r.ContainerName == "db");
        Assert.Contains(summary.Rollbacks, r => r.ContainerName == "api" && r.ErrorMessage!.Contains("api container inspect failed"));

        // First container must have been rolled back to its original instance and restarted
        Assert.True(state.Containers.ContainsKey(oldDbId));
        Assert.Equal("db", state.Containers[oldDbId].Name);
        Assert.True(state.Containers[oldDbId].Running);
        Assert.DoesNotContain(state.Containers.Values, c => c.Name == "db-backup");
    }
}
