using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WheelWizard.Features.CloudSync;

public interface IDropboxOAuthService
{
    Task<(string AccountId, string Email)> ConnectAsync(CancellationToken ct);
    Task<string> GetAccessTokenAsync(CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);
}

public interface ILoopbackOAuthCallbackHandler
{
    Uri RedirectUri { get; }
    Task StartAsync(CancellationToken ct);
    Task<Uri> WaitForCallbackAsync(CancellationToken ct);
    Task StopAsync();
}

public sealed class DropboxLoopbackOAuthCallbackHandler : ILoopbackOAuthCallbackHandler
{
    public static readonly Uri CallbackUri = new("http://localhost:47862/dropbox/");
    private HttpListener _listener = new();
    private bool _started;

    public Uri RedirectUri => CallbackUri;

    public Task StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_listener.IsListening)
            throw new InvalidOperationException("Dropbox callback listener is already running.");
        _listener = new HttpListener();
        _listener.Prefixes.Add(CallbackUri.AbsoluteUri);
        try
        {
            _listener.Start();
            _started = true;
        }
        catch (HttpListenerException e)
        {
            _listener.Close();
            throw new IOException("Dropbox callback port 47862 is unavailable.", e);
        }
        return Task.CompletedTask;
    }

    public async Task<Uri> WaitForCallbackAsync(CancellationToken ct)
    {
        if (!_started)
            throw new InvalidOperationException("OAuth callback listener is not running.");

        var context = await _listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false);
        var response = context.Response;
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "text/plain; charset=utf-8";
        var body = Encoding.UTF8.GetBytes("Dropbox authorization received. You may close this window.");
        await response.OutputStream.WriteAsync(body, ct).ConfigureAwait(false);
        response.Close();
        return context.Request.Url ?? throw new UnauthorizedAccessException("Invalid Dropbox callback.");
    }

    public Task StopAsync()
    {
        if (_started)
        {
            _listener.Stop();
            _listener.Close();
            _started = false;
        }
        return Task.CompletedTask;
    }
}

public sealed class DropboxOAuthService : IDropboxOAuthService
{
    public const string Redirect = "http://localhost:47862/dropbox/";
    private const string AuthorizeEndpoint = "https://www.dropbox.com/oauth2/authorize";
    private const string TokenEndpoint = "https://api.dropboxapi.com/oauth2/token";
    private const string AccountEndpoint = "https://api.dropboxapi.com/2/users/get_current_account";
    private const string ScopeList = "account_info.read files.metadata.read files.content.read files.content.write";
    private readonly HttpClient _httpClient;
    private readonly string _appKey;
    private readonly IProtectedTokenStore _tokenStore;
    private readonly ILoopbackOAuthCallbackHandler _callbackHandler;
    private readonly Action<string> _openBrowser;
    private readonly Func<string?> _accountId;
    private readonly TimeSpan _callbackTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PendingAuth? _pending;
    private string? _connectedAccountId;

    public DropboxOAuthService(
        HttpClient httpClient,
        string appKey,
        IProtectedTokenStore tokenStore,
        ILoopbackOAuthCallbackHandler callbackHandler,
        Action<string> openBrowser,
        Func<string?> accountId,
        TimeSpan? callbackTimeout = null)
    {
        _httpClient = httpClient;
        _appKey = string.IsNullOrWhiteSpace(appKey) ? throw new ArgumentException("Dropbox app key is required.", nameof(appKey)) : appKey;
        _tokenStore = tokenStore;
        if (callbackHandler.RedirectUri != DropboxLoopbackOAuthCallbackHandler.CallbackUri)
            throw new ArgumentException("Dropbox callback handler must use the fixed loopback redirect.", nameof(callbackHandler));
        _callbackHandler = callbackHandler;
        _openBrowser = openBrowser;
        _accountId = accountId;
        _callbackTimeout = callbackTimeout ?? TimeSpan.FromMinutes(5);
    }

    public Uri CreateAuthorizationUri(string? state = null)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var actualState = state ?? Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        _pending = new PendingAuth(verifier, actualState);
        var query = new Dictionary<string, string>
        {
            ["client_id"] = _appKey,
            ["response_type"] = "code",
            ["token_access_type"] = "offline",
            ["scope"] = ScopeList,
            ["state"] = actualState,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["redirect_uri"] = _callbackHandler.RedirectUri.AbsoluteUri,
        };
        return new Uri(AuthorizeEndpoint + "?" + string.Join("&", query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))));
    }

    public async Task<(string AccountId, string Email)> ConnectAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _callbackHandler.StartAsync(ct).ConfigureAwait(false);
            try
            {
                _openBrowser(CreateAuthorizationUri().AbsoluteUri);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_callbackTimeout);
                Uri callback;
                try
                {
                    callback = await _callbackHandler.WaitForCallbackAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
                {
                    throw new TimeoutException("Dropbox authorization timed out.");
                }
                var tokens = await AcceptCallbackAsync(callback, ct).ConfigureAwait(false);
                var accountId = _connectedAccountId ?? throw new InvalidOperationException("Dropbox account identity was not established.");
                var email = _connectedEmail ?? throw new InvalidOperationException("Dropbox email was not received.");
                try
                {
                    await _tokenStore.SaveAsync(accountId, tokens, ct).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException e)
                {
                    throw new SecureStorageUnavailableException("Protected credential storage is unavailable.", e);
                }
                _connectedAccountId = accountId;
                return (accountId, email);
            }
            finally
            {
                await _callbackHandler.StopAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private string? _connectedEmail;

    public async Task<OAuthTokens> AcceptCallbackAsync(Uri callback, CancellationToken ct)
    {
        var pending = _pending ?? throw new InvalidOperationException("No Dropbox authorization is pending.");
        if (callback.Scheme != "http" || callback.Host != "localhost" || callback.Port != 47862 || callback.AbsolutePath != "/dropbox/")
            throw new UnauthorizedAccessException("Dropbox callback route does not match.");
        var query = ParseQuery(callback.Query);
        if (!query.TryGetValue("state", out var receivedState) || !FixedTimeEquals(pending.State, receivedState))
            throw new UnauthorizedAccessException("Dropbox authorization state does not match.");
        if (query.TryGetValue("error", out var error))
            throw new UnauthorizedAccessException("Dropbox authorization was rejected: " + error);
        if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            throw new UnauthorizedAccessException("Dropbox callback did not contain an authorization code.");

        var response = await _httpClient.PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["client_id"] = _appKey,
            ["redirect_uri"] = _callbackHandler.RedirectUri.AbsoluteUri,
            ["code_verifier"] = pending.Verifier,
        }), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new UnauthorizedAccessException("Dropbox rejected the authorization code.");
        var token = await ReadTokenResponseAsync(response, null, ct).ConfigureAwait(false);
        using var accountRequest = new HttpRequestMessage(HttpMethod.Post, AccountEndpoint)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        accountRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var accountResponse = await _httpClient.SendAsync(accountRequest, ct).ConfigureAwait(false);
        if (!accountResponse.IsSuccessStatusCode)
            throw new UnauthorizedAccessException("Dropbox could not identify the authorized account.");
        using var accountJson = JsonDocument.Parse(await accountResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        _connectedAccountId = accountJson.RootElement.GetProperty("account_id").GetString();
        _connectedEmail = accountJson.RootElement.GetProperty("email").GetString();
        if (string.IsNullOrWhiteSpace(_connectedAccountId) || string.IsNullOrWhiteSpace(_connectedEmail))
            throw new InvalidDataException("Dropbox account response omitted required identity.");
        _pending = null;
        return token;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        var accountId = _connectedAccountId ?? _accountId();
        if (string.IsNullOrWhiteSpace(accountId))
            throw new DropboxReconnectRequiredException("Reconnect Dropbox to continue.");
        OAuthTokens? tokens;
        try
        {
            tokens = await _tokenStore.LoadAsync(accountId, ct).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException e)
        {
            throw new SecureStorageUnavailableException("Protected credential storage is unavailable.", e);
        }
        if (tokens is null)
            throw new DropboxReconnectRequiredException("Reconnect Dropbox to continue.");
        if (tokens.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            return tokens.AccessToken;

        try
        {
            using var response = await _httpClient.PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = tokens.RefreshToken,
                ["client_id"] = _appKey,
            }), ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new DropboxReconnectRequiredException("Dropbox refresh failed. Reconnect your account.");
            var refreshed = await ReadTokenResponseAsync(response, tokens.RefreshToken, ct).ConfigureAwait(false);
            try
            {
                await _tokenStore.SaveAsync(accountId, refreshed, ct).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException e)
            {
                throw new SecureStorageUnavailableException("Protected credential storage is unavailable.", e);
            }
            return refreshed.AccessToken;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (SecureStorageUnavailableException)
        {
            throw;
        }
        catch (DropboxReconnectRequiredException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new DropboxReconnectRequiredException("Dropbox refresh failed. Reconnect your account.", e);
        }
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        var accountId = _connectedAccountId ?? _accountId();
        if (string.IsNullOrWhiteSpace(accountId))
            return;
        try
        {
            await _tokenStore.DeleteAsync(accountId, ct).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException e)
        {
            throw new SecureStorageUnavailableException("Protected credential storage is unavailable.", e);
        }
        _connectedAccountId = null;
        _connectedEmail = null;
    }

    private static async Task<OAuthTokens> ReadTokenResponseAsync(HttpResponseMessage response, string? oldRefreshToken, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = json.RootElement;
        var access = root.GetProperty("access_token").GetString();
        var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : oldRefreshToken;
        if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(refresh))
            throw new InvalidDataException("Dropbox token response omitted access or refresh token.");
        var seconds = root.GetProperty("expires_in").GetInt64();
        return new OAuthTokens(access, refresh, DateTimeOffset.UtcNow.AddSeconds(seconds));
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty;
            if (!result.TryAdd(key, value))
                throw new UnauthorizedAccessException("Dropbox callback contains duplicate parameters.");
        }
        return result;
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var left = Encoding.UTF8.GetBytes(expected);
        var right = Encoding.UTF8.GetBytes(actual);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private sealed record PendingAuth(string Verifier, string State);
}
