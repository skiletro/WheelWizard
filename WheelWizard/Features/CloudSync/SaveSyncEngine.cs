namespace WheelWizard.Features.CloudSync;

public enum SyncAction
{
    Noop,
    FirstUpload,
    Pull,
    Push,
    Conflict,
    FreshDevice,
    LocalMissing,
}

public static class SaveSyncEngine
{
    public static SyncAction Decide(
        string? remoteRev,
        string? baselineRev,
        string? localHash,
        string? baselineHash
    )
    {
        if (localHash is null && baselineHash is not null)
            return SyncAction.LocalMissing;

        if (remoteRev is null)
            return localHash is null ? SyncAction.Noop : SyncAction.FirstUpload;

        if (baselineRev is null)
            return localHash is null ? SyncAction.Pull : SyncAction.FreshDevice;

        var localChanged = localHash != baselineHash;
        var remoteChanged = remoteRev != baselineRev;

        return localChanged && remoteChanged
            ? SyncAction.Conflict
            : localChanged
                ? SyncAction.Push
                : remoteChanged
                    ? SyncAction.Pull
                    : SyncAction.Noop;
    }
}
