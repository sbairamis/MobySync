using System.Text;
using System.Text.Json;
using MobySync.Models;
using Docker.DotNet.Models;

namespace MobySync.Helpers;

public class CredentialsHelper(ILogger<CredentialsHelper> logger)
{
    private readonly string _credentialsPath = Path.Combine("/app", "creds", "config.json");

    // Docker Hub credentials can be stored under several keys depending on which tool wrote config.json.
    private static readonly string[] DockerHubKeys =
    [
        "https://index.docker.io/v1/", // Docker Desktop / docker login default
        "registry-1.docker.io",        // Podman, some CI toolchains
        "docker.io"                     // older Docker CLI versions
    ];

    public virtual async Task<AuthConfig> FetchCredentials(string baseImageName)
    {
        var parts = baseImageName.Split('/');

        string registryServerAddress;
        string[] lookupKeys;

        if (parts.Length > 1 && (parts[0].Contains('.') || parts[0].Contains(':')))
        {
            registryServerAddress = parts[0];
            lookupKeys = [registryServerAddress];
        }
        else
        {
            registryServerAddress = "docker.io";
            lookupKeys = DockerHubKeys;
        }

        if (!File.Exists(_credentialsPath))
        {
            logger.LogDebug("No credentials file found at {Path} — pulling {Image} anonymously", _credentialsPath, baseImageName);
            return new AuthConfig();
        }

        return await ParseCredentialsFile(registryServerAddress, lookupKeys, baseImageName);
    }

    private async Task<AuthConfig> ParseCredentialsFile(string registryServerAddress, string[] lookupKeys, string baseImageName)
    {
        try
        {
            var jsonString = await File.ReadAllTextAsync(_credentialsPath);

            var config = JsonSerializer.Deserialize<RegistryCredentials>(jsonString,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (config?.Auths != null)
            {
                foreach (var key in lookupKeys)
                {
                    if (!config.Auths.TryGetValue(key, out var authEntry))
                        continue;

                    var decodedAuthBytes = Convert.FromBase64String(authEntry.Auth);
                    var decodedAuthString = Encoding.UTF8.GetString(decodedAuthBytes);
                    var splitIndex = decodedAuthString.IndexOf(':');

                    if (splitIndex <= 0)
                        continue;

                    logger.LogDebug("Found credentials for {Registry} (key: {Key})", registryServerAddress, key);

                    return new AuthConfig
                    {
                        ServerAddress = registryServerAddress,
                        Username = decodedAuthString.Substring(0, splitIndex),
                        Password = decodedAuthString.Substring(splitIndex + 1)
                    };
                }
            }

            logger.LogDebug("No credentials found for {Registry} — pulling {Image} anonymously", registryServerAddress, baseImageName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read credentials file — pulling {Image} anonymously", baseImageName);
        }

        return new AuthConfig();
    }
}
