namespace WheelWizard.Features.CloudSync;

/// <summary>Fails closed until Secret Service D-Bus round trips are implemented and verified.</summary>
public sealed class ProtectedTokenStoreLinux : IProtectedTokenStore
{
    private static SecureStorageUnavailableException Unavailable() =>
        new("Linux Secret Service is unavailable; Dropbox sign-in is disabled on this build.");

    public Task<OAuthTokens?> LoadAsync(string accountId, CancellationToken ct) => throw Unavailable();
    public Task SaveAsync(string accountId, OAuthTokens tokens, CancellationToken ct) => throw Unavailable();
    public Task DeleteAsync(string accountId, CancellationToken ct) => throw Unavailable();
}
