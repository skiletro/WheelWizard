using NSubstitute;
using WheelWizard.Features.CloudSync;
using WheelWizard.Localization;
using WheelWizard.Settings;
using WheelWizard.Views.Pages.Settings;
using Xunit;

namespace WheelWizard.Test.Features.CloudSync;

public sealed class CloudSyncSettingsTests
{
    [Fact]
    public async Task Connect_action_calls_coordinator_and_disconnected_state_is_presented()
    {
        var coordinator = Substitute.For<ICloudSyncCoordinator>();
        coordinator.State.Returns(new SyncUiState(SyncUiStatus.Disconnected));
        var settings = Substitute.For<ISettingsManager>();
        settings.Get<bool>(settings.CLOUD_AUTO_SYNC).Returns(true);
        var viewModel = CreateViewModel(coordinator, settings);

        Assert.Equal("Dropbox is not connected.", viewModel.StatusText);
        await viewModel.ConnectAsync(CancellationToken.None);

        await coordinator.Received(1).ConnectAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(SyncUiStatus.Conflict)]
    [InlineData(SyncUiStatus.CloudFound)]
    public void Manual_sync_can_reopen_conflict_choices(SyncUiStatus status)
    {
        var coordinator = Substitute.For<ICloudSyncCoordinator>();
        coordinator.State.Returns(new SyncUiState(status));
        var settings = Substitute.For<ISettingsManager>();

        Assert.True(CreateViewModel(coordinator, settings).CanSync);
    }

    [Fact]
    public async Task Manual_sync_remains_available_when_auto_sync_is_disabled()
    {
        var coordinator = Substitute.For<ICloudSyncCoordinator>();
        coordinator.State.Returns(new SyncUiState(SyncUiStatus.Idle));
        var settings = Substitute.For<ISettingsManager>();
        settings.Get<bool>(settings.CLOUD_AUTO_SYNC).Returns(false);
        var viewModel = CreateViewModel(coordinator, settings);

        Assert.False(viewModel.AutoSyncEnabled);
        await viewModel.SyncNowAsync(CancellationToken.None);

        await coordinator.Received(1).SyncNowAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(SyncUiStatus.QueuedForExit, "Sync queued until Dolphin exits.")]
    [InlineData(SyncUiStatus.Conflict, "Cloud save conflicts with local saves.")]
    [InlineData(SyncUiStatus.CloudFound, "Cloud save found; choose an option.")]
    [InlineData(SyncUiStatus.ReconnectNeeded, "Reconnect Dropbox to continue syncing.")]
    [InlineData(SyncUiStatus.NotConfigured, "Dropbox sync is not configured.")]
    [InlineData(SyncUiStatus.Error, "Cloud sync failed.")]
    public void Status_maps_to_user_facing_english(SyncUiStatus status, string expected)
    {
        var coordinator = Substitute.For<ICloudSyncCoordinator>();
        coordinator.State.Returns(new SyncUiState(status));
        var settings = Substitute.For<ISettingsManager>();
        var viewModel = CreateViewModel(coordinator, settings);

        Assert.Equal(expected, viewModel.StatusText);
    }

    [Fact]
    public void Auto_sync_toggle_calls_coordinator()
    {
        var coordinator = Substitute.For<ICloudSyncCoordinator>();
        coordinator.State.Returns(new SyncUiState(SyncUiStatus.Disconnected));
        var settings = Substitute.For<ISettingsManager>();
        settings.Get<bool>(settings.CLOUD_AUTO_SYNC).Returns(true);
        var viewModel = CreateViewModel(coordinator, settings);

        viewModel.SetAutoSync(false);

        coordinator.Received(1).SetAutoSync(false);
    }

    [Theory]
    [InlineData(false, true, true, "Configure an existing Dolphin user folder in Location Settings before connecting Dropbox.")]
    [InlineData(true, false, true, "Dropbox sync is disabled because secure credential storage is unavailable on this platform. Tokens are never stored in plaintext.")]
    [InlineData(true, true, false, "Dropbox sync is unavailable in this build because its public app key is not configured.")]
    public void Disabled_cloud_controls_explain_why(bool hasPaths, bool secureStorage, bool hasAppKey, string reason)
    {
        var coordinator = Substitute.For<ICloudSyncCoordinator>();
        coordinator.State.Returns(new SyncUiState(SyncUiStatus.Disconnected));
        var settings = Substitute.For<ISettingsManager>();
        var viewModel = new CloudSyncSettingsViewModel(coordinator, settings, new EmbeddedYamlLocalizationService(), hasPaths, secureStorage, hasAppKey);

        Assert.Equal(reason, viewModel.AvailabilityText);
        Assert.False(viewModel.CanConnect);
    }

    [Fact]
    public void Missing_app_key_forces_not_configured_status()
    {
        var coordinator = Substitute.For<ICloudSyncCoordinator>();
        coordinator.State.Returns(new SyncUiState(SyncUiStatus.Disconnected));
        var settings = Substitute.For<ISettingsManager>();
        var viewModel = new CloudSyncSettingsViewModel(coordinator, settings, new EmbeddedYamlLocalizationService(), true, true, false);

        Assert.Equal("Dropbox sync is not configured.", viewModel.StatusText);
    }

    [Theory]
    [InlineData(ConflictChoice.UseCloud)]
    [InlineData(ConflictChoice.KeepLocal)]
    [InlineData(ConflictChoice.Cancel)]
    public async Task Manual_sync_prompts_and_resolves_explicit_choice(ConflictChoice choice)
    {
        var state = new SyncUiState(SyncUiStatus.Idle);
        var coordinator = Substitute.For<ICloudSyncCoordinator>();
        coordinator.State.Returns(_ => state);
        coordinator.SyncNowAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            state = new SyncUiState(SyncUiStatus.Conflict, "Cloud and local saves both changed.");
            coordinator.StateChanged += Raise.Event<EventHandler<SyncUiState>>(coordinator, state);
            return Task.CompletedTask;
        });
        var prompt = Substitute.For<ICloudSyncPrompt>();
        prompt.ChooseAsync(Arg.Any<SyncUiState>(), Arg.Any<CancellationToken>()).Returns(choice);
        var settings = Substitute.For<ISettingsManager>();
        var viewModel = CreateViewModel(coordinator, settings, prompt);

        await viewModel.SyncNowAsync(CancellationToken.None);

        await prompt.Received(1).ChooseAsync(Arg.Is<SyncUiState>(s => s.Status == SyncUiStatus.Conflict), Arg.Any<CancellationToken>());
        if (choice == ConflictChoice.Cancel)
            await coordinator.DidNotReceive().ResolveAsync(Arg.Any<ConflictChoice>(), Arg.Any<CancellationToken>());
        else
            await coordinator.Received(1).ResolveAsync(choice, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Manual_sync_does_not_prompt_when_queued_for_live_game()
    {
        var state = new SyncUiState(SyncUiStatus.QueuedForExit, "Sync queued until Dolphin exits.");
        var coordinator = Substitute.For<ICloudSyncCoordinator>();
        coordinator.State.Returns(_ => state);
        coordinator.SyncNowAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            coordinator.StateChanged += Raise.Event<EventHandler<SyncUiState>>(coordinator, state);
            return Task.CompletedTask;
        });
        var prompt = Substitute.For<ICloudSyncPrompt>();
        var viewModel = CreateViewModel(coordinator, Substitute.For<ISettingsManager>(), prompt);

        await viewModel.SyncNowAsync(CancellationToken.None);

        await prompt.DidNotReceive().ChooseAsync(Arg.Any<SyncUiState>(), Arg.Any<CancellationToken>());
        Assert.Equal(SyncUiStatus.QueuedForExit, viewModel.State.Status);
    }

    [Theory]
    [InlineData("Dropbox is offline; retry when online.")]
    [InlineData("Dropbox denied access (403).")]
    [InlineData("Cloud save archive is corrupt and was not restored.")]
    public async Task Coordinator_published_diagnostic_is_preserved_when_manual_action_throws(string message)
    {
        var state = new SyncUiState(SyncUiStatus.Idle);
        var coordinator = Substitute.For<ICloudSyncCoordinator>();
        coordinator.State.Returns(_ => state);
        coordinator.SyncNowAsync(Arg.Any<CancellationToken>()).Returns<Task>(_ =>
        {
            state = new SyncUiState(SyncUiStatus.Error, message);
            coordinator.StateChanged += Raise.Event<EventHandler<SyncUiState>>(coordinator, state);
            throw new IOException("network failure");
        });
        var viewModel = CreateViewModel(coordinator, Substitute.For<ISettingsManager>());

        await viewModel.SyncNowAsync(CancellationToken.None);

        Assert.Equal(message, viewModel.StatusText);
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("403")]
    [InlineData("corrupt-zip")]
    [InlineData("refresh-rejected")]
    public async Task Manual_settings_sync_shows_coordinator_diagnostic(string failure)
    {
        using var directory = new WheelSyncArchiveReaderTests.TemporaryDirectory();
        var paths = new DolphinSavePaths(directory.Path, Path.Combine(directory.Path, "RetroWFC"), Path.Combine(directory.Path, "Wii"), "1");
        var source = Substitute.For<IDolphinSavePathSource>();
        source.TryResolve().Returns(paths);
        var stateStore = Substitute.For<ICloudSyncStateStore>();
        stateStore.ReadFor("account", paths).Returns(new SyncState("device", "account", paths.UserFolder, paths.SaveRoot, paths.WiiRoot, "r1", "local", false, null));
        var oauth = Substitute.For<IDropboxOAuthService>();
        oauth.ConnectAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(("account", "user@example.com")));
        var api = Substitute.For<IDropboxSyncApi>();
        var expectedMessage = failure switch
        {
            "offline" => "network unavailable",
            "403" => "Dropbox access denied (403)",
            "corrupt-zip" => "Invalid save archive: corrupt ZIP",
            _ => "Dropbox refresh token rejected; reconnect."
        };
        if (failure == "corrupt-zip")
        {
            api.GetSaveMetadataAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<CloudSaveMetadata?>(new CloudSaveMetadata("r2", DateTimeOffset.UtcNow)));
            api.DownloadAsync("r2", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                File.WriteAllBytes(call.ArgAt<string>(1), [1]);
                return Task.FromResult(new CloudSaveMetadata("r2", DateTimeOffset.UtcNow));
            });
        }
        else
        {
            Exception failureException = failure switch
            {
                "403" => new DropboxAccessException(expectedMessage, System.Net.HttpStatusCode.Forbidden),
                "refresh-rejected" => new DropboxReconnectRequiredException(expectedMessage),
                _ => new IOException(expectedMessage)
            };
            api.GetSaveMetadataAsync(Arg.Any<CancellationToken>()).Returns<Task<CloudSaveMetadata?>>(_ => throw failureException);
        }
        var snapshotFile = Path.Combine(directory.Path, "snapshot.zip");
        File.WriteAllBytes(snapshotFile, [1]);
        var writer = Substitute.For<IWheelSyncArchiveWriter>();
        writer.CreateAsync(paths, Arg.Any<CancellationToken>()).Returns(Task.FromResult<ArchiveSnapshot?>(new ArchiveSnapshot(snapshotFile, "local")));
        var reader = Substitute.For<IWheelSyncArchiveReader>();
        reader.ValidateAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns<Task<ValidatedArchive>>(_ => throw new InvalidDataException(expectedMessage));
        var settings = Substitute.For<ISettingsManager>();
        settings.Get<bool>(settings.CLOUD_AUTO_SYNC).Returns(true);
        var coordinator = new CloudSyncCoordinator(oauth, api, source, stateStore, writer, reader,
            Substitute.For<ISaveRestoreService>(), Substitute.For<ICloudSyncPrompt>(), settings);
        var viewModel = CreateViewModel(coordinator, settings);
        try
        {
            await coordinator.ConnectAsync(CancellationToken.None);
            await viewModel.SyncNowAsync(CancellationToken.None);

            Assert.Equal(expectedMessage, viewModel.StatusText);
            if (failure == "refresh-rejected")
                Assert.Equal(SyncUiStatus.ReconnectNeeded, viewModel.State.Status);
            else
                Assert.Equal(SyncUiStatus.Error, viewModel.State.Status);
        }
        finally
        {
            viewModel.Dispose();
        }
    }

    [Fact]
    public async Task Unpublished_manual_failure_uses_safe_fallback()
    {
        var coordinator = Substitute.For<ICloudSyncCoordinator>();
        coordinator.State.Returns(new SyncUiState(SyncUiStatus.Idle));
        coordinator.SyncNowAsync(Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new IOException("secret-token-value"));
        var viewModel = CreateViewModel(coordinator, Substitute.For<ISettingsManager>());

        await viewModel.SyncNowAsync(CancellationToken.None);

        Assert.Equal("Cloud sync failed.", viewModel.StatusText);
        Assert.DoesNotContain("secret-token-value", viewModel.StatusText);
    }

    [Fact]
    public async Task Rejected_refresh_is_classified_as_reconnect_needed()
    {
        var coordinator = Substitute.For<ICloudSyncCoordinator>();
        coordinator.State.Returns(new SyncUiState(SyncUiStatus.Idle));
        coordinator.SyncNowAsync(Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new DropboxReconnectRequiredException("Dropbox refresh token rejected; reconnect."));
        var viewModel = CreateViewModel(coordinator, Substitute.For<ISettingsManager>());

        await viewModel.SyncNowAsync(CancellationToken.None);

        Assert.Equal(SyncUiStatus.ReconnectNeeded, viewModel.State.Status);
        Assert.Equal(new EmbeddedYamlLocalizationService().Translate("status.cloud_sync_reconnect"), viewModel.StatusText);
    }

    [Fact]
    public void Cloud_sync_strings_fall_back_to_english_for_other_languages()
    {
        var localization = new EmbeddedYamlLocalizationService();
        foreach (var key in CloudSyncSettingsViewModel.LocalizationKeys)
        {
            var english = localization.TranslateForLanguage(key, "en");
            Assert.NotEqual(key, english);
            Assert.Equal(english, localization.TranslateForLanguage(key, "fr"));
        }

        Assert.Equal("Sync now", localization.TranslateForLanguage("action.cloud_sync_now", "fr"));
    }

    private static CloudSyncSettingsViewModel CreateViewModel(ICloudSyncCoordinator coordinator, ISettingsManager settings, ICloudSyncPrompt? prompt = null) =>
        new(coordinator, settings, new EmbeddedYamlLocalizationService(), true, true, prompt: prompt);
}
