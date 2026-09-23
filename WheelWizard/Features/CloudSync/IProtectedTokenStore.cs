namespace WheelWizard.Features.CloudSync;

public sealed record OAuthTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

public interface IProtectedTokenStore
{
    Task<OAuthTokens?> LoadAsync(string accountId, CancellationToken ct);
    Task SaveAsync(string accountId, OAuthTokens tokens, CancellationToken ct);
    Task DeleteAsync(string accountId, CancellationToken ct);
}

public sealed class SecureStorageUnavailableException(string message, Exception? innerException = null) : Exception(message, innerException);

public sealed class DropboxReconnectRequiredException(string message, Exception? innerException = null) : Exception(message, innerException);
