using System.Text.Json.Serialization;

namespace PulseGen.Atlas.Client;

public sealed record AtlasClientOptions
{
    public Uri IdentityOrigin { get; init; } = new("https://identity.atlasuniversalis.com");
    public Uri LibraryOrigin { get; init; } = new("https://pulse.atlasuniversalis.com");
    public Uri RedirectUri { get; init; } = new("http://127.0.0.1:52731/signin-oidc");
    public string ClientId { get; init; } = "pulsegen-desktop";
    public IReadOnlyList<string> Scopes { get; init; } =
    [
        "openid",
        "profile",
        "offline_access",
        "pulse.library.read",
        "pulse.library.write",
        "pulse.library.publish"
    ];

    public AtlasClientOptions EnsureValid()
    {
        ValidateHttpsOrigin(IdentityOrigin, nameof(IdentityOrigin));
        ValidateHttpsOrigin(LibraryOrigin, nameof(LibraryOrigin));
        if (RedirectUri.Scheme != Uri.UriSchemeHttp || !RedirectUri.IsLoopback ||
            RedirectUri.Host is not ("127.0.0.1" or "localhost") || RedirectUri.Port is < 1024 or > 65535 ||
            string.IsNullOrWhiteSpace(ClientId) || Scopes.Count == 0 || !Scopes.Contains("openid", StringComparer.Ordinal) ||
            !Scopes.Contains("offline_access", StringComparer.Ordinal))
        {
            throw new ArgumentException("Atlas desktop authentication requires a fixed high-port HTTP loopback redirect, a client ID, and openid/offline_access scopes.");
        }
        if (Scopes.Any(scope => string.IsNullOrWhiteSpace(scope) || scope.Any(char.IsWhiteSpace)))
        {
            throw new ArgumentException("Atlas scopes must be non-empty tokens.", nameof(Scopes));
        }
        return this;
    }

    private static void ValidateHttpsOrigin(Uri value, string parameter)
    {
        if (!value.IsAbsoluteUri || value.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(value.Query) ||
            !string.IsNullOrEmpty(value.Fragment) || value.AbsolutePath is not ("" or "/"))
        {
            throw new ArgumentException("Atlas service origins must be absolute HTTPS origins without a path, query, or fragment.", parameter);
        }
    }
}

public sealed record AtlasTokenSet(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAtUtc,
    string RefreshToken,
    string Scope)
{
    public bool IsAccessTokenUsable(TimeProvider timeProvider, TimeSpan? margin = null) =>
        AccessTokenExpiresAtUtc > timeProvider.GetUtcNow().Add(margin ?? TimeSpan.FromMinutes(1));
}

public sealed record PkceAuthorizationRequest(Uri Uri, string State, string CodeVerifier);

public sealed record AuthorizationCallback(string? Code, string? State, string? Error, string? ErrorDescription);

public interface IAuthorizationCallbackReceiver
{
    Task<AuthorizationCallback> ReceiveAsync(Uri redirectUri, CancellationToken cancellationToken);
}

public interface IOidcBrowser
{
    Task OpenAsync(Uri uri, CancellationToken cancellationToken);
}

public interface IRefreshTokenStore
{
    Task<string?> ReadAsync(CancellationToken cancellationToken = default);
    Task WriteAsync(string refreshToken, CancellationToken cancellationToken = default);
    Task DeleteAsync(CancellationToken cancellationToken = default);
}

public interface IAtlasAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

internal sealed record OidcDiscoveryDocument(
    [property: JsonPropertyName("issuer")] string Issuer,
    [property: JsonPropertyName("authorization_endpoint")] Uri AuthorizationEndpoint,
    [property: JsonPropertyName("token_endpoint")] Uri TokenEndpoint,
    [property: JsonPropertyName("revocation_endpoint")] Uri? RevocationEndpoint,
    [property: JsonPropertyName("end_session_endpoint")] Uri? EndSessionEndpoint,
    [property: JsonPropertyName("code_challenge_methods_supported")] IReadOnlyList<string>? CodeChallengeMethodsSupported);

internal sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("token_type")] string? TokenType,
    [property: JsonPropertyName("expires_in")] long ExpiresIn,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("error_description")] string? ErrorDescription);

public sealed class AtlasProtocolException(string code, string message, int? statusCode = null) : Exception(message)
{
    public string Code { get; } = code;
    public int? StatusCode { get; } = statusCode;
}
