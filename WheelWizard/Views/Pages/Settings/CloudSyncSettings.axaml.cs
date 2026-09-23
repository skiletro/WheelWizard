using Avalonia.Interactivity;
using Avalonia.Controls;
using WheelWizard.Features.CloudSync;
using WheelWizard.Localization;
using WheelWizard.Settings;
using WheelWizard.Shared.DependencyInjection;
using WheelWizard.Views;

namespace WheelWizard.Views.Pages.Settings;

public partial class CloudSyncSettings : UserControlBase
{
    [Inject] private ICloudSyncCoordinator Coordinator { get; set; } = null!;
    [Inject] private ICloudSyncPrompt Prompt { get; set; } = null!;
    [Inject] private IDolphinSavePathSource Paths { get; set; } = null!;
    [Inject] private ISettingsManager Settings { get; set; } = null!;
    [Inject] private ILocalizationService Localization { get; set; } = null!;

    private CloudSyncSettingsViewModel? _viewModel;
    public CloudSyncSettings()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
            return;
        _viewModel = new CloudSyncSettingsViewModel(
            Coordinator,
            Settings,
            Localization,
            Settings.DolphinPathsSetupCorrectly() && Paths.TryResolve() is not null,
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(),
            !string.IsNullOrWhiteSpace(CloudSyncBuildConfiguration.AppKey),
            Prompt);
        DataContext = _viewModel;
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
            return;
        _viewModel.Dispose();
        _viewModel = null;
        DataContext = null;
    }

    private async void Connect_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
            await _viewModel.ConnectAsync(CancellationToken.None);
    }

    private async void Disconnect_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
            await _viewModel.DisconnectAsync(CancellationToken.None);
    }

    private async void SyncNow_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
            await _viewModel.SyncNowAsync(CancellationToken.None);
    }

    private void AutoSync_OnChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { IsChecked: { } enabled })
            _viewModel?.SetAutoSync(enabled);
    }
}
