using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MobySync.Helpers;

namespace MobySync.Tests;

public class RegistryAndCredentialsTests
{
    private sealed class RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization,
                request.Headers.Accept.Select(a => a.MediaType!).ToList()));

            return Task.FromResult(responder(request));
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Url,
        AuthenticationHeaderValue? Authorization,
        List<string> AcceptMediaTypes);

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static CredentialsHelper CreateCredentialsHelperWithPath(string path)
    {
        var helper = new CredentialsHelper(NullLogger<CredentialsHelper>.Instance);
        var field = typeof(CredentialsHelper).GetField("_credentialsPath", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(helper, path);
        return helper;
    }

    [Fact]
    public async Task GetManifestDigest_DockerHubOfficialImage_RequestsLibraryScopeAndReturnsDigest()
    {
        var handler = new RecordingHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.StartsWith("https://auth.docker.io/token", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"token":"dockerhub-jwt"}""", Encoding.UTF8, "application/json")
                };
            }

            if (url == "https://registry-1.docker.io/v2/library/nginx/manifests/latest")
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK);
                resp.Headers.TryAddWithoutValidation("Docker-Content-Digest", "sha256:nginx-digest-123");
                return resp;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var helper = new RegistryHelper(NullLogger<RegistryHelper>.Instance, new StubHttpClientFactory(handler));

        var digest = await helper.GetManifestDigest("nginx", "latest", new AuthConfig());

        Assert.Equal("sha256:nginx-digest-123", digest);
        Assert.Equal(2, handler.Requests.Count);

        var tokenReq = handler.Requests[0];
        Assert.Equal(
            "https://auth.docker.io/token?service=registry.docker.io&scope=repository%3Alibrary%2Fnginx%3Apull",
            tokenReq.Url);
        Assert.Null(tokenReq.Authorization);

        var manifestReq = handler.Requests[1];
        Assert.Equal("https://registry-1.docker.io/v2/library/nginx/manifests/latest", manifestReq.Url);
        Assert.Equal("Bearer", manifestReq.Authorization?.Scheme);
        Assert.Equal("dockerhub-jwt", manifestReq.Authorization?.Parameter);
        Assert.Contains("application/vnd.oci.image.index.v1+json", manifestReq.AcceptMediaTypes);
    }

    [Fact]
    public async Task GetManifestDigest_CustomRegistryWithBearerChallenge_DiscoversRealmAndUsesBasicAuth()
    {
        var handler = new RecordingHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url == "https://ghcr.io/v2/")
            {
                var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                challenge.Headers.WwwAuthenticate.Add(
                    new AuthenticationHeaderValue("Bearer", "realm=\"https://ghcr.io/token\",service=\"ghcr.io\""));
                return challenge;
            }

            if (url.StartsWith("https://ghcr.io/token", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"access_token":"ghcr-token"}""", Encoding.UTF8, "application/json")
                };
            }

            if (url == "https://ghcr.io/v2/owner/repo/manifests/edge")
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK);
                resp.Headers.TryAddWithoutValidation("Docker-Content-Digest", "sha256:ghcr-digest-456");
                return resp;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var helper = new RegistryHelper(NullLogger<RegistryHelper>.Instance, new StubHttpClientFactory(handler));
        var creds = new AuthConfig { Username = "octocat", Password = "secret-pat" };

        var digest = await helper.GetManifestDigest("ghcr.io/owner/repo", "edge", creds);

        Assert.Equal("sha256:ghcr-digest-456", digest);
        Assert.Equal(3, handler.Requests.Count);

        var tokenReq = handler.Requests[1];
        Assert.Equal("https://ghcr.io/token?service=ghcr.io&scope=repository%3Aowner%2Frepo%3Apull", tokenReq.Url);
        Assert.Equal("Basic", tokenReq.Authorization?.Scheme);
        var expectedBasic = Convert.ToBase64String(Encoding.UTF8.GetBytes("octocat:secret-pat"));
        Assert.Equal(expectedBasic, tokenReq.Authorization?.Parameter);

        var manifestReq = handler.Requests[2];
        Assert.Equal("Bearer", manifestReq.Authorization?.Scheme);
        Assert.Equal("ghcr-token", manifestReq.Authorization?.Parameter);
    }

    [Fact]
    public async Task GetManifestDigest_CustomRegistryWithoutAuth_FetchesManifestDirectly()
    {
        var handler = new RecordingHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url == "https://local.registry:5000/v2/")
                return new HttpResponseMessage(HttpStatusCode.OK);

            if (url == "https://local.registry:5000/v2/team/service/manifests/latest")
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK);
                resp.Headers.TryAddWithoutValidation("Docker-Content-Digest", "sha256:local-digest-789");
                return resp;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var helper = new RegistryHelper(NullLogger<RegistryHelper>.Instance, new StubHttpClientFactory(handler));

        var digest = await helper.GetManifestDigest("local.registry:5000/team/service", "latest", new AuthConfig());

        Assert.Equal("sha256:local-digest-789", digest);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Null(handler.Requests[1].Authorization);
    }

    [Fact]
    public async Task GetManifestDigest_WhenCredentialedRequestFails_FallsBackToAnonymous()
    {
        var handler = new RecordingHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.StartsWith("https://auth.docker.io/token", StringComparison.Ordinal))
            {
                if (req.Headers.Authorization is not null)
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"token":"anon-token"}""", Encoding.UTF8, "application/json")
                };
            }

            if (url == "https://registry-1.docker.io/v2/neosmemo/memos/manifests/stable")
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK);
                resp.Headers.TryAddWithoutValidation("Docker-Content-Digest", "sha256:memos-digest");
                return resp;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var helper = new RegistryHelper(NullLogger<RegistryHelper>.Instance, new StubHttpClientFactory(handler));
        var creds = new AuthConfig { Username = "expired-user", Password = "bad-password" };

        var digest = await helper.GetManifestDigest("neosmemo/memos", "stable", creds);

        Assert.Equal("sha256:memos-digest", digest);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("Basic", handler.Requests[0].Authorization?.Scheme);
        Assert.Null(handler.Requests[1].Authorization);
        Assert.Equal("Bearer", handler.Requests[2].Authorization?.Scheme);
        Assert.Equal("anon-token", handler.Requests[2].Authorization?.Parameter);
    }

    [Fact]
    public async Task FetchCredentials_WhenFileDoesNotExist_ReturnsEmptyAuthConfig()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"mobysync-missing-{Guid.NewGuid():N}.json");
        var helper = CreateCredentialsHelperWithPath(missingPath);

        var auth = await helper.FetchCredentials("nginx");

        Assert.True(string.IsNullOrEmpty(auth.Username));
        Assert.True(string.IsNullOrEmpty(auth.Password));
        Assert.True(string.IsNullOrEmpty(auth.ServerAddress));
    }

    [Fact]
    public async Task FetchCredentials_WhenFileExists_ParsesDockerHubAndCustomRegistryCredentials()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"mobysync-creds-{Guid.NewGuid():N}.json");
        var hubAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes("hubuser:hub:pass"));
        var ghcrAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes("ghuser:ghp_12345"));

        var json = $$"""
        {
          "auths": {
            "https://index.docker.io/v1/": { "auth": "{{hubAuth}}" },
            "ghcr.io": { "auth": "{{ghcrAuth}}" }
          }
        }
        """;

        await File.WriteAllTextAsync(tempPath, json);

        try
        {
            var helper = CreateCredentialsHelperWithPath(tempPath);

            var officialAuth = await helper.FetchCredentials("nginx");
            Assert.Equal("docker.io", officialAuth.ServerAddress);
            Assert.Equal("hubuser", officialAuth.Username);
            Assert.Equal("hub:pass", officialAuth.Password);

            var hubUserAuth = await helper.FetchCredentials("myorg/myimage");
            Assert.Equal("docker.io", hubUserAuth.ServerAddress);
            Assert.Equal("hubuser", hubUserAuth.Username);

            var ghcrCreds = await helper.FetchCredentials("ghcr.io/sbairamis/mobysync");
            Assert.Equal("ghcr.io", ghcrCreds.ServerAddress);
            Assert.Equal("ghuser", ghcrCreds.Username);
            Assert.Equal("ghp_12345", ghcrCreds.Password);

            var unmatched = await helper.FetchCredentials("quay.io/org/image");
            Assert.True(string.IsNullOrEmpty(unmatched.Username));
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task FetchCredentials_WhenJsonMalformed_ReturnsEmptyAuthConfigWithoutThrowing()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"mobysync-bad-creds-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(tempPath, "{ not valid json");

        try
        {
            var helper = CreateCredentialsHelperWithPath(tempPath);

            var auth = await helper.FetchCredentials("ghcr.io/sbairamis/mobysync");

            Assert.True(string.IsNullOrEmpty(auth.Username));
            Assert.True(string.IsNullOrEmpty(auth.Password));
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}
