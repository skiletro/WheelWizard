using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WheelWizard.Features.CloudSync;

namespace WheelWizard.Test.Features.CloudSync;

public class SaveRestoreServiceTests
{
    [Fact]
    public async Task InvalidArchive_LeavesExistingSaveBytesUnchanged()
    {
        using var temp = new WheelSyncArchiveReaderTests.TemporaryDirectory();
        var saves = Path.Combine(temp.Path, "save");
        Write(saves, "RMCP/rksys.dat", [42, 43]);
        var reader = new WheelSyncArchiveReader(Path.Combine(temp.Path, "stage"));

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ValidateAsync(new MemoryStream([1, 2, 3]), CancellationToken.None));

        Assert.Equal([42, 43], await File.ReadAllBytesAsync(Path.Combine(saves, "RMCP/rksys.dat")));
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "stage")));
    }

    [Fact]
    public async Task RestoreAsync_RestoresEveryAllowedEntryWhenVersionsMatch()
    {
        using var temp = new WheelSyncArchiveReaderTests.TemporaryDirectory();
        var saves = Path.Combine(temp.Path, "save");
        var wii = Path.Combine(temp.Path, "wii");
        var entries = new (string Name, byte[] Bytes)[]
        {
            ("RetroWFC/RMCP/rksys.dat", [1]),
            ("RetroWFC/RMCE/rksys.dat", [2]),
            ("RetroWFC/RMCJ/rksys.dat", [3]),
            ("RetroWFC/RMCK/rksys.dat", [4]),
            ("Wii/shared2/menu/FaceLib/RFL_DB.dat", [5]),
            ("Wii/shared2/Pulsar/RetroRewind6/RRRating.pul", [6]),
            ("Wii/shared2/Pulsar/RetroRewind6/RRGameSettings.pul", [7]),
            ("Wii/shared2/Pulsar/RetroRewind6/RRSettings.pul", [8]),
            ("Wii/shared2/Pulsar/RetroRewind6/Ghosts/new.ghost", [9]),
        };
        using var zip = WheelSyncArchiveReaderTests.CreateZip(entries);
        var reader = new WheelSyncArchiveReader(Path.Combine(temp.Path, "stage"));
        var validated = await reader.ValidateAsync(zip, CancellationToken.None);
        var service = new SaveRestoreService(new WheelSyncArchiveWriter(), Path.Combine(temp.Path, "recovery"));

        await service.RestoreAsync(validated, new DolphinSavePaths(temp.Path, saves, wii, "6.3.1"), CancellationToken.None);

        foreach (var (name, bytes) in entries)
        {
            var root = name.StartsWith("RetroWFC/", StringComparison.Ordinal) ? saves : wii;
            var relative = name[(name.IndexOf('/') + 1)..].Replace('/', Path.DirectorySeparatorChar);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(root, relative)));
        }
    }

    [Fact]
    public async Task RestoreAsync_OverlaysAllowedFilesAndSkipsVersionGatedSettingsAndUnknownFiles()
    {
        using var temp = new WheelSyncArchiveReaderTests.TemporaryDirectory();
        var saves = Path.Combine(temp.Path, "save");
        var wii = Path.Combine(temp.Path, "wii");
        var appData = Path.Combine(temp.Path, "appdata");
        Write(saves, "RMCP/rksys.dat", [9]);
        Write(wii, "shared2/Pulsar/RetroRewind6/RRGameSettings.pul", [8]);
        Write(wii, "shared2/Pulsar/RetroRewind6/Ghosts/old.ghost", [6]);
        using var zip = WheelSyncArchiveReaderTests.CreateZip(
            ("RetroWFC/RMCP/rksys.dat", [1, 2]),
            ("Wii/shared2/Pulsar/RetroRewind6/RRGameSettings.pul", [3]),
            ("Wii/shared2/Pulsar/RetroRewind6/Ghosts/new.ghost", [4]),
            ("Wii/title/unknown.bin", [5])
        );
        var reader = new WheelSyncArchiveReader(Path.Combine(appData, "stage"));
        var validated = await reader.ValidateAsync(zip, CancellationToken.None);
        var service = new SaveRestoreService(new WheelSyncArchiveWriter(), Path.Combine(appData, "recovery"));
        var paths = new DolphinSavePaths(temp.Path, saves, wii, "6.4.0");

        var hash = await service.RestoreAsync(validated, paths, CancellationToken.None);

        Assert.Equal([1, 2], await File.ReadAllBytesAsync(Path.Combine(saves, "RMCP/rksys.dat")));
        Assert.Equal([8], await File.ReadAllBytesAsync(Path.Combine(wii, "shared2/Pulsar/RetroRewind6/RRGameSettings.pul")));
        Assert.True(File.Exists(Path.Combine(wii, "shared2/Pulsar/RetroRewind6/Ghosts/old.ghost")));
        Assert.Equal([4], await File.ReadAllBytesAsync(Path.Combine(wii, "shared2/Pulsar/RetroRewind6/Ghosts/new.ghost")));
        Assert.False(File.Exists(Path.Combine(wii, "title/unknown.bin")));
        Assert.NotEqual(validated.ContentHash, hash);
    }

    [Fact]
    public async Task RestoreAsync_RollsBackEarlierWritesWhenLaterTargetCannotBeWritten()
    {
        using var temp = new WheelSyncArchiveReaderTests.TemporaryDirectory();
        var saves = Path.Combine(temp.Path, "save");
        var wii = Path.Combine(temp.Path, "wii");
        var recovery = Path.Combine(temp.Path, "recovery");
        Write(saves, "RMCP/rksys.dat", [9]);
        Directory.CreateDirectory(Path.Combine(saves, "RMCE/rksys.dat"));
        using var zip = WheelSyncArchiveReaderTests.CreateZip(("RetroWFC/RMCP/rksys.dat", [1]), ("RetroWFC/RMCE/rksys.dat", [2]));
        var reader = new WheelSyncArchiveReader(Path.Combine(temp.Path, "stage"));
        var validated = await reader.ValidateAsync(zip, CancellationToken.None);
        var service = new SaveRestoreService(new WheelSyncArchiveWriter(), recovery);
        var paths = new DolphinSavePaths(temp.Path, saves, wii, "6.3.1");

        await Assert.ThrowsAnyAsync<IOException>(() => service.RestoreAsync(validated, paths, CancellationToken.None));

        Assert.Equal([9], await File.ReadAllBytesAsync(Path.Combine(saves, "RMCP/rksys.dat")));
        Assert.Empty(Directory.GetFiles(recovery, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task RecoverAsync_RestoresSnapshotFromInterruptedJournalBeforeNextRestore()
    {
        using var temp = new WheelSyncArchiveReaderTests.TemporaryDirectory();
        var saves = Path.Combine(temp.Path, "save");
        var wii = Path.Combine(temp.Path, "wii");
        var recovery = Path.Combine(temp.Path, "recovery");
        Write(saves, "RMCP/rksys.dat", [1]);
        var paths = new DolphinSavePaths(temp.Path, saves, wii, "6.3.1");
        var identity = string.Join(
            "\n",
            Path.GetFullPath(paths.UserFolder),
            Path.GetFullPath(paths.SaveRoot),
            Path.GetFullPath(paths.WiiRoot)
        );
        var journalPath = Path.Combine(recovery, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant());
        Directory.CreateDirectory(journalPath);
        await File.WriteAllBytesAsync(Path.Combine(journalPath, "00000.original"), [1]);
        var journal = new
        {
            Entries = new[]
            {
                new
                {
                    ArchivePath = "RetroWFC/RMCP/rksys.dat",
                    Existed = true,
                    BackupName = "00000.original",
                },
            },
        };
        await File.WriteAllTextAsync(Path.Combine(journalPath, "journal.json"), JsonSerializer.Serialize(journal));
        await File.WriteAllBytesAsync(Path.Combine(saves, "RMCP/rksys.dat"), [2]);
        var service = new SaveRestoreService(new WheelSyncArchiveWriter(), recovery);

        await service.RecoverAsync(paths, CancellationToken.None);

        Assert.Equal([1], await File.ReadAllBytesAsync(Path.Combine(saves, "RMCP/rksys.dat")));
        Assert.Empty(Directory.GetFiles(recovery, "*", SearchOption.AllDirectories));
    }

    private static void Write(string root, string relative, byte[] bytes)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }
}
