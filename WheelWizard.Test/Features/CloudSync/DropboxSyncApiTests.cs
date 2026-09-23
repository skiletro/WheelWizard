using System.Net;
using System.Text;
using WheelWizard.Features.CloudSync;

namespace WheelWizard.Test.Features.CloudSync;

public class DropboxSyncApiTests
{
    [Fact]
    public async Task MetadataTypedNotFoundReturnsNull()
    {
        var api = CreateApi(new QueueHandler(Response(HttpStatusCode.Conflict, """{"error":{"path":{".tag":"not_found"}}}""")));

        Assert.Null(await api.GetSaveMetadataAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, """{"error":{"path":{".tag":"other"}}}""")]
    [InlineData(HttpStatusCode.NotFound, "{}")]
    [InlineData(HttpStatusCode.Forbidden, "{}")]
    [InlineData(HttpStatusCode.InternalServerError, "{}")]
    public async Task MetadataErrorsOtherThanTypedPathNotFoundThrow(HttpStatusCode status, string body)
    {
        var api = CreateApi(new QueueHandler(Response(status, body)));

        await Assert.ThrowsAsync<DropboxAccessException>(() => api.GetSaveMetadataAsync(CancellationToken.None));
    }

    [Fact]
    public async Task UploadUsesAddThenUpdateAndConflictNeverRetriesOverwrite()
    {
        var handler = new QueueHandler(
            Response(HttpStatusCode.OK, """{"rev":"r1","server_modified":"2026-09-23T00:00:00Z"}"""),
            Response(HttpStatusCode.OK, """{"rev":"r2","server_modified":"2026-09-23T00:00:00Z"}"""),
            Response(HttpStatusCode.Conflict, """{"error":{"path":{".tag":"conflict"}}}"""));
        var api = CreateApi(handler);
        var file = TempFile("save");
        try
        {
            Assert.Equal("r1", (await api.UploadAsync(file, null, CancellationToken.None)).Rev);
            Assert.Equal("r2", (await api.UploadAsync(file, "r1", CancellationToken.None)).Rev);
            await Assert.ThrowsAsync<DropboxConflictException>(() => api.UploadAsync(file, "r2", CancellationToken.None));
            Assert.Equal(3, handler.Requests.Count);
            Assert.Contains("\"mode\":\"add\"", handler.Args[0]);
            Assert.Contains("\"autorename\":false", handler.Args[0]);
            Assert.Contains("\"mode\":{\".tag\":\"update\",\"update\":\"r1\"}", handler.Args[1]);
            Assert.DoesNotContain("overwrite", string.Join(" ", handler.Args));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task UnauthorizedUploadForcesExactlyOneRefreshAndReplaysBody()
    {
        var handler = new QueueHandler(
            new HttpResponseMessage(HttpStatusCode.Unauthorized),
            Response(HttpStatusCode.OK, """{"rev":"r1","server_modified":"2026-09-23T00:00:00Z"}"""));
        var tokens = new StaticTokenProvider();
        var api = new DropboxSyncApi(tokens, new HttpClient(handler));
        var file = TempFile("replay body");
        try
        {
            Assert.Equal("r1", (await api.UploadAsync(file, null, CancellationToken.None)).Rev);
            Assert.Equal(new[] { false, true }, tokens.ForceRefreshCalls);
            Assert.Equal("replay body", Encoding.UTF8.GetString(handler.Bodies[0]));
            Assert.Equal("replay body", Encoding.UTF8.GetString(handler.Bodies[1]));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task DownloadRequestsExactRevisionAndReturnsMetadata()
    {
        var handler = new QueueHandler(Response(HttpStatusCode.OK, "", """{"rev":"r2","server_modified":"2026-09-23T00:00:00Z"}"""));
        var api = CreateApi(handler);
        var file = TempFile("");
        try
        {
            var metadata = await api.DownloadAsync("r2", file, CancellationToken.None);
            Assert.Equal("r2", metadata.Rev);
            Assert.Contains("\"rev\":\"r2\"", handler.Args.Single());
            Assert.Equal("download", handler.Endpoints.Single());
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task SmallThresholdUsesUploadSessionStartAppendFinish()
    {
        var handler = new QueueHandler(
            Response(HttpStatusCode.OK, """{"session_id":"session-1"}"""),
            Response(HttpStatusCode.OK, "{}"),
            Response(HttpStatusCode.OK, """{"rev":"session-rev","server_modified":"2026-09-23T00:00:00Z"}"""));
        var api = new DropboxSyncApi(new StaticTokenProvider(), new HttpClient(handler), uploadSessionThreshold: 3, chunkSize: 2);
        var file = TempFile("four");
        try
        {
            Assert.Equal("session-rev", (await api.UploadAsync(file, null, CancellationToken.None)).Rev);
            Assert.Equal(new[] { "start", "append_v2", "finish" }, handler.Endpoints);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task StateWriteUsesSharedJsonNamesAndFailureIsNonFatal()
    {
        var handler = new QueueHandler(Response(HttpStatusCode.InternalServerError, "{}"));
        var api = CreateApi(handler);

        await api.WriteStateAsync(new DropboxSyncState("device-1", DateTimeOffset.Parse("2026-09-23T00:00:00Z"), "hash"), CancellationToken.None);

        Assert.Contains("\"path\":\"/state.json\"", handler.Args.Single());
        Assert.Contains("\"deviceId\":\"device-1\"", Encoding.UTF8.GetString(handler.Bodies.Single()));
        Assert.Contains("\"syncedAt\"", Encoding.UTF8.GetString(handler.Bodies.Single()));
        Assert.Contains("\"contentHash\":\"hash\"", Encoding.UTF8.GetString(handler.Bodies.Single()));
    }

    [Fact]
    public async Task StateAndLockJsonCorruptionIsAdvisory()
    {
        var handler = new QueueHandler(
            Response(HttpStatusCode.OK, """{"rev":"lock-rev","server_modified":"2026-09-23T00:00:00Z"}"""),
            CorruptDownload("""{"rev":"lock-rev","server_modified":"2026-09-23T00:00:00Z"}"""),
            Response(HttpStatusCode.OK, """{"rev":"state-rev","server_modified":"2026-09-23T00:00:00Z"}"""),
            CorruptDownload("""{"rev":"state-rev","server_modified":"2026-09-23T00:00:00Z"}"""));
        var api = CreateApi(handler);

        Assert.Null(await api.ReadLockAsync(CancellationToken.None));
        Assert.Null(await api.ReadStateAsync(CancellationToken.None));
    }

    private static DropboxSyncApi CreateApi(QueueHandler handler, long threshold = 140L * 1024 * 1024) =>
        new(new StaticTokenProvider(), new HttpClient(handler), threshold);

    private static string TempFile(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, contents);
        return path;
    }

    private static HttpResponseMessage CorruptDownload(string apiResult) => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{", Encoding.UTF8, "application/octet-stream"),
        Headers = { { "Dropbox-API-Result", apiResult } },
    };

    private static HttpResponseMessage Response(HttpStatusCode code, string json, string? apiResult = null) => new(code)
    {
        Content = new ByteArrayContent(Encoding.UTF8.GetBytes(apiResult is null ? json : "download contents")),
        Headers = { { "Dropbox-API-Result", apiResult ?? "{}" } },
    };

    private sealed class StaticTokenProvider : IDropboxOAuthService
    {
        public Task<(string AccountId, string Email)> ConnectAsync(CancellationToken ct) => throw new NotImplementedException();
        public List<bool> ForceRefreshCalls { get; } = [];
        public Task<string> GetAccessTokenAsync(CancellationToken ct, bool forceRefresh = false)
        {
            ForceRefreshCalls.Add(forceRefresh);
            return Task.FromResult(forceRefresh ? "refreshed" : "token");
        }
        public Task DisconnectAsync(CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Args { get; } = [];
        public List<string> Endpoints { get; } = [];
        public List<byte[]> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Endpoints.Add(request.RequestUri!.AbsolutePath.Split('/').Last());
            Args.Add(request.Headers.TryGetValues("Dropbox-API-Arg", out var values) ? values.Single() : "");
            if (request.Content is not null)
                Bodies.Add(await request.Content.ReadAsByteArrayAsync(cancellationToken));
            return _responses.Dequeue();
        }
    }
}
