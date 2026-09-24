using System.Security.Cryptography;
using System.Text.Json;

namespace WheelWizard.Features.CloudSync;

/// <summary>Stores Dropbox OAuth tokens in the Linux Secret Service (org.freedesktop.secrets).</summary>
public sealed class ProtectedTokenStoreLinux : IProtectedTokenStore
{
    private const string ServiceName = "WheelWizard.DropboxOAuth";
    private const string ItemLabel = "Wheel Wizard Dropbox credentials";

    private readonly ISecretServiceClient _client;

    public ProtectedTokenStoreLinux() => _client = new TmdsSecretServiceClient();

    internal ProtectedTokenStoreLinux(ISecretServiceClient client) => _client = client;

    /// <summary>Heuristic availability probe: a session bus address or socket must be present before the Secret Service is tried.</summary>
    public static bool IsLikelyAvailable()
    {
        var address = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        if (!string.IsNullOrWhiteSpace(address))
            return true;
        var runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return !string.IsNullOrWhiteSpace(runtimeDirectory) && File.Exists(Path.Combine(runtimeDirectory, "bus"));
    }

    public async Task<OAuthTokens?> LoadAsync(string accountId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var attributes = Attributes(accountId);
        try
        {
            var bytes = await _client.SearchSecretAsync(attributes, ct).ConfigureAwait(false);
            if (bytes is null)
                return null;
            try
            {
                return JsonSerializer.Deserialize<OAuthTokens>(bytes)
                    ?? throw new InvalidDataException("Stored Dropbox credentials are invalid.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or CryptographicException)
        {
            throw new SecureStorageUnavailableException("Stored Dropbox credentials are invalid.", e);
        }
    }

    public async Task SaveAsync(string accountId, OAuthTokens tokens, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(tokens);
        var attributes = Attributes(accountId);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(tokens);
        try
        {
            await _client.SaveSecretAsync(attributes, ItemLabel, bytes, ct).ConfigureAwait(false);
            var reloaded = await _client.SearchSecretAsync(attributes, ct).ConfigureAwait(false);
            if (reloaded is null)
                throw new CryptographicException("Linux Secret Service credential round trip did not match.");
            try
            {
                if (JsonSerializer.Deserialize<OAuthTokens>(reloaded) != tokens)
                    throw new CryptographicException("Linux Secret Service credential round trip did not match.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(reloaded);
            }
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or CryptographicException)
        {
            // A round-trip mismatch means what was written is unusable: remove it, then fail closed.
            await DeleteBestEffortAsync(attributes).ConfigureAwait(false);
            throw new SecureStorageUnavailableException("Linux Secret Service is unavailable.", e);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public Task DeleteAsync(string accountId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return _client.DeleteSecretsAsync(Attributes(accountId), ct);
    }

    private async Task DeleteBestEffortAsync(IReadOnlyDictionary<string, string> attributes)
    {
        try
        {
            await _client.DeleteSecretsAsync(attributes, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Cleanup is best-effort; the caller already receives the failure that triggered it.
        }
    }

    private static IReadOnlyDictionary<string, string> Attributes(string accountId) =>
        new Dictionary<string, string>
        {
            ["service"] = ServiceName,
            ["account"] = string.IsNullOrWhiteSpace(accountId)
                ? throw new ArgumentException("Dropbox account ID is required.", nameof(accountId))
                : accountId,
        };
}
