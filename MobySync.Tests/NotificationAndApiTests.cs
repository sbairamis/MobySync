using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MobySync.Extensions;
using MobySync.Filters;
using MobySync.Helpers;
using MobySync.Interfaces;
using MobySync.Models;

namespace MobySync.Tests;

[Collection("SequentialEnvTests")]
public class NotificationAndApiTests
{
    private sealed class CapturingHttpHandler : HttpMessageHandler
    {
        public List<(string Url, string Body)> Posts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Posts.Add((request.RequestUri!.ToString(), body));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ThrowingNotificationHelper : INotificationHelper
    {
        public bool IsConfigured => true;
        public string ProviderName => "BrokenProvider";
        public string WebhookUrl => "http://localhost/broken";

        public Task SendSummary(UpdateSummary summary) => throw new InvalidOperationException("summary boom");
        public Task SendStartupTest() => throw new InvalidOperationException("startup boom");
        public Task SendUpdateStarted() => throw new InvalidOperationException("started boom");
    }

    [Fact]
    public async Task DiscordNotificationHelper_WhenUnconfigured_DoesNotSendRequests()
    {
        var prevUrl = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_URL");
        Environment.SetEnvironmentVariable("DISCORD_WEBHOOK_URL", "");

        try
        {
            var handler = new CapturingHttpHandler();
            var helper = new DiscordNotificationHelper(
                NullLogger<DiscordNotificationHelper>.Instance,
                new StubHttpClientFactory(handler));

            Assert.False(helper.IsConfigured);

            await helper.SendStartupTest();
            await helper.SendUpdateStarted();
            await helper.SendSummary(new UpdateSummary());

            Assert.Empty(handler.Posts);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DISCORD_WEBHOOK_URL", prevUrl);
        }
    }

    [Fact]
    public async Task DiscordNotificationHelper_WhenConfigured_FormatsStartupStartedAndSummariesIncludingChunking()
    {
        var prevUrl = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_URL");
        Environment.SetEnvironmentVariable("DISCORD_WEBHOOK_URL", "https://discord.example/webhook");

        try
        {
            var handler = new CapturingHttpHandler();
            var helper = new DiscordNotificationHelper(
                NullLogger<DiscordNotificationHelper>.Instance,
                new StubHttpClientFactory(handler));

            Assert.True(helper.IsConfigured);

            await helper.SendStartupTest();
            await helper.SendUpdateStarted();

            var noChangesSummary = new UpdateSummary
            {
                TotalDuration = TimeSpan.FromSeconds(12)
            };
            noChangesSummary.UpToDate.Add("nginx");
            noChangesSummary.Skipped.Add(new ContainerUpdateResult
            {
                ContainerName = "postgres",
                ImageName = "postgres",
                ErrorMessage = "Pinned version tag `16.2` — skipping auto-update"
            });

            await helper.SendSummary(noChangesSummary);

            var changesSummary = new UpdateSummary
            {
                TotalDuration = TimeSpan.FromSeconds(45)
            };
            for (var i = 0; i < 20; i++)
            {
                changesSummary.Successes.Add(new ContainerUpdateResult
                {
                    ContainerName = $"service-with-a-reasonably-long-container-name-{i:D2}",
                    ImageName = "ghcr.io/example/service",
                    NewTag = "latest-release-tag",
                    Success = true
                });
            }

            changesSummary.Rollbacks.Add(new ContainerUpdateResult
            {
                ContainerName = "broken-api",
                ImageName = "myorg/api",
                ErrorMessage = "Crashed during healthcheck"
            });
            changesSummary.FailedPulls.Add(new ContainerUpdateResult
            {
                ContainerName = "private-app",
                ImageName = "ghcr.io/private/app",
                ErrorMessage = "401 Unauthorized"
            });
            changesSummary.Skipped.Add(new ContainerUpdateResult
            {
                ContainerName = "local-dev",
                ImageName = "dev:latest",
                ErrorMessage = "Locally built image"
            });

            await helper.SendSummary(changesSummary);

            Assert.Equal(4, handler.Posts.Count);

            using var startupDoc = JsonDocument.Parse(handler.Posts[0].Body);
            Assert.Equal("MobySync Started", startupDoc.RootElement.GetProperty("embeds")[0].GetProperty("title").GetString());

            using var startedDoc = JsonDocument.Parse(handler.Posts[1].Body);
            Assert.Equal("Update Cycle Starting", startedDoc.RootElement.GetProperty("embeds")[0].GetProperty("title").GetString());

            using var noChangeDoc = JsonDocument.Parse(handler.Posts[2].Body);
            var noChangeEmbed = noChangeDoc.RootElement.GetProperty("embeds")[0];
            Assert.Equal("Update Cycle Complete", noChangeEmbed.GetProperty("title").GetString());
            Assert.Contains("nginx", noChangeEmbed.GetProperty("description").GetString());
            Assert.Equal("⏭️ Skipped", noChangeEmbed.GetProperty("fields")[0].GetProperty("name").GetString());

            using var changesDoc = JsonDocument.Parse(handler.Posts[3].Body);
            var fields = changesDoc.RootElement.GetProperty("embeds")[0].GetProperty("fields").EnumerateArray().ToList();
            var fieldNames = fields.Select(f => f.GetProperty("name").GetString()).ToList();

            Assert.Contains("✅ Successful Updates", fieldNames);
            Assert.Contains("✅ Successful Updates (cont.)", fieldNames);
            Assert.Contains("⚠️ Rollbacks", fieldNames);
            Assert.Contains("❌ Failed Pulls", fieldNames);
            Assert.Contains("⏭️ Skipped", fieldNames);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DISCORD_WEBHOOK_URL", prevUrl);
        }
    }

    [Fact]
    public async Task GenericWebhookNotificationHelper_WhenUnconfigured_DoesNotSendRequests()
    {
        var prevUrl = Environment.GetEnvironmentVariable("WEBHOOK_URL");
        Environment.SetEnvironmentVariable("WEBHOOK_URL", "");

        try
        {
            var handler = new CapturingHttpHandler();
            var helper = new GenericWebhookNotificationHelper(
                NullLogger<GenericWebhookNotificationHelper>.Instance,
                new StubHttpClientFactory(handler));

            Assert.False(helper.IsConfigured);

            await helper.SendStartupTest();
            await helper.SendUpdateStarted();
            await helper.SendSummary(new UpdateSummary());

            Assert.Empty(handler.Posts);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WEBHOOK_URL", prevUrl);
        }
    }

    [Fact]
    public async Task GenericWebhookNotificationHelper_WhenConfigured_PostsExpectedJsonEvents()
    {
        var prevUrl = Environment.GetEnvironmentVariable("WEBHOOK_URL");
        Environment.SetEnvironmentVariable("WEBHOOK_URL", "https://webhook.example/hook");

        try
        {
            var handler = new CapturingHttpHandler();
            var helper = new GenericWebhookNotificationHelper(
                NullLogger<GenericWebhookNotificationHelper>.Instance,
                new StubHttpClientFactory(handler));

            Assert.True(helper.IsConfigured);

            await helper.SendStartupTest();
            await helper.SendUpdateStarted();

            var summary = new UpdateSummary
            {
                TotalDuration = TimeSpan.FromSeconds(18)
            };
            summary.Successes.Add(new ContainerUpdateResult
            {
                ContainerName = "web",
                ImageName = "nginx",
                OldTag = "1.24",
                NewTag = "latest",
                Success = true
            });
            summary.Rollbacks.Add(new ContainerUpdateResult
            {
                ContainerName = "api",
                ImageName = "myorg/api",
                ErrorMessage = "unhealthy"
            });
            summary.FailedPulls.Add(new ContainerUpdateResult
            {
                ContainerName = "worker",
                ImageName = "myorg/worker",
                ErrorMessage = "timeout"
            });
            summary.Skipped.Add(new ContainerUpdateResult
            {
                ContainerName = "db",
                ImageName = "postgres",
                ErrorMessage = "pinned"
            });
            summary.UpToDate.Add("redis");

            await helper.SendSummary(summary);

            Assert.Equal(3, handler.Posts.Count);

            using var startupDoc = JsonDocument.Parse(handler.Posts[0].Body);
            Assert.Equal("startup", startupDoc.RootElement.GetProperty("event").GetString());

            using var startedDoc = JsonDocument.Parse(handler.Posts[1].Body);
            Assert.Equal("update_started", startedDoc.RootElement.GetProperty("event").GetString());

            using var cycleDoc = JsonDocument.Parse(handler.Posts[2].Body);
            Assert.Equal("update_cycle", cycleDoc.RootElement.GetProperty("event").GetString());
            Assert.Equal(18, cycleDoc.RootElement.GetProperty("durationSeconds").GetInt64());
            Assert.Equal("web", cycleDoc.RootElement.GetProperty("successes")[0].GetProperty("container").GetString());
            Assert.Equal("api", cycleDoc.RootElement.GetProperty("rollbacks")[0].GetProperty("container").GetString());
            Assert.Equal("worker", cycleDoc.RootElement.GetProperty("failedPulls")[0].GetProperty("container").GetString());
            Assert.Equal("db", cycleDoc.RootElement.GetProperty("skipped")[0].GetProperty("container").GetString());
            Assert.Equal("redis", cycleDoc.RootElement.GetProperty("upToDate")[0].GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("WEBHOOK_URL", prevUrl);
        }
    }

    [Fact]
    public async Task UpdateCoordinator_TryStartManualUpdate_WhenUpdateAlreadyRunning_ReturnsFalse()
    {
        var prevSelfUpdate = Environment.GetEnvironmentVariable("SELF_UPDATE");
        var prevExcluded = Environment.GetEnvironmentVariable("EXCLUDED_CONTAINERS");
        Environment.SetEnvironmentVariable("SELF_UPDATE", "false");
        Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", "");

        try
        {
            var state = new SimulatedDockerState();
            state.Containers["web-id"] = new SimulatedContainer
            {
                Id = "web-id",
                Name = "web",
                Image = "nginx:latest",
                ImageId = "sha256:web",
                Running = true
            };
            state.ImageDigests["sha256:web"] = ["nginx@sha256:old"];

            var digestCheckEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDigestCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var registry = new FakeRegistryHelper(new Dictionary<string, string?>())
            {
                OnGetManifestDigest = async (_, _, _) =>
                {
                    digestCheckEntered.TrySetResult();
                    await releaseDigestCheck.Task;
                    return "sha256:old";
                }
            };

            var dockerHelper = new DockerHelper(
                NullLogger<DockerHelper>.Instance,
                new FakeCredentialsHelper(),
                registry,
                state.CreateClient(),
                TimeSpan.FromMilliseconds(5),
                "mobysync-host");

            var coordinator = new UpdateCoordinator(
                dockerHelper,
                [new FakeNotificationHelper()],
                NullLogger<UpdateCoordinator>.Instance);

            var scheduledTask = coordinator.ExecuteScheduledUpdate();
            await digestCheckEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var startedSecond = await coordinator.TryStartManualUpdate();
            Assert.False(startedSecond);

            releaseDigestCheck.TrySetResult();
            await scheduledTask;
        }
        finally
        {
            Environment.SetEnvironmentVariable("SELF_UPDATE", prevSelfUpdate);
            Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", prevExcluded);
        }
    }

    [Fact]
    public async Task UpdateCoordinator_SendStartupNotifications_WhenOneProviderThrows_StillNotifiesOtherProviders()
    {
        var state = new SimulatedDockerState();
        var dockerHelper = new DockerHelper(
            NullLogger<DockerHelper>.Instance,
            new FakeCredentialsHelper(),
            new FakeRegistryHelper(new Dictionary<string, string?>()),
            state.CreateClient(),
            TimeSpan.FromMilliseconds(5),
            "mobysync-host");

        var workingProvider = new FakeNotificationHelper();
        var coordinator = new UpdateCoordinator(
            dockerHelper,
            [new ThrowingNotificationHelper(), workingProvider],
            NullLogger<UpdateCoordinator>.Instance);

        await coordinator.SendStartupNotifications();

        Assert.Equal(1, workingProvider.StartupTestCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key")]
    public async Task ApiKeyFilter_WhenHeaderMissingOrInvalid_ReturnsUnauthorizedAndSkipsNext(string? headerValue)
    {
        var prevApiKey = Environment.GetEnvironmentVariable("API_KEY");
        Environment.SetEnvironmentVariable("API_KEY", "expected-secret");

        try
        {
            var filter = new ApiKeyFilter();
            var httpContext = new DefaultHttpContext();
            if (headerValue is not null)
            {
                httpContext.Request.Headers["X-API-KEY"] = headerValue;
            }

            var nextInvoked = false;
            var invocationContext = new DefaultEndpointFilterInvocationContext(httpContext);

            var result = await filter.InvokeAsync(invocationContext, _ =>
            {
                nextInvoked = true;
                return ValueTask.FromResult<object?>(Results.Ok());
            });

            Assert.False(nextInvoked);
            var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
            Assert.Equal(StatusCodes.Status401Unauthorized, statusResult.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("API_KEY", prevApiKey);
        }
    }

    [Fact]
    public async Task ApiKeyFilter_WhenHeaderMatches_InvokesNextDelegate()
    {
        var prevApiKey = Environment.GetEnvironmentVariable("API_KEY");
        Environment.SetEnvironmentVariable("API_KEY", "expected-secret");

        try
        {
            var filter = new ApiKeyFilter();
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Headers["X-API-KEY"] = "expected-secret";

            var nextInvoked = false;
            var invocationContext = new DefaultEndpointFilterInvocationContext(httpContext);

            var result = await filter.InvokeAsync(invocationContext, _ =>
            {
                nextInvoked = true;
                return ValueTask.FromResult<object?>(Results.Accepted());
            });

            Assert.True(nextInvoked);
            var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
            Assert.Equal(StatusCodes.Status202Accepted, statusResult.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("API_KEY", prevApiKey);
        }
    }

    [Fact]
    public async Task UpdateCoordinator_GetStatus_ReportsIdleRunningNextRunLastSummaryAndHistory()
    {
        var prevSelfUpdate = Environment.GetEnvironmentVariable("SELF_UPDATE");
        var prevExcluded = Environment.GetEnvironmentVariable("EXCLUDED_CONTAINERS");
        Environment.SetEnvironmentVariable("SELF_UPDATE", "false");
        Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", "");

        try
        {
            var state = new SimulatedDockerState();
            state.Containers["web-id"] = new SimulatedContainer
            {
                Id = "web-id",
                Name = "web",
                Image = "nginx:latest",
                ImageId = "sha256:web",
                Running = true
            };
            state.ImageDigests["sha256:web"] = ["nginx@sha256:old"];

            var digestCheckEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDigestCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var registry = new FakeRegistryHelper(new Dictionary<string, string?>())
            {
                OnGetManifestDigest = async (_, _, _) =>
                {
                    digestCheckEntered.TrySetResult();
                    await releaseDigestCheck.Task;
                    return "sha256:new";
                }
            };

            var dockerHelper = new DockerHelper(
                NullLogger<DockerHelper>.Instance,
                new FakeCredentialsHelper(),
                registry,
                state.CreateClient(),
                TimeSpan.FromMilliseconds(5),
                "mobysync-host");

            var coordinator = new UpdateCoordinator(
                dockerHelper,
                [new FakeNotificationHelper()],
                NullLogger<UpdateCoordinator>.Instance);

            var initialStatus = coordinator.GetStatus();
            Assert.Equal("idle", initialStatus.State);
            Assert.Null(initialStatus.LastRunUtc);
            Assert.Null(initialStatus.LastSummary);
            Assert.Empty(initialStatus.History);

            var nextRunUtc = new DateTime(2026, 11, 25, 21, 30, 0, DateTimeKind.Utc);
            coordinator.SetNextScheduledRun(nextRunUtc, "Europe/Athens");

            var scheduledTask = coordinator.ExecuteScheduledUpdate();
            await digestCheckEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var runningStatus = coordinator.GetStatus();
            Assert.Equal("running", runningStatus.State);
            Assert.Equal(nextRunUtc, runningStatus.NextScheduledRunUtc);
            Assert.Equal("Europe/Athens", runningStatus.Timezone);

            releaseDigestCheck.TrySetResult();
            await scheduledTask;

            var completedStatus = coordinator.GetStatus();
            Assert.Equal("idle", completedStatus.State);
            Assert.NotNull(completedStatus.LastRunUtc);
            Assert.NotNull(completedStatus.LastSummary);
            Assert.Single(completedStatus.LastSummary.Successes);

            var entry = Assert.Single(completedStatus.History);
            Assert.Equal("scheduled", entry.Trigger);
            Assert.Equal("web", entry.Summary.Successes[0].ContainerName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SELF_UPDATE", prevSelfUpdate);
            Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", prevExcluded);
        }
    }

    [Fact]
    public async Task UpdateCoordinator_CheckForUpdates_DryRunDetectsAvailableUpdatesWithoutPullingOrReplacing()
    {
        var prevExcluded = Environment.GetEnvironmentVariable("EXCLUDED_CONTAINERS");
        Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", "");

        try
        {
            var state = new SimulatedDockerState();

            state.Containers["web-id"] = new SimulatedContainer
            {
                Id = "web-id",
                Name = "web",
                Image = "nginx:latest",
                ImageId = "sha256:web-img",
                Running = true
            };
            state.ImageDigests["sha256:web-img"] = ["nginx@sha256:web-old"];

            state.Containers["redis-id"] = new SimulatedContainer
            {
                Id = "redis-id",
                Name = "redis",
                Image = "redis:alpine",
                ImageId = "sha256:redis-img",
                Running = true
            };
            state.ImageDigests["sha256:redis-img"] = ["redis@sha256:redis-current"];

            state.Containers["db-id"] = new SimulatedContainer
            {
                Id = "db-id",
                Name = "db",
                Image = "postgres:16.1",
                ImageId = "sha256:db-img",
                Running = true
            };

            var registry = new FakeRegistryHelper(new Dictionary<string, string?>
            {
                ["nginx:latest"] = "sha256:web-new",
                ["redis:alpine"] = "sha256:redis-current"
            });

            var dockerHelper = new DockerHelper(
                NullLogger<DockerHelper>.Instance,
                new FakeCredentialsHelper(),
                registry,
                state.CreateClient(),
                TimeSpan.FromMilliseconds(5),
                "mobysync-host");

            var coordinator = new UpdateCoordinator(
                dockerHelper,
                [new FakeNotificationHelper()],
                NullLogger<UpdateCoordinator>.Instance);

            var checkSummary = await coordinator.CheckForUpdates();

            Assert.True(checkSummary.HasUpdates);
            var available = Assert.Single(checkSummary.UpdatesAvailable);
            Assert.Equal("web", available.ContainerName);
            Assert.Equal("nginx", available.ImageName);
            Assert.Equal("latest", available.TargetTag);
            Assert.Equal("sha256:web-old", available.CurrentDigest);
            Assert.Equal("sha256:web-new", available.RemoteDigest);

            Assert.Equal(["redis"], checkSummary.UpToDate);
            var skipped = Assert.Single(checkSummary.Skipped);
            Assert.Equal("db", skipped.ContainerName);
            Assert.Empty(checkSummary.Errors);

            Assert.Empty(state.PulledImages);
            Assert.Empty(state.OperationLog);
            Assert.True(state.Containers["web-id"].Running);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", prevExcluded);
        }
    }

    [Fact]
    public async Task UpdateCoordinator_ExecuteTargetedUpdate_UpdatesTargetAndDependencyGroupOnly()
    {
        var prevExcluded = Environment.GetEnvironmentVariable("EXCLUDED_CONTAINERS");
        Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", "");

        try
        {
            var state = new SimulatedDockerState();

            state.Containers["db-id"] = new SimulatedContainer
            {
                Id = "db-id",
                Name = "db",
                Image = "postgres:latest",
                ImageId = "sha256:db-old",
                Running = true
            };
            state.ImageDigests["sha256:db-old"] = ["postgres@sha256:old"];

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

            state.Containers["other-id"] = new SimulatedContainer
            {
                Id = "other-id",
                Name = "other-service",
                Image = "caddy:latest",
                ImageId = "sha256:other-old",
                Running = true
            };
            state.ImageDigests["sha256:other-old"] = ["caddy@sha256:old"];

            var registry = new FakeRegistryHelper(new Dictionary<string, string?>
            {
                ["postgres:latest"] = "sha256:new",
                ["myorg/api:latest"] = "sha256:new",
                ["caddy:latest"] = "sha256:new"
            });

            var dockerHelper = new DockerHelper(
                NullLogger<DockerHelper>.Instance,
                new FakeCredentialsHelper(),
                registry,
                state.CreateClient(),
                TimeSpan.FromMilliseconds(5),
                "mobysync-host");

            var notificationHelper = new FakeNotificationHelper();
            var coordinator = new UpdateCoordinator(
                dockerHelper,
                [notificationHelper],
                NullLogger<UpdateCoordinator>.Instance);

            var summary = await coordinator.ExecuteTargetedUpdate("api", includeDependencies: true);

            Assert.NotNull(summary);
            Assert.Equal(2, summary.Successes.Count);
            Assert.Equal("db", summary.Successes[0].ContainerName);
            Assert.Equal("api", summary.Successes[1].ContainerName);
            Assert.True(state.Containers.ContainsKey("other-id"));

            var status = coordinator.GetStatus();
            Assert.Equal("targeted:api", status.History[0].Trigger);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", prevExcluded);
        }
    }

    [Fact]
    public async Task UpdateCoordinator_TryStartTargetedUpdate_ReturnsNotFoundForMissingContainer_AndConflictWhenBusy()
    {
        var prevSelfUpdate = Environment.GetEnvironmentVariable("SELF_UPDATE");
        var prevExcluded = Environment.GetEnvironmentVariable("EXCLUDED_CONTAINERS");
        Environment.SetEnvironmentVariable("SELF_UPDATE", "false");
        Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", "");

        try
        {
            var state = new SimulatedDockerState();
            state.Containers["web-id"] = new SimulatedContainer
            {
                Id = "web-id",
                Name = "web",
                Image = "nginx:latest",
                ImageId = "sha256:web",
                Running = true
            };
            state.ImageDigests["sha256:web"] = ["nginx@sha256:old"];

            var digestCheckEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDigestCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var registry = new FakeRegistryHelper(new Dictionary<string, string?>())
            {
                OnGetManifestDigest = async (_, _, _) =>
                {
                    digestCheckEntered.TrySetResult();
                    await releaseDigestCheck.Task;
                    return "sha256:old";
                }
            };

            var dockerHelper = new DockerHelper(
                NullLogger<DockerHelper>.Instance,
                new FakeCredentialsHelper(),
                registry,
                state.CreateClient(),
                TimeSpan.FromMilliseconds(5),
                "mobysync-host");

            var coordinator = new UpdateCoordinator(
                dockerHelper,
                [new FakeNotificationHelper()],
                NullLogger<UpdateCoordinator>.Instance);

            var missingStatus = await coordinator.TryStartTargetedUpdate("nonexistent");
            Assert.Equal(TargetedTriggerStatus.ContainerNotFound, missingStatus);
            Assert.Equal("idle", coordinator.GetStatus().State);

            var scheduledTask = coordinator.ExecuteScheduledUpdate();
            await digestCheckEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var conflictStatus = await coordinator.TryStartTargetedUpdate("web");
            Assert.Equal(TargetedTriggerStatus.AlreadyRunning, conflictStatus);

            releaseDigestCheck.TrySetResult();
            await scheduledTask;
        }
        finally
        {
            Environment.SetEnvironmentVariable("SELF_UPDATE", prevSelfUpdate);
            Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", prevExcluded);
        }
    }

    [Fact]
    public async Task UpdateCoordinator_ExecuteTargetedUpdate_WhenContainerNotFound_ReturnsNullWithoutSendingNotifications()
    {
        var prevExcluded = Environment.GetEnvironmentVariable("EXCLUDED_CONTAINERS");
        Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", "");

        try
        {
            var state = new SimulatedDockerState();
            var dockerHelper = new DockerHelper(
                NullLogger<DockerHelper>.Instance,
                new FakeCredentialsHelper(),
                new FakeRegistryHelper(new Dictionary<string, string?>()),
                state.CreateClient(),
                TimeSpan.FromMilliseconds(5),
                "mobysync-host");

            var notificationHelper = new FakeNotificationHelper();
            var coordinator = new UpdateCoordinator(
                dockerHelper,
                [notificationHelper],
                NullLogger<UpdateCoordinator>.Instance);

            var summary = await coordinator.ExecuteTargetedUpdate("missing-app");

            Assert.Null(summary);
            Assert.Equal(0, notificationHelper.UpdateStartedCount);
            Assert.Empty(notificationHelper.SentSummaries);
            Assert.Empty(coordinator.GetStatus().History);
            Assert.Equal("idle", coordinator.GetStatus().State);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", prevExcluded);
        }
    }

    [Fact]
    public async Task UpdateCoordinator_ExecuteTargetedUpdate_WhenIncludeDependenciesFalse_UpdatesOnlyTargetContainer()
    {
        var prevExcluded = Environment.GetEnvironmentVariable("EXCLUDED_CONTAINERS");
        Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", "");

        try
        {
            var state = new SimulatedDockerState();

            state.Containers["db-id"] = new SimulatedContainer
            {
                Id = "db-id",
                Name = "db",
                Image = "postgres:latest",
                ImageId = "sha256:db-old",
                Running = true
            };
            state.ImageDigests["sha256:db-old"] = ["postgres@sha256:old"];

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

            var registry = new FakeRegistryHelper(new Dictionary<string, string?>
            {
                ["postgres:latest"] = "sha256:new",
                ["myorg/api:latest"] = "sha256:new"
            });

            var dockerHelper = new DockerHelper(
                NullLogger<DockerHelper>.Instance,
                new FakeCredentialsHelper(),
                registry,
                state.CreateClient(),
                TimeSpan.FromMilliseconds(5),
                "mobysync-host");

            var coordinator = new UpdateCoordinator(
                dockerHelper,
                [new FakeNotificationHelper()],
                NullLogger<UpdateCoordinator>.Instance);

            var summary = await coordinator.ExecuteTargetedUpdate("api", includeDependencies: false);

            Assert.NotNull(summary);
            var updated = Assert.Single(summary.Successes);
            Assert.Equal("api", updated.ContainerName);
            Assert.True(state.Containers.ContainsKey("db-id"));
            Assert.False(state.Containers.ContainsKey("api-id"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", prevExcluded);
        }
    }

    [Fact]
    public async Task UpdateCoordinator_GetStatus_CapsHistoryAtTwentyMostRecentRuns()
    {
        var prevSelfUpdate = Environment.GetEnvironmentVariable("SELF_UPDATE");
        var prevExcluded = Environment.GetEnvironmentVariable("EXCLUDED_CONTAINERS");
        Environment.SetEnvironmentVariable("SELF_UPDATE", "false");
        Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", "");

        try
        {
            var state = new SimulatedDockerState();
            var dockerHelper = new DockerHelper(
                NullLogger<DockerHelper>.Instance,
                new FakeCredentialsHelper(),
                new FakeRegistryHelper(new Dictionary<string, string?>()),
                state.CreateClient(),
                TimeSpan.FromMilliseconds(5),
                "mobysync-host");

            var coordinator = new UpdateCoordinator(
                dockerHelper,
                [new FakeNotificationHelper()],
                NullLogger<UpdateCoordinator>.Instance);

            for (var i = 0; i < 25; i++)
            {
                await coordinator.ExecuteScheduledUpdate();
            }

            var status = coordinator.GetStatus();
            Assert.Equal(20, status.History.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SELF_UPDATE", prevSelfUpdate);
            Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", prevExcluded);
        }
    }

    [Fact]
    public async Task RouteExtensions_MapUpdaterEndpoints_HandlesStatusCheckAndTargetedTriggerResponses()
    {
        var prevApiKey = Environment.GetEnvironmentVariable("API_KEY");
        var prevExcluded = Environment.GetEnvironmentVariable("EXCLUDED_CONTAINERS");
        Environment.SetEnvironmentVariable("API_KEY", "route-test-key");
        Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", "");

        try
        {
            var state = new SimulatedDockerState();
            state.Containers["web-id"] = new SimulatedContainer
            {
                Id = "web-id",
                Name = "web",
                Image = "nginx:latest",
                ImageId = "sha256:web-img",
                Running = true
            };
            state.ImageDigests["sha256:web-img"] = ["nginx@sha256:old"];

            var registry = new FakeRegistryHelper(new Dictionary<string, string?>
            {
                ["nginx:latest"] = "sha256:new"
            });

            var dockerHelper = new DockerHelper(
                NullLogger<DockerHelper>.Instance,
                new FakeCredentialsHelper(),
                registry,
                state.CreateClient(),
                TimeSpan.FromMilliseconds(5),
                "mobysync-host");

            var coordinator = new UpdateCoordinator(
                dockerHelper,
                [new FakeNotificationHelper()],
                NullLogger<UpdateCoordinator>.Instance);

            var builder = WebApplication.CreateBuilder();
            builder.Services.AddSingleton(coordinator);
            await using var app = builder.Build();
            app.MapGroup("/api")
                .AddEndpointFilter<ApiKeyFilter>()
                .MapUpdaterEndpoints();

            var endpoints = ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(ds => ds.Endpoints)
                .OfType<RouteEndpoint>()
                .ToList();

            async Task<(int StatusCode, string Body)> InvokeEndpointAsync(
                string routePattern,
                string? apiKey,
                Dictionary<string, object?>? routeValues = null,
                string? queryString = null)
            {
                var endpoint = Assert.Single(endpoints, e => e.RoutePattern.RawText == routePattern);
                var context = new DefaultHttpContext
                {
                    RequestServices = app.Services
                };
                context.Response.Body = new MemoryStream();
                if (apiKey is not null)
                    context.Request.Headers["X-API-KEY"] = apiKey;
                if (routeValues is not null)
                {
                    foreach (var kvp in routeValues)
                        context.Request.RouteValues[kvp.Key] = kvp.Value;
                }
                if (!string.IsNullOrEmpty(queryString))
                    context.Request.QueryString = new QueryString(queryString);

                await endpoint.RequestDelegate!(context);
                context.Response.Body.Position = 0;
                using var reader = new StreamReader(context.Response.Body);
                return (context.Response.StatusCode, await reader.ReadToEndAsync());
            }

            // Missing API key -> 401
            var (unauthStatus, _) = await InvokeEndpointAsync("/api/status", apiKey: null);
            Assert.Equal(StatusCodes.Status401Unauthorized, unauthStatus);

            // GET /api/status -> 200 with state=idle
            var (statusCode, statusBody) = await InvokeEndpointAsync("/api/status", "route-test-key");
            Assert.Equal(StatusCodes.Status200OK, statusCode);
            using (var doc = JsonDocument.Parse(statusBody))
            {
                Assert.Equal("idle", doc.RootElement.GetProperty("state").GetString());
            }

            // POST /api/update/check -> 200 with web in updatesAvailable
            var (checkCode, checkBody) = await InvokeEndpointAsync("/api/update/check", "route-test-key");
            Assert.Equal(StatusCodes.Status200OK, checkCode);
            using (var doc = JsonDocument.Parse(checkBody))
            {
                Assert.True(doc.RootElement.GetProperty("hasUpdates").GetBoolean());
                Assert.Equal("web", doc.RootElement.GetProperty("updatesAvailable")[0].GetProperty("containerName").GetString());
            }

            // POST /api/update/trigger/missing -> 404
            var (notFoundCode, _) = await InvokeEndpointAsync(
                "/api/update/trigger/{containerName}",
                "route-test-key",
                new Dictionary<string, object?> { ["containerName"] = "missing" });
            Assert.Equal(StatusCodes.Status404NotFound, notFoundCode);

            // POST /api/update/trigger/web?includeDependencies=false -> 202
            var (acceptedCode, acceptedBody) = await InvokeEndpointAsync(
                "/api/update/trigger/{containerName}",
                "route-test-key",
                new Dictionary<string, object?> { ["containerName"] = "web" },
                "?includeDependencies=false");
            Assert.Equal(StatusCodes.Status202Accepted, acceptedCode);
            using (var doc = JsonDocument.Parse(acceptedBody))
            {
                Assert.Equal("web", doc.RootElement.GetProperty("container").GetString());
                Assert.False(doc.RootElement.GetProperty("includeDependencies").GetBoolean());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("API_KEY", prevApiKey);
            Environment.SetEnvironmentVariable("EXCLUDED_CONTAINERS", prevExcluded);
        }
    }
}
