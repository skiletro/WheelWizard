using System.IO.Compression;
using System.Text;
using WheelWizard.Features.CloudSync;

namespace WheelWizard.Test.Features.CloudSync;

public class WheelSyncArchiveReaderTests
{
    [Fact]
    public async Task ValidateAsync_StagesAllowedFilesAndSkipsUnknownSafeEntries()
    {
        using var temp = new TemporaryDirectory();
        var zip = CreateZip(("RetroWFC/RMCP/rksys.dat", [1, 2]), ("future/safe.bin", [3]));
        var reader = new WheelSyncArchiveReader(Path.Combine(temp.Path, "stage"));

        var validated = await reader.ValidateAsync(zip, CancellationToken.None);

        Assert.Equal("6.3.1", validated.RrVersion);
        Assert.Single(validated.Entries);
        Assert.Equal("RetroWFC/RMCP/rksys.dat", validated.Entries[0].ArchivePath);
        Assert.Equal([1, 2], await File.ReadAllBytesAsync(validated.Entries[0].StagedFilePath));
        Assert.Equal(
            SaveContentHash.Compute(CreateZip(("RetroWFC/RMCP/rksys.dat", [1, 2]), ("future/safe.bin", [3]))),
            validated.ContentHash
        );
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("type")]
    [InlineData("future")]
    public async Task ValidateAsync_RejectsUnsupportedManifest(string manifestKind)
    {
        using var temp = new TemporaryDirectory();
        var json = manifestKind switch
        {
            "missing" => "missing",
            "type" => "{\"type\":\"other\",\"version\":2}",
            _ => "{\"type\":\"wheelwitch-save\",\"version\":3}",
        };
        var zip = CreateZip(("RetroWFC/RMCP/rksys.dat", [1]), json);
        var reader = new WheelSyncArchiveReader(Path.Combine(temp.Path, "stage"));
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ValidateAsync(zip, CancellationToken.None));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("C:/absolute")]
    public async Task ValidateAsync_RejectsUnsafePaths(string path)
    {
        using var temp = new TemporaryDirectory();
        var zip = CreateZip((path, [1]));
        var reader = new WheelSyncArchiveReader(Path.Combine(temp.Path, "stage"));
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ValidateAsync(zip, CancellationToken.None));
    }

    [Fact]
    public async Task ValidateAsync_RejectsDuplicatesSymlinksOversizeAndCorruption()
    {
        using var temp = new TemporaryDirectory();
        var duplicate = CreateZip(("RetroWFC/RMCP/rksys.dat", [1]), ("RetroWFC/RMCP/rksys.dat", [2]));
        var reader = new WheelSyncArchiveReader(Path.Combine(temp.Path, "stage"));
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ValidateAsync(duplicate, CancellationToken.None));

        var symlink = CreateZip(("future/link", [1]), unixMode: 0xA1FF);
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ValidateAsync(symlink, CancellationToken.None));

        var smallEntryReader = new WheelSyncArchiveReader(Path.Combine(temp.Path, "stage"), new ArchiveSafetyLimits(10, 2, 1000));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => smallEntryReader.ValidateAsync(CreateZip(("a", [1, 2, 3])), CancellationToken.None)
        );
        var smallCountReader = new WheelSyncArchiveReader(Path.Combine(temp.Path, "stage"), new ArchiveSafetyLimits(2, 100, 1000));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => smallCountReader.ValidateAsync(CreateZip(("a", [1]), ("b", [2])), CancellationToken.None)
        );

        await Assert.ThrowsAnyAsync<InvalidDataException>(() => reader.ValidateAsync(new MemoryStream([1, 2, 3]), CancellationToken.None));
    }

    [Fact]
    public async Task ValidateAsync_RejectsTotalLimitBeforeStaging()
    {
        using var temp = new TemporaryDirectory();
        var reader = new WheelSyncArchiveReader(Path.Combine(temp.Path, "stage"), new ArchiveSafetyLimits(10, 1000, 37));
        await Assert.ThrowsAsync<InvalidDataException>(
            () =>
                reader.ValidateAsync(
                    CreateZip([("a", [1]), ("b", [2])], "{\"type\":\"wheelwitch-save\",\"version\":2}", null),
                    CancellationToken.None
                )
        );
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "stage")));
    }

    internal static MemoryStream CreateZip((string Name, byte[] Bytes) item, string? manifestJson = null, int? unixMode = null) =>
        CreateZip([item], manifestJson, unixMode);

    internal static MemoryStream CreateZip(params (string Name, byte[] Bytes)[] items) => CreateZip(items, null, null);

    private static MemoryStream CreateZip((string Name, byte[] Bytes)[] items, string? manifestJson, int? unixMode)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (manifestJson is not null && manifestJson != "missing")
            {
                var manifest = archive.CreateEntry("manifest.json");
                using var output = manifest.Open();
                output.Write(Encoding.UTF8.GetBytes(manifestJson));
            }
            else if (manifestJson is null && items.Length > 0)
            {
                // Insert a valid manifest unless caller explicitly requests a missing one.
                var manifest = archive.CreateEntry("manifest.json");
                using var output = manifest.Open();
                output.Write(Encoding.UTF8.GetBytes("{\"type\":\"wheelwitch-save\",\"version\":2,\"rrVersion\":\"6.3.1\"}"));
            }

            foreach (var item in items)
            {
                var entry = archive.CreateEntry(item.Name);
                if (unixMode.HasValue)
                    entry.ExternalAttributes = unixMode.Value << 16;
                using var output = entry.Open();
                output.Write(item.Bytes);
            }
        }
        stream.Position = 0;
        return stream;
    }

    internal sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
