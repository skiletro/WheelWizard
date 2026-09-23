using WheelWizard.Features.CloudSync;

namespace WheelWizard.Test.Features.CloudSync;

public sealed class SaveSyncEngineTests
{
    public static TheoryData<string?, string?, string?, string?, SyncAction> Decisions =>
        new()
        {
            { null, null, "a", null, SyncAction.FirstUpload },
            { null, null, null, null, SyncAction.Noop },
            { "r1", null, null, null, SyncAction.Pull },
            { "r1", null, "a", null, SyncAction.FreshDevice },
            { "r1", "r1", "b", "a", SyncAction.Push },
            { "r2", "r1", "a", "a", SyncAction.Pull },
            { "r2", "r1", "b", "a", SyncAction.Conflict },
            { "r1", "r1", null, "a", SyncAction.LocalMissing },
            { null, "r1", null, "a", SyncAction.LocalMissing },
            { "r1", "r1", "a", "a", SyncAction.Noop },
            { "r2", "r1", "a", "b", SyncAction.Conflict },
        };

    [Theory]
    [MemberData(nameof(Decisions))]
    public void Decide_returns_expected_sync_action(
        string? remoteRev,
        string? baselineRev,
        string? localHash,
        string? baselineHash,
        SyncAction expected
    )
    {
        Assert.Equal(expected, SaveSyncEngine.Decide(remoteRev, baselineRev, localHash, baselineHash));
    }
}
