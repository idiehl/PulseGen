using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PulseGen.Atlas.Client;
using Xunit;

namespace PulseGen.Atlas.Client.Tests;

public sealed class AtlasClientTests
{
    private static readonly AtlasClientOptions Options = new()
    {
        IdentityOrigin = new("https://identity.test"),
        LibraryOrigin = new("https://pulse.test"),
        RedirectUri = new("http://127.0.0.1:52731/signin-oidc")
    };

    [Fact]
    public void AuthorizationRequestUsesS256PkceAndRequiredScopes()
    {
        var request = AtlasOidcClient.CreateAuthorizationRequest(new Uri("https://identity.test/connect/authorize"), Options);
        var query = ParseQuery(request.Uri.Query);
        var expectedChallenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(request.CodeVerifier)));

        Assert.Equal("code", query["response_type"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(expectedChallenge, query["code_challenge"]);
        Assert.Equal(request.State, query["state"]);
        Assert.Contains("offline_access", query["scope"].Split(' '));
        Assert.Contains("pulse.library.publish", query["scope"].Split(' '));
        Assert.InRange(request.CodeVerifier.Length, 43, 128);
    }

    [Fact]
    public async Task InteractiveSignInValidatesStateAndStoresRotatingCredential()
    {
        var callback = new FakeCallbackReceiver();
        var browser = new FakeBrowser(uri =>
        {
            var query = ParseQuery(uri.Query);
            callback.Complete(new("authorization-code", query["state"], null, null));
        });
        var credential = new FakeRefreshTokenStore();
        var handler = new DelegatingTestHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return Json(Discovery());
            }
            var form = ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("authorization_code", form["grant_type"]);
            Assert.InRange(form["code_verifier"].Length, 43, 128);
            return Json(new
            {
                access_token = "access-one",
                token_type = "Bearer",
                expires_in = 900,
                refresh_token = "refresh-one",
                id_token = "identity-one",
                scope = "openid offline_access pulse.library.read"
            });
        });
        using var http = new HttpClient(handler);
        using var client = new AtlasOidcClient(Options, http, credential, browser, callback);

        var result = await client.SignInAsync();

        Assert.Equal("access-one", result.AccessToken);
        Assert.Equal("refresh-one", credential.Value);
        Assert.Equal(1, credential.WriteCount);
        Assert.True(client.HasMemorySession);
    }

    [Fact]
    public async Task InteractiveSignInRejectsMismatchedStateBeforeTokenExchange()
    {
        var callback = new FakeCallbackReceiver();
        var browser = new FakeBrowser(_ => callback.Complete(new("authorization-code", "wrong-state", null, null)));
        var credential = new FakeRefreshTokenStore();
        var tokenCalls = 0;
        var handler = new DelegatingTestHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(Json(Discovery()));
            }
            tokenCalls++;
            return Task.FromResult(Json(new { access_token = "unexpected" }));
        });
        using var http = new HttpClient(handler);
        using var client = new AtlasOidcClient(Options, http, credential, browser, callback);

        var error = await Assert.ThrowsAsync<AtlasProtocolException>(() => client.SignInAsync());

        Assert.Equal("invalid_state", error.Code);
        Assert.Equal(0, tokenCalls);
        Assert.Null(credential.Value);
    }

    [Fact]
    public async Task RefreshRotationCommitsOnlyAfterSuccessfulResponseAndInvalidGrantDeletesIt()
    {
        var credential = new FakeRefreshTokenStore { Value = "refresh-old" };
        var tokenCalls = 0;
        var handler = new DelegatingTestHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return Json(Discovery());
            }
            tokenCalls++;
            var form = ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("refresh-old", form["refresh_token"]);
            return tokenCalls == 1
                ? Json(new { access_token = "access-rotated", token_type = "Bearer", expires_in = 900, refresh_token = "refresh-new" })
                : Json(new { error = "invalid_grant", error_description = "replayed" }, HttpStatusCode.BadRequest);
        });
        using var http = new HttpClient(handler);
        using var client = new AtlasOidcClient(Options, http, credential);

        Assert.Equal("access-rotated", await client.GetAccessTokenAsync());
        Assert.Equal("refresh-new", credential.Value);
        Assert.Equal(1, credential.WriteCount);
        Assert.Equal(1, tokenCalls);

        var secondCredential = new FakeRefreshTokenStore { Value = "refresh-old" };
        using var secondClient = new AtlasOidcClient(Options, http, secondCredential);
        var error = await Assert.ThrowsAsync<AtlasProtocolException>(() => secondClient.GetAccessTokenAsync());
        Assert.Equal("invalid_grant", error.Code);
        Assert.Null(secondCredential.Value);
        Assert.Equal(1, secondCredential.DeleteCount);
    }

    [Fact]
    public async Task MultipartUploadAndConflictRevisionPreserveContentIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pulsegen-atlas-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "project.json");
        await File.WriteAllTextAsync(file, "{\"schemaVersion\":1,\"name\":\"test\"}");
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(file)));
        var assetId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var sawAnonymousPartPut = false;
        var handler = new DelegatingTestHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.Host == "spaces.test")
            {
                Assert.Null(request.Headers.Authorization);
                Assert.Equal(await File.ReadAllBytesAsync(file, cancellationToken), await request.Content!.ReadAsByteArrayAsync(cancellationToken));
                sawAnonymousPartPut = true;
                var partResponse = new HttpResponseMessage(HttpStatusCode.OK);
                partResponse.Headers.ETag = new EntityTagHeaderValue("\"part-one\"");
                return partResponse;
            }
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-access", request.Headers.Authorization?.Parameter);
            if (request.RequestUri.AbsolutePath == "/api/v1/assets/uploads")
            {
                var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert.Equal(expectedHash, body.RootElement.GetProperty("sha256").GetString());
                return Json(new
                {
                    sessionId,
                    existingAssetId = (Guid?)null,
                    expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(15),
                    partSizeBytes = 5 * 1024 * 1024,
                    parts = new[] { new { partNumber = 1, url = "https://spaces.test/upload/one" } }
                });
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/complete", StringComparison.Ordinal))
            {
                return Json(new { assetId, sha256 = expectedHash, sizeBytes = new FileInfo(file).Length });
            }
            Assert.Equal($"/api/v1/projects/{projectId:D}/revisions", request.RequestUri.AbsolutePath);
            Assert.Contains("\"base-etag\"", request.Headers.GetValues("If-Match"));
            return Json(new
            {
                projectId,
                revisionId,
                disposition = 1,
                branchName = "conflict/test",
                eTag = "\"conflict-etag\"",
                currentMainRevisionId = (Guid?)null,
                currentMainETag = "\"base-etag\""
            }, HttpStatusCode.Conflict);
        });
        using var http = new HttpClient(handler);
        var client = new AtlasLibraryClient(http, new FixedTokenProvider(), Options);
        try
        {
            var asset = await client.UploadAssetAsync(file, "application/json");
            var revision = await client.CreateProjectRevisionAsync(
                projectId,
                "Test",
                asset.AssetId,
                Guid.NewGuid(),
                "\"base-etag\"",
                "sync test");

            Assert.True(sawAnonymousPartPut);
            Assert.Equal(expectedHash, asset.Sha256);
            Assert.True(revision.IsConflictBranch);
            Assert.Equal("conflict/test", revision.BranchName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MultipartUploadRejectsAnUnboundedPartGrantBeforeAllocation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pulsegen-atlas-bounds-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "project.json");
        await File.WriteAllTextAsync(file, "{}");
        var handler = new DelegatingTestHandler((_, _) => Task.FromResult(Json(new
        {
            sessionId = Guid.NewGuid(),
            existingAssetId = (Guid?)null,
            expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(15),
            partSizeBytes = 256 * 1024 * 1024,
            parts = new[] { new { partNumber = 1, url = "https://spaces.test/upload/one" } }
        })));
        using var http = new HttpClient(handler);
        var client = new AtlasLibraryClient(http, new FixedTokenProvider(), Options);
        try
        {
            var error = await Assert.ThrowsAsync<AtlasProtocolException>(() => client.BeginUploadAsync(file));
            Assert.Equal("invalid_upload_grant", error.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static object Discovery() => new
    {
        issuer = "https://identity.test/",
        authorization_endpoint = "https://identity.test/connect/authorize",
        token_endpoint = "https://identity.test/connect/token",
        revocation_endpoint = "https://identity.test/connect/revoke",
        end_session_endpoint = "https://identity.test/connect/logout",
        code_challenge_methods_supported = new[] { "S256" }
    };

    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };

    private static Dictionary<string, string> ParseQuery(string value) => value.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(pair => pair.Split('=', 2))
        .ToDictionary(
            pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')),
            pair => Uri.UnescapeDataString((pair.Length == 2 ? pair[1] : string.Empty).Replace('+', ' ')),
            StringComparer.Ordinal);

    private static string Base64Url(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class DelegatingTestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class FakeRefreshTokenStore : IRefreshTokenStore
    {
        public string? Value { get; set; }
        public int WriteCount { get; private set; }
        public int DeleteCount { get; private set; }
        public Task<string?> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Value);
        public Task WriteAsync(string refreshToken, CancellationToken cancellationToken = default)
        {
            Value = refreshToken;
            WriteCount++;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(CancellationToken cancellationToken = default)
        {
            Value = null;
            DeleteCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCallbackReceiver : IAuthorizationCallbackReceiver
    {
        private readonly TaskCompletionSource<AuthorizationCallback> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AuthorizationCallback> ReceiveAsync(Uri redirectUri, CancellationToken cancellationToken) => completion.Task.WaitAsync(cancellationToken);
        public void Complete(AuthorizationCallback callback) => completion.SetResult(callback);
    }

    private sealed class FakeBrowser(Action<Uri> opened) : IOidcBrowser
    {
        public Task OpenAsync(Uri uri, CancellationToken cancellationToken)
        {
            opened(uri);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTokenProvider : IAtlasAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("test-access");
    }
}
