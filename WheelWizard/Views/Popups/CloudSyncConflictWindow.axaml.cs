using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;
using WheelWizard.Features.CloudSync;
using WheelWizard.Localization;

namespace WheelWizard.Views.Popups;

public partial class CloudSyncConflictWindow : Window, ICloudSyncPrompt
{
    private readonly ILocalizationService _localization;
    private readonly SyncUiState? _state;
    private readonly TaskCompletionSource<ConflictChoice>? _choice;

    public CloudSyncConflictWindow(ILocalizationService localization)
    {
        _localization = localization;
        InitializeComponent();
    }

    private CloudSyncConflictWindow(ILocalizationService localization, SyncUiState state,
        TaskCompletionSource<ConflictChoice> choice) : this(localization)
    {
        _state = state;
        _choice = choice;
        TitleText.Text = _localization.Translate("title.cloud_sync_conflict");
        MessageText.Text = state.Status == SyncUiStatus.CloudFound
            ? _localization.Translate("status.cloud_sync_cloud_found")
            : _localization.Translate("status.cloud_sync_conflict");
        UseCloudButton.Content = _localization.Translate("action.cloud_sync_use_cloud");
        KeepLocalButton.Content = _localization.Translate("action.cloud_sync_keep_local");
        PlayWithoutSyncingButton.Content = _localization.Translate("action.cloud_sync_play_without_syncing");
        CancelButton.Content = _localization.Translate("action.cancel");
        Closed += (_, _) => _choice.TrySetResult(ConflictChoice.Cancel);
    }

    public async Task<ConflictChoice> ChooseAsync(SyncUiState state, CancellationToken ct)
    {
        var choice = new TaskCompletionSource<ConflictChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new CloudSyncConflictWindow(_localization, state, choice);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (owner is { IsVisible: true })
                dialog.ShowDialog(owner);
            else
                dialog.Show();
        });
        using var registration = ct.Register(() => Dispatcher.UIThread.Post(dialog.Close));
        return await choice.Task.ConfigureAwait(false);
    }

    private void UseCloud_Click(object? sender, RoutedEventArgs e) => Finish(ConflictChoice.UseCloud);
    private void KeepLocal_Click(object? sender, RoutedEventArgs e) => Finish(ConflictChoice.KeepLocal);
    private void PlayWithoutSyncing_Click(object? sender, RoutedEventArgs e) => Finish(ConflictChoice.PlayWithoutSync);
    private void Cancel_Click(object? sender, RoutedEventArgs e) => Finish(ConflictChoice.Cancel);

    private void Finish(ConflictChoice choice)
    {
        _choice?.TrySetResult(choice);
        Close();
    }
}
