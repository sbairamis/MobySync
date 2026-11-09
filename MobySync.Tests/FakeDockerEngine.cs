using System.Reflection;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MobySync.Helpers;
using MobySync.Interfaces;
using MobySync.Models;

namespace MobySync.Tests;

public class SimulatedContainer
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
    public string ImageId { get; set; } = string.Empty;
    public bool Running { get; set; } = true;
    public bool Restarting { get; set; }
    public bool OOMKilled { get; set; }
    public bool Dead { get; set; }
    public long ExitCode { get; set; }
    public long Pid { get; set; } = 1000;
    public long RestartCount { get; set; }
    public string StartedAt { get; set; } = "2026-11-09T19:00:00.000000000Z";
    public string? HealthStatus { get; set; }
    public string? HealthLogOutput { get; set; }
    public int InspectCount { get; set; }
    public IDictionary<string, string> Labels { get; set; } = new Dictionary<string, string>();
    public IList<string> Env { get; set; } = new List<string>();
    public string Hostname { get; set; } = string.Empty;
    public HostConfig HostConfig { get; set; } = new();
    public IDictionary<string, EndpointSettings> Networks { get; set; } = new Dictionary<string, EndpointSettings>();
}

public class SimulatedDockerState
{
    public Dictionary<string, SimulatedContainer> Containers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> ImageDigests { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string FromImage, string Tag)> PulledImages { get; } = [];
    public List<string> OperationLog { get; } = [];
    public bool PruneCalled { get; set; }

    /// <summary>
    /// Optional hook to simulate a newly created container crashing after StartContainerAsync.
    /// </summary>
    public Func<SimulatedContainer, bool>? OnStartContainerCrash { get; set; }

    /// <summary>
    /// Optional hook invoked each time a container is inspected (useful for simulating health transitions or crash loops).
    /// </summary>
    public Action<SimulatedContainer>? OnInspectContainer { get; set; }

    /// <summary>
    /// Optional hook to throw during image pull.
    /// </summary>
    public Action<ImagesCreateParameters>? OnPullImage { get; set; }

    private int _nextIdCounter = 1;

    public IDockerClient CreateClient()
    {
        var containersProxy = DispatchProxy.Create<IContainerOperations, ContainerOperationsProxy>();
        ((ContainerOperationsProxy)(object)containersProxy).State = this;

        var imagesProxy = DispatchProxy.Create<IImageOperations, ImageOperationsProxy>();
        ((ImageOperationsProxy)(object)imagesProxy).State = this;

        var clientProxy = DispatchProxy.Create<IDockerClient, DockerClientProxy>();
        var clientImpl = (DockerClientProxy)(object)clientProxy;
        clientImpl.Containers = containersProxy;
        clientImpl.Images = imagesProxy;

        return clientProxy;
    }

    public string NextContainerId() => $"f9e8d7c6b5a4{_nextIdCounter++:D4}00000000";
}

public class DockerClientProxy : DispatchProxy
{
    public IContainerOperations Containers { get; set; } = null!;
    public IImageOperations Images { get; set; } = null!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        return targetMethod?.Name switch
        {
            "get_Containers" => Containers,
            "get_Images" => Images,
            "Dispose" => null,
            _ => throw new NotImplementedException($"IDockerClient.{targetMethod?.Name} is not implemented in proxy.")
        };
    }
}

public class ContainerOperationsProxy : DispatchProxy
{
    public SimulatedDockerState State { get; set; } = null!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        return targetMethod?.Name switch
        {
            nameof(IContainerOperations.ListContainersAsync) => ListContainersAsync(),
            nameof(IContainerOperations.InspectContainerAsync) => InspectContainerAsync((string)args![0]!),
            nameof(IContainerOperations.RenameContainerAsync) => RenameContainerAsync((string)args![0]!, (ContainerRenameParameters)args[1]!),
            nameof(IContainerOperations.StopContainerAsync) => StopContainerAsync((string)args![0]!),
            nameof(IContainerOperations.CreateContainerAsync) => CreateContainerAsync((CreateContainerParameters)args![0]!),
            nameof(IContainerOperations.StartContainerAsync) => StartContainerAsync((string)args![0]!),
            nameof(IContainerOperations.RemoveContainerAsync) => RemoveContainerAsync((string)args![0]!),
            _ => throw new NotImplementedException($"IContainerOperations.{targetMethod?.Name} is not implemented in proxy.")
        };
    }

    private Task<IList<ContainerListResponse>> ListContainersAsync()
    {
        IList<ContainerListResponse> list = State.Containers.Values.Select(c => new ContainerListResponse
        {
            ID = c.Id,
            Names = [$"/{c.Name.TrimStart('/')}"],
            Image = c.Image,
            ImageID = c.ImageId,
            State = c.Running ? "running" : "exited",
            Labels = c.Labels
        }).ToList();

        return Task.FromResult(list);
    }

    private Task<ContainerInspectResponse> InspectContainerAsync(string id)
    {
        if (!State.Containers.TryGetValue(id, out var c))
            throw new InvalidOperationException($"Container '{id}' not found.");

        c.InspectCount++;
        State.OnInspectContainer?.Invoke(c);

        Health? health = null;
        if (!string.IsNullOrEmpty(c.HealthStatus))
        {
            health = new Health
            {
                Status = c.HealthStatus,
                Log = string.IsNullOrEmpty(c.HealthLogOutput)
                    ? []
                    : [new HealthcheckResult { Output = c.HealthLogOutput, ExitCode = 1 }]
            };
        }

        return Task.FromResult(new ContainerInspectResponse
        {
            ID = c.Id,
            Name = $"/{c.Name.TrimStart('/')}",
            RestartCount = c.RestartCount,
            Config = new ContainerConfig
            {
                Image = c.Image,
                Env = new List<string>(c.Env),
                Labels = new Dictionary<string, string>(c.Labels),
                Hostname = c.Hostname
            },
            HostConfig = c.HostConfig,
            NetworkSettings = new NetworkSettings
            {
                Networks = c.Networks
            },
            State = new State
            {
                Running = c.Running,
                Restarting = c.Restarting,
                OOMKilled = c.OOMKilled,
                Dead = c.Dead,
                ExitCode = c.ExitCode,
                Pid = c.Pid,
                StartedAt = c.StartedAt,
                Health = health
            }
        });
    }

    private Task RenameContainerAsync(string id, ContainerRenameParameters parameters)
    {
        if (!State.Containers.TryGetValue(id, out var c))
            throw new InvalidOperationException($"Container '{id}' not found for rename.");

        var newName = (parameters.NewName ?? string.Empty).TrimStart('/');
        State.OperationLog.Add($"rename:{c.Name}->{newName}");
        c.Name = newName;
        return Task.CompletedTask;
    }

    private Task<bool> StopContainerAsync(string id)
    {
        if (!State.Containers.TryGetValue(id, out var c))
            throw new InvalidOperationException($"Container '{id}' not found for stop.");

        State.OperationLog.Add($"stop:{c.Name}");
        c.Running = false;
        return Task.FromResult(true);
    }

    private Task<CreateContainerResponse> CreateContainerAsync(CreateContainerParameters parameters)
    {
        var name = (parameters.Name ?? string.Empty).TrimStart('/');
        if (State.Containers.Values.Any(existing => string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Conflict: container name '{name}' is already in use.");
        }

        var newId = State.NextContainerId();
        var hostname = string.IsNullOrEmpty(parameters.Hostname) ? newId[..12] : parameters.Hostname;

        var created = new SimulatedContainer
        {
            Id = newId,
            Name = name,
            Image = parameters.Image,
            ImageId = $"sha256:img-{parameters.Image}",
            Running = false,
            Hostname = hostname,
            Env = parameters.Env ?? new List<string>(),
            Labels = parameters.Labels ?? new Dictionary<string, string>(),
            HostConfig = parameters.HostConfig ?? new HostConfig(),
            Networks = parameters.NetworkingConfig?.EndpointsConfig ?? new Dictionary<string, EndpointSettings>()
        };

        State.Containers[newId] = created;
        State.OperationLog.Add($"create:{name}:{parameters.Image}");

        return Task.FromResult(new CreateContainerResponse { ID = newId });
    }

    private Task<bool> StartContainerAsync(string id)
    {
        if (!State.Containers.TryGetValue(id, out var c))
            throw new InvalidOperationException($"Container '{id}' not found for start.");

        State.OperationLog.Add($"start:{c.Name}");

        if (State.OnStartContainerCrash != null && State.OnStartContainerCrash(c))
        {
            c.Running = false;
            c.ExitCode = 139;
        }
        else
        {
            c.Running = true;
            c.ExitCode = 0;
        }

        return Task.FromResult(true);
    }

    private Task RemoveContainerAsync(string id)
    {
        if (State.Containers.TryGetValue(id, out var c))
        {
            State.OperationLog.Add($"remove:{c.Name}");
            State.Containers.Remove(id);
        }

        return Task.CompletedTask;
    }
}

public class ImageOperationsProxy : DispatchProxy
{
    public SimulatedDockerState State { get; set; } = null!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        return targetMethod?.Name switch
        {
            nameof(IImageOperations.InspectImageAsync) => InspectImageAsync((string)args![0]!),
            nameof(IImageOperations.CreateImageAsync) => CreateImageAsync((ImagesCreateParameters)args![0]!),
            nameof(IImageOperations.PruneImagesAsync) => PruneImagesAsync(),
            _ => throw new NotImplementedException($"IImageOperations.{targetMethod?.Name} is not implemented in proxy.")
        };
    }

    private Task<ImageInspectResponse> InspectImageAsync(string imageId)
    {
        var digests = State.ImageDigests.TryGetValue(imageId, out var list) ? list : [];
        return Task.FromResult(new ImageInspectResponse
        {
            ID = imageId,
            RepoDigests = digests
        });
    }

    private Task CreateImageAsync(ImagesCreateParameters parameters)
    {
        State.OnPullImage?.Invoke(parameters);
        State.PulledImages.Add((parameters.FromImage ?? string.Empty, parameters.Tag ?? string.Empty));
        State.OperationLog.Add($"pull:{parameters.FromImage}:{parameters.Tag}");
        return Task.CompletedTask;
    }

    private Task<ImagesPruneResponse> PruneImagesAsync()
    {
        State.PruneCalled = true;
        State.OperationLog.Add("prune");
        return Task.FromResult(new ImagesPruneResponse());
    }
}

public class FakeCredentialsHelper() : CredentialsHelper(NullLogger<CredentialsHelper>.Instance)
{
    public override Task<AuthConfig> FetchCredentials(string baseImageName) =>
        Task.FromResult(new AuthConfig());
}

public class FakeRegistryHelper(Dictionary<string, string?> remoteDigests)
    : RegistryHelper(NullLogger<RegistryHelper>.Instance, null!)
{
    public override Task<string?> GetManifestDigest(string baseImageName, string tag, AuthConfig credentials)
    {
        var key = $"{baseImageName}:{tag}";
        return Task.FromResult(remoteDigests.GetValueOrDefault(key));
    }
}

public class FakeNotificationHelper : INotificationHelper
{
    public bool IsConfigured => true;
    public string ProviderName => "FakeProvider";
    public string WebhookUrl => "http://localhost/webhook";

    public List<UpdateSummary> SentSummaries { get; } = [];
    public int UpdateStartedCount { get; private set; }
    public int StartupTestCount { get; private set; }

    public Task SendSummary(UpdateSummary summary)
    {
        SentSummaries.Add(summary);
        return Task.CompletedTask;
    }

    public Task SendStartupTest()
    {
        StartupTestCount++;
        return Task.CompletedTask;
    }

    public Task SendUpdateStarted()
    {
        UpdateStartedCount++;
        return Task.CompletedTask;
    }
}
