using System.ComponentModel;
using Avalonia.Threading;
using WheelWizard.Features.CloudSync;
using WheelWizard.Localization;
using WheelWizard.Settings;

namespace WheelWizard.Views.Pages.Settings;

public sealed class CloudSyncSettingsViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly IReadOnlyDictionary<SyncUiStatus, string> StatusKeys = new Dictionary<SyncUiStatus, string>
    {
        [SyncUiStatus.NotConfigured] = "status.cloud_sync_not_configured",
        [SyncUiStatus.Disconnected] = "status.cloud_sync_disconnected",
        [SyncUiStatus.Idle] = "status.cloud_sync_idle",
        [SyncUiStatus.Busy] = "status.cloud_sync_busy",
        [SyncUiStatus.Conflict] = "status.cloud_sync_conflict",
        [SyncUiStatus.CloudFound] = "status.cloud_sync_cloud_found",
        [SyncUiStatus.QueuedForExit] = "status.cloud_sync_queued",
        [SyncUiStatus.ReconnectNeeded] = "status.cloud_sync_reconnect",
        [SyncUiStatus.Error] = "status.cloud_sync_error",
    };

    public static IReadOnlyCollection<string> LocalizationKeys { get; } =
    [
        "action.cloud_sync_connect", "action.cloud_sync_disconnect", "action.cloud_sync_now",
        "option.cloud_auto_sync", "status.cloud_sync_not_configured", "status.cloud_sync_disconnected",
        "status.cloud_sync_idle", "status.cloud_sync_busy", "status.cloud_sync_conflict",
        "status.cloud_sync_cloud_found", "status.cloud_sync_queued", "status.cloud_sync_reconnect",
        "status.cloud_sync_error", "helper_text.cloud_sync_path_unavailable",
        "helper_text.cloud_sync_secure_storage_unavailable", "helper_text.cloud_sync_app_key_missing",
        "helper_text.cloud_sync_overlay_limit", "title.cloud_sync_settings", "title.cloud_sync_conflict",
        "label.cloud_sync_account", "label.cloud_sync_last_sync",
        "action.cloud_sync_use_cloud", "action.cloud_sync_keep_local", "action.cloud_sync_play_without_syncing",
    ];

    private readonly ICloudSyncCoordinator _coordinator;
    private readonly ISettingsManager _settings;
    private readonly ILocalizationService _localization;
    private readonly bool _hasDolphinPaths;
    private readonly bool _secureStorageAvailable;
    private readonly bool _hasAppKey;
    private SyncUiState _state;

    public CloudSyncSettingsViewModel(ICloudSyncCoordinator coordinator, ISettingsManager settings,
        ILocalizationService localization, bool hasDolphinPaths, bool secureStorageAvailable, bool hasAppKey = true)
    {
        _coordinator = coordinator;
        _settings = settings;
        _localization = localization;
        _hasDolphinPaths = hasDolphinPaths;
        _secureStorageAvailable = secureStorageAvailable;
        _hasAppKey = hasAppKey;
        _state = coordinator.State;
        _coordinator.StateChanged += CoordinatorOnStateChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public bool AutoSyncEnabled => _settings.Get<bool>(_settings.CLOUD_AUTO_SYNC);
    public SyncUiState State => _state;
    public bool CanConnect => _hasDolphinPaths && _secureStorageAvailable && _hasAppKey && _state.Status != SyncUiStatus.Busy;
    public bool CanToggleAutoSync => _hasDolphinPaths;
    public bool CanSync => _hasDolphinPaths && _secureStorageAvailable && _hasAppKey && _state.Status is
        SyncUiStatus.Idle or SyncUiStatus.Conflict or SyncUiStatus.CloudFound or SyncUiStatus.QueuedForExit or SyncUiStatus.Error;
    public bool CanDisconnect => _state.Status != SyncUiStatus.Busy && _state.Status != SyncUiStatus.Disconnected && _state.Status != SyncUiStatus.NotConfigured;
    public string StatusText => !_hasAppKey
        ? _localization.Translate(StatusKeys[SyncUiStatus.NotConfigured])
        : _state.Message ?? _localization.Translate(StatusKeys[_state.Status]);
    public string AvailabilityText => !_hasAppKey
        ? _localization.Translate("helper_text.cloud_sync_app_key_missing")
        : !_secureStorageAvailable
            ? _localization.Translate("helper_text.cloud_sync_secure_storage_unavailable")
            : !_hasDolphinPaths
                ? _localization.Translate("helper_text.cloud_sync_path_unavailable")
                : _localization.Translate("helper_text.cloud_sync_overlay_limit");
    public string? AccountText => _state.AccountId;
    public string? LastSyncText => _state.LastSync?.ToLocalTime().ToString("g");

    public async Task ConnectAsync(CancellationToken ct)
    {
        if (CanConnect)
            await RunCoordinatorActionAsync(() => _coordinator.ConnectAsync(ct));
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        if (CanDisconnect)
            await RunCoordinatorActionAsync(() => _coordinator.DisconnectAsync(ct));
    }

    public async Task SyncNowAsync(CancellationToken ct)
    {
        if (CanSync)
            await RunCoordinatorActionAsync(() => _coordinator.SyncNowAsync(ct));
    }

    private async Task RunCoordinatorActionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            UpdateState(new SyncUiState(SyncUiStatus.Error));
        }
    }

    public void SetAutoSync(bool enabled)
    {
        _coordinator.SetAutoSync(enabled);
        OnPropertyChanged(nameof(AutoSyncEnabled));
    }

    private void CoordinatorOnStateChanged(object? sender, SyncUiState state)
    {
        if (Dispatcher.UIThread.CheckAccess())
            UpdateState(state);
        else
            Dispatcher.UIThread.Post(() => UpdateState(state));
    }

    private void UpdateState(SyncUiState state)
    {
        _state = state;
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(AccountText));
        OnPropertyChanged(nameof(LastSyncText));
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanToggleAutoSync));
        OnPropertyChanged(nameof(CanSync));
        OnPropertyChanged(nameof(CanDisconnect));
    }

    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    public void Dispose() => _coordinator.StateChanged -= CoordinatorOnStateChanged;
}
