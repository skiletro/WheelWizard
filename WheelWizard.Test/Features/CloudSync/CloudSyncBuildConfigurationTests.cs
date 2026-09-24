using WheelWizard.Features.CloudSync;

namespace WheelWizard.Test.Features.CloudSync;

public sealed class CloudSyncBuildConfigurationTests
{
    [Fact]
    public void Default_build_embeds_the_public_dropbox_app_key()
    {
        // Catches a missing or mistyped csproj default, which silently downgrades
        // every local build to the "app key is not configured" state.
        Assert.Equal("42eh8jvnnx5vqnd", CloudSyncBuildConfiguration.AppKey);
    }
}
