using System.IO.Compression;
using System.Text.Json;
using WheelWizard.Features.CloudSync;

namespace WheelWizard.Test.Features.CloudSync;

public class WheelSyncArchiveWriterTests
{
    [Fact]
    public async Task CreateAsync_WritesOnlyDolphinSaveFilesAndV2Manifest()
    {
        using var root = new TemporaryDirectory();
        var saves = Path.Combine(root.Path, "RetroWFC");
        var wii = Path.Combine(root.Path, "Wii");
        AddFile(saves, "RMCP/rksys.dat", [1]);
        AddFile(saves, "RMCE/rksys.dat", [2]);
        AddFile(saves, "RMCJ/rksys.dat", [3]);
        AddFile(saves, "RMCK/rksys.dat", [4]);
        AddFile(wii, "shared2/menu/FaceLib/RFL_DB.dat", [5]);
        AddFile(wii, "shared2/Pulsar/RetroRewind6/RRRating.pul", [6]);
        AddFile(wii, "shared2/Pulsar/RetroRewind6/RRGameSettings.pul", [7]);
        AddFile(wii, "shared2/Pulsar/RetroRewind6/RRSettings.pul", [8]);
        AddFile(wii, "shared2/Pulsar/RetroRewind6/Ghosts/ghost.ghost", [9]);
        AddFile(wii, "title/00010000/48414241/data/save.bin", [10]);
        AddFile(wii, "shared2/Pulsar/OtherPack/not-included.pul", [11]);
        AddFile(root.Path, "recomp/save.bin", [12]);

        var writer = new WheelSyncArchiveWriter();
        var snapshot = await writer.CreateAsync(new DolphinSavePaths(root.Path, saves, wii, "6.3.1"), CancellationToken.None);

        Assert.NotNull(snapshot);
        try
        {
            using var stream = File.OpenRead(snapshot.FilePath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var names = archive.Entries.Select(entry => entry.FullName).ToArray();
            Assert.Equal("manifest.json", names[0]);
            Assert.Equal(
                new[]
                {
                    "manifest.json",
                    "RetroWFC/RMCP/rksys.dat",
                    "RetroWFC/RMCE/rksys.dat",
                    "RetroWFC/RMCJ/rksys.dat",
                    "RetroWFC/RMCK/rksys.dat",
                    "Wii/shared2/menu/FaceLib/RFL_DB.dat",
                    "Wii/shared2/Pulsar/RetroRewind6/RRRating.pul",
                    "Wii/shared2/Pulsar/RetroRewind6/RRGameSettings.pul",
                    "Wii/shared2/Pulsar/RetroRewind6/RRSettings.pul",
                    "Wii/shared2/Pulsar/RetroRewind6/Ghosts/ghost.ghost",
                },
                names
            );
            using var manifest = JsonDocument.Parse(archive.Entries[0].Open());
            var document = manifest.RootElement;
            Assert.Equal("wheelwitch-save", document.GetProperty("type").GetString());
            Assert.Equal(2, document.GetProperty("version").GetInt32());
            Assert.Equal("6.3.1", document.GetProperty("rrVersion").GetString());
            var contents = document.GetProperty("contents");
            Assert.True(contents.GetProperty("retroWfc").GetBoolean());
            Assert.Empty(contents.GetProperty("vanillaSaves").EnumerateArray());
            Assert.False(contents.GetProperty("patchedIso").GetBoolean());
            Assert.True(contents.GetProperty("faceLib").GetBoolean());
            Assert.True(contents.GetProperty("pulsar").GetBoolean());
            Assert.True(contents.GetProperty("ghosts").GetBoolean());
            Assert.Equal(snapshot.Hash, SaveContentHash.Compute(File.OpenRead(snapshot.FilePath)));
        }
        finally
        {
            File.Delete(snapshot.FilePath);
        }
    }

    [Fact]
    public async Task CreateAsync_ReturnsNullWhenNoIncludedFilesExist()
    {
        using var root = new TemporaryDirectory();
        var snapshot = await new WheelSyncArchiveWriter().CreateAsync(
            new DolphinSavePaths(root.Path, Path.Combine(root.Path, "save"), Path.Combine(root.Path, "wii"), null),
            CancellationToken.None
        );
        Assert.Null(snapshot);
    }

    private static void AddFile(string root, string relative, byte[] bytes)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
