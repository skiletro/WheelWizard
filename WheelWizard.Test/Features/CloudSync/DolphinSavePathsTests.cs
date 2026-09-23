using WheelWizard.Features.CloudSync;

namespace WheelWizard.Test.Features.CloudSync;

public class DolphinSavePathsTests
{
    [Fact]
    public void TryResolve_UsesConfiguredDolphinAndIndependentSaveAndNandPaths()
    {
        using var userFolder = new TemporaryDirectory();
        var loadPath = Path.Combine(userFolder.Path, "custom-load");
        var nandRoot = Path.Combine(userFolder.Path, "custom-nand");
        Directory.CreateDirectory(loadPath);
        Directory.CreateDirectory(nandRoot);

        var source = new PathManagerDolphinSavePathSource(
            () => userFolder.Path,
            () => Path.Combine(loadPath, "Riivolution", "riivolution", "save", "RetroWFC"),
            () => nandRoot,
            () => "1.2.3"
        );

        var paths = source.TryResolve();

        Assert.NotNull(paths);
        Assert.Equal(userFolder.Path, paths.UserFolder);
        Assert.Equal(Path.Combine(loadPath, "Riivolution", "riivolution", "save", "RetroWFC"), paths.SaveRoot);
        Assert.Equal(nandRoot, paths.WiiRoot);
        Assert.Equal("1.2.3", paths.InstalledRrVersion);
    }

    [Fact]
    public void TryResolve_ReturnsNullWhenDolphinUserFolderIsNotConfigured()
    {
        var source = new PathManagerDolphinSavePathSource(() => "", () => "/save", () => "/wii", () => null);

        Assert.Null(source.TryResolve());
    }

    [Fact]
    public void TryResolve_ReturnsNullWhenDolphinUserFolderDoesNotExist()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var missingFolder = Path.Combine(temporaryDirectory.Path, "missing-user-folder");
        var source = new PathManagerDolphinSavePathSource(() => missingFolder, () => "/save", () => "/wii", () => null);

        Assert.Null(source.TryResolve());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
