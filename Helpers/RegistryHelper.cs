using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Docker.DotNet.Models;

namespace MobySync.Helpers;

public class RegistryHelper(ILogger<RegistryHelper> logger, IHttpClientFactory httpClientFactory)
{
    private const string DockerHubRegistry = "registry-1.docker.io";
    private const string DockerHubAuthRealm = "https://auth.docker.io/token";
    private const string DockerHubService = "registry.docker.io";

    private static readonly string[] ManifestMediaTypes =
    [
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.docker.distribution.manifest.v2+json",
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.oci.image.manifest.v1+json"
    ];

    // Returns the manifest digest currently published for baseImageName:tag.
    // Uses credentials if provided; falls back to anonymous on auth failure.
    public virtual async Task<string?> GetManifestDigest(string baseImageName, string tag, AuthConfig credentials)
    {
        var (registryHost, repository) = ParseImageName(baseImageName);
        var hasCredentials = !string.IsNullOrEmpty(credentials.Username);

        try
        {
            var token = await GetBearerToken(registryHost, repository, credentials.Username, credentials.Password);
            return await FetchDigest(registryHost, repository, tag, token);
        }
        catch (Exception ex) when (hasCredentials)
        {
            logger.LogWarning("Credentialed registry check failed for {Image}, retrying anonymously: {Message}",
                baseImageName, ex.Message);
            var token = await GetBearerToken(registryHost, repository, null, null);
            return await FetchDigest(registryHost, repository, tag, token);
        }
    }

    // Returns (registryHost, repository) parsed from a base image name (no tag).
    // e.g. "ghcr.io/home-assistant/home-assistant" → ("ghcr.io", "home-assistant/home-assistant")
    //      "neosmemo/memos"                         → ("registry-1.docker.io", "neosmemo/memos")
    //      "nginx"                                  → ("registry-1.docker.io", "library/nginx")
    private static (string registryHost, string repository) ParseImageName(string baseImageName)
    {
        var parts = baseImageName.Split('/');

        if (parts.Length > 1 && (parts[0].Contains('.') || parts[0].Contains(':')))
            return (parts[0], string.Join("/", parts.Skip(1)));

        // Docker Hub — single-segment names are official "library" images
        var repository = parts.Length == 1 ? $"library/{baseImageName}" : baseImageName;
        return (DockerHubRegistry, repository);
    }

    private async Task<string?> GetBearerToken(string registryHost, string repository,
        string? username, string? password)
    {
        string realm, service;

        if (registryHost == DockerHubRegistry)
        {
            realm = DockerHubAuthRealm;
            service = DockerHubService;
        }
        else
        {
            (realm, service) = await DiscoverAuthEndpoint(registryHost);

            if (string.IsNullOrEmpty(realm))
                return null; // Registry requires no auth
        }

        var scope = $"repository:{repository}:pull";
        var tokenUrl = $"{realm}?service={Uri.EscapeDataString(service)}&scope={Uri.EscapeDataString(scope)}";

        var client = httpClientFactory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, tokenUrl);

        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
        {
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        }

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        if (json.TryGetProperty("token", out var tokenProp) && tokenProp.GetString() is { } t)
            return t;

        if (json.TryGetProperty("access_token", out var accessProp) && accessProp.GetString() is { } a)
            return a;

        throw new InvalidOperationException($"No token in auth response from {realm}");
    }

    // Probes /v2/ to discover the Bearer auth realm and service for a registry.
    // Returns empty strings if the registry needs no authentication.
    private async Task<(string realm, string service)> DiscoverAuthEndpoint(string registryHost)
    {
        var client = httpClientFactory.CreateClient();
        var response = await client.GetAsync($"https://{registryHost}/v2/");

        if (response.StatusCode == HttpStatusCode.OK)
            return (string.Empty, string.Empty);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
            throw new InvalidOperationException(
                $"Unexpected status {(int)response.StatusCode} from {registryHost}/v2/");

        var wwwAuth = response.Headers.WwwAuthenticate.ToString();
        var realm = ExtractHeaderParam(wwwAuth, "realm");
        var service = ExtractHeaderParam(wwwAuth, "service");

        if (string.IsNullOrEmpty(realm))
            throw new InvalidOperationException(
                $"Could not parse Www-Authenticate from {registryHost}: {wwwAuth}");

        return (realm, service);
    }

    private async Task<string?> FetchDigest(string registryHost, string repository, string tag, string? token)
    {
        var client = httpClientFactory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://{registryHost}/v2/{repository}/manifests/{tag}");

        foreach (var mediaType in ManifestMediaTypes)
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(mediaType));

        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        return response.Headers.TryGetValues("Docker-Content-Digest", out var values)
            ? values.FirstOrDefault()
            : null;
    }

    private static string ExtractHeaderParam(string header, string param)
    {
        var key = $"{param}=\"";
        var start = header.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return string.Empty;
        start += key.Length;
        var end = header.IndexOf('"', start);
        return end < 0 ? string.Empty : header[start..end];
    }
}
