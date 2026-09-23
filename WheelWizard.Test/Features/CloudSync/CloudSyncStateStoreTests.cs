using WheelWizard.Features.CloudSync;

namespace WheelWizard.Test.Features.CloudSync;

public class CloudSyncStateStoreTests
{
    [Fact]
    public void ReadPersistedAccount_DistinguishesMissingValidAndCorruptState()
    {
        using var directory = new TemporaryDirectory();
        var store = new CloudSyncStateStore(() => directory.Path);

        Assert.Equal(PersistedAccountStatus.Missing, store.ReadPersistedAccount().Status);

        var paths = CreatePaths(directory.Path);
        store.Update(store.ReadFor("dbid:account", paths));
        Assert.Equal(new PersistedAccountRead(PersistedAccountStatus.Valid, "dbid:account"), store.ReadPersistedAccount());

        File.WriteAllText(Path.Combine(directory.Path, CloudSyncStateStore.StateFileName), "{not-json");
        Assert.Equal(PersistedAccountStatus.Corrupt, store.ReadPersistedAccount().Status);
    }

    [Fact]
    public void ReadFor_RetainsStateForSameAccountAndPathsAfterReopen()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreatePaths(directory.Path);
        var store = new CloudSyncStateStore(() => directory.Path);
        var original = store.ReadFor("account", paths) with
        {
            Rev = "rev-1",
            LocalHash = "hash-1",
            SessionPending = true,
            LastSync = DateTimeOffset.UtcNow,
        };
        store.Update(original);

        var reopened = new CloudSyncStateStore(() => directory.Path).ReadFor("account", paths);

        Assert.Equal(original, reopened);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("save")]
    [InlineData("wii")]
    [InlineData("account")]
    public void ReadFor_ClearsBaselineOnAccountOrResolvedPathChange_AndKeepsDeviceId(string change)
    {
        using var directory = new TemporaryDirectory();
        var originalPaths = CreatePaths(directory.Path);
        var store = new CloudSyncStateStore(() => directory.Path);
        var original = store.ReadFor("account", originalPaths) with
        {
            Rev = "rev-1",
            LocalHash = "hash-1",
            SessionPending = true,
            LastSync = DateTimeOffset.UtcNow,
        };
        store.Update(original);
        var changedPaths = change switch
        {
            "user" => originalPaths with { UserFolder = Path.Combine(directory.Path, "other-user") },
            "save" => originalPaths with { SaveRoot = Path.Combine(directory.Path, "other-save") },
            "wii" => originalPaths with { WiiRoot = Path.Combine(directory.Path, "other-wii") },
            _ => originalPaths,
        };

        var result = store.ReadFor(change == "account" ? "other-account" : "account", changedPaths);

        Assert.Equal(original.DeviceId, result.DeviceId);
        Assert.Null(result.Rev);
        Assert.Null(result.LocalHash);
        Assert.False(result.SessionPending);
        Assert.Null(result.LastSync);
        Assert.Equal(change == "account" ? "other-account" : "account", result.AccountId);
        Assert.Equal(Path.GetFullPath(changedPaths.UserFolder), result.DolphinUserFolder);
        Assert.Equal(Path.GetFullPath(changedPaths.SaveRoot), result.SaveRoot);
        Assert.Equal(Path.GetFullPath(changedPaths.WiiRoot), result.WiiRoot);
    }

    [Fact]
    public void ReadFor_NormalizesPathBeforeComparing()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreatePaths(directory.Path);
        var store = new CloudSyncStateStore(() => directory.Path);
        var original = store.ReadFor("account", paths) with { Rev = "rev-1", LocalHash = "hash-1" };
        store.Update(original);
        var withDotSegments = paths with
        {
            UserFolder = Path.Combine(directory.Path, "user", "nested", ".."),
            SaveRoot = Path.Combine(directory.Path, "save", "nested", ".."),
            WiiRoot = Path.Combine(directory.Path, "wii", "nested", ".."),
        };

        var result = new CloudSyncStateStore(() => directory.Path).ReadFor("account", withDotSegments);

        Assert.Equal("rev-1", result.Rev);
        Assert.Equal("hash-1", result.LocalHash);
    }

    [Fact]
    public void ReadFor_FailsClosedToFreshDeviceWhenJsonIsCorrupt()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, CloudSyncStateStore.StateFileName), "{not-json");
        var paths = CreatePaths(directory.Path);

        var state = new CloudSyncStateStore(() => directory.Path).ReadFor("account", paths);

        Assert.False(string.IsNullOrWhiteSpace(state.DeviceId));
        Assert.Null(state.Rev);
        Assert.Null(state.LocalHash);
        Assert.False(state.SessionPending);
    }

    [Fact]
    public void ReadFor_PreservesPendingSessionAfterRestart()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreatePaths(directory.Path);
        var firstStore = new CloudSyncStateStore(() => directory.Path);
        var pending = firstStore.ReadFor("account", paths) with { SessionPending = true };
        firstStore.Update(pending);

        var reopenedState = new CloudSyncStateStore(() => directory.Path).ReadFor("account", paths);

        Assert.True(reopenedState.SessionPending);
    }

    [Fact]
    public void ClearAccount_ClearsAssociationWithoutTouchingSaves()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreatePaths(directory.Path);
        var saveFile = Path.Combine(paths.SaveRoot, "save.dat");
        Directory.CreateDirectory(paths.SaveRoot);
        File.WriteAllText(saveFile, "save");
        var store = new CloudSyncStateStore(() => directory.Path);
        var original = store.ReadFor("account", paths) with { Rev = "rev", LocalHash = "hash" };
        store.Update(original);

        store.ClearAccount();
        var cleared = new CloudSyncStateStore(() => directory.Path).ReadFor("account", paths);

        Assert.Equal(original.DeviceId, cleared.DeviceId);
        Assert.Null(cleared.Rev);
        Assert.Null(cleared.LocalHash);
        Assert.False(cleared.SessionPending);
        Assert.Equal("save", File.ReadAllText(saveFile));
    }

    private static DolphinSavePaths CreatePaths(string root) => new(
        Path.Combine(root, "user"),
        Path.Combine(root, "save"),
        Path.Combine(root, "wii"),
        "1.0"
    );

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
