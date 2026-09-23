using System.IO.Compression;
using System.Text.Json;
using WheelWizard.Services;

namespace WheelWizard.Features.CloudSync;

public sealed record ArchiveSnapshot(string FilePath, string Hash);

public interface IWheelSyncArchiveWriter
{
    Task<ArchiveSnapshot?> CreateAsync(DolphinSavePaths paths, CancellationToken ct);
}

public sealed class WheelSyncArchiveWriter : IWheelSyncArchiveWriter
{
    private static readonly string[] Regions = ["RMCP", "RMCE", "RMCJ", "RMCK"];
    private static readonly string[] PulsarFiles = ["RRRating.pul", "RRGameSettings.pul", "RRSettings.pul"];
    private const string PulsarRoot = "shared2/Pulsar/RetroRewind6";

    public async Task<ArchiveSnapshot?> CreateAsync(DolphinSavePaths paths, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ct.ThrowIfCancellationRequested();

        var included = new List<(string ArchiveName, string SourcePath)>();
        foreach (var region in Regions)
            AddRegularFile(included, $"RetroWFC/{region}/rksys.dat", Path.Combine(paths.SaveRoot, region, "rksys.dat"));

        AddRegularFile(
            included,
            "Wii/shared2/menu/FaceLib/RFL_DB.dat",
            Path.Combine(paths.WiiRoot, "shared2", "menu", "FaceLib", "RFL_DB.dat")
        );
        foreach (var file in PulsarFiles)
            AddRegularFile(
                included,
                $"Wii/{PulsarRoot}/{file}",
                Path.Combine(paths.WiiRoot, PulsarRoot.Replace('/', Path.DirectorySeparatorChar), file)
            );

        var ghosts = Path.Combine(paths.WiiRoot, PulsarRoot.Replace('/', Path.DirectorySeparatorChar), "Ghosts");
        if (Directory.Exists(ghosts) && !IsReparsePoint(ghosts))
        {
            var enumerationOptions = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            foreach (var source in Directory.EnumerateFiles(ghosts, "*", enumerationOptions).OrderBy(path => path, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                if (IsRegularFile(source) && !HasReparsePointBetween(ghosts, source))
                {
                    var relative = Path.GetRelativePath(ghosts, source).Replace(Path.DirectorySeparatorChar, '/');
                    included.Add(($"Wii/{PulsarRoot}/Ghosts/{relative}", source));
                }
            }
        }

        if (included.Count == 0)
            return null;

        var directory = Path.Combine(PathManager.WheelWizardAppdataPath, "cloud-sync", "tmp");
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, $"{Guid.NewGuid():N}.zip");

        try
        {
            await using (
                var output = new FileStream(filePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true)
            )
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var manifest = new
                    {
                        type = "wheelwitch-save",
                        version = 2,
                        createdAt = DateTimeOffset.UtcNow,
                        contents = new
                        {
                            retroWfc = included.Any(item => item.ArchiveName.StartsWith("RetroWFC/", StringComparison.Ordinal)),
                            vanillaSaves = Array.Empty<string>(),
                            patchedIso = false,
                            faceLib = included.Any(item => item.ArchiveName == "Wii/shared2/menu/FaceLib/RFL_DB.dat"),
                            pulsar = included.Any(item => item.ArchiveName.StartsWith($"Wii/{PulsarRoot}/", StringComparison.Ordinal)),
                            ghosts = included.Any(item =>
                                item.ArchiveName.StartsWith($"Wii/{PulsarRoot}/Ghosts/", StringComparison.Ordinal)
                            ),
                        },
                        rrVersion = paths.InstalledRrVersion,
                    };
                    var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                    await using (var manifestStream = manifestEntry.Open())
                        await JsonSerializer.SerializeAsync(
                            manifestStream,
                            manifest,
                            new JsonSerializerOptions
                            {
                                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                            },
                            ct
                        );

                    foreach (var item in included)
                    {
                        ct.ThrowIfCancellationRequested();
                        var entry = archive.CreateEntry(item.ArchiveName, CompressionLevel.Optimal);
                        await using var entryOutput = entry.Open();
                        await using var source = new FileStream(
                            item.SourcePath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            81920,
                            useAsync: true
                        );
                        await source.CopyToAsync(entryOutput, ct);
                    }
                }

                output.Position = 0;
                var hash = SaveContentHash.Compute(output);
                return new ArchiveSnapshot(filePath, hash);
            }
        }
        catch
        {
            File.Delete(filePath);
            throw;
        }
    }

    private static void AddRegularFile(List<(string ArchiveName, string SourcePath)> included, string entryName, string sourcePath)
    {
        if (IsRegularFile(sourcePath))
            included.Add((entryName, sourcePath));
    }

    private static bool IsRegularFile(string path)
    {
        if (!File.Exists(path))
            return false;
        var attributes = File.GetAttributes(path);
        return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
    }

    private static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool HasReparsePointBetween(string root, string file)
    {
        var current = Path.GetDirectoryName(file);
        while (current is not null && current.StartsWith(root, StringComparison.Ordinal))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return true;
            if (string.Equals(current, root, StringComparison.Ordinal))
                break;
            current = Path.GetDirectoryName(current);
        }
        return false;
    }
}
