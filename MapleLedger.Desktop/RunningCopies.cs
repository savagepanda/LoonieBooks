using System.ComponentModel;
using System.Diagnostics;

namespace MapleLedger.Desktop;

/// <summary>
/// Other LoonieBooks launchers this Windows user has open. Usually the previous version, still
/// running when a new one is unzipped beside it and opened: the new one closes it and takes over,
/// so upgrading is just opening the new copy.
/// </summary>
internal static class RunningCopies
{
    public sealed record Copy(Process Process, string ExePath, Version Version)
    {
        public string Folder => Path.GetDirectoryName(ExePath)!;
    }

    /// <summary>This copy's build (<c>FileVersion</c>, stamped yyyy.MMdd.HHmm by publishing; 1.0.0.0 for local builds and older versions).</summary>
    public static Version Mine => VersionOf(Environment.ProcessPath!);

    public static Version VersionOf(string exePath)
    {
        var info = FileVersionInfo.GetVersionInfo(exePath);
        return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
    }

    public static bool IsThisCopy(Copy copy) =>
        string.Equals(Path.GetFullPath(copy.ExePath), Path.GetFullPath(Environment.ProcessPath!), StringComparison.OrdinalIgnoreCase);

    /// <summary>The launcher's exe name before the project was renamed LoonieBooks. A copy of it still open is an older version.</summary>
    private const string LegacyName = "MapleLedger";

    public static List<Copy> Find()
    {
        var copies = new List<Copy>();
        var names = new[] { Path.GetFileNameWithoutExtension(Environment.ProcessPath!), LegacyName }.Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var process in names.SelectMany(Others))
        {
            if (PathOf(process) is { } path)
            {
                copies.Add(new Copy(process, path, VersionOf(path)));
            }
            else
            {
                process.Dispose();
            }
        }

        return copies;
    }

    /// <summary>
    /// The event a launcher waits on to stop (<see cref="Close"/>, desktopbuild.bat). Its window's close
    /// button may only hide it, and a hidden window can't be closed from outside.
    /// </summary>
    public static string StopSignalName(int processId) => $@"Local\MapleLedger.Desktop.Stop.{processId}";

    /// <summary>
    /// Asks a copy to stop, so it stops its own web host and API and logs why: by its stop signal, or
    /// (versions before it had one) by closing its window. By force if it hasn't gone within the timeout
    /// (its hosts are in its job object and stop with it). Then stops anything still running from its
    /// folder. Returns whether it's gone.
    /// </summary>
    public static bool Close(Copy copy, TimeSpan timeout)
    {
        var process = copy.Process;
        try
        {
            if (EventWaitHandle.TryOpenExisting(StopSignalName(process.Id), out var stop))
            {
                using (stop) stop.Set();
            }
            else
            {
                process.CloseMainWindow();
            }

            if (!process.WaitForExit(timeout))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Already gone, or can't be touched (then HasExited below says so).
        }

        var folder = copy.Folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var name in new[] { "MapleLedger.Web", "MapleLedger.ApiService" })
        {
            foreach (var host in Others(name))
            {
                using (host)
                {
                    if (PathOf(host)?.StartsWith(folder, StringComparison.OrdinalIgnoreCase) != true) continue;
                    try
                    {
                        host.Kill();
                        host.WaitForExit(TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                    {
                    }
                }
            }
        }

        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>This Windows session's processes with that name, other than this one.</summary>
    private static IEnumerable<Process> Others(string name)
    {
        var session = Process.GetCurrentProcess().SessionId;
        foreach (var process in Process.GetProcessesByName(name))
        {
            if (process.Id != Environment.ProcessId && process.SessionId == session)
            {
                yield return process;
            }
            else
            {
                process.Dispose();
            }
        }
    }

    private static string? PathOf(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }
}
