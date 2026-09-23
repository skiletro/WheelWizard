using NSubstitute;
using WheelWizard.Features.CloudSync;
using WheelWizard.Settings;
using Xunit;

namespace WheelWizard.Test.Features.CloudSync;

public sealed class ForeignLockLifecycleTests
{
    [Fact]
    public async Task Idle_startup_and_repeated_focus_preserve_foreign_lock_and_defer_sync()
    {
        using var directory = new WheelSyncArchiveReaderTests.TemporaryDirectory();
        var fixture = Create(directory.Path, "r1", "local");
        fixture.Dropbox.Save = new CloudSaveMetadata("r2", DateTimeOffset.UtcNow);
        var foreignLock = ForeignLock();
        fixture.Dropbox.Lock = foreignLock;

        fixture.Oauth.RestoreSessionAsync("account", Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        await fixture.Coordinator.RestoreSessionAsync(CancellationToken.None);
        await fixture.Coordinator.ReconcileSessionAsync(false, CancellationToken.None);
        await fixture.Coordinator.ReconcileSessionAsync(false, CancellationToken.None);

        Assert.Same(foreignLock, fixture.Dropbox.Lock);
        Assert.Equal(0, fixture.Dropbox.DeleteLockCount);
        Assert.Equal(0, fixture.Dropbox.UploadCount);
        Assert.Equal(0, fixture.Dropbox.DownloadCount);
        Assert.Equal(2, fixture.Dropbox.MetadataReadCount);
        Assert.Equal(0, fixture.Restore.RestoreCount);
    }

    [Fact]
    public async Task Session_start_does_not_replace_fresh_foreign_lock_or_restore_saves()
    {
        using var directory = new WheelSyncArchiveReaderTests.TemporaryDirectory();
        var fixture = Create(directory.Path, "r1", "local");
        var foreignLock = ForeignLock();
        fixture.Dropbox.Lock = foreignLock;

        await fixture.Coordinator.SessionStartedAsync(CancellationToken.None);

        Assert.Same(foreignLock, fixture.Dropbox.Lock);
        Assert.Equal(0, fixture.Dropbox.WriteLockCount);
        Assert.Equal(0, fixture.Restore.RestoreCount);
        Assert.True(fixture.StateStore.Current!.SessionPending);
    }

    [Fact]
    public async Task Exit_preserves_replacement_foreign_lock_and_foreign_freshness_defers_pull()
    {
        using var directory = new WheelSyncArchiveReaderTests.TemporaryDirectory();
        var fixture = Create(directory.Path, "r1", "local");
        await fixture.Coordinator.SessionStartedAsync(CancellationToken.None);
        var foreignLock = ForeignLock();
        fixture.Dropbox.Lock = foreignLock;
        fixture.Dropbox.Save = new CloudSaveMetadata("r2", DateTimeOffset.UtcNow);

        await fixture.Coordinator.SessionEndedAsync(CancellationToken.None);

        Assert.Same(foreignLock, fixture.Dropbox.Lock);
        Assert.Equal(0, fixture.Dropbox.DeleteLockCount);
        Assert.Equal(0, fixture.Dropbox.DownloadCount);
        Assert.Equal(0, fixture.Restore.RestoreCount);
    }

    [Fact]
    public async Task Exit_deletes_unchanged_owned_lock_once_then_syncs()
    {
        using var directory = new WheelSyncArchiveReaderTests.TemporaryDirectory();
        var fixture = Create(directory.Path, "r1", "local");
        fixture.Dropbox.Save = new CloudSaveMetadata("r1", DateTimeOffset.UtcNow);

        await fixture.Coordinator.SessionStartedAsync(CancellationToken.None);
        await fixture.Coordinator.SessionEndedAsync(CancellationToken.None);

        Assert.Null(fixture.Dropbox.Lock);
        Assert.Equal(1, fixture.Dropbox.DeleteLockCount);
        Assert.Equal(1, fixture.Dropbox.MetadataReadCount);
    }

    [Fact]
    public async Task Auto_sync_disabled_has_no_lock_traffic()
    {
        using var directory = new WheelSyncArchiveReaderTests.TemporaryDirectory();
        var fixture = Create(directory.Path, "r1", "local", autoSync: false);

        await fixture.Coordinator.SessionStartedAsync(CancellationToken.None);
        await fixture.Coordinator.SessionEndedAsync(CancellationToken.None);

        Assert.Equal(0, fixture.Dropbox.LockReadCount);
        Assert.Equal(0, fixture.Dropbox.WriteLockCount);
        Assert.Equal(0, fixture.Dropbox.DeleteLockCount);
    }

    [Fact]
    public async Task Uncertain_lock_read_and_failed_write_never_establish_delete_ownership()
    {
        using var directory = new WheelSyncArchiveReaderTests.TemporaryDirectory();
        var fixture = Create(directory.Path, "r1", "local");
        fixture.Dropbox.FailReads = true;
        await fixture.Coordinator.SessionStartedAsync(CancellationToken.None);
        await fixture.Coordinator.SessionEndedAsync(CancellationToken.None);
        Assert.Equal(0, fixture.Dropbox.WriteLockCount);
        Assert.Equal(0, fixture.Dropbox.DeleteLockCount);

        var second = Create(directory.Path, "r1", "local");
        second.Dropbox.FailWrites = true;
        await second.Coordinator.SessionStartedAsync(CancellationToken.None);
        await second.Coordinator.SessionEndedAsync(CancellationToken.None);
        Assert.Equal(1, second.Dropbox.WriteLockCount);
        Assert.Equal(0, second.Dropbox.DeleteLockCount);
    }

    private static DropboxSyncLock ForeignLock() => new("android-device", DateTimeOffset.UtcNow)
    {
        ServerModified = DateTimeOffset.UtcNow,
    };

    private static Fixture Create(string root, string? baselineRev, string? baselineHash, bool autoSync = true)
    {
        var paths = new DolphinSavePaths(root, Path.Combine(root, "RetroWFC"), Path.Combine(root, "Wii"), "1");
        var store = new MemoryStateStore(new SyncState("desktop-device", "account", paths.UserFolder, paths.SaveRoot, paths.WiiRoot, baselineRev, baselineHash, false, null));
        var source = Substitute.For<IDolphinSavePathSource>();
        source.TryResolve().Returns(paths);
        var oauth = Substitute.For<IDropboxOAuthService>();
        oauth.ConnectAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(("account", "person@example.com")));
        var settings = Substitute.For<ISettingsManager>();
        settings.Get<bool>(settings.CLOUD_AUTO_SYNC).Returns(autoSync);
        var writer = Substitute.For<IWheelSyncArchiveWriter>();
        writer.CreateAsync(paths, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            var path = Path.GetTempFileName();
            return Task.FromResult<ArchiveSnapshot?>(new ArchiveSnapshot(path, "local"));
        });
        var restore = new TrackingRestoreService();
        var api = new FakeDropbox();
        oauth.RestoreSessionAsync("account", Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        var coordinator = new CloudSyncCoordinator(oauth, api, source, store, writer,
            Substitute.For<IWheelSyncArchiveReader>(), restore, Substitute.For<ICloudSyncPrompt>(), settings);
        coordinator.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
        return new Fixture(coordinator, api, store, restore, oauth);
    }

    private sealed record Fixture(CloudSyncCoordinator Coordinator, FakeDropbox Dropbox, MemoryStateStore StateStore, TrackingRestoreService Restore, IDropboxOAuthService Oauth);

    private sealed class MemoryStateStore(SyncState current) : ICloudSyncStateStore
    {
        public SyncState? Current { get; private set; } = current;
        public PersistedAccountRead ReadPersistedAccount() => new(PersistedAccountStatus.Valid, Current?.AccountId);
        public SyncState ReadFor(string accountId, DolphinSavePaths paths) => Current!;
        public void Update(SyncState state) => Current = state;
        public void ClearAccount() => Current = Current! with { AccountId = null, Rev = null, LocalHash = null, SessionPending = false };
    }

    private sealed class TrackingRestoreService : ISaveRestoreService
    {
        public int RestoreCount { get; private set; }
        public Task RecoverAsync(DolphinSavePaths paths, CancellationToken ct) => Task.CompletedTask;
        public Task<string> RestoreAsync(ValidatedArchive archive, DolphinSavePaths paths, CancellationToken ct)
        {
            RestoreCount++;
            return Task.FromResult("restored");
        }
    }

    private sealed class FakeDropbox : IDropboxSyncApi
    {
        public CloudSaveMetadata? Save { get; set; }
        public DropboxSyncLock? Lock { get; set; }
        public bool FailReads { get; set; }
        public bool FailWrites { get; set; }
        public int MetadataReadCount { get; private set; }
        public int LockReadCount { get; private set; }
        public int WriteLockCount { get; private set; }
        public int DeleteLockCount { get; private set; }
        public int UploadCount { get; private set; }
        public int DownloadCount { get; private set; }

        public Task<CloudSaveMetadata?> GetSaveMetadataAsync(CancellationToken ct)
        {
            MetadataReadCount++;
            return Task.FromResult(Save);
        }
        public Task<CloudSaveMetadata> DownloadAsync(string rev, string destination, CancellationToken ct)
        {
            DownloadCount++;
            throw new InvalidOperationException("Unexpected pull in foreign-lock test.");
        }
        public Task<CloudSaveMetadata> UploadAsync(string source, string? expectedRev, CancellationToken ct)
        {
            UploadCount++;
            throw new InvalidOperationException("Unexpected push in foreign-lock test.");
        }
        public Task<DropboxSyncLock?> ReadLockAsync(CancellationToken ct)
        {
            LockReadCount++;
            if (FailReads) throw new IOException("lock read uncertain");
            return Task.FromResult(Lock);
        }
        public Task<bool> WriteLockAsync(DropboxSyncLock cloudLock, CancellationToken ct)
        {
            WriteLockCount++;
            if (FailWrites) return Task.FromResult(false);
            Lock = cloudLock with
            {
                StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(cloudLock.StartedAt.ToUnixTimeMilliseconds()),
                ServerModified = DateTimeOffset.UtcNow,
            };
            return Task.FromResult(true);
        }
        public Task DeleteLockAsync(CancellationToken ct)
        {
            DeleteLockCount++;
            Lock = null;
            return Task.CompletedTask;
        }
        public Task<DropboxSyncState?> ReadStateAsync(CancellationToken ct) => Task.FromResult<DropboxSyncState?>(null);
        public Task WriteStateAsync(DropboxSyncState state, CancellationToken ct) => Task.CompletedTask;
    }
}
