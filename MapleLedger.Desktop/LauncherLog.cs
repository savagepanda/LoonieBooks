using System.Globalization;

namespace MapleLedger.Desktop;

/// <summary>
/// The launcher's own log (logs\launcher.log): when it started and stopped the site, and why it
/// exited. Closing the launcher stops LoonieBooks, so this is how to tell "the window was closed"
/// from a crash. If the last run has no "Launcher exiting" line, the process was ended from outside
/// (Task Manager, signing out, or a crash Windows didn't report).
/// </summary>
public static class LauncherLog
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();

    public static string Path => System.IO.Path.Combine(SiteHost.LogFolder, "launcher.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(SiteHost.LogFolder);
                var file = new FileInfo(Path);
                if (file.Exists && file.Length > MaxBytes)
                {
                    // Keep one older copy rather than letting it grow forever.
                    File.Move(Path, Path + ".old", overwrite: true);
                }

                var stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
                File.AppendAllText(Path, $"{stamp}  {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never stop the launcher.
        }
    }
}
