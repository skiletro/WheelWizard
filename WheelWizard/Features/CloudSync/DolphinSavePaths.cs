using WheelWizard.CustomDistributions;
using WheelWizard.Services;
using WheelWizard.Views;

namespace WheelWizard.Features.CloudSync;

public sealed record DolphinSavePaths(string UserFolder, string SaveRoot, string WiiRoot, string? InstalledRrVersion);

public interface IDolphinSavePathSource
{
    DolphinSavePaths? TryResolve();
}

public sealed class PathManagerDolphinSavePathSource : IDolphinSavePathSource
{
    private readonly Func<string> _userFolderPath;
    private readonly Func<string> _saveRoot;
    private readonly Func<string> _wiiRoot;
    private readonly Func<string?> _installedRrVersion;

    public PathManagerDolphinSavePathSource(
        Func<string>? userFolderPath = null,
        Func<string>? saveRoot = null,
        Func<string>? wiiRoot = null,
        Func<string?>? installedRrVersion = null
    )
    {
        _userFolderPath = userFolderPath ?? (() => PathManager.UserFolderPath);
        _saveRoot = saveRoot ?? (() => PathManager.SaveFolderPath);
        _wiiRoot = wiiRoot ?? (() => PathManager.WiiFolderPath);
        _installedRrVersion =
            installedRrVersion
            ?? (
                () =>
                    App.Services.GetService(typeof(ICustomDistributionSingletonService))
                        is ICustomDistributionSingletonService distributions
                        ? distributions.RetroRewind.GetCurrentVersion()?.ToString()
                        : null
            );
    }

    public DolphinSavePaths? TryResolve()
    {
        var userFolder = _userFolderPath();
        if (string.IsNullOrWhiteSpace(userFolder) || !Directory.Exists(userFolder))
            return null;

        return new DolphinSavePaths(userFolder, _saveRoot(), _wiiRoot(), _installedRrVersion());
    }
}
