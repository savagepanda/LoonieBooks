using System.Diagnostics;

namespace MapleLedger.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // One launcher per Windows user: a second copy couldn't take the port anyway. A different
        // copy (such as a new version unzipped beside the old one) replaces the one that's open.
        using var instance = new Mutex(initiallyOwned: true, @"Local\MapleLedger.Desktop", out var first);
        if (!first && !TakeOver(instance))
        {
            return;
        }

        LauncherLog.Write($"Launcher started (version {RunningCopies.Mine}, {Environment.ProcessPath}, process {Environment.ProcessId}).");

        // Errors on the window's thread: log them and keep the site running rather than closing.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            LauncherLog.Write("Unexpected error in the launcher window: " + e.Exception);
            MessageBox.Show(
                $"LoonieBooks' launcher hit an unexpected error, but is still running.\n\n{e.Exception.Message}\n\nDetails are in {LauncherLog.Path}.",
                "LoonieBooks", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };

        // Errors anywhere else end the process (and with it the site): at least record why.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LauncherLog.Write("Launcher crashed: " + e.ExceptionObject);

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        LauncherLog.Write("Launcher exiting.");
    }

    /// <summary>
    /// Another launcher is open. If it's this same copy, or a newer version, show that one instead.
    /// Otherwise (usually the previous version) close it, and return true once this one may start.
    /// </summary>
    private static bool TakeOver(Mutex instance)
    {
        var running = RunningCopies.Find();
        try
        {
            if (running.Count == 0)
            {
                // It's just closing.
                return Acquire(instance, TimeSpan.FromSeconds(5)) || ShowRunning(null);
            }

            if (running.Any(RunningCopies.IsThisCopy)) return ShowRunning(null);

            var mine = RunningCopies.Mine;
            if (running.FirstOrDefault(c => c.Version > mine) is { } newer) return ShowRunning(newer);

            foreach (var copy in running)
            {
                LauncherLog.Write($"Replacing the LoonieBooks open from {copy.Folder} (version {copy.Version}) with this copy (version {mine}, {AppContext.BaseDirectory}).");
                RunningCopies.Close(copy, TimeSpan.FromSeconds(15));
            }

            if (Acquire(instance, TimeSpan.FromSeconds(10))) return true;

            LauncherLog.Write("The LoonieBooks that was open didn't close.");
            MessageBox.Show(
                $"Another copy of LoonieBooks is open ({running[0].Folder}) and didn't close. Close its window, then open this one again.",
                "LoonieBooks", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        finally
        {
            foreach (var copy in running) copy.Process.Dispose();
        }
    }

    /// <summary>Opens the site of the launcher that's already running. Returns false: this one doesn't start.</summary>
    private static bool ShowRunning(RunningCopies.Copy? newer)
    {
        LauncherLog.Write(newer is null
            ? $"Opened again ({Environment.ProcessPath}) while LoonieBooks is open; showing that one."
            : $"Opened ({Environment.ProcessPath}, version {RunningCopies.Mine}) while a newer version is open from {newer.Folder} (version {newer.Version}); showing that one.");
        if (newer is not null)
        {
            MessageBox.Show(
                $"A newer version of LoonieBooks is already open (from {newer.Folder}), so this older copy won't replace it. " +
                "You can delete this copy's folder.",
                "LoonieBooks", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        if (DesktopSettings.Load() is { } settings)
        {
            Process.Start(new ProcessStartInfo(settings.BrowseUrl) { UseShellExecute = true });
        }
        else if (newer is null)
        {
            MessageBox.Show("LoonieBooks is already open. Look for its window on the taskbar, or its maple leaf icon by the clock.", "LoonieBooks");
        }

        return false;
    }

    private static bool Acquire(Mutex instance, TimeSpan timeout)
    {
        try
        {
            return instance.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            // The other launcher ended without releasing it (closed or stopped): it's ours now.
            return true;
        }
    }
}
