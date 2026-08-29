using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace PulseGen.Atlas.Client;

public sealed record AtlasUploadPartGrant(int PartNumber, Uri Url);
public sealed record AtlasCompletedPart(int PartNumber, string ETag);
public sealed record AtlasUploadLease(
    Guid SessionId,
    Guid? ExistingAssetId,
    DateTimeOffset ExpiresAtUtc,
    int PartSizeBytes,
    IReadOnlyList<AtlasUploadPartGrant> Parts,
    string FilePath,
    string Sha256,
    long SizeBytes,
    string FileName,
    string MediaType,
    DateTime LastWriteTimeUtc);
public sealed record AtlasAssetResult(Guid AssetId, string Sha256, long SizeBytes);
public sealed record AtlasRevisionResult(
    Guid ProjectId,
    Guid RevisionId,
    int Disposition,
    string BranchName,
    string ETag,
    Guid? CurrentMainRevisionId,
    string? CurrentMainETag)
{
    public bool IsConflictBranch => Disposition == 1;
}
public sealed record AtlasLibrarySummary(Guid OwnerId, int Assets, int Projects, int Publications);
public sealed record AtlasPublicationResult(Guid Id, string Slug, int Version, string Title, DateTimeOffset PublishedAtUtc);
public sealed record AtlasPublishRequest(
    string Slug,
    string Title,
    string Description,
    string Type,
    string License,
    string ProvenanceJson,
    Guid ContentAssetId,
    Guid PreviewAssetId,
    Guid RenderReceiptAssetId,
    bool OwnerConfirmed);

public sealed class AtlasLibraryClient
{
    private const int MaximumPartSizeBytes = 128 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient http;
    private readonly IAtlasAccessTokenProvider tokens;
    private readonly Uri origin;

    public AtlasLibraryClient(HttpClient http, IAtlasAccessTokenProvider tokens, AtlasClientOptions options)
    {
        this.http = http ?? throw new ArgumentNullException(nameof(http));
        this.tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        origin = options.EnsureValid().LibraryOrigin;
    }

    public async Task<AtlasUploadLease> BeginUploadAsync(
        string filePath,
        string mediaType = "application/octet-stream",
        CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(filePath);
        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= 0)
        {
            throw new FileNotFoundException("Atlas uploads require a non-empty local file.", path);
        }
        var sha256 = await ComputeSha256Async(path, cancellationToken);
        file.Refresh();
        if (!file.Exists || file.Length <= 0)
        {
            throw new InvalidDataException("The local asset changed while its content identity was being calculated.");
        }
        var payload = new { sha256, sizeBytes = file.Length, fileName = file.Name, mediaType };
        using var request = await CreateAuthorizedRequestAsync(HttpMethod.Post, "/api/v1/assets/uploads", cancellationToken);
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var result = await ReadRequiredAsync<BeginUploadResponse>(response, cancellationToken);
        if (result.ExistingAssetId is null && result.SessionId == Guid.Empty)
        {
            throw new AtlasProtocolException("invalid_library_response", "Atlas returned an upload without an asset or resumable session.");
        }
        ValidatePartGrants(result.Parts, result.PartSizeBytes, file.Length);
        return new(
            result.SessionId,
            result.ExistingAssetId,
            result.ExpiresAtUtc,
            result.PartSizeBytes,
            result.Parts,
            path,
            sha256,
            file.Length,
            file.Name,
            mediaType,
            file.LastWriteTimeUtc);
    }

    public async Task<AtlasUploadLease> ResumeUploadAsync(AtlasUploadLease lease, CancellationToken cancellationToken = default)
    {
        EnsureLocalFileUnchanged(lease);
        using var request = await CreateAuthorizedRequestAsync(
            HttpMethod.Post,
            $"/api/v1/assets/uploads/{lease.SessionId:D}/resume",
            cancellationToken);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var result = await ReadRequiredAsync<BeginUploadResponse>(response, cancellationToken);
        ValidatePartGrants(result.Parts, result.PartSizeBytes, lease.SizeBytes);
        return lease with
        {
            ExpiresAtUtc = result.ExpiresAtUtc,
            PartSizeBytes = result.PartSizeBytes,
            Parts = result.Parts
        };
    }

    public async Task<IReadOnlyList<AtlasCompletedPart>> UploadPartsAsync(
        AtlasUploadLease lease,
        IProgress<(int Completed, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (lease.ExistingAssetId is not null || lease.Parts.Count == 0)
        {
            return [];
        }
        EnsureLocalFileUnchanged(lease);
        var completed = new List<AtlasCompletedPart>(lease.Parts.Count);
        await using var input = new FileStream(
            lease.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        foreach (var grant in lease.Parts.OrderBy(part => part.PartNumber))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (grant.Url.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(grant.Url.UserInfo) || !string.IsNullOrEmpty(grant.Url.Fragment))
            {
                throw new AtlasProtocolException("invalid_upload_grant", "Atlas returned an unsafe multipart upload grant.");
            }
            var offset = checked((long)(grant.PartNumber - 1) * lease.PartSizeBytes);
            var length = checked((int)Math.Min(lease.PartSizeBytes, lease.SizeBytes - offset));
            var buffer = GC.AllocateUninitializedArray<byte>(length);
            try
            {
                input.Position = offset;
                await input.ReadExactlyAsync(buffer, cancellationToken);
                using var request = new HttpRequestMessage(HttpMethod.Put, grant.Url)
                {
                    Content = new ByteArrayContent(buffer)
                };
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    throw new AtlasProtocolException("part_upload_failed", $"Atlas multipart part {grant.PartNumber} was rejected.", (int)response.StatusCode);
                }
                var etag = response.Headers.ETag?.Tag;
                if (string.IsNullOrWhiteSpace(etag) && response.Headers.TryGetValues("ETag", out var etagValues))
                {
                    etag = etagValues.FirstOrDefault();
                }
                if (string.IsNullOrWhiteSpace(etag) || etag.Length > 256)
                {
                    throw new AtlasProtocolException("missing_part_etag", $"Atlas multipart part {grant.PartNumber} did not return a bounded ETag.");
                }
                completed.Add(new(grant.PartNumber, etag));
                progress?.Report((completed.Count, lease.Parts.Count));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }

        EnsureLocalFileUnchanged(lease);
        var finalHash = await ComputeSha256Async(lease.FilePath, cancellationToken);
        if (!FixedTimeHashEquals(lease.Sha256, finalHash))
        {
            throw new InvalidDataException("The local asset changed during upload; the remote multipart session was not committed.");
        }
        return completed;
    }

    public async Task<AtlasAssetResult> CompleteUploadAsync(
        AtlasUploadLease lease,
        IReadOnlyList<AtlasCompletedPart> completedParts,
        CancellationToken cancellationToken = default)
    {
        if (lease.ExistingAssetId is not null)
        {
            return new(lease.ExistingAssetId.Value, lease.Sha256, lease.SizeBytes);
        }
        if (lease.SessionId == Guid.Empty || completedParts.Count != lease.Parts.Count)
        {
            throw new ArgumentException("Multipart completion must exactly match the active Atlas upload lease.", nameof(completedParts));
        }
        EnsureLocalFileUnchanged(lease);
        using var request = await CreateAuthorizedRequestAsync(
            HttpMethod.Post,
            $"/api/v1/assets/uploads/{lease.SessionId:D}/complete",
            cancellationToken);
        request.Content = JsonContent.Create(new { parts = completedParts }, options: JsonOptions);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var result = await ReadRequiredAsync<AtlasAssetResult>(response, cancellationToken);
        if (!FixedTimeHashEquals(result.Sha256, lease.Sha256) || result.SizeBytes != lease.SizeBytes)
        {
            throw new AtlasProtocolException("invalid_library_response", "Atlas committed an asset identity that differs from the local content.");
        }
        return result;
    }

    public async Task<AtlasAssetResult> UploadAssetAsync(
        string filePath,
        string mediaType = "application/octet-stream",
        IProgress<(int Completed, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var lease = await BeginUploadAsync(filePath, mediaType, cancellationToken);
        var completed = await UploadPartsAsync(lease, progress, cancellationToken);
        return await CompleteUploadAsync(lease, completed, cancellationToken);
    }

    public async Task<AtlasRevisionResult> CreateProjectRevisionAsync(
        Guid projectId,
        string projectName,
        Guid manifestAssetId,
        Guid? baseRevisionId,
        string? baseETag,
        string message,
        CancellationToken cancellationToken = default)
    {
        using var request = await CreateAuthorizedRequestAsync(
            HttpMethod.Post,
            $"/api/v1/projects/{projectId:D}/revisions",
            cancellationToken);
        request.Headers.TryAddWithoutValidation("If-Match", baseETag ?? "*");
        request.Content = JsonContent.Create(new { projectName, manifestAssetId, baseRevisionId, message }, options: JsonOptions);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Conflict))
        {
            await ThrowLibraryErrorAsync(response, cancellationToken);
        }
        return await ReadRequiredAsync<AtlasRevisionResult>(response, cancellationToken, allowConflict: true);
    }

    public async Task<AtlasLibrarySummary> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        using var request = await CreateAuthorizedRequestAsync(HttpMethod.Get, "/api/v1/me/library", cancellationToken);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return await ReadRequiredAsync<AtlasLibrarySummary>(response, cancellationToken);
    }

    public async Task<AtlasPublicationResult> PublishAsync(AtlasPublishRequest publication, CancellationToken cancellationToken = default)
    {
        if (!publication.OwnerConfirmed)
        {
            throw new ArgumentException("Publishing requires an explicit owner-confirmed request.", nameof(publication));
        }
        using var request = await CreateAuthorizedRequestAsync(HttpMethod.Post, "/api/v1/publications", cancellationToken);
        request.Content = JsonContent.Create(publication, options: JsonOptions);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return await ReadRequiredAsync<AtlasPublicationResult>(response, cancellationToken);
    }

    private async Task<HttpRequestMessage> CreateAuthorizedRequestAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        var accessToken = await tokens.GetAccessTokenAsync(cancellationToken);
        var request = new HttpRequestMessage(method, new Uri(origin, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static async Task<T> ReadRequiredAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken,
        bool allowConflict = false)
    {
        if (!response.IsSuccessStatusCode && !(allowConflict && response.StatusCode == HttpStatusCode.Conflict))
        {
            await ThrowLibraryErrorAsync(response, cancellationToken);
        }
        var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
        return result ?? throw new AtlasProtocolException("invalid_library_response", "Atlas returned an empty library response.", (int)response.StatusCode);
    }

    private static async Task ThrowLibraryErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var detail = "Atlas rejected the library request.";
        if (body.Length <= 1024 * 1024)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("detail", out var property) && property.ValueKind == JsonValueKind.String)
                {
                    detail = property.GetString() ?? detail;
                }
                else if (document.RootElement.TryGetProperty("error", out property) && property.ValueKind == JsonValueKind.String)
                {
                    detail = property.GetString() ?? detail;
                }
            }
            catch (JsonException)
            {
            }
        }
        throw new AtlasProtocolException("library_request_failed", detail, (int)response.StatusCode);
    }

    private static void ValidatePartGrants(IReadOnlyList<AtlasUploadPartGrant> parts, int partSize, long totalSize)
    {
        if (parts.Count == 0)
        {
            if (partSize != 0)
            {
                throw new AtlasProtocolException("invalid_upload_grant", "Atlas returned an empty upload with a nonzero part size.");
            }
            return;
        }
        if (partSize is < 5 * 1024 * 1024 or > MaximumPartSizeBytes || totalSize <= 0)
        {
            throw new AtlasProtocolException("invalid_upload_grant", "Atlas returned an invalid multipart part size.");
        }
        var expectedCount = checked((int)((totalSize + partSize - 1L) / partSize));
        if (parts.Count != expectedCount || parts.Count > 10_000 ||
            parts.Select(part => part.PartNumber).Order().Where((part, index) => part != index + 1).Any())
        {
            throw new AtlasProtocolException("invalid_upload_grant", "Atlas returned an inconsistent multipart grant.");
        }
    }

    private static void EnsureLocalFileUnchanged(AtlasUploadLease lease)
    {
        var file = new FileInfo(lease.FilePath);
        if (!file.Exists || file.Length != lease.SizeBytes || file.LastWriteTimeUtc != lease.LastWriteTimeUtc)
        {
            throw new InvalidDataException("The local asset changed after the Atlas upload lease was created.");
        }
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static bool FixedTimeHashEquals(string left, string right)
    {
        if (left.Length != 64 || right.Length != 64)
        {
            return false;
        }
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed record BeginUploadResponse(
        Guid SessionId,
        Guid? ExistingAssetId,
        DateTimeOffset ExpiresAtUtc,
        int PartSizeBytes,
        IReadOnlyList<AtlasUploadPartGrant> Parts);
}
