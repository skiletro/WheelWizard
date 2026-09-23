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
