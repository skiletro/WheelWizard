using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using WheelWizard.Features.CloudSync;

namespace WheelWizard.Test.Features.CloudSync;

public sealed class WheelWitchInteropTests
{
    private static readonly (string ArchivePath, string Tree, string RelativePath, byte[] Bytes)[] SyntheticFiles =
    [
        ("RetroWFC/RMCP/rksys.dat", "save", "RMCP/rksys.dat", [1, 2, 3]),
        ("RetroWFC/RMCE/rksys.dat", "save", "RMCE/rksys.dat", [4, 5, 6]),
        ("RetroWFC/RMCJ/rksys.dat", "save", "RMCJ/rksys.dat", [7, 8, 9]),
        ("RetroWFC/RMCK/rksys.dat", "save", "RMCK/rksys.dat", [10, 11, 12]),
        ("Wii/shared2/menu/FaceLib/RFL_DB.dat", "wii", "shared2/menu/FaceLib/RFL_DB.dat", [13, 14]),
        ("Wii/shared2/Pulsar/RetroRewind6/RRRating.pul", "wii", "shared2/Pulsar/RetroRewind6/RRRating.pul", [15, 16]),
        ("Wii/shared2/Pulsar/RetroRewind6/RRGameSettings.pul", "wii", "shared2/Pulsar/RetroRewind6/RRGameSettings.pul", [17, 18]),
        ("Wii/shared2/Pulsar/RetroRewind6/RRSettings.pul", "wii", "shared2/Pulsar/RetroRewind6/RRSettings.pul", [19, 20]),
        ("Wii/shared2/Pulsar/RetroRewind6/Ghosts/sample.ghost", "wii", "shared2/Pulsar/RetroRewind6/Ghosts/sample.ghost", [21, 22]),
    ];

    [Fact]
    public async Task AndroidStyleArchive_RoundTripsEveryAllowlistedFileAndCanonicalHash()
    {
        using var temp = new WheelSyncArchiveReaderTests.TemporaryDirectory();
        var saveRoot = Path.Combine(temp.Path, "source-save");
        var wiiRoot = Path.Combine(temp.Path, "source-wii");
        foreach (var file in SyntheticFiles)
            Write(Path.Combine(file.Tree == "save" ? saveRoot : wiiRoot, file.RelativePath), file.Bytes);

        var writer = new WheelSyncArchiveWriter();
        var snapshot = await writer.CreateAsync(
            new DolphinSavePaths(temp.Path, saveRoot, wiiRoot, "6.3.1"),
            CancellationToken.None
        );
        Assert.NotNull(snapshot);
        try
        {
            await using var zip = File.OpenRead(snapshot.FilePath);
            AssertHashesEqual(snapshot.Hash, IndependentCanonicalHash(zip));
            zip.Position = 0;
            var reader = new WheelSyncArchiveReader(Path.Combine(temp.Path, "staging"));
            using var validated = await reader.ValidateAsync(zip, CancellationToken.None);
            using var validationZip = File.OpenRead(snapshot.FilePath);
            AssertHashesEqual(validated.ContentHash, IndependentCanonicalHash(validationZip));

            var restoredSave = Path.Combine(temp.Path, "restored-save");
            var restoredWii = Path.Combine(temp.Path, "restored-wii");
            var restore = new SaveRestoreService(writer, Path.Combine(temp.Path, "recovery"));
            await restore.RestoreAsync(
                validated,
                new DolphinSavePaths(temp.Path, restoredSave, restoredWii, "6.3.1"),
                CancellationToken.None
            );

            foreach (var file in SyntheticFiles)
            {
                var actual = await File.ReadAllBytesAsync(Path.Combine(file.Tree == "save" ? restoredSave : restoredWii, file.RelativePath));
                Assert.True(actual.AsSpan().SequenceEqual(file.Bytes));
            }
        }
        finally
        {
            File.Delete(snapshot.FilePath);
        }
    }

    [Fact]
    public async Task VersionMismatchPreservesLocalSettingsAndEffectiveBaselinePreventsNextSyncPush()
    {
        using var temp = new WheelSyncArchiveReaderTests.TemporaryDirectory();
        var target = Path.Combine(temp.Path, "wii", "shared2", "Pulsar", "RetroRewind6", "RRGameSettings.pul");
        Write(target, [31, 32]);
        using var zip = WheelSyncArchiveReaderTests.CreateZip(
            ("Wii/shared2/Pulsar/RetroRewind6/RRGameSettings.pul", [41, 42]),
            "{\"type\":\"wheelwitch-save\",\"version\":2,\"rrVersion\":\"different-build\"}"
        );
        var writer = new WheelSyncArchiveWriter();
        var reader = new WheelSyncArchiveReader(Path.Combine(temp.Path, "staging"));
        using var validated = await reader.ValidateAsync(zip, CancellationToken.None);
        var paths = new DolphinSavePaths(temp.Path, Path.Combine(temp.Path, "save"), Path.Combine(temp.Path, "wii"), "installed-build");
        var restore = new SaveRestoreService(writer, Path.Combine(temp.Path, "recovery"));

        var effectiveLocalHash = await restore.RestoreAsync(validated, paths, CancellationToken.None);

        Assert.True((await File.ReadAllBytesAsync(target)).AsSpan().SequenceEqual(new byte[] { 31, 32 }));
        Assert.Equal(SyncAction.Noop, SaveSyncEngine.Decide("remote-revision", "remote-revision", effectiveLocalHash, effectiveLocalHash));
    }

    [Fact]
    public void MissingRestoredEntry_DoesNotExposeArchivePathInFailure()
    {
        const string privateEntryName = "private-ghost-name.ghost";
        using var temp = new WheelSyncArchiveReaderTests.TemporaryDirectory();
        var missingPath = Path.Combine(temp.Path, privateEntryName);

        var error = Assert.Throws<IOException>(() => OpenRestoredEntry(missingPath));

        Assert.Equal("Restored archive content is unavailable.", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void BothChangedSidesProduceConflict()
    {
        Assert.Equal(SyncAction.Conflict, SaveSyncEngine.Decide("remote-new", "remote-old", "local-new", "local-old"));
    }

    [Fact]
    public async Task OptInAndroidArchive_RestoresInDisposableTreeWithoutWritingArchiveDetailsToOutput()
    {
        var fixturePath = Environment.GetEnvironmentVariable("WHEELWITCH_ANDROID_SAVE_ZIP");
        if (string.IsNullOrWhiteSpace(fixturePath))
            return;

        using var temp = new WheelSyncArchiveReaderTests.TemporaryDirectory();
        await using var source = new FileStream(fixturePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var reader = new WheelSyncArchiveReader(Path.Combine(temp.Path, "staging"));
        using var validated = await reader.ValidateAsync(source, CancellationToken.None);
        var independentlyComputedHash = IndependentCanonicalHash(source);
        Assert.True(
            CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(validated.ContentHash),
                Encoding.ASCII.GetBytes(independentlyComputedHash)
            ),
            "Archive canonical hash validation failed."
        );

        var saveRoot = Path.Combine(temp.Path, "disposable-save");
        var wiiRoot = Path.Combine(temp.Path, "disposable-wii");
        var restore = new SaveRestoreService(new WheelSyncArchiveWriter(), Path.Combine(temp.Path, "recovery"));
        await restore.RestoreAsync(validated, new DolphinSavePaths(temp.Path, saveRoot, wiiRoot, validated.RrVersion), CancellationToken.None);

        source.Position = 0;
        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in validated.Entries)
        {
            var sourceEntry = archive.GetEntry(entry.ArchivePath);
            Assert.True(sourceEntry is not null, "Validated archive entry was not found.");
            var (root, relative) = entry.ArchivePath.StartsWith("RetroWFC/", StringComparison.Ordinal)
                ? (saveRoot, entry.ArchivePath["RetroWFC/".Length..])
                : (wiiRoot, entry.ArchivePath["Wii/".Length..]);
            await using var originalBytes = sourceEntry!.Open();
            await using var restoredBytes = OpenRestoredEntry(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            Assert.True(await StreamsEqualAsync(originalBytes, restoredBytes));
        }
    }

    private static FileStream OpenRestoredEntry(string path)
    {
        try
        {
            return File.OpenRead(path);
        }
        catch (IOException)
        {
            throw new IOException("Restored archive content is unavailable.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new IOException("Restored archive content is unavailable.");
        }
    }

    private static string IndependentCanonicalHash(Stream zip)
    {
        zip.Position = 0;
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        using var canonical = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (
            var entry in archive
                .Entries.Where(item => !item.FullName.EndsWith("/", StringComparison.Ordinal) && item.FullName != "manifest.json")
                .OrderBy(item => item.FullName, StringComparer.Ordinal)
        )
        {
            canonical.AppendData(Encoding.UTF8.GetBytes(entry.FullName));
            using var contents = entry.Open();
            canonical.AppendData(SHA256.HashData(contents));
        }
        return Convert.ToHexString(canonical.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AssertHashesEqual(string expected, string actual) =>
        Assert.True(
            CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual)),
            "Canonical hash validation failed."
        );

    private static async Task<bool> StreamsEqualAsync(Stream left, Stream right)
    {
        var leftBuffer = new byte[81920];
        var rightBuffer = new byte[81920];
        while (true)
        {
            var leftRead = await left.ReadAsync(leftBuffer);
            var rightRead = await right.ReadAsync(rightBuffer);
            if (leftRead != rightRead || !leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead)))
                return false;
            if (leftRead == 0)
                return true;
        }
    }

    private static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }
}
