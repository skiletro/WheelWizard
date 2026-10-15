using WheelWizard.Mods.Views;
using WheelWizard.Views.Dialogs.Base;
using WheelWizard.Views.Shell.Navigation;

namespace WheelWizard.Mods.Views.Dialogs;

public partial class ModIndependentWindow : PopupContent
{
    private ModContent ModDetailViewer { get; }

    private INavigationService Navigation { get; }
    private readonly CancellationTokenSource _lifetime = new();

    public ModIndependentWindow(ModContent modDetailViewer, INavigationService navigation, string windowTitle = "Mod Details")
        : base(windowTitle)
    {
        ModDetailViewer = modDetailViewer;
        Navigation = navigation;
        InitializeComponent();
        IsDismissible = true;
        ModDetailHost.Content = ModDetailViewer;
        if (PopupTitle == "Mod Details")
            PopupTitle = t("popup_title.mod_details");
    }

    public async Task LoadModAsync(int modId, string? newDownloadUrl = null)
    {
        await ModDetailViewer.LoadModDetailsAsync(modId, newDownloadUrl, _lifetime.Token);
    }

    protected override void BeforeClose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        Navigation.NavigateTo<ModsPage>();
        base.BeforeClose();
    }
}
