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
                        var userPathMatch = DolphinProcessUserPath.Match(ReadCommandLine(process.Id), paths.UserFolder);
                        if (userPathMatch == false)
                            continue;
                        // Missing or inaccessible command lines leave ownership uncertain. Defer sync;
                        // focus/startup reconciliation retries after the process exits.
                        return true;
                    }
                    if (launchedProcessIds.Contains(process.Id) && !process.HasExited)
                        return true;
                }
                catch
                {
                    // An uninspectable candidate cannot be treated as a confirmed exit.
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

    private static string? ReadCommandLine(int processId)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var commandLinePath = $"/proc/{processId}/cmdline";
                return File.Exists(commandLinePath) ? File.ReadAllText(commandLinePath).Replace('\0', ' ') : null;
            }
            if (OperatingSystem.IsMacOS())
            {
                using var process = Process.Start(new ProcessStartInfo("ps")
                {
                    ArgumentList = { "-o", "args=", "-p", processId.ToString() },
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (process is null)
                    return null;
                var commandLine = process.StandardOutput.ReadToEnd();
                return process.WaitForExit(1000) && process.ExitCode == 0 ? commandLine : null;
            }
        }
        catch { }
        return null;
    }

    private static bool SamePath(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison); }
        catch { return false; }
    }
}

public static class DolphinProcessUserPath
{
    private static readonly System.Text.RegularExpressions.Regex UserArgument = new(
        "(?:^|\\s)(?:-u|--user)(?:=|\\s+)(?:\\\"([^\\\"]*)\\\"|'([^']*)'|([^\\s]+))",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant
    );

    /// <summary>Returns null when process arguments cannot prove which Dolphin user folder is active.</summary>
    public static bool? Match(string? commandLine, string resolvedUserFolder)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return null;
        var match = UserArgument.Match(commandLine);
        if (!match.Success)
            return null;
        var argument = match.Groups[1].Success ? match.Groups[1].Value
            : match.Groups[2].Success ? match.Groups[2].Value
            : match.Groups[3].Value;
        try
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(Path.GetFullPath(argument), Path.GetFullPath(resolvedUserFolder), comparison);
        }
        catch { return null; }
    }
}
