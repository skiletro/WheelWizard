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

    [Fact]
    public async Task Coordinator_failure_is_reported_as_error_state_without_escaping_ui_action()
    {
        var coordinator = Substitute.For<ICloudSyncCoordinator>();
        coordinator.State.Returns(new SyncUiState(SyncUiStatus.Idle));
        coordinator.SyncNowAsync(Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new IOException("network failure"));
        var settings = Substitute.For<ISettingsManager>();
        var viewModel = CreateViewModel(coordinator, settings);

        await viewModel.SyncNowAsync(CancellationToken.None);

        Assert.Equal(SyncUiStatus.Error, viewModel.State.Status);
        Assert.Equal("Cloud sync failed.", viewModel.StatusText);
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

    private static CloudSyncSettingsViewModel CreateViewModel(ICloudSyncCoordinator coordinator, ISettingsManager settings) =>
        new(coordinator, settings, new EmbeddedYamlLocalizationService(), true, true);
}
