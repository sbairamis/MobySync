using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MobySync.Helpers;

namespace MobySync.Tests;

[Collection("SequentialEnvTests")]
public class DockerHelperUpdateCycleTests
{
    private const string SelfId = "a1b2c3d4e5f6111122223333";
    private const string SelfHostname = "a1b2c3d4e5f6";

    private static DockerHelper CreateHelper(
        SimulatedDockerState state,
        FakeRegistryHelper registry,
        FakeCredentialsHelper? credentials = null)
    {
        return new DockerHelper(
            NullLogger<DockerHelper>.Instance,
            credentials ?? new FakeCredentialsHelper(),
            registry,
            state.CreateClient(),
            stabilityWindow: TimeSpan.FromMilliseconds(5),
            selfHostname: SelfHostname,
            healthCheckTimeout: TimeSpan.FromSeconds(1),
            pollInterval: TimeSpan.FromMilliseconds(2));
    }

    private static void ResetUpdateEnvVars(
        string? prevExcluded = null,
        string? prevOverrides = null,
        string? prevPrune = null,
        string? prevSelfUpdate = null)
    {
        Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", prevExcluded);
        Environment.SetEnvironmentVariable("IMAGE_TAG_OVERRIDES", prevOverrides);
        Environment.SetEnvironmentVariable("PRUNE_IMAGES", prevPrune);
        Environment.SetEnvironmentVariable("SELF_UPDATE", prevSelfUpdate);
    }

    [Fact]
    public async Task CheckForImageUpdates_ExcludesSelfStoppedAndConfiguredExclusions()
    {
        var prevExcluded = Environment.GetEnvironmentVariable("EXCLUDED_CONTAINERS");
        var prevOverrides = Environment.GetEnvironmentVariable("IMAGE_TAG_OVERRIDES");
        var prevPrune = Environment.GetEnvironmentVariable("PRUNE_IMAGES");

        Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", "portainer, redis");
        Environment.SetEnvironmentVariable("IMAGE_TAG_OVERRIDES", "");
        Environment.SetEnvironmentVariable("PRUNE_IMAGES", "false");

        try
        {
            var state = new SimulatedDockerState();

            state.Containers[SelfId] = new SimulatedContainer
            {
                Id = SelfId,
                Name = "mobysync",
                Image = "ghcr.io/sbairamis/mobysync:latest",
                ImageId = "sha256:self",
                Running = true,
                Hostname = SelfHostname
            };
            state.ImageDigests["sha256:self"] = ["ghcr.io/sbairamis/mobysync@sha256:old-self"];

            state.Containers["helper-id"] = new SimulatedContainer
            {
                Id = "helper-id",
                Name = DockerHelper.SelfUpdaterContainerName,
                Image = "ghcr.io/sbairamis/mobysync:latest",
                ImageId = "sha256:self",
                Running = true
            };

            state.Containers["stopped-id"] = new SimulatedContainer
            {
                Id = "stopped-id",
                Name = "stopped-app",
                Image = "nginx:latest",
                ImageId = "sha256:nginx-old",
                Running = false
            };

            state.Containers["portainer-id"] = new SimulatedContainer
            {
                Id = "portainer-id",
                Name = "portainer",
                Image = "portainer/portainer-ce:latest",
                ImageId = "sha256:portainer-old",
                Running = true
            };

            state.Containers["redis-id"] = new SimulatedContainer
            {
                Id = "redis-id",
                Name = "redis",
                Image = "redis:latest",
                ImageId = "sha256:redis-old",
                Running = true
            };

            state.Containers["web-id"] = new SimulatedContainer
            {
                Id = "web-id",
                Name = "web-app",
                Image = "nginx:latest",
                ImageId = "sha256:nginx-old",
                Running = true,
                Hostname = "web-id"
            };
            state.ImageDigests["sha256:nginx-old"] = ["nginx@sha256:nginx-old-digest"];

            var registry = new FakeRegistryHelper(new Dictionary<string, string?>
            {
                ["nginx:latest"] = "sha256:nginx-new-digest",
                ["ghcr.io/sbairamis/mobysync:latest"] = "sha256:new-self",
                ["portainer/portainer-ce:latest"] = "sha256:portainer-new",
                ["redis:latest"] = "sha256:redis-new"
            });

            var helper = CreateHelper(state, registry);
            var summary = await helper.CheckForImageUpdates();

            var updated = Assert.Single(summary.Successes);
            Assert.Equal("web-app", updated.ContainerName);
            Assert.Single(state.PulledImages);
            Assert.Equal(("nginx", "latest"), state.PulledImages[0]);

            Assert.True(state.Containers.ContainsKey(SelfId));
            Assert.True(state.Containers.ContainsKey("helper-id"));
            Assert.True(state.Containers.ContainsKey("stopped-id"));
            Assert.True(state.Containers.ContainsKey("portainer-id"));
            Assert.True(state.Containers.ContainsKey("redis-id"));
            Assert.False(state.Containers.ContainsKey("web-id"));
        }
        finally
        {
            ResetUpdateEnvVars(prevExcluded, prevOverrides, prevPrune);
        }
    }

    [Fact]
    public async Task CheckForImageUpdates_TagResolutionPrecedence_RespectsLabelsStackAndImageOverrides()
    {
        var prevExcluded = Environment.GetEnvironmentVariable("EXCLUDED_CONTAINERS");
        var prevOverrides = Environment.GetEnvironmentVariable("IMAGE_TAG_OVERRIDES");

        Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", "");
        Environment.SetEnvironmentVariable("IMAGE_TAG_OVERRIDES", "mystack:api:stack-tag,nginx:image-tag");

        try
        {
            var state = new SimulatedDockerState();

            state.Containers["c1"] = new SimulatedContainer
            {
                Id = "c1",
                Name = "c1-label",
                Image = "nginx:edge",
                ImageId = "sha256:c1",
                Running = true,
                Labels = new Dictionary<string, string>
                {
                    ["com.mobysync.target-tag"] = "label-tag",
                    ["com.docker.compose.project"] = "mystack",
                    ["com.docker.compose.service"] = "api"
                }
            };
            state.ImageDigests["sha256:c1"] = ["nginx@sha256:old"];

            state.Containers["c2"] = new SimulatedContainer
            {
                Id = "c2",
                Name = "c2-stack",
                Image = "nginx:edge",
                ImageId = "sha256:c2",
                Running = true,
                Labels = new Dictionary<string, string>
                {
                    ["com.docker.compose.project"] = "mystack",
                    ["com.docker.compose.service"] = "api"
                }
            };
            state.ImageDigests["sha256:c2"] = ["nginx@sha256:old"];

            state.Containers["c3"] = new SimulatedContainer
            {
                Id = "c3",
                Name = "c3-image",
                Image = "nginx:1.25-alpine",
                ImageId = "sha256:c3",
                Running = true
            };
            state.ImageDigests["sha256:c3"] = ["nginx@sha256:old"];

            state.Containers["c4"] = new SimulatedContainer
            {
                Id = "c4",
                Name = "c4-custom-port",
                Image = "localhost:5000/custom:canary",
                ImageId = "sha256:c4",
                Running = true
            };
            state.ImageDigests["sha256:c4"] = ["localhost:5000/custom@sha256:old"];

            state.Containers["c5"] = new SimulatedContainer
            {
                Id = "c5",
                Name = "c5-untagged",
                Image = "localhost:5000/untagged",
                ImageId = "sha256:c5",
                Running = true
            };
            state.ImageDigests["sha256:c5"] = ["localhost:5000/untagged@sha256:old"];

            var registry = new FakeRegistryHelper(new Dictionary<string, string?>
            {
                ["nginx:label-tag"] = "sha256:new-label",
                ["nginx:stack-tag"] = "sha256:new-stack",
                ["nginx:image-tag"] = "sha256:new-image",
                ["localhost:5000/custom:canary"] = "sha256:new-canary",
                ["localhost:5000/untagged:latest"] = "sha256:new-untagged"
            });

            var helper = CreateHelper(state, registry);
            var summary = await helper.CheckForImageUpdates();

            Assert.Equal(5, summary.Successes.Count);
            Assert.Contains(summary.Successes, s => s.ContainerName == "c1-label" && s.NewTag == "label-tag");
            Assert.Contains(summary.Successes, s => s.ContainerName == "c2-stack" && s.NewTag == "stack-tag");
            Assert.Contains(summary.Successes, s => s.ContainerName == "c3-image" && s.NewTag == "image-tag");
            Assert.Contains(summary.Successes, s => s.ContainerName == "c4-custom-port" && s.ImageName == "localhost:5000/custom" && s.NewTag == "canary");
            Assert.Contains(summary.Successes, s => s.ContainerName == "c5-untagged" && s.ImageName == "localhost:5000/untagged" && s.NewTag == "latest");
        }
        finally
        {
            ResetUpdateEnvVars(prevExcluded, prevOverrides);
        }
    }

    [Fact]
    public async Task CheckForImageUpdates_SkipsPinnedSemverAndLocalImages_AndTracksUpToDate()
    {
        var prevExcluded = Environment.GetEnvironmentVariable("EXCLUDED_CONTAINERS");
        var prevOverrides = Environment.GetEnvironmentVariable("IMAGE_TAG_OVERRIDES");

        Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", "");
        Environment.SetEnvironmentVariable("IMAGE_TAG_OVERRIDES", "");

        try
        {
            var state = new SimulatedDockerState();

            state.Containers["pinned-v"] = new SimulatedContainer
            {
                Id = "pinned-v",
                Name = "pinned-v-app",
                Image = "app:v1.2.3",
                ImageId = "sha256:pinned-v",
                Running = true
            };

            state.Containers["pinned-num"] = new SimulatedContainer
            {
                Id = "pinned-num",
                Name = "pinned-num-app",
                Image = "app:2.0.1",
                ImageId = "sha256:pinned-num",
                Running = true
            };

            state.Containers["local-app"] = new SimulatedContainer
            {
                Id = "local-app",
                Name = "local-built-app",
                Image = "my-local-image:dev",
                ImageId = "sha256:local-only",
                Running = true
            };
            // Empty digests simulates a locally built image without RepoDigests
            state.ImageDigests["sha256:local-only"] = [];

            state.Containers["current-app"] = new SimulatedContainer
            {
                Id = "current-app",
                Name = "up-to-date-app",
                Image = "redis:alpine",
                ImageId = "sha256:redis-current",
                Running = true
            };
            state.ImageDigests["sha256:redis-current"] = ["redis@sha256:same-digest"];

            var registry = new FakeRegistryHelper(new Dictionary<string, string?>
            {
                ["redis:alpine"] = "sha256:same-digest"
            });

            var helper = CreateHelper(state, registry);
            var summary = await helper.CheckForImageUpdates();

            Assert.Equal(3, summary.Skipped.Count);
            Assert.Contains(summary.Skipped, s => s.ContainerName == "pinned-v-app");
            Assert.Contains(summary.Skipped, s => s.ContainerName == "pinned-num-app");
            Assert.Contains(summary.Skipped, s => s.ContainerName == "local-built-app");

            var upToDate = Assert.Single(summary.UpToDate);
            Assert.Equal("up-to-date-app", upToDate);

            Assert.Empty(summary.Successes);
            Assert.Empty(state.PulledImages);
            Assert.Equal(4, state.Containers.Count);
        }
        finally
        {
            ResetUpdateEnvVars(prevExcluded, prevOverrides);
        }
    }

    [Fact]
    public async Task CheckForImageUpdates_DependencyChain_UpdatesInTopologicalOrder()
    {
        var prevExcluded = Environment.GetEnvironmentVariable("EXCLUDED_CONTAINERS");
        var prevOverrides = Environment.GetEnvironmentVariable("IMAGE_TAG_OVERRIDES");

        Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", "");
        Environment.SetEnvironmentVariable("IMAGE_TAG_OVERRIDES", "");

        try
        {
            var state = new SimulatedDockerState();

            // Insert in reverse dependency order to ensure topological sorting determines execution order
            state.Containers["web-id"] = new SimulatedContainer
            {
                Id = "web-id",
                Name = "web",
                Image = "myorg/web:latest",
                ImageId = "sha256:web-old",
                Running = true,
                Labels = new Dictionary<string, string> { ["com.mobysync.depends-on"] = "api" }
            };
            state.ImageDigests["sha256:web-old"] = ["myorg/web@sha256:old"];

            state.Containers["api-id"] = new SimulatedContainer
            {
                Id = "api-id",
                Name = "api",
                Image = "myorg/api:latest",
                ImageId = "sha256:api-old",
                Running = true,
                Labels = new Dictionary<string, string> { ["com.mobysync.depends-on"] = "db" }
            };
            state.ImageDigests["sha256:api-old"] = ["myorg/api@sha256:old"];

            state.Containers["db-id"] = new SimulatedContainer
            {
                Id = "db-id",
                Name = "db",
                Image = "postgres:latest",
                ImageId = "sha256:db-old",
                Running = true
            };
            state.ImageDigests["sha256:db-old"] = ["postgres@sha256:old"];

            var registry = new FakeRegistryHelper(new Dictionary<string, string?>
            {
                ["myorg/web:latest"] = "sha256:new",
                ["myorg/api:latest"] = "sha256:new",
                ["postgres:latest"] = "sha256:new"
            });

            var helper = CreateHelper(state, registry);
            var summary = await helper.CheckForImageUpdates();

            Assert.Equal(3, summary.Successes.Count);
            Assert.Empty(summary.Rollbacks);
            Assert.DoesNotContain(state.Containers.Values, c => c.Name.EndsWith("-backup", StringComparison.Ordinal));

            var dbCreateIdx = state.OperationLog.IndexOf("create:db:postgres:latest");
            var apiCreateIdx = state.OperationLog.IndexOf("create:api:myorg/api:latest");
            var webCreateIdx = state.OperationLog.IndexOf("create:web:myorg/web:latest");

            Assert.True(dbCreateIdx >= 0 && apiCreateIdx > dbCreateIdx && webCreateIdx > apiCreateIdx,
                $"Expected create order db -> api -> web, got log: {string.Join(", ", state.OperationLog)}");
        }
        finally
        {
            ResetUpdateEnvVars(prevExcluded, prevOverrides);
        }
    }

    [Fact]
    public async Task CheckForImageUpdates_WhenLastDependentContainerCrashes_RollsBackGroupInReverseOrder()
    {
        var prevExcluded = Environment.GetEnvironmentVariable("EXCLUDED_CONTAINERS");
        var prevOverrides = Environment.GetEnvironmentVariable("IMAGE_TAG_OVERRIDES");

        Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", "");
        Environment.SetEnvironmentVariable("IMAGE_TAG_OVERRIDES", "");

        try
        {
            const string originalDbId = "db-orig-000011112222";
            const string originalApiId = "api-orig-000011112222";
            const string originalWebId = "web-orig-000011112222";

            var state = new SimulatedDockerState();

            state.Containers[originalDbId] = new SimulatedContainer
            {
                Id = originalDbId,
                Name = "db",
                Image = "postgres:latest",
                ImageId = "sha256:db-old",
                Running = true,
                HostConfig = new HostConfig { NetworkMode = "frontend" },
                Networks = new Dictionary<string, EndpointSettings>
                {
                    ["frontend"] = new() { NetworkID = "net-frontend", Aliases = ["db", originalDbId[..12]] },
                    ["backend"] = new() { NetworkID = "net-backend", Aliases = ["db-internal", originalDbId[..12]] }
                }
            };
            state.ImageDigests["sha256:db-old"] = ["postgres@sha256:old"];

            state.Containers[originalApiId] = new SimulatedContainer
            {
                Id = originalApiId,
                Name = "api",
                Image = "myorg/api:latest",
                ImageId = "sha256:api-old",
                Running = true,
                Labels = new Dictionary<string, string> { ["com.mobysync.depends-on"] = "db" }
            };
            state.ImageDigests["sha256:api-old"] = ["myorg/api@sha256:old"];

            state.Containers[originalWebId] = new SimulatedContainer
            {
                Id = originalWebId,
                Name = "web",
                Image = "myorg/web:latest",
                ImageId = "sha256:web-old",
                Running = true,
                Labels = new Dictionary<string, string> { ["com.mobysync.depends-on"] = "api" }
            };
            state.ImageDigests["sha256:web-old"] = ["myorg/web@sha256:old"];

            state.OnStartContainerCrash = c => c.Name == "web" && c.Id != originalWebId;

            var registry = new FakeRegistryHelper(new Dictionary<string, string?>
            {
                ["postgres:latest"] = "sha256:new",
                ["myorg/api:latest"] = "sha256:new",
                ["myorg/web:latest"] = "sha256:new"
            });

            var helper = CreateHelper(state, registry);
            var summary = await helper.CheckForImageUpdates();

            Assert.Empty(summary.Successes);
            Assert.Equal(3, summary.Rollbacks.Count);
            Assert.Contains(summary.Rollbacks, r => r.ContainerName == "db");
            Assert.Contains(summary.Rollbacks, r => r.ContainerName == "api");
            Assert.Contains(summary.Rollbacks, r => r.ContainerName == "web");

            Assert.Equal(3, state.Containers.Count);
            Assert.True(state.Containers.ContainsKey(originalDbId));
            Assert.True(state.Containers.ContainsKey(originalApiId));
            Assert.True(state.Containers.ContainsKey(originalWebId));

            Assert.Equal("db", state.Containers[originalDbId].Name);
            Assert.Equal("api", state.Containers[originalApiId].Name);
            Assert.Equal("web", state.Containers[originalWebId].Name);
            Assert.True(state.Containers[originalDbId].Running);
            Assert.True(state.Containers[originalApiId].Running);
            Assert.True(state.Containers[originalWebId].Running);
            Assert.True(state.Containers[originalDbId].Networks.ContainsKey("backend"));

            // Verify LIFO rollback order: web -> api -> db
            var webRestoreIdx = state.OperationLog.IndexOf("rename:web-backup->web");
            var apiRestoreIdx = state.OperationLog.IndexOf("rename:api-backup->api");
            var dbRestoreIdx = state.OperationLog.IndexOf("rename:db-backup->db");

            Assert.True(webRestoreIdx >= 0 && apiRestoreIdx > webRestoreIdx && dbRestoreIdx > apiRestoreIdx,
                $"Expected reverse rollback order web -> api -> db, got log: {string.Join(", ", state.OperationLog)}");
        }
        finally
        {
            ResetUpdateEnvVars(prevExcluded, prevOverrides);
        }
    }

    [Fact]
    public async Task CheckForImageUpdates_WhenOneGroupFailsPull_OtherIndependentGroupStillUpdates()
    {
        var prevExcluded = Environment.GetEnvironmentVariable("EXCLUDED_CONTAINERS");
        var prevOverrides = Environment.GetEnvironmentVariable("IMAGE_TAG_OVERRIDES");

        Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", "");
        Environment.SetEnvironmentVariable("IMAGE_TAG_OVERRIDES", "");

        try
        {
            var state = new SimulatedDockerState();

            state.Containers["g1-db-id"] = new SimulatedContainer
            {
                Id = "g1-db-id",
                Name = "g1-db",
                Image = "postgres:latest",
                ImageId = "sha256:db-old",
                Running = true
            };
            state.ImageDigests["sha256:db-old"] = ["postgres@sha256:old"];

            state.Containers["g1-app-id"] = new SimulatedContainer
            {
                Id = "g1-app-id",
                Name = "g1-app",
                Image = "myorg/broken:latest",
                ImageId = "sha256:broken-old",
                Running = true,
                Labels = new Dictionary<string, string> { ["com.mobysync.depends-on"] = "g1-db" }
            };
            state.ImageDigests["sha256:broken-old"] = ["myorg/broken@sha256:old"];

            state.Containers["g2-id"] = new SimulatedContainer
            {
                Id = "g2-id",
                Name = "g2-standalone",
                Image = "caddy:latest",
                ImageId = "sha256:caddy-old",
                Running = true
            };
            state.ImageDigests["sha256:caddy-old"] = ["caddy@sha256:old"];

            state.OnPullImage = p =>
            {
                if (p.FromImage == "myorg/broken")
                    throw new InvalidOperationException("manifest not found");
            };

            var registry = new FakeRegistryHelper(new Dictionary<string, string?>
            {
                ["postgres:latest"] = "sha256:new",
                ["myorg/broken:latest"] = "sha256:new",
                ["caddy:latest"] = "sha256:new"
            });

            var helper = CreateHelper(state, registry);
            var summary = await helper.CheckForImageUpdates();

            var failed = Assert.Single(summary.FailedPulls);
            Assert.Equal("g1-app", failed.ContainerName);

            var success = Assert.Single(summary.Successes);
            Assert.Equal("g2-standalone", success.ContainerName);

            // Group 1 containers must remain untouched because Group 1 pull aborted
            Assert.True(state.Containers.ContainsKey("g1-db-id"));
            Assert.True(state.Containers.ContainsKey("g1-app-id"));
            Assert.False(state.Containers.ContainsKey("g2-id"));
        }
        finally
        {
            ResetUpdateEnvVars(prevExcluded, prevOverrides);
        }
    }

    [Fact]
    public async Task CheckForImageUpdates_WhenCredentialedPullFailsWithAuthError_RetriesAnonymouslyAndSucceeds()
    {
        var prevExcluded = Environment.GetEnvironmentVariable("EXCLUDED_CONTAINERS");
        var prevOverrides = Environment.GetEnvironmentVariable("IMAGE_TAG_OVERRIDES");

        Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", "");
        Environment.SetEnvironmentVariable("IMAGE_TAG_OVERRIDES", "");

        try
        {
            var state = new SimulatedDockerState();

            state.Containers["app-id"] = new SimulatedContainer
            {
                Id = "app-id",
                Name = "private-app",
                Image = "private.io/app:latest",
                ImageId = "sha256:app-old",
                Running = true
            };
            state.ImageDigests["sha256:app-old"] = ["private.io/app@sha256:old"];

            var creds = new FakeCredentialsHelper();
            creds.CredentialsByImage["private.io/app"] = new AuthConfig
            {
                ServerAddress = "private.io",
                Username = "stale-user",
                Password = "stale-password"
            };

            var seenAuthUsernames = new List<string?>();
            state.OnPullImageWithAuth = (_, auth) =>
            {
                seenAuthUsernames.Add(auth?.Username);
                if (!string.IsNullOrEmpty(auth?.Username))
                {
                    throw new InvalidOperationException("unauthorized: bad credentials");
                }
            };

            var registry = new FakeRegistryHelper(new Dictionary<string, string?>
            {
                ["private.io/app:latest"] = "sha256:new"
            });

            var helper = CreateHelper(state, registry, creds);
            var summary = await helper.CheckForImageUpdates();

            Assert.Equal(2, seenAuthUsernames.Count);
            Assert.Equal("stale-user", seenAuthUsernames[0]);
            Assert.Null(seenAuthUsernames[1]);

            var updated = Assert.Single(summary.Successes);
            Assert.Equal("private-app", updated.ContainerName);
            Assert.Empty(summary.FailedPulls);
        }
        finally
        {
            ResetUpdateEnvVars(prevExcluded, prevOverrides);
        }
    }

    [Fact]
    public async Task CheckForImageUpdates_WhenPullStreamReportsJsonError_AbortsGroupUpdateAndRecordsFailedPull()
    {
        var prevExcluded = Environment.GetEnvironmentVariable("EXCLUDED_CONTAINERS");
        var prevOverrides = Environment.GetEnvironmentVariable("IMAGE_TAG_OVERRIDES");

        Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", "");
        Environment.SetEnvironmentVariable("IMAGE_TAG_OVERRIDES", "");

        try
        {
            var state = new SimulatedDockerState();

            state.Containers["app-id"] = new SimulatedContainer
            {
                Id = "app-id",
                Name = "web-app",
                Image = "nginx:latest",
                ImageId = "sha256:app-old",
                Running = true
            };
            state.ImageDigests["sha256:app-old"] = ["nginx@sha256:old"];

            state.OnPullImageProgress = (_, progress) =>
            {
                progress?.Report(new JSONMessage
                {
                    Error = new JSONError
                    {
                        Message = "write /var/lib/docker: no space left on device"
                    }
                });
            };

            var registry = new FakeRegistryHelper(new Dictionary<string, string?>
            {
                ["nginx:latest"] = "sha256:new"
            });

            var helper = CreateHelper(state, registry);
            var summary = await helper.CheckForImageUpdates();

            var failed = Assert.Single(summary.FailedPulls);
            Assert.Equal("web-app", failed.ContainerName);
            Assert.Contains("no space left on device", failed.ErrorMessage);
            Assert.Empty(summary.Successes);
            Assert.True(state.Containers["app-id"].Running);
            Assert.DoesNotContain(state.OperationLog, op => op.StartsWith("stop:", StringComparison.Ordinal));
        }
        finally
        {
            ResetUpdateEnvVars(prevExcluded, prevOverrides);
        }
    }

    [Fact]
    public async Task CheckForImageUpdates_WhenContainerHasAutoRemoveEnabled_SkipsContainer()
    {
        var prevExcluded = Environment.GetEnvironmentVariable("EXCLUDED_CONTAINERS");
        var prevOverrides = Environment.GetEnvironmentVariable("IMAGE_TAG_OVERRIDES");

        Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", "");
        Environment.SetEnvironmentVariable("IMAGE_TAG_OVERRIDES", "");

        try
        {
            var state = new SimulatedDockerState();

            state.Containers["ephemeral-id"] = new SimulatedContainer
            {
                Id = "ephemeral-id",
                Name = "ephemeral-worker",
                Image = "alpine:latest",
                ImageId = "sha256:alpine-old",
                Running = true,
                HostConfig = new HostConfig { AutoRemove = true }
            };
            state.ImageDigests["sha256:alpine-old"] = ["alpine@sha256:old"];

            var registry = new FakeRegistryHelper(new Dictionary<string, string?>
            {
                ["alpine:latest"] = "sha256:new"
            });

            var helper = CreateHelper(state, registry);
            var summary = await helper.CheckForImageUpdates();

            var skipped = Assert.Single(summary.Skipped);
            Assert.Equal("ephemeral-worker", skipped.ContainerName);
            Assert.Contains("AutoRemove", skipped.ErrorMessage);
            Assert.Empty(summary.Successes);
            Assert.Empty(state.PulledImages);
            Assert.True(state.Containers["ephemeral-id"].Running);
        }
        finally
        {
            ResetUpdateEnvVars(prevExcluded, prevOverrides);
        }
    }

    [Fact]
    public async Task CheckForImageUpdates_WhenOneGroupHasCircularDependency_SkipsCyclicGroupAndUpdatesOtherGroups()
    {
        var prevExcluded = Environment.GetEnvironmentVariable("EXCLUDED_CONTAINERS");
        var prevOverrides = Environment.GetEnvironmentVariable("IMAGE_TAG_OVERRIDES");

        Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", "");
        Environment.SetEnvironmentVariable("IMAGE_TAG_OVERRIDES", "");

        try
        {
            var state = new SimulatedDockerState();

            state.Containers["cycle-a-id"] = new SimulatedContainer
            {
                Id = "cycle-a-id",
                Name = "cycle-a",
                Image = "myorg/a:latest",
                ImageId = "sha256:a-old",
                Running = true,
                Labels = new Dictionary<string, string> { ["com.mobysync.depends-on"] = "cycle-b" }
            };
            state.ImageDigests["sha256:a-old"] = ["myorg/a@sha256:old"];

            state.Containers["cycle-b-id"] = new SimulatedContainer
            {
                Id = "cycle-b-id",
                Name = "cycle-b",
                Image = "myorg/b:latest",
                ImageId = "sha256:b-old",
                Running = true,
                Labels = new Dictionary<string, string> { ["com.mobysync.depends-on"] = "cycle-a" }
            };
            state.ImageDigests["sha256:b-old"] = ["myorg/b@sha256:old"];

            state.Containers["standalone-id"] = new SimulatedContainer
            {
                Id = "standalone-id",
                Name = "standalone-app",
                Image = "caddy:latest",
                ImageId = "sha256:caddy-old",
                Running = true
            };
            state.ImageDigests["sha256:caddy-old"] = ["caddy@sha256:old"];

            var registry = new FakeRegistryHelper(new Dictionary<string, string?>
            {
                ["myorg/a:latest"] = "sha256:new",
                ["myorg/b:latest"] = "sha256:new",
                ["caddy:latest"] = "sha256:new"
            });

            var helper = CreateHelper(state, registry);
            var summary = await helper.CheckForImageUpdates();

            Assert.Equal(2, summary.Skipped.Count);
            Assert.Contains(summary.Skipped, s => s.ContainerName == "cycle-a");
            Assert.Contains(summary.Skipped, s => s.ContainerName == "cycle-b");

            var success = Assert.Single(summary.Successes);
            Assert.Equal("standalone-app", success.ContainerName);
            Assert.True(state.Containers.ContainsKey("cycle-a-id"));
            Assert.True(state.Containers.ContainsKey("cycle-b-id"));
            Assert.False(state.Containers.ContainsKey("standalone-id"));
        }
        finally
        {
            ResetUpdateEnvVars(prevExcluded, prevOverrides);
        }
    }

    [Fact]
    public async Task CheckForImageUpdates_WhenOrphanedStoppedBackupExistsWithNoPrimaryContainer_RecoversAndStartsBackup()
    {
        var prevExcluded = Environment.GetEnvironmentVariable("EXCLUDED_CONTAINERS");
        var prevOverrides = Environment.GetEnvironmentVariable("IMAGE_TAG_OVERRIDES");

        Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", "");
        Environment.SetEnvironmentVariable("IMAGE_TAG_OVERRIDES", "");

        try
        {
            var state = new SimulatedDockerState();

            state.Containers["redis-backup-id"] = new SimulatedContainer
            {
                Id = "redis-backup-id",
                Name = "redis-backup",
                Image = "redis:latest",
                ImageId = "sha256:redis-old",
                Running = false
            };
            state.ImageDigests["sha256:redis-old"] = ["redis@sha256:current"];

            var registry = new FakeRegistryHelper(new Dictionary<string, string?>
            {
                ["redis:latest"] = "sha256:current"
            });

            var helper = CreateHelper(state, registry);
            var summary = await helper.CheckForImageUpdates();

            Assert.Equal("redis", state.Containers["redis-backup-id"].Name);
            Assert.True(state.Containers["redis-backup-id"].Running);
            Assert.Contains("redis", summary.UpToDate);
            Assert.Contains("rename:redis-backup->redis", state.OperationLog);
            Assert.Contains("start:redis", state.OperationLog);
        }
        finally
        {
            ResetUpdateEnvVars(prevExcluded, prevOverrides);
        }
    }
}
