using System.Diagnostics;
using WheelWizard.Services;
using WheelWizard.Views;

namespace WheelWizard.Features.CloudSync;

public interface IDolphinProcessProbe
{
    bool IsDolphinRunning(DolphinSavePaths paths, IReadOnlyCollection<int> launchedProcessIds);
}

public interface IDolphinSessionMonitor
{
    void Register(Process started, DolphinSavePaths paths);
    bool IsDolphinRunning(DolphinSavePaths paths);
    Task WaitForExitAsync(DolphinSavePaths paths, CancellationToken ct);
}

public sealed class DolphinSessionMonitor : IDolphinSessionMonitor
{
    private readonly IDolphinProcessProbe _processProbe;
    private readonly TimeSpan _pollInterval;
    private readonly object _sync = new();
    private readonly Dictionary<string, Dictionary<int, Process>> _sessions = new(StringComparer.Ordinal);

    public DolphinSessionMonitor(IDolphinProcessProbe processProbe, TimeSpan? pollInterval = null)
    {
        _processProbe = processProbe;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
    }

    public void Register(Process started, DolphinSavePaths paths)
    {
        ArgumentNullException.ThrowIfNull(started);
        var key = GetPathKey(paths);
        lock (_sync)
        {
            if (!_sessions.TryGetValue(key, out var processes))
                _sessions[key] = processes = [];
            processes[started.Id] = started;
        }
    }

    public bool IsDolphinRunning(DolphinSavePaths paths)
    {
        var key = GetPathKey(paths);
        int[] processIds;
        lock (_sync)
            processIds = _sessions.TryGetValue(key, out var processes) ? processes.Keys.ToArray() : [];
        return _processProbe.IsDolphinRunning(paths, processIds);
    }

    public async Task WaitForExitAsync(DolphinSavePaths paths, CancellationToken ct)
    {
        while (IsDolphinRunning(paths))
            await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
        lock (_sync)
        {
            if (_sessions.Remove(GetPathKey(paths), out var processes))
            {
                foreach (var process in processes.Values)
                    process.Dispose();
            }
        }
    }

    private static string GetPathKey(DolphinSavePaths paths) => string.Join('\n',
        Path.GetFullPath(paths.UserFolder), Path.GetFullPath(paths.SaveRoot), Path.GetFullPath(paths.WiiRoot));
}

public sealed class SystemDolphinProcessProbe : IDolphinProcessProbe
{
    public bool IsDolphinRunning(DolphinSavePaths paths, IReadOnlyCollection<int> launchedProcessIds)
    {
        var configuredPath = PathManager.DolphinFilePath;
        var configuredName = Path.GetFileNameWithoutExtension(configuredPath);
        if (string.IsNullOrWhiteSpace(configuredName))
            return true; // Unknown configuration cannot prove that Dolphin exited.
        var processNames = PathManager.IsFlatpakDolphinFilePath(configuredPath)
            ? new[] { configuredName, "dolphin-emu", "Dolphin" }.Distinct(StringComparer.OrdinalIgnoreCase)
            : new[] { configuredName };

        foreach (var process in processNames.SelectMany(Process.GetProcessesByName))
        {
            using (process)
            {
                try
                {
                    var executable = process.MainModule?.FileName;
                    var configuredExecutable = !string.IsNullOrEmpty(executable) && SamePath(executable, configuredPath);
                    var flatpakDolphin = PathManager.IsFlatpakDolphinFilePath(configuredPath)
                        && string.Equals(Path.GetFileNameWithoutExtension(executable), process.ProcessName, StringComparison.OrdinalIgnoreCase);
                    if (configuredExecutable || flatpakDolphin)
                    {
                        // Command lines are not consistently available across platforms. A matching
                        // Dolphin executable is conservatively treated as live if path ownership is
                        // ambiguous; false positives defer sync, never restore into a running game.
                        return true;
                    }
                    if (launchedProcessIds.Contains(process.Id) && !process.HasExited)
                        return true;
                }
                catch
                {
                    // Access denied or a transient process race cannot be treated as a confirmed exit.
                    if (launchedProcessIds.Contains(process.Id))
                        return true;
                }
            }
        }

        foreach (var processId in launchedProcessIds)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (!process.HasExited)
                    return true; // A still-live wrapper may still be starting its Dolphin child.
            }
            catch (ArgumentException) { }
            catch { return true; }
        }

        return false;
    }

    private static bool SamePath(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison); }
        catch { return false; }
    }
}
