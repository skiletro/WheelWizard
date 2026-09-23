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

    [Fact]
    public async Task Dismissing_conflict_prompt_cancels_launch_without_resolving_conflict()
    {
        var fixture = CreateFixture("r1", "old-local");
        fixture.Writer.CreateAsync(fixture.Paths, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<ArchiveSnapshot?>(Snapshot("new-local")));
        fixture.Api.GetSaveMetadataAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<CloudSaveMetadata?>(new CloudSaveMetadata("r2", DateTimeOffset.UtcNow)));
        fixture.Prompt.ChooseAsync(Arg.Any<SyncUiState>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(ConflictChoice.Cancel));

        await fixture.Coordinator.ConnectAsync(CancellationToken.None);
        var decision = await fixture.Coordinator.PrepareLaunchAsync(CancellationToken.None);

        Assert.Equal(LaunchSyncDecision.Cancelled, decision);
        Assert.Equal(SyncUiStatus.Conflict, fixture.Coordinator.State.Status);
        await fixture.Api.DidNotReceive().UploadAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await fixture.Api.DidNotReceive().DownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Account_change_rebinds_baseline_before_sync()
    {
        var fixture = CreateFixture("r1", "old-local");
        fixture.Oauth.ConnectAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromResult(("account", "first@example.com")),
            Task.FromResult(("other-account", "second@example.com")));
        fixture.StateStore.ReadFor("other-account", fixture.Paths).Returns(new SyncState("device", "other-account", fixture.Paths.UserFolder, fixture.Paths.SaveRoot, fixture.Paths.WiiRoot, null, null, false, null));
        fixture.Writer.CreateAsync(fixture.Paths, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<ArchiveSnapshot?>(Snapshot("local")));
        fixture.Api.GetSaveMetadataAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<CloudSaveMetadata?>(new CloudSaveMetadata("r2", DateTimeOffset.UtcNow)));

        await fixture.Coordinator.ConnectAsync(CancellationToken.None);
        await fixture.Coordinator.ConnectAsync(CancellationToken.None);
        await fixture.Coordinator.SyncNowAsync(CancellationToken.None);

        fixture.StateStore.Received(1).ReadFor("other-account", fixture.Paths);
        Assert.Equal("other-account", fixture.Coordinator.State.AccountId);
        Assert.Equal(SyncUiStatus.CloudFound, fixture.Coordinator.State.Status);
        await fixture.Api.DidNotReceive().UploadAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fresh_device_with_local_and_cloud_save_waits_for_choice()
    {
        var fixture = CreateFixture(null, null);
        fixture.Writer.CreateAsync(fixture.Paths, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<ArchiveSnapshot?>(Snapshot("local")));
        fixture.Api.GetSaveMetadataAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<CloudSaveMetadata?>(new CloudSaveMetadata("r2", DateTimeOffset.UtcNow)));

        await fixture.Coordinator.ConnectAsync(CancellationToken.None);
        await fixture.Coordinator.SyncNowAsync(CancellationToken.None);

        Assert.Equal(SyncUiStatus.CloudFound, fixture.Coordinator.State.Status);
        await fixture.Api.DidNotReceive().UploadAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Local_only_change_uploads_with_current_revision()
    {
        var fixture = CreateFixture("r1", "old-local");
        fixture.Writer.CreateAsync(fixture.Paths, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<ArchiveSnapshot?>(Snapshot("new-local")));
        fixture.Api.GetSaveMetadataAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<CloudSaveMetadata?>(new CloudSaveMetadata("r1", DateTimeOffset.UtcNow)));
        fixture.Api.UploadAsync(Arg.Any<string>(), "r1", Arg.Any<CancellationToken>()).Returns(Task.FromResult(new CloudSaveMetadata("r2", DateTimeOffset.UtcNow)));

        await fixture.Coordinator.ConnectAsync(CancellationToken.None);
        await fixture.Coordinator.SyncNowAsync(CancellationToken.None);

        await fixture.Api.Received(1).UploadAsync(Arg.Any<string>(), "r1", Arg.Any<CancellationToken>());
        Assert.Equal(SyncUiStatus.Idle, fixture.Coordinator.State.Status);
    }

    [Fact]
    public async Task Remote_only_change_restores_cloud_revision()
    {
        var fixture = CreateFixture("r1", "old-local");
        fixture.Writer.CreateAsync(fixture.Paths, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<ArchiveSnapshot?>(Snapshot("old-local")));
        fixture.Api.GetSaveMetadataAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<CloudSaveMetadata?>(new CloudSaveMetadata("r2", DateTimeOffset.UtcNow)));
        fixture.Api.DownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            File.WriteAllBytes(call.ArgAt<string>(1), [1, 2, 3]);
            return Task.FromResult(new CloudSaveMetadata("r2", DateTimeOffset.UtcNow));
        });
        fixture.Reader.ValidateAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<ValidatedArchive>(null!));
        fixture.Restore.RestoreAsync(Arg.Any<ValidatedArchive>(), fixture.Paths, Arg.Any<CancellationToken>()).Returns("restored-hash");

        await fixture.Coordinator.ConnectAsync(CancellationToken.None);
        await fixture.Coordinator.SyncNowAsync(CancellationToken.None);

        await fixture.Api.Received(1).DownloadAsync("r2", Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.Restore.Received(1).RestoreAsync(Arg.Any<ValidatedArchive>(), fixture.Paths, Arg.Any<CancellationToken>());
        fixture.StateStore.Received().Update(Arg.Is<SyncState>(state => state.Rev == "r2" && state.LocalHash == "restored-hash"));
    }

    [Fact]
    public async Task Missing_cloud_and_local_save_first_uploads()
    {
        var fixture = CreateFixture(null, null);
        fixture.Writer.CreateAsync(fixture.Paths, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<ArchiveSnapshot?>(Snapshot("local")));
        fixture.Api.GetSaveMetadataAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<CloudSaveMetadata?>(null));
        fixture.Api.UploadAsync(Arg.Any<string>(), Arg.Is<string?>(rev => rev == null), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new CloudSaveMetadata("r1", DateTimeOffset.UtcNow)));

        await fixture.Coordinator.ConnectAsync(CancellationToken.None);
        await fixture.Coordinator.SyncNowAsync(CancellationToken.None);

        await fixture.Api.Received(1).UploadAsync(Arg.Any<string>(), Arg.Is<string?>(rev => rev == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SkippedSettingsDoNotCausePush()
    {
        using var temp = new WheelSyncArchiveReaderTests.TemporaryDirectory();
        var paths = new DolphinSavePaths(temp.Path, Path.Combine(temp.Path, "RetroWFC"), Path.Combine(temp.Path, "Wii"), "6.4.0");
        Directory.CreateDirectory(paths.SaveRoot);
        Directory.CreateDirectory(paths.WiiRoot);
        var gatedLocal = Path.Combine(paths.WiiRoot, "shared2/Pulsar/RetroRewind6/RRGameSettings.pul");
        Directory.CreateDirectory(Path.GetDirectoryName(gatedLocal)!);
        await File.WriteAllBytesAsync(gatedLocal, [8]);
        var writer = new WheelSyncArchiveWriter();
        var initial = await writer.CreateAsync(paths, CancellationToken.None);
        Assert.NotNull(initial);
        var baselineHash = initial.Hash;
        File.Delete(initial.FilePath);

        var api = Substitute.For<IDropboxSyncApi>();
        api.GetSaveMetadataAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<CloudSaveMetadata?>(new CloudSaveMetadata("r2", DateTimeOffset.UtcNow)));
        var zip = WheelSyncArchiveReaderTests.CreateZip(("Wii/shared2/Pulsar/RetroRewind6/RRGameSettings.pul", [3]), manifestJson: "{\"type\":\"wheelwitch-save\",\"version\":2,\"rrVersion\":\"6.3.1\"}");
        var zipBytes = zip.ToArray();
        api.DownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            File.WriteAllBytes(call.ArgAt<string>(1), zipBytes);
            return Task.FromResult(new CloudSaveMetadata("r2", DateTimeOffset.UtcNow));
        });

        var source = Substitute.For<IDolphinSavePathSource>();
        source.TryResolve().Returns(paths);
        var store = Substitute.For<ICloudSyncStateStore>();
        store.ReadFor("account", paths).Returns(new SyncState("device", "account", paths.UserFolder, paths.SaveRoot, paths.WiiRoot, "r1", baselineHash, false, null));
        var settings = Substitute.For<ISettingsManager>();
        settings.Get<bool>(settings.CLOUD_AUTO_SYNC).Returns(true);
        var reader = new WheelSyncArchiveReader(Path.Combine(temp.Path, "stage"));
        var restore = new SaveRestoreService(writer, Path.Combine(temp.Path, "recovery"));
        var oauth = Substitute.For<IDropboxOAuthService>();
        oauth.ConnectAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(("account", "user@example.com")));
        var coordinator = new CloudSyncCoordinator(oauth, api, source, store, writer, reader, restore, Substitute.For<ICloudSyncPrompt>(), settings);

        await coordinator.ConnectAsync(CancellationToken.None);
        await coordinator.SyncNowAsync(CancellationToken.None);
        var effectiveHash = await writer.CreateAsync(paths, CancellationToken.None);
        Assert.NotNull(effectiveHash);
        File.Delete(effectiveHash.FilePath);
        await coordinator.SyncNowAsync(CancellationToken.None);

        Assert.Equal(new byte[] { 8 }, await File.ReadAllBytesAsync(gatedLocal));
        store.Received().Update(Arg.Is<SyncState>(state => state.Rev == "r2" && state.LocalHash == effectiveHash.Hash));
        await api.DidNotReceive().UploadAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Local_change_during_download_prevents_cloud_restore()
    {
        var fixture = CreateFixture("r1", "old-local");
        var snapshotCount = 0;
        fixture.Writer.CreateAsync(fixture.Paths, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<ArchiveSnapshot?>(Snapshot(Interlocked.Increment(ref snapshotCount) == 1 ? "old-local" : "changed")));
        fixture.Api.GetSaveMetadataAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<CloudSaveMetadata?>(new CloudSaveMetadata("r2", DateTimeOffset.UtcNow)));
        fixture.Api.DownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            File.WriteAllBytes(call.ArgAt<string>(1), [1, 2, 3]);
            return Task.FromResult(new CloudSaveMetadata("r2", DateTimeOffset.UtcNow));
        });
        fixture.Reader.ValidateAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<ValidatedArchive>(null!));

        await fixture.Coordinator.ConnectAsync(CancellationToken.None);
        await fixture.Coordinator.SyncNowAsync(CancellationToken.None);

        Assert.Equal(SyncUiStatus.Conflict, fixture.Coordinator.State.Status);
        await fixture.Restore.DidNotReceive().RestoreAsync(Arg.Any<ValidatedArchive>(), Arg.Any<DolphinSavePaths>(), Arg.Any<CancellationToken>());
        await fixture.Api.DidNotReceive().WriteStateAsync(Arg.Any<DropboxSyncState>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Path_change_rebinds_baseline_and_prompts_before_overwrite()
    {
        var fixture = CreateFixture("r1", "old-local");
        var newPaths = new DolphinSavePaths("/other-dolphin", "/other-dolphin/RetroWFC", "/other-dolphin/Wii", "1");
        fixture.Source.TryResolve().Returns(fixture.Paths, newPaths);
        fixture.StateStore.ReadFor("account", newPaths).Returns(new SyncState("device", "account", newPaths.UserFolder, newPaths.SaveRoot, newPaths.WiiRoot, null, null, false, null));
        fixture.Writer.CreateAsync(newPaths, Arg.Any<CancellationToken>()).Returns(Task.FromResult<ArchiveSnapshot?>(Snapshot("new-root-content")));
        fixture.Api.GetSaveMetadataAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<CloudSaveMetadata?>(new CloudSaveMetadata("r2", DateTimeOffset.UtcNow)));

        await fixture.Coordinator.ConnectAsync(CancellationToken.None);
        await fixture.Coordinator.SyncNowAsync(CancellationToken.None);

        Assert.Equal(SyncUiStatus.CloudFound, fixture.Coordinator.State.Status);
        fixture.StateStore.Received(1).ReadFor("account", newPaths);
        await fixture.Restore.DidNotReceive().RestoreAsync(Arg.Any<ValidatedArchive>(), Arg.Any<DolphinSavePaths>(), Arg.Any<CancellationToken>());
        await fixture.Api.DidNotReceive().UploadAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Path_change_before_upload_stops_old_root_upload()
    {
        var fixture = CreateFixture("r1", "old-local");
        var newPaths = new DolphinSavePaths("/other-dolphin", "/other-dolphin/RetroWFC", "/other-dolphin/Wii", "1");
        fixture.Source.TryResolve().Returns(fixture.Paths, fixture.Paths, newPaths);
        fixture.StateStore.ReadFor("account", newPaths).Returns(new SyncState("device", "account", newPaths.UserFolder, newPaths.SaveRoot, newPaths.WiiRoot, null, null, false, null));
        fixture.Writer.CreateAsync(fixture.Paths, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<ArchiveSnapshot?>(Snapshot("new-local")));
        fixture.Api.GetSaveMetadataAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<CloudSaveMetadata?>(new CloudSaveMetadata("r1", DateTimeOffset.UtcNow)));

        await fixture.Coordinator.ConnectAsync(CancellationToken.None);
        await fixture.Coordinator.SyncNowAsync(CancellationToken.None);

        Assert.Equal(SyncUiStatus.CloudFound, fixture.Coordinator.State.Status);
        fixture.StateStore.Received(1).ReadFor("account", newPaths);
        await fixture.Api.DidNotReceive().UploadAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Corrupt_zip_does_not_restore_or_advance_baseline()
    {
        using var temp = new WheelSyncArchiveReaderTests.TemporaryDirectory();
        var paths = new DolphinSavePaths(temp.Path, Path.Combine(temp.Path, "RetroWFC"), Path.Combine(temp.Path, "Wii"), "1");
        var localFile = Path.Combine(paths.SaveRoot, "RMCP/rksys.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(localFile)!);
        await File.WriteAllBytesAsync(localFile, [7, 8]);
        var writer = new WheelSyncArchiveWriter();
        var localSnapshot = await writer.CreateAsync(paths, CancellationToken.None);
        Assert.NotNull(localSnapshot);
        var localHash = localSnapshot.Hash;
        File.Delete(localSnapshot.FilePath);

        var api = Substitute.For<IDropboxSyncApi>();
        api.GetSaveMetadataAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<CloudSaveMetadata?>(new CloudSaveMetadata("r2", DateTimeOffset.UtcNow)));
        api.DownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            File.WriteAllBytes(call.ArgAt<string>(1), [1, 2, 3]);
            return Task.FromResult(new CloudSaveMetadata("r2", DateTimeOffset.UtcNow));
        });
        var source = Substitute.For<IDolphinSavePathSource>();
        source.TryResolve().Returns(paths);
        var store = Substitute.For<ICloudSyncStateStore>();
        store.ReadFor("account", paths).Returns(new SyncState("device", "account", paths.UserFolder, paths.SaveRoot, paths.WiiRoot, "r1", localHash, false, null));
        var settings = Substitute.For<ISettingsManager>();
        settings.Get<bool>(settings.CLOUD_AUTO_SYNC).Returns(true);
        var oauth = Substitute.For<IDropboxOAuthService>();
        oauth.ConnectAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(("account", "user@example.com")));
        var reader = new WheelSyncArchiveReader(Path.Combine(temp.Path, "stage"));
        var restore = new SaveRestoreService(writer, Path.Combine(temp.Path, "recovery"));
        var coordinator = new CloudSyncCoordinator(oauth, api, source, store, writer, reader, restore, Substitute.For<ICloudSyncPrompt>(), settings);

        await coordinator.ConnectAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.SyncNowAsync(CancellationToken.None));

        Assert.Equal(new byte[] { 7, 8 }, await File.ReadAllBytesAsync(localFile));
        store.DidNotReceive().Update(Arg.Is<SyncState>(state => state.Rev == "r2"));
    }

    [Fact]
    public async Task State_json_write_failure_does_not_fail_successful_upload()
    {
        var fixture = CreateFixture(null, null);
        fixture.Writer.CreateAsync(fixture.Paths, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<ArchiveSnapshot?>(Snapshot("local")));
        fixture.Api.GetSaveMetadataAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<CloudSaveMetadata?>(null));
        fixture.Api.UploadAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new CloudSaveMetadata("r1", DateTimeOffset.UtcNow)));
        fixture.Api.WriteStateAsync(Arg.Any<DropboxSyncState>(), Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new IOException("state write failed"));

        await fixture.Coordinator.ConnectAsync(CancellationToken.None);
        await fixture.Coordinator.SyncNowAsync(CancellationToken.None);

        Assert.Equal(SyncUiStatus.Idle, fixture.Coordinator.State.Status);
        fixture.StateStore.Received().Update(Arg.Is<SyncState>(state => state.Rev == "r1" && state.LocalHash == "local"));
    }

    [Fact]
    public async Task Metadata_forbidden_is_not_cloud_missing_or_uploadable()
    {
        var fixture = CreateFixture("r1", "old-local");
        fixture.Writer.CreateAsync(fixture.Paths, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<ArchiveSnapshot?>(Snapshot("new-local")));
        fixture.Api.GetSaveMetadataAsync(Arg.Any<CancellationToken>()).Returns<Task<CloudSaveMetadata?>>(_ => throw new DropboxAccessException("forbidden", System.Net.HttpStatusCode.Forbidden));

        await fixture.Coordinator.ConnectAsync(CancellationToken.None);
        await Assert.ThrowsAsync<DropboxAccessException>(() => fixture.Coordinator.SyncNowAsync(CancellationToken.None));

        Assert.Equal(SyncUiStatus.Error, fixture.Coordinator.State.Status);
        await fixture.Api.DidNotReceive().UploadAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Metadata_timeout_auto_sync_is_offline_error_without_upload()
    {
        var fixture = CreateFixture("r1", "old-local");
        fixture.Writer.CreateAsync(fixture.Paths, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<ArchiveSnapshot?>(Snapshot("new-local")));
        fixture.Api.GetSaveMetadataAsync(Arg.Any<CancellationToken>()).Returns<Task<CloudSaveMetadata?>>(_ => throw new TimeoutException("metadata timeout"));

        await fixture.Coordinator.ConnectAsync(CancellationToken.None);
        await fixture.Coordinator.AutoSyncAsync(CancellationToken.None);

        Assert.Equal(SyncUiStatus.Error, fixture.Coordinator.State.Status);
        await fixture.Api.DidNotReceive().UploadAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Metadata_timeout_manual_sync_reports_error_without_upload()
    {
        var fixture = CreateFixture("r1", "old-local");
        fixture.Api.GetSaveMetadataAsync(Arg.Any<CancellationToken>()).Returns<Task<CloudSaveMetadata?>>(_ => throw new TimeoutException("metadata timeout"));

        await fixture.Coordinator.ConnectAsync(CancellationToken.None);
        await Assert.ThrowsAsync<TimeoutException>(() => fixture.Coordinator.SyncNowAsync(CancellationToken.None));

        Assert.Equal(SyncUiStatus.Error, fixture.Coordinator.State.Status);
        await fixture.Api.DidNotReceive().UploadAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Prelaunch_play_without_sync_choice_allows_launch_without_restore_or_upload()
    {
        var fixture = CreateFixture(null, null);
        fixture.Writer.CreateAsync(fixture.Paths, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<ArchiveSnapshot?>(Snapshot("local")));
        fixture.Api.GetSaveMetadataAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<CloudSaveMetadata?>(new CloudSaveMetadata("r1", DateTimeOffset.UtcNow)));
        fixture.Prompt.ChooseAsync(Arg.Any<SyncUiState>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(ConflictChoice.PlayWithoutSync));
        await fixture.Coordinator.ConnectAsync(CancellationToken.None);

        var decision = await fixture.Coordinator.PrepareLaunchAsync(CancellationToken.None);

        Assert.Equal(LaunchSyncDecision.ProceedWithoutSync, decision);
        Assert.Equal(SyncUiStatus.Idle, fixture.Coordinator.State.Status);
        await fixture.Api.DidNotReceive().UploadAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await fixture.Api.DidNotReceive().DownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.Restore.DidNotReceive().RestoreAsync(Arg.Any<ValidatedArchive>(), Arg.Any<DolphinSavePaths>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Confirmed_game_exit_runs_exactly_one_post_exit_sync()
    {
        var fixture = CreateFixture("r1", "local");
        fixture.Writer.CreateAsync(fixture.Paths, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<ArchiveSnapshot?>(Snapshot("local")));
        fixture.Api.GetSaveMetadataAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<CloudSaveMetadata?>(new CloudSaveMetadata("r1", DateTimeOffset.UtcNow)));
        await fixture.Coordinator.ConnectAsync(CancellationToken.None);
        await fixture.Coordinator.SessionStartedAsync(CancellationToken.None);

        await fixture.Coordinator.SessionEndedAsync(CancellationToken.None);

        await fixture.Api.Received(1).GetSaveMetadataAsync(Arg.Any<CancellationToken>());
        Assert.Equal(SyncUiStatus.Idle, fixture.Coordinator.State.Status);
    }

    [Fact]
    public async Task Focus_reconciliation_while_game_active_does_not_pull()
    {
        var fixture = CreateFixture("r1", "local");
        fixture.StateStore.ReadFor("account", fixture.Paths).Returns(new SyncState("device", "account", fixture.Paths.UserFolder, fixture.Paths.SaveRoot, fixture.Paths.WiiRoot, "r1", "local", true, null));
        await fixture.Coordinator.ConnectAsync(CancellationToken.None);

        await fixture.Coordinator.ReconcileSessionAsync(dolphinConfirmedRunning: true, CancellationToken.None);

        Assert.Equal(SyncUiStatus.QueuedForExit, fixture.Coordinator.State.Status);
        await fixture.Api.DidNotReceive().GetSaveMetadataAsync(Arg.Any<CancellationToken>());
        await fixture.Restore.DidNotReceive().RestoreAsync(Arg.Any<ValidatedArchive>(), Arg.Any<DolphinSavePaths>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Launch_failure_clears_pending_marker_without_post_exit_sync()
    {
        var fixture = CreateFixture("r1", "old-local");
        await fixture.Coordinator.ConnectAsync(CancellationToken.None);

        await fixture.Coordinator.SessionStartedAsync(CancellationToken.None);
        await fixture.Coordinator.SessionLaunchFailedAsync(CancellationToken.None);

        fixture.StateStore.Received().Update(Arg.Is<SyncState>(state => !state.SessionPending));
        await fixture.Api.DidNotReceive().GetSaveMetadataAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Startup_reconciliation_keeps_pending_session_when_dolphin_is_live()
    {
        var fixture = CreateFixture("r1", "old-local");
        fixture.StateStore.ReadFor("account", fixture.Paths).Returns(new SyncState("device", "account", fixture.Paths.UserFolder, fixture.Paths.SaveRoot, fixture.Paths.WiiRoot, "r1", "old-local", true, null));

        await fixture.Coordinator.ConnectAsync(CancellationToken.None);
        await fixture.Coordinator.ReconcileSessionAsync(dolphinConfirmedRunning: true, CancellationToken.None);

        Assert.Equal(SyncUiStatus.QueuedForExit, fixture.Coordinator.State.Status);
        fixture.StateStore.DidNotReceive().Update(Arg.Is<SyncState>(state => !state.SessionPending));
        await fixture.Api.DidNotReceive().GetSaveMetadataAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Startup_reconciliation_clears_pending_only_after_dolphin_confirmed_absent()
    {
        var fixture = CreateFixture("r1", "old-local");
        fixture.StateStore.ReadFor("account", fixture.Paths).Returns(new SyncState("device", "account", fixture.Paths.UserFolder, fixture.Paths.SaveRoot, fixture.Paths.WiiRoot, "r1", "old-local", true, null));
        fixture.Api.GetSaveMetadataAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<CloudSaveMetadata?>(new CloudSaveMetadata("r1", DateTimeOffset.UtcNow)));
        fixture.Writer.CreateAsync(fixture.Paths, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<ArchiveSnapshot?>(Snapshot("old-local")));

        await fixture.Coordinator.ConnectAsync(CancellationToken.None);
        await fixture.Coordinator.ReconcileSessionAsync(dolphinConfirmedRunning: false, CancellationToken.None);

        fixture.StateStore.Received().Update(Arg.Is<SyncState>(state => !state.SessionPending));
        await fixture.Api.Received(1).GetSaveMetadataAsync(Arg.Any<CancellationToken>());
        Assert.Equal(SyncUiStatus.Idle, fixture.Coordinator.State.Status);
    }

    private static ArchiveSnapshot Snapshot(string hash)
    {
        var path = Path.GetTempFileName();
        return new ArchiveSnapshot(path, hash);
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
        return new CoordinatorFixture(paths, oauth, source, store, api, writer, reader, restore, prompt, coordinator);
    }

    private sealed record CoordinatorFixture(DolphinSavePaths Paths, IDropboxOAuthService Oauth, IDolphinSavePathSource Source,
        ICloudSyncStateStore StateStore, IDropboxSyncApi Api, IWheelSyncArchiveWriter Writer, IWheelSyncArchiveReader Reader,
        ISaveRestoreService Restore, ICloudSyncPrompt Prompt, CloudSyncCoordinator Coordinator);

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
