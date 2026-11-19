namespace MapleLedger.Core.Desktop;

/// <summary>
/// How the API tells the desktop launcher why it stopped while opening the ledger, so the launcher
/// can say what happened instead of showing a generic error.
/// </summary>
public static class ApiExitCodes
{
    /// <summary>
    /// Updating the ledger to this version failed. The ledger was put back exactly as it was
    /// before (from the copy taken just before updating), so the previous version still opens it.
    /// </summary>
    public const int UpgradeFailed = 3;

    /// <summary>The ledger was last opened by a newer version, which this one can't read safely.</summary>
    public const int LedgerFromNewerVersion = 4;

    /// <summary>Updating failed and the ledger couldn't be put back either; the copy taken before is in the backups folder.</summary>
    public const int UpgradeFailedNotRestored = 5;
}
