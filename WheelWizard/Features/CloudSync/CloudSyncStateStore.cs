using System.Runtime.InteropServices;
using System.Text.Json;
using WheelWizard.Services;

namespace WheelWizard.Features.CloudSync;

public sealed record SyncState(
    string DeviceId,
    string? AccountId,
    string? DolphinUserFolder,
    string? SaveRoot,
    string? WiiRoot,
    string? Rev,
    string? LocalHash,
    bool SessionPending,
    DateTimeOffset? LastSync
);

public interface ICloudSyncStateStore
{
    SyncState ReadFor(string accountId, DolphinSavePaths paths);
    void Update(SyncState state);
    void ClearAccount();
}

public sealed class CloudSyncStateStore : ICloudSyncStateStore
{
    public const string StateFileName = "cloud-sync-state.json";
    private static readonly StringComparison PathComparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly Func<string> _appDataPath;
    private readonly object _gate = new();

    public CloudSyncStateStore(Func<string>? appDataPath = null) => _appDataPath = appDataPath ?? (() => PathManager.WheelWizardAppdataPath);

    public SyncState ReadFor(string accountId, DolphinSavePaths paths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(paths);

        var userFolder = Path.GetFullPath(paths.UserFolder);
        var saveRoot = Path.GetFullPath(paths.SaveRoot);
        var wiiRoot = Path.GetFullPath(paths.WiiRoot);
        lock (_gate)
        {
            var state = ReadStoredOrFresh();
            if (
                !string.Equals(state.AccountId, accountId, StringComparison.Ordinal)
                || !SamePath(state.DolphinUserFolder, userFolder)
                || !SamePath(state.SaveRoot, saveRoot)
                || !SamePath(state.WiiRoot, wiiRoot)
            )
            {
                state = state with
                {
                    AccountId = accountId,
                    DolphinUserFolder = userFolder,
                    SaveRoot = saveRoot,
                    WiiRoot = wiiRoot,
                    Rev = null,
                    LocalHash = null,
                    SessionPending = false,
                    LastSync = null,
                };
                WriteAtomically(state);
            }

            return state;
        }
    }

    public void Update(SyncState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
            WriteAtomically(state);
    }

    public void ClearAccount()
    {
        lock (_gate)
        {
            var state = ReadStoredOrFresh() with
            {
                AccountId = null,
                DolphinUserFolder = null,
                SaveRoot = null,
                WiiRoot = null,
                Rev = null,
                LocalHash = null,
                SessionPending = false,
                LastSync = null,
            };
            WriteAtomically(state);
        }
    }

    private SyncState ReadStoredOrFresh()
    {
        var filePath = GetStateFilePath();
        if (File.Exists(filePath))
        {
            try
            {
                var state = JsonSerializer.Deserialize<SyncState>(File.ReadAllText(filePath));
                if (state is not null && !string.IsNullOrWhiteSpace(state.DeviceId))
                    return state;
            }
            catch (JsonException)
            {
                // Invalid state must behave like a fresh installation, never retain a partial baseline.
            }
        }

        var fresh = NewDeviceState();
        WriteAtomically(fresh);
        return fresh;
    }

    private void WriteAtomically(SyncState state)
    {
        var filePath = GetStateFilePath();
        var directory = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private string GetStateFilePath() => Path.Combine(_appDataPath(), StateFileName);

    private static bool SamePath(string? storedPath, string requestedPath) =>
        storedPath is not null
        && string.Equals(Path.GetFullPath(storedPath), requestedPath, PathComparison);

    private static SyncState NewDeviceState() => new(Guid.NewGuid().ToString("D"), null, null, null, null, null, null, false, null);
}
