using Docker.DotNet.Models;

namespace MobySync.Models;

public class ContainerCheckpoint
{
    public string OldId {get; init;} = string.Empty;   
    public string NewId {get; init;} = string.Empty; 
    public string OriginalName {get; init;} = string.Empty; 
    public string BackupName {get; init;} = string.Empty; 
    public IDictionary<string, EndpointSettings>? OriginalNetworks { get; init; }
}