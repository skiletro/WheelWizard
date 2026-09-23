using NSubstitute;
using WheelWizard.Features.CloudSync;
using WheelWizard.Settings;
using Xunit;

namespace WheelWizard.Test.Features.CloudSync;

public sealed class CloudSyncCoordinatorTests
{
    [Fact]
    public async Task Both_changed_waits_for_choice_without_uploading()
    {
        var oauth = Substitute.For<IDropboxOAuthService>();
        var api = Substitute.For<IDropboxSyncApi>();
        var paths = new DolphinSavePaths("/dolphin", "/dolphin/RetroWFC", "/dolphin/Wii", "1");
        var source = Substitute.For<IDolphinSavePathSource>();
        source.TryResolve().Returns(paths);
        var store = Substitute.For<ICloudSyncStateStore>();
        store.ReadFor("account", paths).Returns(new SyncState("device", "account", paths.UserFolder, paths.SaveRoot, paths.WiiRoot, "r1", "old-local", false, null));
        var settings = Substitute.For<ISettingsManager>();
        settings.Get<bool>(settings.CLOUD_AUTO_SYNC).Returns(true);
        var writer = Substitute.For<IWheelSyncArchiveWriter>();
        var snapshotFile = Path.GetTempFileName();
        writer.CreateAsync(paths, Arg.Any<CancellationToken>()).Returns(Task.FromResult<ArchiveSnapshot?>(new ArchiveSnapshot(snapshotFile, "new-local")));
        var reader = Substitute.For<IWheelSyncArchiveReader>();
        var restore = Substitute.For<ISaveRestoreService>();
        var prompt = Substitute.For<ICloudSyncPrompt>();
        oauth.ConnectAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(("account", "user@example.com")));
        api.GetSaveMetadataAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<CloudSaveMetadata?>(new CloudSaveMetadata("r2", DateTimeOffset.UtcNow)));
        var coordinator = new CloudSyncCoordinator(oauth, api, source, store, writer, reader, restore, prompt, settings);
        try
        {
            await coordinator.ConnectAsync(CancellationToken.None);
            await coordinator.SyncNowAsync(CancellationToken.None);
            Assert.Equal(SyncUiStatus.Conflict, coordinator.State.Status);
            await api.DidNotReceive().UploadAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }
        finally { if (File.Exists(snapshotFile)) File.Delete(snapshotFile); }
    }

    [Fact]
    public async Task Upload_cas_conflict_rechecks_cloud_and_publishes_conflict()
    {
        var fixture = CreateFixture("r1", "old-local");
        fixture.Writer.CreateAsync(fixture.Paths, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            var file = Path.GetTempFileName();
            return Task.FromResult<ArchiveSnapshot?>(new ArchiveSnapshot(file, "new-local"));
        });
        fixture.Api.GetSaveMetadataAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CloudSaveMetadata?>(new CloudSaveMetadata("r1", DateTimeOffset.UtcNow)),
                Task.FromResult<CloudSaveMetadata?>(new CloudSaveMetadata("r2", DateTimeOffset.UtcNow)));
        fixture.Api.UploadAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<CloudSaveMetadata>>(_ => throw new DropboxConflictException("conflict"));

        await fixture.Coordinator.ConnectAsync(CancellationToken.None);
        await fixture.Coordinator.SyncNowAsync(CancellationToken.None);

        Assert.Equal(SyncUiStatus.Conflict, fixture.Coordinator.State.Status);
        await fixture.Api.Received(2).GetSaveMetadataAsync(Arg.Any<CancellationToken>());
        await fixture.Api.DidNotReceive().WriteStateAsync(Arg.Any<DropboxSyncState>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Manual_sync_queued_in_session_runs_after_exit_even_when_auto_sync_disabled()
    {
        var fixture = CreateFixture("r1", null, autoSync: false);
        fixture.Writer.CreateAsync(fixture.Paths, Arg.Any<CancellationToken>()).Returns(Task.FromResult<ArchiveSnapshot?>(null));
        fixture.Api.GetSaveMetadataAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<CloudSaveMetadata?>(null));
        fixture.Api.DeleteLockAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        await fixture.Coordinator.ConnectAsync(CancellationToken.None);
        await fixture.Coordinator.SessionStartedAsync(CancellationToken.None);

        await fixture.Coordinator.SyncNowAsync(CancellationToken.None);
        Assert.Equal(SyncUiStatus.QueuedForExit, fixture.Coordinator.State.Status);
        await fixture.Api.DidNotReceive().GetSaveMetadataAsync(Arg.Any<CancellationToken>());

        await fixture.Coordinator.SessionEndedAsync(CancellationToken.None);

        await fixture.Api.Received(1).GetSaveMetadataAsync(Arg.Any<CancellationToken>());
        Assert.Equal(SyncUiStatus.Idle, fixture.Coordinator.State.Status);
    }

    [Fact]
    public async Task Use_cloud_choice_is_deferred_until_session_exit()
    {
        var fixture = CreateFixture("r1", "old-local");
        fixture.Writer.CreateAsync(fixture.Paths, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            var file = Path.GetTempFileName();
            return Task.FromResult<ArchiveSnapshot?>(new ArchiveSnapshot(file, "new-local"));
        });
        fixture.Api.GetSaveMetadataAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<CloudSaveMetadata?>(new CloudSaveMetadata("r2", DateTimeOffset.UtcNow)));
        fixture.Api.DownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            File.WriteAllBytes(call.ArgAt<string>(1), [1, 2, 3]);
            return Task.FromResult(new CloudSaveMetadata("r2", DateTimeOffset.UtcNow));
        });
        fixture.Reader.ValidateAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns<Task<ValidatedArchive>>(_ => throw new InvalidDataException("bad archive"));

        await fixture.Coordinator.ConnectAsync(CancellationToken.None);
        await fixture.Coordinator.SyncNowAsync(CancellationToken.None);
        Assert.Equal(SyncUiStatus.Conflict, fixture.Coordinator.State.Status);
        await fixture.Coordinator.SessionStartedAsync(CancellationToken.None);

        await fixture.Coordinator.ResolveAsync(ConflictChoice.UseCloud, CancellationToken.None);
        await fixture.Api.DidNotReceive().DownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Coordinator.SessionEndedAsync(CancellationToken.None));
        await fixture.Api.Received(1).DownloadAsync("r2", Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.Restore.DidNotReceive().RestoreAsync(Arg.Any<ValidatedArchive>(), Arg.Any<DolphinSavePaths>(), Arg.Any<CancellationToken>());
    }

    private static CoordinatorFixture CreateFixture(string? baselineRev, string? baselineHash, bool autoSync = true)
    {
        var oauth = Substitute.For<IDropboxOAuthService>();
        oauth.ConnectAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(("account", "user@example.com")));
        var api = Substitute.For<IDropboxSyncApi>();
        var paths = new DolphinSavePaths("/dolphin", "/dolphin/RetroWFC", "/dolphin/Wii", "1");
        var source = Substitute.For<IDolphinSavePathSource>();
        source.TryResolve().Returns(paths);
        var store = Substitute.For<ICloudSyncStateStore>();
        store.ReadFor("account", paths).Returns(new SyncState("device", "account", paths.UserFolder, paths.SaveRoot, paths.WiiRoot, baselineRev, baselineHash, false, null));
        var settings = Substitute.For<ISettingsManager>();
        settings.Get<bool>(settings.CLOUD_AUTO_SYNC).Returns(autoSync);
        var writer = Substitute.For<IWheelSyncArchiveWriter>();
        var reader = Substitute.For<IWheelSyncArchiveReader>();
        var restore = Substitute.For<ISaveRestoreService>();
        var prompt = Substitute.For<ICloudSyncPrompt>();
        var coordinator = new CloudSyncCoordinator(oauth, api, source, store, writer, reader, restore, prompt, settings);
        return new CoordinatorFixture(paths, api, writer, reader, restore, coordinator);
    }

    private sealed record CoordinatorFixture(DolphinSavePaths Paths, IDropboxSyncApi Api, IWheelSyncArchiveWriter Writer,
        IWheelSyncArchiveReader Reader, ISaveRestoreService Restore, CloudSyncCoordinator Coordinator);

    [Fact]
    public async Task Concurrent_sync_now_calls_are_serialized()
    {
        var oauth = Substitute.For<IDropboxOAuthService>();
        var api = Substitute.For<IDropboxSyncApi>();
        var paths = new DolphinSavePaths("/dolphin", "/dolphin/RetroWFC", "/dolphin/Wii", "1");
        var source = Substitute.For<IDolphinSavePathSource>();
        source.TryResolve().Returns(paths);
        var store = Substitute.For<ICloudSyncStateStore>();
        store.ReadFor("account", paths).Returns(new SyncState("device", "account", paths.UserFolder, paths.SaveRoot, paths.WiiRoot, null, null, false, null));
        var settings = Substitute.For<ISettingsManager>();
        settings.Get<bool>(settings.CLOUD_AUTO_SYNC).Returns(true);
        var writer = Substitute.For<IWheelSyncArchiveWriter>();
        var reader = Substitute.For<IWheelSyncArchiveReader>();
        var restore = Substitute.For<ISaveRestoreService>();
        var prompt = Substitute.For<ICloudSyncPrompt>();
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;
        oauth.ConnectAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(("account", "user@example.com")));
        api.GetSaveMetadataAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            var current = Interlocked.Increment(ref active);
            maximumActive = Math.Max(maximumActive, current);
            started.TrySetResult();
            return inFlight.Task.ContinueWith<CloudSaveMetadata?>(task =>
            {
                Interlocked.Decrement(ref active);
                return null;
            }, TaskScheduler.Default);
        });
        var coordinator = new CloudSyncCoordinator(oauth, api, source, store, writer, reader, restore, prompt, settings);
        await coordinator.ConnectAsync(CancellationToken.None);

        var first = coordinator.SyncNowAsync(CancellationToken.None);
        await started.Task;
        var second = coordinator.SyncNowAsync(CancellationToken.None);
        inFlight.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, maximumActive);
        Assert.Equal(SyncUiStatus.Idle, coordinator.State.Status);
    }
}
