using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PulseGen.Atlas.Client;

public sealed class AtlasOidcClient : IAtlasAccessTokenProvider, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AtlasClientOptions options;
    private readonly HttpClient http;
    private readonly IRefreshTokenStore refreshTokens;
    private readonly IOidcBrowser browser;
    private readonly IAuthorizationCallbackReceiver callbackReceiver;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim sessionGate = new(1, 1);
    private OidcDiscoveryDocument? discovery;
    private AtlasTokenSet? tokens;

    public AtlasOidcClient(
        AtlasClientOptions options,
        HttpClient http,
        IRefreshTokenStore refreshTokens,
        IOidcBrowser? browser = null,
        IAuthorizationCallbackReceiver? callbackReceiver = null,
        TimeProvider? timeProvider = null)
    {
        this.options = options.EnsureValid();
        this.http = http ?? throw new ArgumentNullException(nameof(http));
        this.refreshTokens = refreshTokens ?? throw new ArgumentNullException(nameof(refreshTokens));
        this.browser = browser ?? new SystemOidcBrowser();
        this.callbackReceiver = callbackReceiver ?? new LoopbackAuthorizationReceiver();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool HasMemorySession => tokens is not null;

    public async Task<AtlasTokenSet> SignInAsync(CancellationToken cancellationToken = default)
    {
        await sessionGate.WaitAsync(cancellationToken);
        try
        {
            var document = await GetDiscoveryAsync(cancellationToken);
            var request = CreateAuthorizationRequest(document.AuthorizationEndpoint, options);
            using var callbackCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var callbackTask = callbackReceiver.ReceiveAsync(options.RedirectUri, callbackCancellation.Token);
            await Task.Yield();
            if (callbackTask.IsFaulted)
            {
                await callbackTask;
            }
            try
            {
                await browser.OpenAsync(request.Uri, cancellationToken);
            }
            catch
            {
                callbackCancellation.Cancel();
                await ObserveCancellationAsync(callbackTask);
                throw;
            }

            AuthorizationCallback callback;
            try
            {
                callback = await callbackTask;
            }
            finally
            {
                callbackCancellation.Cancel();
            }
            if (!FixedTimeEquals(request.State, callback.State))
            {
                throw new AtlasProtocolException("invalid_state", "Atlas returned an authorization response with an invalid state value.");
            }
            if (!string.IsNullOrWhiteSpace(callback.Error))
            {
                throw new AtlasProtocolException(callback.Error, callback.ErrorDescription ?? "Atlas authorization was not completed.");
            }
            if (string.IsNullOrWhiteSpace(callback.Code))
            {
                throw new AtlasProtocolException("missing_code", "Atlas did not return an authorization code.");
            }

            var response = await RequestTokenAsync(document.TokenEndpoint,
            [
                new("grant_type", "authorization_code"),
                new("client_id", options.ClientId),
                new("code", callback.Code),
                new("redirect_uri", options.RedirectUri.AbsoluteUri),
                new("code_verifier", request.CodeVerifier)
            ], cancellationToken);
            var signedIn = ValidateTokenResponse(response, requireRefreshToken: true, previousRefreshToken: null);
            await refreshTokens.WriteAsync(signedIn.RefreshToken, cancellationToken);
            tokens = signedIn;
            return signedIn;
        }
        finally
        {
            sessionGate.Release();
        }
    }

    public async Task<bool> TryRestoreAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await GetAccessTokenAsync(cancellationToken);
            return true;
        }
        catch (AtlasProtocolException exception) when (exception.Code is "not_signed_in" or "invalid_grant")
        {
            return false;
        }
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (tokens is not null && tokens.IsAccessTokenUsable(timeProvider))
        {
            return tokens.AccessToken;
        }

        await sessionGate.WaitAsync(cancellationToken);
        try
        {
            if (tokens is not null && tokens.IsAccessTokenUsable(timeProvider))
            {
                return tokens.AccessToken;
            }
            var refreshToken = tokens?.RefreshToken ?? await refreshTokens.ReadAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                throw new AtlasProtocolException("not_signed_in", "PulseGen is not signed in to Atlas.");
            }

            var document = await GetDiscoveryAsync(cancellationToken);
            TokenResponse response;
            try
            {
                response = await RequestTokenAsync(document.TokenEndpoint,
                [
                    new("grant_type", "refresh_token"),
                    new("client_id", options.ClientId),
                    new("refresh_token", refreshToken),
                    new("scope", string.Join(' ', options.Scopes))
                ], cancellationToken);
            }
            catch (AtlasProtocolException exception) when (exception.Code == "invalid_grant")
            {
                tokens = null;
                await refreshTokens.DeleteAsync(CancellationToken.None);
                throw;
            }

            var refreshed = ValidateTokenResponse(response, requireRefreshToken: false, refreshToken);
            if (!string.Equals(refreshed.RefreshToken, refreshToken, StringComparison.Ordinal))
            {
                await refreshTokens.WriteAsync(refreshed.RefreshToken, cancellationToken);
            }
            tokens = refreshed;
            return refreshed.AccessToken;
        }
        finally
        {
            sessionGate.Release();
        }
    }

    public async Task RevokeAndForgetAsync(CancellationToken cancellationToken = default)
    {
        await sessionGate.WaitAsync(cancellationToken);
        try
        {
            var refreshToken = tokens?.RefreshToken ?? await refreshTokens.ReadAsync(cancellationToken);
            try
            {
                if (!string.IsNullOrWhiteSpace(refreshToken))
                {
                    var document = await GetDiscoveryAsync(cancellationToken);
                    if (document.RevocationEndpoint is null)
                    {
                        throw new AtlasProtocolException("revocation_unavailable", "Atlas Identity did not advertise token revocation.");
                    }
                    using var content = new FormUrlEncodedContent(
                    [
                        new("token", refreshToken),
                        new("token_type_hint", "refresh_token"),
                        new("client_id", options.ClientId)
                    ]);
                    using var response = await http.PostAsync(document.RevocationEndpoint, content, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new AtlasProtocolException("revocation_failed", "Atlas did not accept the refresh-token revocation.", (int)response.StatusCode);
                    }
                }
            }
            finally
            {
                tokens = null;
                await refreshTokens.DeleteAsync(CancellationToken.None);
            }
        }
        finally
        {
            sessionGate.Release();
        }
    }

    public static PkceAuthorizationRequest CreateAuthorizationRequest(Uri authorizationEndpoint, AtlasClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(authorizationEndpoint);
        options.EnsureValid();
        var state = RandomToken(32);
        var verifier = RandomToken(64);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = options.ClientId,
            ["redirect_uri"] = options.RedirectUri.AbsoluteUri,
            ["scope"] = string.Join(' ', options.Scopes),
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        };
        var builder = new UriBuilder(authorizationEndpoint)
        {
            Query = string.Join('&', query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"))
        };
        return new(builder.Uri, state, verifier);
    }

    private async Task<OidcDiscoveryDocument> GetDiscoveryAsync(CancellationToken cancellationToken)
    {
        if (discovery is not null)
        {
            return discovery;
        }
        var uri = new Uri(options.IdentityOrigin, "/.well-known/openid-configuration");
        using var response = await http.GetAsync(uri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new AtlasProtocolException("discovery_failed", "Atlas Identity discovery is unavailable.", (int)response.StatusCode);
        }
        var document = await response.Content.ReadFromJsonAsync<OidcDiscoveryDocument>(JsonOptions, cancellationToken)
            ?? throw new AtlasProtocolException("invalid_discovery", "Atlas Identity returned an empty discovery document.");
        ValidateDiscovery(document);
        discovery = document;
        return document;
    }

    private void ValidateDiscovery(OidcDiscoveryDocument document)
    {
        if (!Uri.TryCreate(document.Issuer, UriKind.Absolute, out var issuer) || !SameOrigin(issuer, options.IdentityOrigin) ||
            !SameOrigin(document.AuthorizationEndpoint, options.IdentityOrigin) || !SameOrigin(document.TokenEndpoint, options.IdentityOrigin) ||
            document.RevocationEndpoint is not null && !SameOrigin(document.RevocationEndpoint, options.IdentityOrigin) ||
            document.EndSessionEndpoint is not null && !SameOrigin(document.EndSessionEndpoint, options.IdentityOrigin) ||
            document.CodeChallengeMethodsSupported?.Contains("S256", StringComparer.Ordinal) != true)
        {
            throw new AtlasProtocolException("invalid_discovery", "Atlas Identity discovery did not match the configured HTTPS origin or mandatory S256 PKCE support.");
        }
    }

    private async Task<TokenResponse> RequestTokenAsync(
        Uri tokenEndpoint,
        IEnumerable<KeyValuePair<string, string>> fields,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(fields);
        using var response = await http.PostAsync(tokenEndpoint, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (body.Length > 1024 * 1024)
        {
            throw new AtlasProtocolException("invalid_token_response", "Atlas returned an oversized token response.", (int)response.StatusCode);
        }
        TokenResponse? tokenResponse;
        try
        {
            tokenResponse = JsonSerializer.Deserialize<TokenResponse>(body, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new AtlasProtocolException("invalid_token_response", $"Atlas returned malformed token JSON: {exception.Message}", (int)response.StatusCode);
        }
        if (tokenResponse is null)
        {
            throw new AtlasProtocolException("invalid_token_response", "Atlas returned an empty token response.", (int)response.StatusCode);
        }
        if (!response.IsSuccessStatusCode || !string.IsNullOrWhiteSpace(tokenResponse.Error))
        {
            throw new AtlasProtocolException(
                tokenResponse.Error ?? "token_request_failed",
                tokenResponse.ErrorDescription ?? "Atlas rejected the token request.",
                (int)response.StatusCode);
        }
        return tokenResponse;
    }

    private AtlasTokenSet ValidateTokenResponse(TokenResponse response, bool requireRefreshToken, string? previousRefreshToken)
    {
        var refreshToken = response.RefreshToken ?? previousRefreshToken;
        if (string.IsNullOrWhiteSpace(response.AccessToken) || !string.Equals(response.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            response.ExpiresIn is < 30 or > 86_400 || requireRefreshToken && string.IsNullOrWhiteSpace(refreshToken) ||
            string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new AtlasProtocolException("invalid_token_response", "Atlas returned an incomplete or out-of-bounds token response.");
        }
        return new(
            response.AccessToken,
            timeProvider.GetUtcNow().AddSeconds(response.ExpiresIn),
            refreshToken,
            response.Scope ?? string.Join(' ', options.Scopes));
    }

    private static bool SameOrigin(Uri candidate, Uri expected) =>
        candidate.IsAbsoluteUri && candidate.Scheme == Uri.UriSchemeHttps &&
        string.Equals(candidate.IdnHost, expected.IdnHost, StringComparison.OrdinalIgnoreCase) && candidate.Port == expected.Port;

    private static bool FixedTimeEquals(string expected, string? actual)
    {
        if (actual is null)
        {
            return false;
        }
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        try
        {
            return expectedBytes.Length == actualBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedBytes);
            CryptographicOperations.ZeroMemory(actualBytes);
        }
    }

    private static string RandomToken(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task ObserveCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose() => sessionGate.Dispose();
}
