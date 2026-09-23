using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WheelWizard.Features.CloudSync;

public interface ISaveRestoreService
{
    Task<string> RestoreAsync(ValidatedArchive archive, DolphinSavePaths paths, CancellationToken ct);
    Task RecoverAsync(DolphinSavePaths paths, CancellationToken ct);
}

public sealed class SaveRestoreService : ISaveRestoreService
{
    private static readonly JsonSerializerOptions JournalJsonOptions = new() { PropertyNameCaseInsensitive = false };
    private readonly IWheelSyncArchiveWriter _archiveWriter;
    private readonly string _recoveryRoot;

    public SaveRestoreService(IWheelSyncArchiveWriter archiveWriter, string recoveryRoot)
    {
        _archiveWriter = archiveWriter ?? throw new ArgumentNullException(nameof(archiveWriter));
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryRoot);
        _recoveryRoot = Path.GetFullPath(recoveryRoot);
    }

    public async Task<string> RestoreAsync(ValidatedArchive archive, DolphinSavePaths paths, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(paths);
        await RecoverAsync(paths, ct);
        ct.ThrowIfCancellationRequested();

        var versionsMatch =
            archive.RrVersion is not null
            && paths.InstalledRrVersion is not null
            && string.Equals(archive.RrVersion, paths.InstalledRrVersion, StringComparison.Ordinal);
        var changes = archive
            .Entries.Where(entry => !IsVersionGated(entry.ArchivePath) || versionsMatch)
            .Select(entry => new RestoreChange(entry, ResolveTarget(entry.ArchivePath, paths)))
            .ToArray();
        foreach (var change in changes)
            EnsureNoReparsePoints(change.TargetPath, change.Entry.ArchivePath, paths);

        if (changes.Length > 0)
        {
            var journalPath = GetJournalPath(paths);
            Directory.CreateDirectory(_recoveryRoot);
            var preparingPath = journalPath + "." + Guid.NewGuid().ToString("N") + ".new";
            Directory.CreateDirectory(preparingPath);
            try
            {
                var records = new List<JournalEntry>(changes.Length);
                for (var index = 0; index < changes.Length; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    var change = changes[index];
                    var exists = File.Exists(change.TargetPath);
                    var backupName = index.ToString("D5") + ".original";
                    if (exists)
                        await CopyDurablyAsync(change.TargetPath, Path.Combine(preparingPath, backupName), ct);
                    records.Add(new JournalEntry(change.Entry.ArchivePath, exists, backupName));
                }

                var journal = new RecoveryJournal(records);
                var journalFile = Path.Combine(preparingPath, "journal.json");
                await WriteDurablyAsync(journalFile, JsonSerializer.SerializeToUtf8Bytes(journal), ct);
                if (Directory.Exists(journalPath))
                    throw new IOException("Unrecovered save restore journal exists");
                Directory.Move(preparingPath, journalPath);

                try
                {
                    foreach (var change in changes)
                    {
                        ct.ThrowIfCancellationRequested();
                        var parent = Path.GetDirectoryName(change.TargetPath)!;
                        Directory.CreateDirectory(parent);
                        EnsureNoReparsePoints(change.TargetPath, change.Entry.ArchivePath, paths);
                        var temporary = Path.Combine(parent, ".wheel-sync-" + Guid.NewGuid().ToString("N") + ".tmp");
                        try
                        {
                            await CopyDurablyAsync(change.Entry.StagedFilePath, temporary, ct);
                            File.Move(temporary, change.TargetPath, overwrite: true);
                        }
                        finally
                        {
                            if (File.Exists(temporary))
                                File.Delete(temporary);
                        }
                    }

                    var effectiveHash = await ComputeLocalHashAsync(paths, ct);
                    var committedPath = journalPath + ".committed";
                    if (Directory.Exists(committedPath))
                        Directory.Delete(committedPath, recursive: true);
                    Directory.Move(journalPath, committedPath);
                    try
                    {
                        Directory.Delete(committedPath, recursive: true);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    return effectiveHash;
                }
                catch (Exception restoreException)
                {
                    try
                    {
                        await RollBackAsync(journalPath, paths, CancellationToken.None);
                    }
                    catch (Exception rollbackException)
                    {
                        throw new AggregateException(
                            "Save restore failed and recovery is still required",
                            restoreException,
                            rollbackException
                        );
                    }
                    throw;
                }
            }
            finally
            {
                if (Directory.Exists(preparingPath))
                    Directory.Delete(preparingPath, recursive: true);
            }
        }

        return await ComputeLocalHashAsync(paths, ct);
    }

    public async Task RecoverAsync(DolphinSavePaths paths, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ct.ThrowIfCancellationRequested();
        var journalPath = GetJournalPath(paths);
        if (Directory.Exists(journalPath))
            await RollBackAsync(journalPath, paths, ct);
        var committedPath = journalPath + ".committed";
        if (Directory.Exists(committedPath))
            Directory.Delete(committedPath, recursive: true);
    }

    private async Task<string> ComputeLocalHashAsync(DolphinSavePaths paths, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var snapshot = await _archiveWriter.CreateAsync(paths, ct);
        if (snapshot is null)
            return Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant();
        try
        {
            return snapshot.Hash;
        }
        finally
        {
            if (File.Exists(snapshot.FilePath))
                File.Delete(snapshot.FilePath);
        }
    }

    private async Task RollBackAsync(string journalPath, DolphinSavePaths paths, CancellationToken ct)
    {
        var journalFile = Path.Combine(journalPath, "journal.json");
        RecoveryJournal journal;
        try
        {
            await using var stream = new FileStream(journalFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            journal =
                await JsonSerializer.DeserializeAsync<RecoveryJournal>(stream, JournalJsonOptions, ct)
                ?? throw new InvalidDataException("Recovery journal is empty");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Recovery journal is invalid; local saves were not changed", exception);
        }

        foreach (var record in journal.Entries.Reverse())
        {
            ct.ThrowIfCancellationRequested();
            if (
                record.ArchivePath is null
                || WheelSyncArchiveReader.MapKnownPath(record.ArchivePath) is null
                || record.BackupName is null
                || Path.GetFileName(record.BackupName) != record.BackupName
            )
                throw new InvalidDataException("Recovery journal contains an unsafe path");
            var target = ResolveTarget(record.ArchivePath, paths);
            EnsureNoReparsePoints(target, record.ArchivePath, paths);
            if (record.Existed)
            {
                var backup = Path.Combine(journalPath, record.BackupName);
                if (!File.Exists(backup))
                    throw new InvalidDataException("Recovery snapshot is missing");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temporary = Path.Combine(
                    Path.GetDirectoryName(target)!,
                    ".wheel-sync-recovery-" + Guid.NewGuid().ToString("N") + ".tmp"
                );
                try
                {
                    await CopyDurablyAsync(backup, temporary, ct);
                    File.Move(temporary, target, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
            }
            else if (File.Exists(target))
            {
                File.Delete(target);
            }
        }

        Directory.Delete(journalPath, recursive: true);
    }

    private string GetJournalPath(DolphinSavePaths paths)
    {
        var identity = string.Join(
            "\n",
            Path.GetFullPath(paths.UserFolder),
            Path.GetFullPath(paths.SaveRoot),
            Path.GetFullPath(paths.WiiRoot)
        );
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return Path.Combine(_recoveryRoot, key);
    }

    private static string ResolveTarget(string archivePath, DolphinSavePaths paths)
    {
        var (root, relative) = archivePath.StartsWith("RetroWFC/", StringComparison.Ordinal)
            ? (paths.SaveRoot, archivePath["RetroWFC/".Length..])
            : (paths.WiiRoot, archivePath["Wii/".Length..]);
        var fullRoot = Path.GetFullPath(root);
        var target = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Unsafe save target");
        return target;
    }

    private static void EnsureNoReparsePoints(string target, string archivePath, DolphinSavePaths paths)
    {
        var root = Path.GetFullPath(archivePath.StartsWith("RetroWFC/", StringComparison.Ordinal) ? paths.SaveRoot : paths.WiiRoot);
        var fullTarget = Path.GetFullPath(target);
        var volumeRoot = Path.GetPathRoot(root)!;
        var current = volumeRoot;
        foreach (var component in root[volumeRoot.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (File.Exists(current) || Directory.Exists(current))
                ThrowIfReparsePoint(current);
        }
        var relative = Path.GetRelativePath(root, fullTarget);
        foreach (var component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (File.Exists(current) || Directory.Exists(current))
                ThrowIfReparsePoint(current);
        }
    }

    private static void ThrowIfReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Save target contains a symlink or reparse point");
    }

    private static bool IsVersionGated(string archivePath) => archivePath == "Wii/shared2/Pulsar/RetroRewind6/RRGameSettings.pul";

    private static async Task CopyDurablyAsync(string source, string destination, CancellationToken ct)
    {
        await using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.WriteThrough
        );
        await input.CopyToAsync(output, ct);
        await output.FlushAsync(ct);
        output.Flush(flushToDisk: true);
    }

    private static async Task WriteDurablyAsync(string destination, byte[] contents, CancellationToken ct)
    {
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            8192,
            FileOptions.Asynchronous | FileOptions.WriteThrough
        );
        await output.WriteAsync(contents, ct);
        await output.FlushAsync(ct);
        output.Flush(flushToDisk: true);
    }

    private sealed record RestoreChange(ValidatedArchiveEntry Entry, string TargetPath);

    private sealed record RecoveryJournal(IReadOnlyList<JournalEntry> Entries);

    private sealed record JournalEntry(string ArchivePath, bool Existed, string BackupName);
}
