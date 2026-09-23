using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace WheelWizard.Features.CloudSync;

public sealed record CloudSaveMetadata(string Rev, DateTimeOffset ServerModified);
public sealed record DropboxSyncLock(string DeviceId, DateTimeOffset StartedAt)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTimeOffset ServerModified { get; init; }
}
public sealed record DropboxSyncState(string DeviceId, DateTimeOffset SyncedAt, string ContentHash);

public interface IDropboxSyncApi
{
    Task<CloudSaveMetadata?> GetSaveMetadataAsync(CancellationToken ct);
    Task<CloudSaveMetadata> DownloadAsync(string rev, string destination, CancellationToken ct);
    Task<CloudSaveMetadata> UploadAsync(string source, string? expectedRev, CancellationToken ct);
    Task<DropboxSyncLock?> ReadLockAsync(CancellationToken ct);
    Task WriteLockAsync(DropboxSyncLock cloudLock, CancellationToken ct);
    Task DeleteLockAsync(CancellationToken ct);
    Task<DropboxSyncState?> ReadStateAsync(CancellationToken ct);
    Task WriteStateAsync(DropboxSyncState state, CancellationToken ct);
}

public class DropboxAccessException(string message, HttpStatusCode statusCode, Exception? innerException = null) : Exception(message, innerException)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

public sealed class DropboxConflictException(string message) : DropboxAccessException(message, HttpStatusCode.Conflict);

public sealed class DropboxSyncApi : IDropboxSyncApi
{
    private const string ApiRoot = "https://api.dropboxapi.com/2/files/";
    private const string ContentRoot = "https://content.dropboxapi.com/2/files/";
    private const int ChunkSize = 8 * 1024 * 1024;
    private readonly IDropboxOAuthService _oauth;
    private readonly HttpClient _http;
    private readonly long _uploadSessionThreshold;
    private readonly int _chunkSize;

    public DropboxSyncApi(IDropboxOAuthService oauth, HttpClient httpClient, long uploadSessionThreshold = 140L * 1024 * 1024, int chunkSize = ChunkSize)
    {
        _oauth = oauth ?? throw new ArgumentNullException(nameof(oauth));
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (uploadSessionThreshold < 0)
            throw new ArgumentOutOfRangeException(nameof(uploadSessionThreshold));
        if (chunkSize < 1)
            throw new ArgumentOutOfRangeException(nameof(chunkSize));
        _uploadSessionThreshold = uploadSessionThreshold;
        _chunkSize = chunkSize;
    }

    public async Task<CloudSaveMetadata?> GetSaveMetadataAsync(CancellationToken ct)
    {
        using var response = await SendAsync(() => JsonRequest(HttpMethod.Post, ApiRoot + "get_metadata", new { path = "/save.zip" }), ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict && IsPathError(response, "not_found"))
            return null;
        EnsureSuccess(response, "Could not read Dropbox save metadata.");
        return await ReadMetadataAsync(response.Content, ct).ConfigureAwait(false);
    }

    public async Task<CloudSaveMetadata> DownloadAsync(string rev, string destination, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rev);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var arg = JsonSerializer.Serialize(new { path = "/save.zip", rev });
        using var response = await SendAsync(() => ContentRequest("download", arg), ct).ConfigureAwait(false);
        EnsureSuccess(response, "Could not download Dropbox save.");
        var result = ReadApiResult(response) ?? throw new InvalidDataException("Dropbox download omitted revision metadata.");
        var metadata = ParseMetadata(result);
        if (!string.Equals(metadata.Rev, rev, StringComparison.Ordinal))
            throw new DropboxAccessException("Dropbox returned a different save revision than requested.", HttpStatusCode.OK);
        var fullPath = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await using var output = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await response.Content.CopyToAsync(output, ct).ConfigureAwait(false);
        return metadata;
    }

    public async Task<CloudSaveMetadata> UploadAsync(string source, string? expectedRev, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var temp = Path.Combine(Path.GetTempPath(), $"wheelwizard-dropbox-{Guid.NewGuid():N}.tmp");
        try
        {
            long length;
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var bounded = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                length = await CopyBoundedAsync(input, bounded, ct).ConfigureAwait(false);
            }
            var mode = MakeMode(expectedRev);
            var commit = MakeCommit(mode);
            if (length <= _uploadSessionThreshold)
                return await UploadSingleAsync(temp, expectedRev, ct).ConfigureAwait(false);
            return await UploadSessionAsync(temp, length, commit, expectedRev, ct).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public Task<DropboxSyncLock?> ReadLockAsync(CancellationToken ct) => ReadLockAsyncCore(ct);
    public Task<DropboxSyncState?> ReadStateAsync(CancellationToken ct) => ReadStateAsyncCore(ct);

    public async Task WriteLockAsync(DropboxSyncLock cloudLock, CancellationToken ct)
    {
        try { await UploadJsonAsync("/lock.json", cloudLock, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { }
    }

    public async Task DeleteLockAsync(CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync(() => JsonRequest(HttpMethod.Post, ApiRoot + "delete_v2", new { path = "/lock.json" }), ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Conflict && IsPathError(response, "not_found")) return;
            EnsureSuccess(response, "Could not delete Dropbox sync lock.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { }
    }

    public async Task WriteStateAsync(DropboxSyncState state, CancellationToken ct)
    {
        try { await UploadJsonAsync("/state.json", state, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { }
    }

    private async Task<DropboxSyncLock?> ReadLockAsyncCore(CancellationToken ct)
    {
        var (bytes, metadata) = await ReadAdvisoryBytesAsync("/lock.json", ct).ConfigureAwait(false);
        if (bytes is null || metadata is null) return null;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var deviceId = GetProperty(root, "deviceId").GetString() ?? throw new JsonException();
            var startedAt = DateTimeOffset.FromUnixTimeMilliseconds(GetProperty(root, "startedAt").GetInt64());
            return new DropboxSyncLock(deviceId, startedAt) { ServerModified = metadata.ServerModified };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private async Task<DropboxSyncState?> ReadStateAsyncCore(CancellationToken ct)
    {
        var (bytes, _) = await ReadAdvisoryBytesAsync("/state.json", ct).ConfigureAwait(false);
        if (bytes is null) return null;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            return new DropboxSyncState(GetProperty(root, "deviceId").GetString() ?? throw new JsonException(),
                DateTimeOffset.FromUnixTimeMilliseconds(GetProperty(root, "syncedAt").GetInt64()),
                GetProperty(root, "contentHash").GetString() ?? throw new JsonException());
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private async Task<(byte[]? Bytes, CloudSaveMetadata? Metadata)> ReadAdvisoryBytesAsync(string path, CancellationToken ct)
    {
        using var response = await SendAsync(() => JsonRequest(HttpMethod.Post, ApiRoot + "get_metadata", new { path }), ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict && IsPathError(response, "not_found")) return (null, null);
        EnsureSuccess(response, $"Could not read Dropbox {path}.");
        var metadata = await ReadMetadataAsync(response.Content, ct).ConfigureAwait(false);
        var arg = JsonSerializer.Serialize(new { path, rev = metadata.Rev });
        using var download = await SendAsync(() => ContentRequest("download", arg), ct).ConfigureAwait(false);
        EnsureSuccess(download, $"Could not download Dropbox {path}.");
        return (await download.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false), metadata);
    }

    private async Task UploadJsonAsync<T>(string path, T value, CancellationToken ct)
    {
        object wireValue = value switch
        {
            DropboxSyncLock cloudLock => new { deviceId = cloudLock.DeviceId, startedAt = cloudLock.StartedAt.ToUnixTimeMilliseconds() },
            DropboxSyncState state => new { deviceId = state.DeviceId, syncedAt = state.SyncedAt.ToUnixTimeMilliseconds(), contentHash = state.ContentHash },
            _ => throw new InvalidOperationException("Unsupported Dropbox advisory JSON type."),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(wireValue, JsonOptions);
        var arg = JsonSerializer.Serialize(new { path, mode = "overwrite", autorename = false, strict_conflict = true });
        using var response = await SendAsync(() => ContentRequest("upload", arg, () => new ByteArrayContent(bytes)), ct).ConfigureAwait(false);
        EnsureSuccess(response, $"Could not write Dropbox {path}.");
    }

    private async Task<CloudSaveMetadata> UploadSingleAsync(string file, string? expectedRev, CancellationToken ct)
    {
        var commit = MakeCommit(MakeMode(expectedRev));
        using var response = await SendAsync(() => ContentRequest("upload", JsonSerializer.Serialize(commit), () => new StreamContent(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))), ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            if (IsPathError(response, "conflict")) throw new DropboxConflictException("Dropbox save changed during upload.");
            EnsureSuccess(response, "Dropbox rejected save upload.");
        }
        EnsureSuccess(response, "Could not upload Dropbox save.");
        return await ReadMetadataAsync(response.Content, ct).ConfigureAwait(false);
    }

    private async Task<CloudSaveMetadata> UploadSessionAsync(string file, long length, object commit, string? expectedRev, CancellationToken ct)
    {
        string sessionId;
        await using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, _chunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var first = new byte[(int)Math.Min(_chunkSize, length)];
            await stream.ReadExactlyAsync(first, ct).ConfigureAwait(false);
            using var response = await SendAsync(() => ContentRequest("upload_session/start", "{}", () => new ByteArrayContent(first)), ct).ConfigureAwait(false);
            EnsureSuccess(response, "Could not start Dropbox upload session.");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            sessionId = json.RootElement.GetProperty("session_id").GetString() ?? throw new InvalidDataException("Dropbox omitted upload session ID.");
            long offset = first.Length;
            while (offset < length)
            {
                var chunk = new byte[(int)Math.Min(_chunkSize, length - offset)];
                await stream.ReadExactlyAsync(chunk, ct).ConfigureAwait(false);
                var appendArg = JsonSerializer.Serialize(new { cursor = new { session_id = sessionId, offset }, close = false });
                using var append = await SendAsync(() => ContentRequest("upload_session/append_v2", appendArg, () => new ByteArrayContent(chunk)), ct).ConfigureAwait(false);
                EnsureSuccess(append, "Could not append Dropbox upload session.");
                offset += chunk.Length;
            }
            var remaining = Array.Empty<byte>();
            var finishArg = JsonSerializer.Serialize(new { cursor = new { session_id = sessionId, offset }, commit });
            using var finish = await SendAsync(() => ContentRequest("upload_session/finish", finishArg, () => new ByteArrayContent(remaining)), ct).ConfigureAwait(false);
            if (finish.StatusCode == HttpStatusCode.Conflict && IsPathError(finish, "conflict"))
                throw new DropboxConflictException("Dropbox save changed during upload.");
            EnsureSuccess(finish, "Could not finish Dropbox upload session.");
            return await ReadMetadataAsync(finish.Content, ct).ConfigureAwait(false);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> makeRequest, CancellationToken ct)
    {
        var forceRefresh = false;
        var refreshedAfterUnauthorized = false;
        var rateLimitRetries = 0;
        while (true)
        {
            using var request = makeRequest();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _oauth.GetAccessTokenAsync(ct, forceRefresh).ConfigureAwait(false));
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && !refreshedAfterUnauthorized)
            {
                response.Dispose();
                refreshedAfterUnauthorized = true;
                forceRefresh = true;
                continue;
            }
            if ((int)response.StatusCode == 429 && rateLimitRetries < 3)
            {
                var retryAfter = response.Headers.RetryAfter;
                var delay = retryAfter?.Delta ?? (retryAfter?.Date is { } retryDate ? retryDate - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(1));
                response.Dispose();
                rateLimitRetries++;
                await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, ct).ConfigureAwait(false);
                forceRefresh = false;
                continue;
            }
            return response;
        }
    }

    private static HttpRequestMessage JsonRequest(HttpMethod method, string url, object body) => new(method, url)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    private static HttpRequestMessage ContentRequest(string endpoint, string arg, Func<HttpContent>? content = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ContentRoot + endpoint);
        request.Headers.TryAddWithoutValidation("Dropbox-API-Arg", arg);
        request.Content = content?.Invoke() ?? new ByteArrayContent([]);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return request;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static JsonElement GetProperty(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        throw new JsonException($"Missing advisory field '{name}'.");
    }

    private static object MakeMode(string? rev) => rev is null ? "add" : new Dictionary<string, string> { [".tag"] = "update", ["update"] = rev };
    private static object MakeCommit(object mode) => new { path = "/save.zip", mode, autorename = false, strict_conflict = true };

    private static async Task<long> CopyBoundedAsync(Stream input, Stream output, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            total = checked(total + read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        return total;
    }

    private static async Task<CloudSaveMetadata> ReadMetadataAsync(HttpContent content, CancellationToken ct) =>
        ParseMetadata(await content.ReadAsStringAsync(ct).ConfigureAwait(false));

    private static CloudSaveMetadata ParseMetadata(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new CloudSaveMetadata(root.GetProperty("rev").GetString() ?? throw new InvalidDataException("Dropbox omitted revision."),
            DateTimeOffset.Parse(root.GetProperty("server_modified").GetString() ?? throw new InvalidDataException("Dropbox omitted server modified time."), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    private static string? ReadApiResult(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Dropbox-API-Result", out var responseValues)) return responseValues.SingleOrDefault();
        if (response.Content.Headers.TryGetValues("Dropbox-API-Result", out var contentValues)) return contentValues.SingleOrDefault();
        return null;
    }

    private static bool IsPathError(HttpResponseMessage response, string expectedTag)
    {
        try
        {
            var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(json);
            return document.RootElement.GetProperty("error").GetProperty("path").GetProperty(".tag").GetString() == expectedTag;
        }
        catch (Exception) { return false; }
    }

    private static void EnsureSuccess(HttpResponseMessage response, string message)
    {
        if (!response.IsSuccessStatusCode)
            throw new DropboxAccessException(message, response.StatusCode);
    }
}
