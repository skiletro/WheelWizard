using System.Diagnostics;
using WheelWizard.Features.CloudSync;
using Xunit;

namespace WheelWizard.Test.Features.CloudSync;

public sealed class DolphinSessionMonitorTests
{
    [Fact]
    public async Task Wrapper_exit_does_not_complete_session_while_dolphin_child_is_alive()
    {
        var probe = new FakeDolphinProcessProbe { Running = true };
        var monitor = new DolphinSessionMonitor(probe, TimeSpan.FromMilliseconds(10));
        var paths = new DolphinSavePaths("/dolphin", "/dolphin/RetroWFC", "/dolphin/Wii", "1");
        using var wrapper = StartExitedWrapper();
        monitor.Register(wrapper, paths);
        await wrapper.WaitForExitAsync();
        var wait = monitor.WaitForExitAsync(paths, CancellationToken.None);
        await Task.Delay(50);

        Assert.True(monitor.IsDolphinRunning(paths));
        Assert.False(wait.IsCompleted);
        probe.Running = false;
        await wait.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(monitor.IsDolphinRunning(paths));
    }

    [Fact]
    public async Task Wait_for_exit_stays_pending_when_process_probe_cannot_verify_exit()
    {
        var probe = new FakeDolphinProcessProbe { Running = true };
        var monitor = new DolphinSessionMonitor(probe, TimeSpan.FromMilliseconds(10));
        var paths = new DolphinSavePaths("/dolphin", "/dolphin/RetroWFC", "/dolphin/Wii", "1");
        using var process = Process.GetCurrentProcess();
        monitor.Register(process, paths);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.WaitForExitAsync(paths, cancellation.Token));
        Assert.True(monitor.IsDolphinRunning(paths));
    }

    private static Process StartExitedWrapper()
    {
        if (OperatingSystem.IsWindows())
            return Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false })!;
        return Process.Start(new ProcessStartInfo("/bin/sh", "-c 'exit 0'") { UseShellExecute = false })!;
    }

    private sealed class FakeDolphinProcessProbe : IDolphinProcessProbe
    {
        public bool Running { get; set; }
        public bool IsDolphinRunning(DolphinSavePaths paths, IReadOnlyCollection<int> launchedProcessIds) => Running;
    }
}
