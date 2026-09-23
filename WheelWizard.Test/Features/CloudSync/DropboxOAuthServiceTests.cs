using System.Net;
using System.Text;
using WheelWizard.Features.CloudSync;

namespace WheelWizard.Test.Features.CloudSync;

public class DropboxOAuthServiceTests
{
    [Fact]
    public void CreateAuthorizationUri_UsesDropboxPkceAndExactLoopbackRedirect()
    {
        var service = CreateService(new FakeTokenStore(), new QueueHandler());

        var uri = service.CreateAuthorizationUri();
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);

        Assert.Equal("client-key", query["client_id"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("offline", query["token_access_type"]);
        Assert.Equal("account_info.read files.metadata.read files.content.read files.content.write", query["scope"]);
        Assert.False(string.IsNullOrWhiteSpace(query["state"]));
        Assert.False(string.IsNullOrWhiteSpace(query["code_challenge"]));
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("http://localhost:47862/dropbox/", query["redirect_uri"]);
        Assert.Null(query["code"]);
        Assert.Null(query["access_token"]);
        Assert.Null(query["refresh_token"]);
    }

    [Theory]
    [InlineData("http://localhost:47862/other/?state=valid&code=abc")]
    [InlineData("http://evil.example/dropbox/?state=valid&code=abc")]
    [InlineData("http://localhost:47862/dropbox/?state=wrong&code=abc")]
    public async Task AcceptCallbackAsync_RejectsWrongCallback(string callback)
    {
        var service = CreateService(new FakeTokenStore(), new QueueHandler());
        service.CreateAuthorizationUri(state: "valid");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.AcceptCallbackAsync(new Uri(callback), CancellationToken.None));
    }

    [Fact]
    public async Task ConnectAsync_TimesOutWhenCallbackDoesNotArrive()
    {
        var service = new DropboxOAuthService(
            new HttpClient(new QueueHandler()), "client-key", new FakeTokenStore(), new TimeoutCallbackHandler(), _ => { }, () => null,
            TimeSpan.FromMilliseconds(1));

        await Assert.ThrowsAsync<TimeoutException>(() => service.ConnectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ConnectAsync_ReportsBusyCallbackPort()
    {
        var service = new DropboxOAuthService(new HttpClient(new QueueHandler()), "client-key", new FakeTokenStore(), new BusyCallbackHandler(), _ => { }, () => null);

        await Assert.ThrowsAsync<IOException>(() => service.ConnectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AcceptCallbackAsync_RejectsTokenEndpointFailure()
    {
        var service = CreateService(new FakeTokenStore(), new QueueHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)));
        service.CreateAuthorizationUri(state: "valid");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.AcceptCallbackAsync(new Uri("http://localhost:47862/dropbox/?state=valid&code=code"), CancellationToken.None));
    }

    [Fact]
    public async Task GetAccessTokenAsync_RetainsPreviousRefreshTokenWhenResponseOmitsIt()
    {
        var store = new FakeTokenStore
        {
            Tokens = new OAuthTokens("expired", "refresh-old", DateTimeOffset.UtcNow.AddMinutes(-1)),
        };
        var handler = new QueueHandler(_ => JsonResponse("""{"access_token":"access-new","token_type":"bearer","expires_in":14400}"""), _ => JsonResponse("""{"account_id":"dbid:account","email":"person@example.com"}"""));
        var service = CreateService(store, handler, () => "dbid:account");

        var token = await service.GetAccessTokenAsync(CancellationToken.None);

        Assert.Equal("access-new", token);
        Assert.Equal("refresh-old", store.Tokens!.RefreshToken);
        Assert.Single(handler.Requests);
        var refreshForm = await handler.Requests[0].Content!.ReadAsStringAsync();
        Assert.Contains("grant_type=refresh_token", refreshForm);
        Assert.Contains("refresh_token=refresh-old", refreshForm);
        Assert.Contains("client_id=client-key", refreshForm);
    }

    [Fact]
    public async Task GetAccessTokenAsync_ReportsReconnectAfterRefreshFailure()
    {
        var store = new FakeTokenStore
        {
            Tokens = new OAuthTokens("expired", "refresh-old", DateTimeOffset.UtcNow.AddMinutes(-1)),
        };
        var service = CreateService(store, new QueueHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)), () => "dbid:account");

        await Assert.ThrowsAsync<DropboxReconnectRequiredException>(() => service.GetAccessTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DisconnectAsync_WaitsForRefreshBeforeDeletingStoredCredentials()
    {
        var store = new FakeTokenStore
        {
            Tokens = new OAuthTokens("expired", "refresh-old", DateTimeOffset.UtcNow.AddMinutes(-1)),
        };
        var handler = new BlockedRefreshHandler();
        var service = new DropboxOAuthService(new HttpClient(handler), "client-key", store, new FakeCallbackHandler(), _ => { }, () => "dbid:account");

        var refreshTask = service.GetAccessTokenAsync(CancellationToken.None);
        await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var disconnectTask = service.DisconnectAsync(CancellationToken.None);
        await Task.Delay(75);
        var disconnectWaitedForRefresh = !disconnectTask.IsCompleted;

        handler.CompleteWith(JsonResponse("""{"access_token":"access-new","expires_in":14400}"""));
        await refreshTask;
        await disconnectTask;

        Assert.True(disconnectWaitedForRefresh);
        Assert.Null(store.Tokens);
    }

    [Fact]
    public async Task ConnectAsync_DoesNotSaveTokensWhenProtectedStorageUnavailable()
    {
        var store = new FakeTokenStore { SaveException = new UnauthorizedAccessException("keyring unavailable") };
        var handler = new QueueHandler(_ => JsonResponse("""{"access_token":"access","refresh_token":"refresh","expires_in":14400}"""), _ => JsonResponse("""{"account_id":"dbid:account","email":"person@example.com"}"""));
        var callback = new FakeCallbackHandler();
        var service = CreateService(store, handler, callbackHandler: callback, browser: url => callback.SetState(System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["state"]!));

        await Assert.ThrowsAsync<SecureStorageUnavailableException>(() => service.ConnectAsync(CancellationToken.None));
        Assert.Null(store.Tokens);
        Assert.Equal(2, handler.Requests.Count);
        var exchangeForm = await handler.Requests[0].Content!.ReadAsStringAsync();
        Assert.Contains("grant_type=authorization_code", exchangeForm);
        Assert.Contains("client_id=client-key", exchangeForm);
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%3A47862%2Fdropbox%2F", exchangeForm);
        Assert.Contains("code_verifier=", exchangeForm);
        Assert.Equal("Bearer access", handler.Requests[1].Headers.Authorization?.ToString());
    }

    private static DropboxOAuthService CreateService(
        FakeTokenStore store,
        QueueHandler handler,
        Func<string?>? accountId = null,
        ILoopbackOAuthCallbackHandler? callbackHandler = null,
        Action<string>? browser = null) => new(
        new HttpClient(handler), "client-key", store,
        callbackHandler ?? new FakeCallbackHandler(),
        browser ?? (_ => { }), accountId ?? (() => null));

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class FakeTokenStore : IProtectedTokenStore
    {
        public OAuthTokens? Tokens { get; set; }
        public Exception? SaveException { get; set; }

        public Task<OAuthTokens?> LoadAsync(string accountId, CancellationToken ct) => Task.FromResult(Tokens);
        public Task SaveAsync(string accountId, OAuthTokens tokens, CancellationToken ct)
        {
            if (SaveException is not null)
                throw SaveException;
            Tokens = tokens;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string accountId, CancellationToken ct)
        {
            Tokens = null;
            return Task.CompletedTask;
        }
    }

    private sealed class TimeoutCallbackHandler : ILoopbackOAuthCallbackHandler
    {
        public Uri RedirectUri => new("http://localhost:47862/dropbox/");
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public async Task<Uri> WaitForCallbackAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Unreachable");
        }
        public Task StopAsync() => Task.CompletedTask;
    }

    private sealed class BusyCallbackHandler : ILoopbackOAuthCallbackHandler
    {
        public Uri RedirectUri => new("http://localhost:47862/dropbox/");
        public Task StartAsync(CancellationToken ct) => throw new IOException("port busy");
        public Task<Uri> WaitForCallbackAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task StopAsync() => Task.CompletedTask;
    }

    private sealed class FakeCallbackHandler : ILoopbackOAuthCallbackHandler
    {
        private string _state = string.Empty;
        public Uri RedirectUri => new("http://localhost:47862/dropbox/");
        public void SetState(string state) => _state = state;
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<Uri> WaitForCallbackAsync(CancellationToken ct) => Task.FromResult(new Uri($"http://localhost:47862/dropbox/?state={_state}&code=code"));
        public Task StopAsync() => Task.CompletedTask;
    }

    private sealed class BlockedRefreshHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<HttpResponseMessage> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void CompleteWith(HttpResponseMessage response) => _response.TrySetResult(response);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestStarted.TrySetResult();
            return await _response.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class QueueHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new(responses);
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_responses.Dequeue()(request));
        }
    }
}
