using WheelWizard.Settings;

namespace WheelWizard.Features.CloudSync;

public enum SyncUiStatus
{
    NotConfigured,
    Disconnected,
    Idle,
    Busy,
    Conflict,
    CloudFound,
    QueuedForExit,
    ReconnectNeeded,
    Error,
}

public sealed record SyncUiState(SyncUiStatus Status, string? Message = null, string? AccountId = null, DateTimeOffset? LastSync = null);
public enum LaunchSyncDecision { Ready, NeedsChoice, ProceedWithoutSync, Cancelled }
public enum ConflictChoice { UseCloud, KeepLocal, PlayWithoutSync, Cancel }

public interface ICloudSyncPrompt
{
    Task<ConflictChoice> ChooseAsync(SyncUiState state, CancellationToken ct);
}

public interface ICloudSyncCoordinator
{
    SyncUiState State { get; }
    event EventHandler<SyncUiState>? StateChanged;
    Task ConnectAsync(CancellationToken ct);
    Task RestoreSessionAsync(CancellationToken ct);
    Task AutoSyncAsync(CancellationToken ct);
    Task SyncNowAsync(CancellationToken ct);
    Task<LaunchSyncDecision> PrepareLaunchAsync(CancellationToken ct);
    Task SessionStartedAsync(CancellationToken ct);
    Task SessionLaunchFailedAsync(CancellationToken ct);
    Task SessionEndedAsync(CancellationToken ct);
    Task ReconcileSessionAsync(bool dolphinConfirmedRunning, CancellationToken ct);
    Task ResolveAsync(ConflictChoice choice, CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);
    void SetAutoSync(bool enabled);
}

public sealed class CloudSyncCoordinator : ICloudSyncCoordinator
{
    private readonly IDropboxOAuthService _oauth;
    private readonly IDropboxSyncApi _dropbox;
    private readonly IDolphinSavePathSource _paths;
    private readonly ICloudSyncStateStore _stateStore;
    private readonly IWheelSyncArchiveWriter _writer;
    private readonly IWheelSyncArchiveReader _reader;
    private readonly ISaveRestoreService _restore;
    private readonly ICloudSyncPrompt _prompt;
    private readonly ISettingsManager _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accountId;
    private string? _email;
    private DolphinSavePaths? _resolvedPaths;
    private SyncState? _baseline;
    private string? _pendingRev;
    private SyncAction? _pendingAction;
    private bool _sessionActive;
    private bool _manualSyncQueued;
    private ConflictChoice? _deferredChoice;

    public CloudSyncCoordinator(IDropboxOAuthService oauth, IDropboxSyncApi dropbox, IDolphinSavePathSource paths,
        ICloudSyncStateStore stateStore, IWheelSyncArchiveWriter writer, IWheelSyncArchiveReader reader,
        ISaveRestoreService restore, ICloudSyncPrompt prompt, ISettingsManager settings, bool configured = true)
    {
        _oauth = oauth;
        _dropbox = dropbox;
        _paths = paths;
        _stateStore = stateStore;
        _writer = writer;
        _reader = reader;
        _restore = restore;
        _prompt = prompt;
        _settings = settings;
        State = new SyncUiState(configured ? SyncUiStatus.Disconnected : SyncUiStatus.NotConfigured);
    }

    public SyncUiState State { get; private set; }
    public event EventHandler<SyncUiState>? StateChanged;

    public async Task ConnectAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Publish(SyncUiStatus.Busy, "Connecting to Dropbox.");
            var identity = await _oauth.ConnectAsync(ct).ConfigureAwait(false);
            _accountId = identity.AccountId;
            _email = identity.Email;
            _resolvedPaths = _paths.TryResolve();
            if (_resolvedPaths is null)
            {
                Publish(SyncUiStatus.NotConfigured, "Configure a Dolphin user folder before syncing.", identity.AccountId);
                return;
            }
            _baseline = _stateStore.ReadFor(identity.AccountId, _resolvedPaths);
            Publish(SyncUiStatus.Idle, identity.Email, identity.AccountId, _baseline.LastSync);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Publish(SyncUiStatus.Error, exception.Message);
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task RestoreSessionAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _accountId = null;
            _email = null;
            _baseline = null;
            _resolvedPaths = _paths.TryResolve();
            var persisted = _stateStore.ReadPersistedAccount();
            if (persisted.Status == PersistedAccountStatus.Missing)
            {
                Publish(SyncUiStatus.Disconnected, "No saved Dropbox connection exists on this device.");
                return;
            }
            if (persisted.Status == PersistedAccountStatus.Corrupt)
            {
                Publish(SyncUiStatus.Disconnected, "Saved Dropbox connection state is corrupt; reconnect to sync.");
                return;
            }
            if (string.IsNullOrWhiteSpace(persisted.AccountId))
            {
                Publish(SyncUiStatus.Disconnected, "Dropbox is disconnected.");
                return;
            }
            if (_resolvedPaths is null)
            {
                Publish(SyncUiStatus.NotConfigured, "Configure a Dolphin user folder before syncing.", persisted.AccountId);
                return;
            }

            var baseline = _stateStore.ReadFor(persisted.AccountId, _resolvedPaths);
            if (!await _oauth.RestoreSessionAsync(persisted.AccountId, ct).ConfigureAwait(false))
            {
                Publish(SyncUiStatus.Disconnected, "Saved Dropbox credentials are missing; reconnect to sync.", persisted.AccountId);
                return;
            }
            _accountId = persisted.AccountId;
            _baseline = baseline;
            Publish(SyncUiStatus.Idle, "Dropbox connection restored.", _accountId, baseline.LastSync);
        }
        catch (SecureStorageUnavailableException exception)
        {
            Publish(SyncUiStatus.Error, exception.Message);
        }
        finally { _gate.Release(); }
    }

    public Task AutoSyncAsync(CancellationToken ct) => RunSyncAsync(manual: false, ct);
    public Task SyncNowAsync(CancellationToken ct) => RunSyncAsync(manual: true, ct);

    public async Task<LaunchSyncDecision> PrepareLaunchAsync(CancellationToken ct)
    {
        await AutoSyncAsync(ct).ConfigureAwait(false);
        if (State.Status is SyncUiStatus.Conflict or SyncUiStatus.CloudFound)
        {
            var choice = await _prompt.ChooseAsync(State, ct).ConfigureAwait(false);
            if (choice == ConflictChoice.Cancel)
                return LaunchSyncDecision.Cancelled;
            await ResolveAsync(choice, ct).ConfigureAwait(false);
            return choice == ConflictChoice.PlayWithoutSync ? LaunchSyncDecision.ProceedWithoutSync : LaunchSyncDecision.Ready;
        }
        return State.Status is SyncUiStatus.Error or SyncUiStatus.ReconnectNeeded
            ? LaunchSyncDecision.ProceedWithoutSync
            : LaunchSyncDecision.Ready;
    }

    public async Task SessionStartedAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _sessionActive = true;
            if (_baseline is not null)
            {
                _baseline = _baseline with { SessionPending = true };
                _stateStore.Update(_baseline);
                if (_settings.Get<bool>(_settings.CLOUD_AUTO_SYNC))
                {
                    try { await _dropbox.WriteLockAsync(new DropboxSyncLock(_baseline.DeviceId, DateTimeOffset.UtcNow), ct).ConfigureAwait(false); } catch { }
                }
            }
        }
        finally { _gate.Release(); }
    }

    public async Task SessionLaunchFailedAsync(CancellationToken ct) => await EndSessionAsync(syncAfter: false, ct).ConfigureAwait(false);
    public async Task SessionEndedAsync(CancellationToken ct) => await EndSessionAsync(syncAfter: true, ct).ConfigureAwait(false);

    public async Task ReconcileSessionAsync(bool dolphinConfirmedRunning, CancellationToken ct)
    {
        if (dolphinConfirmedRunning)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                RefreshResolvedPaths();
                _sessionActive = true;
                Publish(SyncUiStatus.QueuedForExit, "Sync deferred while Dolphin is running.", _accountId, _baseline?.LastSync);
            }
            finally { _gate.Release(); }
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { RefreshResolvedPaths(); }
        finally { _gate.Release(); }
        await EndSessionAsync(syncAfter: true, ct).ConfigureAwait(false);
    }

    public async Task ResolveAsync(ConflictChoice choice, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (choice == ConflictChoice.Cancel || _pendingAction is null || _resolvedPaths is null || _accountId is null || _baseline is null)
                return;
            if (_sessionActive || _baseline.SessionPending)
            {
                _deferredChoice = choice;
                Publish(SyncUiStatus.QueuedForExit, "Conflict choice queued until Dolphin exits.", _accountId, _baseline.LastSync);
                return;
            }
            if (choice == ConflictChoice.PlayWithoutSync)
            {
                _pendingAction = null;
                Publish(SyncUiStatus.Idle, "Playing without syncing.", _accountId, _baseline.LastSync);
                return;
            }
            if (choice == ConflictChoice.KeepLocal)
            {
                await PushAsync(_pendingRev, ct).ConfigureAwait(false);
            }
            else if (_pendingRev is not null)
            {
                await PullAsync(_pendingRev, ct).ConfigureAwait(false);
            }
            _pendingAction = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Publish(IsReconnectRequired(exception) ? SyncUiStatus.ReconnectNeeded : SyncUiStatus.Error, exception.Message, _accountId, _baseline?.LastSync);
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _oauth.DisconnectAsync(ct).ConfigureAwait(false);
            _stateStore.ClearAccount();
            _accountId = null;
            _email = null;
            _baseline = null;
            _resolvedPaths = null;
            _pendingAction = null;
            Publish(SyncUiStatus.Disconnected);
        }
        finally { _gate.Release(); }
    }

    public void SetAutoSync(bool enabled) => _settings.Set(_settings.CLOUD_AUTO_SYNC, enabled);

    private async Task RunSyncAsync(bool manual, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!manual && !_settings.Get<bool>(_settings.CLOUD_AUTO_SYNC)) return;
            if (_accountId is null || _resolvedPaths is null || _baseline is null)
            {
                if (State.Status is not (SyncUiStatus.Disconnected or SyncUiStatus.NotConfigured))
                    Publish(SyncUiStatus.Disconnected, "Connect Dropbox to sync.");
                return;
            }
            if (_sessionActive || _baseline.SessionPending)
            {
                if (manual) _manualSyncQueued = true;
                Publish(SyncUiStatus.QueuedForExit, "Sync queued until Dolphin exits.", _accountId, _baseline.LastSync);
                return;
            }
            await SyncCoreAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Publish(IsReconnectRequired(exception) ? SyncUiStatus.ReconnectNeeded : SyncUiStatus.Error, exception.Message, _accountId, _baseline?.LastSync);
            if (manual) throw;
        }
        finally { _gate.Release(); }
    }

    private async Task SyncCoreAsync(CancellationToken ct)
    {
        RefreshResolvedPaths();
        var paths = _resolvedPaths!;
        var baseline = _baseline!;
        Publish(SyncUiStatus.Busy, "Checking cloud save.", _accountId, baseline.LastSync);
        await _restore.RecoverAsync(paths, ct).ConfigureAwait(false);
        var remote = await _dropbox.GetSaveMetadataAsync(ct).ConfigureAwait(false);
        var cloudLock = await _dropbox.ReadLockAsync(ct).ConfigureAwait(false);
        if (IsFreshForeignLock(cloudLock, baseline.DeviceId))
        {
            Publish(SyncUiStatus.Idle, "Sync deferred while another device holds a fresh Dropbox lock.", _accountId, baseline.LastSync);
            return;
        }
        var snapshot = await MakeSnapshotAsync(paths, ct).ConfigureAwait(false);
        var localHash = snapshot?.Hash;
        var action = SaveSyncEngine.Decide(remote?.Rev, baseline.Rev, localHash, baseline.LocalHash);
        DeleteSnapshot(snapshot);
        if (action is SyncAction.Conflict or SyncAction.FreshDevice)
        {
            _pendingRev = remote?.Rev;
            _pendingAction = action;
            var status = action == SyncAction.Conflict ? SyncUiStatus.Conflict : SyncUiStatus.CloudFound;
            Publish(status, action == SyncAction.Conflict ? "Cloud and local saves both changed." : "Cloud save found; choose whether to restore it.", _accountId, baseline.LastSync);
            return;
        }
        if (action == SyncAction.Pull)
        {
            await PullAsync(remote!.Rev, ct, localHash).ConfigureAwait(false);
            return;
        }
        if (action is SyncAction.Push or SyncAction.FirstUpload)
        {
            await PushAsync(remote?.Rev, ct).ConfigureAwait(false);
            return;
        }
        if (action == SyncAction.LocalMissing)
        {
            Publish(SyncUiStatus.Error, "Local save archive is empty; refusing to overwrite existing cloud save.", _accountId, baseline.LastSync);
            return;
        }
        Publish(SyncUiStatus.Idle, null, _accountId, baseline.LastSync);
    }

    private async Task PushAsync(string? expectedRev, CancellationToken ct)
    {
        var paths = _resolvedPaths!;
        if (!RefreshPathsBeforeOperation(paths, expectedRev)) return;
        paths = _resolvedPaths!;
        var snapshot = await MakeSnapshotAsync(paths, ct).ConfigureAwait(false);
        if (snapshot is null)
        {
            Publish(SyncUiStatus.Error, "No local save data to upload.", _accountId, _baseline?.LastSync);
            return;
        }
        try
        {
            var current = await MakeSnapshotAsync(paths, ct).ConfigureAwait(false);
            try
            {
                if (current?.Hash != snapshot.Hash)
                {
                    Publish(SyncUiStatus.Error, "Local saves changed while preparing upload; retry sync.", _accountId, _baseline?.LastSync);
                    return;
                }
            }
            finally { DeleteSnapshot(current); }
            if (!RefreshPathsBeforeOperation(paths, expectedRev)) return;
            try
            {
                var metadata = await _dropbox.UploadAsync(snapshot.FilePath, expectedRev, ct).ConfigureAwait(false);
                await CommitSuccessAsync(metadata.Rev, snapshot.Hash, ct).ConfigureAwait(false);
            }
            catch (DropboxConflictException)
            {
                await RecheckAfterUploadConflictAsync(ct).ConfigureAwait(false);
            }
        }
        finally { DeleteSnapshot(snapshot); }
    }

    private async Task PullAsync(string rev, CancellationToken ct, string? expectedLocalHash = null)
    {
        var paths = _resolvedPaths!;
        var path = Path.Combine(Path.GetTempPath(), $"wheelwizard-cloud-{Guid.NewGuid():N}.zip");
        var beforeDownload = expectedLocalHash is null
            ? await MakeSnapshotAsync(paths, ct).ConfigureAwait(false)
            : null;
        var localHashAtDownload = expectedLocalHash ?? beforeDownload?.Hash;
        try
        {
            var downloaded = await _dropbox.DownloadAsync(rev, path, ct).ConfigureAwait(false);
            if (downloaded.Rev != rev) throw new InvalidDataException("Dropbox save revision changed during download.");
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = await _reader.ValidateAsync(stream, ct).ConfigureAwait(false);

            RefreshResolvedPaths();
            if (!SameRoots(paths, _resolvedPaths!))
            {
                _pendingRev = rev;
                _pendingAction = SyncAction.FreshDevice;
                Publish(SyncUiStatus.CloudFound, "Dolphin save paths changed during download; choose whether to restore cloud save.", _accountId, _baseline?.LastSync);
                return;
            }

            var current = await MakeSnapshotAsync(paths, ct).ConfigureAwait(false);
            try
            {
                if (current?.Hash != localHashAtDownload)
                {
                    var latest = await _dropbox.GetSaveMetadataAsync(ct).ConfigureAwait(false);
                    if (latest is null)
                    {
                        Publish(SyncUiStatus.Error, "Local saves changed during download; cloud save is now missing.", _accountId, _baseline?.LastSync);
                        return;
                    }
                    _pendingRev = latest.Rev;
                    _pendingAction = SyncAction.Conflict;
                    Publish(SyncUiStatus.Conflict, "Local saves changed during cloud download; choose how to resolve the conflict.", _accountId, _baseline?.LastSync);
                    return;
                }
            }
            finally { DeleteSnapshot(current); }

            var restoredHash = await _restore.RestoreAsync(archive, paths, ct).ConfigureAwait(false);
            await CommitSuccessAsync(rev, restoredHash, ct).ConfigureAwait(false);
        }
        finally
        {
            DeleteSnapshot(beforeDownload);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private async Task<ArchiveSnapshot?> MakeSnapshotAsync(DolphinSavePaths paths, CancellationToken ct) => await _writer.CreateAsync(paths, ct).ConfigureAwait(false);

    private static void DeleteSnapshot(ArchiveSnapshot? snapshot)
    {
        if (snapshot is not null && File.Exists(snapshot.FilePath)) File.Delete(snapshot.FilePath);
    }

    private async Task CommitSuccessAsync(string rev, string hash, CancellationToken ct)
    {
        var syncedAt = DateTimeOffset.UtcNow;
        _baseline = _baseline! with { Rev = rev, LocalHash = hash, LastSync = syncedAt };
        _stateStore.Update(_baseline);
        try { await _dropbox.WriteStateAsync(new DropboxSyncState(_baseline.DeviceId, syncedAt, hash), ct).ConfigureAwait(false); }
        catch (Exception) { }
        Publish(SyncUiStatus.Idle, null, _accountId, syncedAt);
    }

    private async Task RecheckAfterUploadConflictAsync(CancellationToken ct)
    {
        var remote = await _dropbox.GetSaveMetadataAsync(ct).ConfigureAwait(false);
        if (remote is null)
        {
            Publish(SyncUiStatus.Error, "Dropbox save changed during upload and is now missing.", _accountId, _baseline?.LastSync);
            return;
        }

        var local = await MakeSnapshotAsync(_resolvedPaths!, ct).ConfigureAwait(false);
        if (local is null)
        {
            Publish(SyncUiStatus.Error, "Local save archive is empty after upload conflict.", _accountId, _baseline?.LastSync);
            return;
        }
        try
        {
            var action = SaveSyncEngine.Decide(remote.Rev, _baseline!.Rev, local.Hash, _baseline.LocalHash);
            _pendingRev = remote.Rev;
            _pendingAction = action is SyncAction.FreshDevice ? SyncAction.FreshDevice : SyncAction.Conflict;
            var status = _pendingAction == SyncAction.FreshDevice ? SyncUiStatus.CloudFound : SyncUiStatus.Conflict;
            Publish(status, "Dropbox save changed during upload; choose how to resolve it.", _accountId, _baseline.LastSync);
        }
        finally { DeleteSnapshot(local); }
    }

    private async Task EndSessionAsync(bool syncAfter, CancellationToken ct)
    {
        ConflictChoice? deferredChoice;
        bool manualSyncQueued;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _sessionActive = false;
            if (_baseline is not null)
            {
                _baseline = _baseline with { SessionPending = false };
                _stateStore.Update(_baseline);
                if (_accountId is not null && _settings.Get<bool>(_settings.CLOUD_AUTO_SYNC))
                {
                    try { await _dropbox.DeleteLockAsync(ct).ConfigureAwait(false); } catch { }
                }
            }
            deferredChoice = _deferredChoice;
            _deferredChoice = null;
            manualSyncQueued = _manualSyncQueued;
            _manualSyncQueued = false;
        }
        finally { _gate.Release(); }

        if (deferredChoice is { } choice) await ResolveAsync(choice, ct).ConfigureAwait(false);
        if (manualSyncQueued) await SyncNowAsync(ct).ConfigureAwait(false);
        else if (syncAfter) await AutoSyncAsync(ct).ConfigureAwait(false);
    }

    private void RefreshResolvedPaths()
    {
        var currentPaths = _paths.TryResolve();
        if (currentPaths is null)
        {
            _resolvedPaths = null;
            _baseline = null;
            throw new InvalidOperationException("Dolphin save paths are unavailable.");
        }
        if (_resolvedPaths is not null && SameRoots(_resolvedPaths, currentPaths))
            return;

        _resolvedPaths = currentPaths;
        if (_accountId is not null)
            _baseline = _stateStore.ReadFor(_accountId, currentPaths);
    }

    private static bool SameRoots(DolphinSavePaths left, DolphinSavePaths right) =>
        SamePath(left.UserFolder, right.UserFolder)
        && SamePath(left.SaveRoot, right.SaveRoot)
        && SamePath(left.WiiRoot, right.WiiRoot);

    private bool RefreshPathsBeforeOperation(DolphinSavePaths paths, string? remoteRev)
    {
        RefreshResolvedPaths();
        if (SameRoots(paths, _resolvedPaths!)) return true;

        if (remoteRev is not null)
        {
            _pendingRev = remoteRev;
            _pendingAction = SyncAction.FreshDevice;
            Publish(SyncUiStatus.CloudFound, "Dolphin save paths changed before sync; choose how to use the cloud save.", _accountId, _baseline?.LastSync);
        }
        else
        {
            Publish(SyncUiStatus.Idle, "Dolphin save paths changed before sync; retry to use new paths.", _accountId, _baseline?.LastSync);
        }
        return false;
    }

    private static bool SamePath(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
    }

    private static bool IsFreshForeignLock(DropboxSyncLock? cloudLock, string deviceId)
    {
        if (cloudLock is null || string.Equals(cloudLock.DeviceId, deviceId, StringComparison.Ordinal)) return false;
        return DateTimeOffset.UtcNow - cloudLock.ServerModified <= TimeSpan.FromHours(2);
    }

    private static bool IsReconnectRequired(Exception exception) =>
        exception is DropboxReconnectRequiredException
        or DropboxAccessException { StatusCode: System.Net.HttpStatusCode.Unauthorized };

    private void Publish(SyncUiStatus status, string? message = null, string? account = null, DateTimeOffset? lastSync = null)
    {
        State = new SyncUiState(status, message, account, lastSync);
        StateChanged?.Invoke(this, State);
    }
}
