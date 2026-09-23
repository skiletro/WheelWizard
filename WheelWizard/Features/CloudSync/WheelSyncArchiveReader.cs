using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Text.Json;

namespace WheelWizard.Features.CloudSync;

public sealed record ArchiveSafetyLimits(
    int MaximumEntries = 10_000,
    long MaximumEntryBytes = 64L * 1024 * 1024,
    long MaximumTotalBytes = 512L * 1024 * 1024
);

public sealed record ValidatedArchiveEntry(string ArchivePath, string StagedFilePath, long Length);

public sealed class ValidatedArchive : IDisposable
{
    internal ValidatedArchive(IReadOnlyList<ValidatedArchiveEntry> entries, string contentHash, string? rrVersion, string stagingDirectory)
    {
        Entries = new ReadOnlyCollection<ValidatedArchiveEntry>(entries.ToArray());
        ContentHash = contentHash;
        RrVersion = rrVersion;
        StagingDirectory = stagingDirectory;
    }

    public IReadOnlyList<ValidatedArchiveEntry> Entries { get; }
    public string ContentHash { get; }
    public string? RrVersion { get; }
    internal string StagingDirectory { get; }

    public void Dispose()
    {
        if (Directory.Exists(StagingDirectory))
            Directory.Delete(StagingDirectory, recursive: true);
    }
}

public interface IWheelSyncArchiveReader
{
    Task<ValidatedArchive> ValidateAsync(Stream zip, CancellationToken ct);
}

public sealed class WheelSyncArchiveReader : IWheelSyncArchiveReader
{
    private static readonly ArchiveSafetyLimits DefaultLimits = new();
    private readonly string _stagingRoot;
    private readonly ArchiveSafetyLimits _limits;

    public WheelSyncArchiveReader(string stagingRoot, ArchiveSafetyLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        _stagingRoot = Path.GetFullPath(stagingRoot);
        _limits = limits ?? DefaultLimits;
        if (_limits.MaximumEntries < 1 || _limits.MaximumEntryBytes < 0 || _limits.MaximumTotalBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }

    public async Task<ValidatedArchive> ValidateAsync(Stream zip, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(zip);
        ct.ThrowIfCancellationRequested();

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException)
        {
            throw new InvalidDataException("Invalid save archive", exception);
        }

        using (archive)
        {
            if (archive.Entries.Count > _limits.MaximumEntries)
                throw new InvalidDataException("Save archive has too many entries");

            var names = new HashSet<string>(StringComparer.Ordinal);
            long totalBytes = 0;
            var selected = new List<(ZipArchiveEntry Entry, string ArchivePath)>();
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (!names.Add(entry.FullName))
                    throw new InvalidDataException("Duplicate save archive path");
                ValidateEntryPath(entry);
                if (entry.Length > _limits.MaximumEntryBytes)
                    throw new InvalidDataException("Save archive entry is too large");
                try
                {
                    totalBytes = checked(totalBytes + entry.Length);
                }
                catch (OverflowException exception)
                {
                    throw new InvalidDataException("Save archive is too large", exception);
                }
                if (totalBytes > _limits.MaximumTotalBytes)
                    throw new InvalidDataException("Save archive is too large");

                if (!entry.FullName.EndsWith("/", StringComparison.Ordinal) && entry.FullName != "manifest.json")
                {
                    var allowedPath = MapKnownPath(entry.FullName);
                    if (allowedPath is not null)
                        selected.Add((entry, allowedPath));
                }
            }

            var manifestEntry = archive.Entries.FirstOrDefault(entry => entry.FullName == "manifest.json");
            var rrVersion = await ReadManifestAsync(manifestEntry, ct);
            if (zip.CanSeek)
                zip.Position = 0;
            var contentHash = SaveContentHash.Compute(zip);

            var stagingDirectory = Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDirectory);
            try
            {
                var staged = new List<ValidatedArchiveEntry>(selected.Count);
                for (var index = 0; index < selected.Count; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (entry, path) = selected[index];
                    var stagedPath = Path.Combine(stagingDirectory, index.ToString("D5") + ".entry");
                    await using var input = entry.Open();
                    await using var output = new FileStream(
                        stagedPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        81920,
                        FileOptions.Asynchronous | FileOptions.WriteThrough
                    );
                    await CopyBoundedAsync(input, output, entry.Length, ct);
                    await output.FlushAsync(ct);
                    output.Flush(flushToDisk: true);
                    staged.Add(new ValidatedArchiveEntry(path, stagedPath, entry.Length));
                }

                return new ValidatedArchive(staged, contentHash, rrVersion, stagingDirectory);
            }
            catch
            {
                Directory.Delete(stagingDirectory, recursive: true);
                throw;
            }
        }
    }

    private static async Task<string?> ReadManifestAsync(ZipArchiveEntry? manifestEntry, CancellationToken ct)
    {
        if (manifestEntry is null)
            throw new InvalidDataException("Save archive manifest is missing");
        try
        {
            await using var stream = manifestEntry.Open();
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = document.RootElement;
            if (
                root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || type.GetString() != "wheelwitch-save"
                || !root.TryGetProperty("version", out var version)
                || !version.TryGetInt32(out var versionNumber)
                || versionNumber is < 1 or > 2
            )
                throw new InvalidDataException("Unsupported save archive");
            if (
                root.TryGetProperty("rrVersion", out var rrVersion)
                && rrVersion.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)
            )
                throw new InvalidDataException("Invalid save archive version");
            return rrVersion.ValueKind == JsonValueKind.String ? rrVersion.GetString() : null;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Invalid save archive manifest", exception);
        }
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long declaredLength, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) != 0)
        {
            copied = checked(copied + read);
            if (copied > declaredLength)
                throw new InvalidDataException("Save archive entry length changed during extraction");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        if (copied != declaredLength)
            throw new InvalidDataException("Save archive entry length does not match its header");
    }

    private static void ValidateEntryPath(ZipArchiveEntry entry)
    {
        var name = entry.FullName;
        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixType == 0xA000)
            throw new InvalidDataException("Symbolic links are not allowed in save archives");
        if (name.Length == 0 || name[0] == '/' || name[0] == '\\' || name.Contains('\\') || Path.IsPathRooted(name))
            throw new InvalidDataException("Unsafe save path");
        if (name.Length >= 2 && char.IsAsciiLetter(name[0]) && name[1] == ':')
            throw new InvalidDataException("Unsafe save path");
        var components = name.Split('/');
        if (components.Any(component => component is ".." or "." || component.Length == 0 && !name.EndsWith("/", StringComparison.Ordinal)))
            throw new InvalidDataException("Unsafe save path");
        if (components.Any(component => component.Length == 0 && component != components[^1]))
            throw new InvalidDataException("Unsafe save path");
    }

    internal static string? MapKnownPath(string path)
    {
        if (path.StartsWith("RetroWFC/", StringComparison.Ordinal))
        {
            var region = path["RetroWFC/".Length..];
            if (
                region.Length == "RMCP/rksys.dat".Length
                && (
                    region.StartsWith("RMCP/", StringComparison.Ordinal)
                    || region.StartsWith("RMCE/", StringComparison.Ordinal)
                    || region.StartsWith("RMCJ/", StringComparison.Ordinal)
                    || region.StartsWith("RMCK/", StringComparison.Ordinal)
                )
                && region.EndsWith("/rksys.dat", StringComparison.Ordinal)
            )
                return path;
            return null;
        }

        const string wiiPrefix = "Wii/";
        if (!path.StartsWith(wiiPrefix, StringComparison.Ordinal))
            return null;
        var wiiPath = path[wiiPrefix.Length..];
        if (wiiPath == "shared2/menu/FaceLib/RFL_DB.dat")
            return path;
        const string pulsarPrefix = "shared2/Pulsar/RetroRewind6/";
        if (!wiiPath.StartsWith(pulsarPrefix, StringComparison.Ordinal))
            return null;
        var pulsarPath = wiiPath[pulsarPrefix.Length..];
        if (pulsarPath is "RRRating.pul" or "RRGameSettings.pul" or "RRSettings.pul")
            return path;
        return pulsarPath.StartsWith("Ghosts/", StringComparison.Ordinal) && pulsarPath.Length > "Ghosts/".Length ? path : null;
    }
}
