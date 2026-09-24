using System.Text;
using System.Text.Json;
using WheelWizard.Features.CloudSync;

namespace WheelWizard.Test.Features.CloudSync;

public class ProtectedTokenStoreLinuxTests
{
    private const string Service = "WheelWizard.DropboxOAuth";
    private const string Label = "Wheel Wizard Dropbox credentials";

    private static IReadOnlyDictionary<string, string> ExpectedAttributes(string accountId) =>
        new Dictionary<string, string> { ["service"] = Service, ["account"] = accountId };

    private static OAuthTokens Tokens() =>
        new(
            "fake-access-token",
            "fake-refresh-token",
            DateTimeOffset.Parse("2026-09-23T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture)
        );

    [Fact]
    public async Task SaveAsync_StoresJsonAtServiceAndAccountAttributes_WithCreateOrReplaceSemantics()
    {
        var fake = new FakeSecretServiceClient();
        var store = new ProtectedTokenStoreLinux(fake);
        var tokens = Tokens();

        await store.SaveAsync("dbid:account", tokens, CancellationToken.None);
        await store.SaveAsync("dbid:account", tokens with { AccessToken = "fake-access-token-2" }, CancellationToken.None);

        Assert.All(
            fake.SavedSecrets,
            saved =>
            {
                Assert.Equal(ExpectedAttributes("dbid:account"), saved.Attributes);
                Assert.Equal(Label, saved.Label);
            }
        );
        var stored = JsonSerializer.Deserialize<OAuthTokens>(Assert.Single(fake.Items).Value);
        Assert.NotNull(stored);
        Assert.Equal("fake-access-token-2", stored.AccessToken);
        Assert.Empty(fake.DeletedCalls);
    }

    [Fact]
    public async Task SaveAsync_RoundTripSucceeds_WithoutDeletingStoredSecret()
    {
        var fake = new FakeSecretServiceClient();
        var store = new ProtectedTokenStoreLinux(fake);

        await store.SaveAsync("dbid:account", Tokens(), CancellationToken.None);

        Assert.Null(fake.SearchOverride);
        Assert.Empty(fake.DeletedCalls);
        Assert.NotNull(fake.CurrentSecretOrNull(ExpectedAttributes("dbid:account")));
    }

    [Fact]
    public async Task SaveAsync_WhenReloadedValueDiffers_ThrowsSecureStorageUnavailable_AndDeletesWrittenSecret()
    {
        var fake = new FakeSecretServiceClient();
        var replaced = Tokens() with { AccessToken = "replaced-token" };
        fake.SearchOverride = _ => JsonSerializer.SerializeToUtf8Bytes(replaced);
        var store = new ProtectedTokenStoreLinux(fake);

        var exception = await Assert.ThrowsAsync<SecureStorageUnavailableException>(
            () => store.SaveAsync("dbid:account", Tokens(), CancellationToken.None)
        );

        Assert.NotNull(exception);
        Assert.Equal(ExpectedAttributes("dbid:account"), Assert.Single(fake.DeletedCalls));
    }

    [Fact]
    public async Task SaveAsync_WhenClientFailsVerification_ThrowsSecureStorageUnavailable_WithoutDeleting()
    {
        var fake = new FakeSecretServiceClient();
        fake.SearchOverride = _ =>
            throw new SecureStorageUnavailableException("Linux Secret Service is unavailable.", new InvalidOperationException("dbus"));
        var store = new ProtectedTokenStoreLinux(fake);

        await Assert.ThrowsAsync<SecureStorageUnavailableException>(
            () => store.SaveAsync("dbid:account", Tokens(), CancellationToken.None)
        );

        Assert.Empty(fake.DeletedCalls);
    }

    [Fact]
    public async Task LoadAsync_WhenNoSecretFound_ReturnsNull()
    {
        var fake = new FakeSecretServiceClient();
        var store = new ProtectedTokenStoreLinux(fake);

        Assert.Null(await store.LoadAsync("dbid:account", CancellationToken.None));
    }

    [Fact]
    public async Task LoadAsync_ReturnsStoredTokens()
    {
        var fake = new FakeSecretServiceClient();
        fake.SearchOverride = _ => JsonSerializer.SerializeToUtf8Bytes(Tokens());
        var store = new ProtectedTokenStoreLinux(fake);

        Assert.Equal(Tokens(), await store.LoadAsync("dbid:account", CancellationToken.None));
    }

    [Fact]
    public async Task LoadAsync_WhenStoredJsonIsInvalid_ThrowsSecureStorageUnavailable()
    {
        var fake = new FakeSecretServiceClient();
        fake.SearchOverride = _ => Encoding.UTF8.GetBytes("not-json");
        var store = new ProtectedTokenStoreLinux(fake);

        await Assert.ThrowsAsync<SecureStorageUnavailableException>(() => store.LoadAsync("dbid:account", CancellationToken.None));
    }

    [Fact]
    public async Task DeleteAsync_PassesExactServiceAndAccountAttributes()
    {
        var fake = new FakeSecretServiceClient();
        var store = new ProtectedTokenStoreLinux(fake);

        await store.DeleteAsync("dbid:account", CancellationToken.None);

        Assert.Equal(ExpectedAttributes("dbid:account"), Assert.Single(fake.DeletedCalls));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Methods_WithEmptyAccountId_ThrowArgumentException(string accountId)
    {
        var fake = new FakeSecretServiceClient();
        var store = new ProtectedTokenStoreLinux(fake);

        await Assert.ThrowsAsync<ArgumentException>(() => store.LoadAsync(accountId, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(accountId, Tokens(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync(accountId, CancellationToken.None));
    }

    [Fact]
    public async Task OptInLinuxSecretServiceLive_RoundTripsAndDeletesWithoutLoggingTokenValues()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WHEELWIZARD_SECRET_SERVICE_LIVE")))
            return;

        var store = new ProtectedTokenStoreLinux();
        const string accountId = "wheelwizard-live-smoke";
        var tokens = Tokens();

        await store.SaveAsync(accountId, tokens, CancellationToken.None);
        Assert.Equal(tokens, await store.LoadAsync(accountId, CancellationToken.None));

        await store.DeleteAsync(accountId, CancellationToken.None);
        Assert.Null(await store.LoadAsync(accountId, CancellationToken.None));
    }

    private sealed record SavedSecret(IReadOnlyDictionary<string, string> Attributes, string Label, byte[] Value);

    private sealed class FakeSecretServiceClient : ISecretServiceClient
    {
        private readonly List<SavedSecret> _secrets = new();

        public List<SavedSecret> SavedSecrets { get; } = new();

        public List<IReadOnlyDictionary<string, string>> DeletedCalls { get; } = new();

        public Func<IReadOnlyDictionary<string, string>, byte[]?>? SearchOverride { get; set; }

        public IReadOnlyList<SavedSecret> Items => _secrets;

        public SavedSecret? CurrentSecretOrNull(IReadOnlyDictionary<string, string> attributes) =>
            _secrets.FirstOrDefault(secret => Matches(secret, attributes));

        public Task<byte[]?> SearchSecretAsync(IReadOnlyDictionary<string, string> attributes, CancellationToken ct) =>
            Task.FromResult<byte[]?>(
                SearchOverride is not null ? SearchOverride(attributes)?.ToArray() : CurrentSecretOrNull(attributes)?.Value.ToArray()
            );

        public Task SaveSecretAsync(IReadOnlyDictionary<string, string> attributes, string label, byte[] value, CancellationToken ct)
        {
            var existing = CurrentSecretOrNull(attributes);
            if (existing is not null)
                _secrets.Remove(existing);
            var secret = new SavedSecret(attributes, label, value.ToArray());
            _secrets.Add(secret);
            SavedSecrets.Add(secret);
            return Task.CompletedTask;
        }

        public Task DeleteSecretsAsync(IReadOnlyDictionary<string, string> attributes, CancellationToken ct)
        {
            DeletedCalls.Add(attributes);
            foreach (var secret in _secrets.Where(secret => Matches(secret, attributes)).ToList())
                _secrets.Remove(secret);
            return Task.CompletedTask;
        }

        private static bool Matches(SavedSecret secret, IReadOnlyDictionary<string, string> attributes) =>
            attributes.Count == secret.Attributes.Count
            && attributes.All(pair => secret.Attributes.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }
}
