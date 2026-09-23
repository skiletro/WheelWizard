using WheelWizard.Features.CloudSync;

namespace WheelWizard.Test.Features.CloudSync;

public class ProtectedTokenStoreTests
{
    [Fact]
    public async Task ProtectedTokenStore_WhenBackendUnavailable_FailsClosedWithoutPlaintextFallback()
    {
        var store = new UnavailableProtectedTokenStore();
        var tokens = new OAuthTokens("access-secret", "refresh-secret", DateTimeOffset.UtcNow.AddHours(1));

        await Assert.ThrowsAsync<SecureStorageUnavailableException>(() => store.SaveAsync("dbid:account", tokens, CancellationToken.None));
        await Assert.ThrowsAsync<SecureStorageUnavailableException>(() => store.LoadAsync("dbid:account", CancellationToken.None));
        await Assert.ThrowsAsync<SecureStorageUnavailableException>(() => store.DeleteAsync("dbid:account", CancellationToken.None));
    }

    [Fact]
    public async Task LinuxProtectedTokenStore_FailsClosedWithoutPersistingTokens()
    {
        var store = new ProtectedTokenStoreLinux();
        var tokens = new OAuthTokens("access-secret", "refresh-secret", DateTimeOffset.UtcNow.AddHours(1));

        await Assert.ThrowsAsync<SecureStorageUnavailableException>(() => store.SaveAsync("dbid:account", tokens, CancellationToken.None));
        await Assert.ThrowsAsync<SecureStorageUnavailableException>(() => store.LoadAsync("dbid:account", CancellationToken.None));
        await Assert.ThrowsAsync<SecureStorageUnavailableException>(() => store.DeleteAsync("dbid:account", CancellationToken.None));
    }

    private sealed class UnavailableProtectedTokenStore : IProtectedTokenStore
    {
        private static SecureStorageUnavailableException Unavailable() => new("OS credential storage unavailable");
        public Task<OAuthTokens?> LoadAsync(string accountId, CancellationToken ct) => throw Unavailable();
        public Task SaveAsync(string accountId, OAuthTokens tokens, CancellationToken ct) => throw Unavailable();
        public Task DeleteAsync(string accountId, CancellationToken ct) => throw Unavailable();
    }
}
