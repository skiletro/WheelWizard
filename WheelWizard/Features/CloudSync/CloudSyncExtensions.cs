using System.Diagnostics;
using System.Reflection;
using WheelWizard.Services;
using WheelWizard.Views.Popups;

namespace WheelWizard.Features.CloudSync;

public static class CloudSyncExtensions
{
    public static IServiceCollection AddCloudSync(this IServiceCollection services)
    {
        services.AddSingleton<IDolphinSavePathSource, PathManagerDolphinSavePathSource>();
        services.AddSingleton<ICloudSyncStateStore, CloudSyncStateStore>();
        services.AddSingleton<IWheelSyncArchiveWriter, WheelSyncArchiveWriter>();
        services.AddSingleton<IWheelSyncArchiveReader>(_ => new WheelSyncArchiveReader(Path.Combine(PathManager.WheelWizardAppdataPath, "cloud-sync-staging")));
        services.AddSingleton<ISaveRestoreService>(provider => new SaveRestoreService(
            provider.GetRequiredService<IWheelSyncArchiveWriter>(),
            Path.Combine(PathManager.WheelWizardAppdataPath, "cloud-sync-recovery")));
        services.AddSingleton<IProtectedTokenStore>(_ => OperatingSystem.IsWindows()
            ? new ProtectedTokenStoreWindows()
            : OperatingSystem.IsMacOS()
                ? new ProtectedTokenStoreMac()
                : new ProtectedTokenStoreLinux());
        services.AddSingleton<ILoopbackOAuthCallbackHandler, DropboxLoopbackOAuthCallbackHandler>();
        services.AddSingleton<IDropboxOAuthService>(provider =>
        {
            var appKey = CloudSyncBuildConfiguration.AppKey;
            if (string.IsNullOrWhiteSpace(appKey))
                return new UnconfiguredDropboxOAuthService();
            return new DropboxOAuthService(
                new HttpClient(), appKey,
                provider.GetRequiredService<IProtectedTokenStore>(),
                provider.GetRequiredService<ILoopbackOAuthCallbackHandler>(),
                url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }),
                () => null);
        });
        services.AddSingleton<IDropboxSyncApi>(provider => new DropboxSyncApi(provider.GetRequiredService<IDropboxOAuthService>(), new HttpClient()));
        services.AddSingleton<ICloudSyncPrompt, CloudSyncConflictWindow>();
        services.AddSingleton<ICloudSyncCoordinator>(provider => new CloudSyncCoordinator(
            provider.GetRequiredService<IDropboxOAuthService>(),
            provider.GetRequiredService<IDropboxSyncApi>(),
            provider.GetRequiredService<IDolphinSavePathSource>(),
            provider.GetRequiredService<ICloudSyncStateStore>(),
            provider.GetRequiredService<IWheelSyncArchiveWriter>(),
            provider.GetRequiredService<IWheelSyncArchiveReader>(),
            provider.GetRequiredService<ISaveRestoreService>(),
            provider.GetRequiredService<ICloudSyncPrompt>(),
            provider.GetRequiredService<WheelWizard.Settings.ISettingsManager>(),
            !string.IsNullOrWhiteSpace(CloudSyncBuildConfiguration.AppKey)));
        return services;
    }
}

public static class CloudSyncBuildConfiguration
{
    public static string AppKey { get; } = typeof(CloudSyncBuildConfiguration).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "WheelSyncDropboxAppKey")?.Value ?? string.Empty;
}

internal sealed class UnconfiguredDropboxOAuthService : IDropboxOAuthService
{
    private static InvalidOperationException Unconfigured() => new("Dropbox sync is not configured in this build. Configure the public Dropbox app key and rebuild.");
    public Task<(string AccountId, string Email)> ConnectAsync(CancellationToken ct) => Task.FromException<(string, string)>(Unconfigured());
    public Task<bool> RestoreSessionAsync(string accountId, CancellationToken ct) => Task.FromResult(false);
    public Task<string> GetAccessTokenAsync(CancellationToken ct, bool forceRefresh = false) => Task.FromException<string>(Unconfigured());
    public Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;
}
