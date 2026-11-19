namespace MapleLedger.Data;

/// <summary>
/// How a transaction's tags are stored: their ids in one column, each wrapped in bars
/// ("|tg-1|tg-2|"), so "has this tag" is a plain substring test that SQLite and SQL Server both run
/// in the database, and adding or removing a tag is a single bulk update. Ids, not names, so a tag
/// can be renamed without touching its transactions.
/// </summary>
public static class TagList
{
    public const char Separator = '|';
    public const int MaxIdLength = 15; // "tg-" and 12 hex digits

    /// <summary>Room for <see cref="Core.LedgerTag.MaxPerTransaction"/> tags with a margin.</summary>
    public const int MaxLength = 512;

    public static string NewId() => $"tg-{Guid.NewGuid():N}"[..MaxIdLength];

    /// <summary>What a stored list contains when it has this tag.</summary>
    public static string Marker(string id) => $"{Separator}{id}{Separator}";

    /// <summary>The stored form of these ids (in order, without repeats), or null for none.</summary>
    public static string? Format(IEnumerable<string>? ids)
    {
        var list = (ids ?? []).Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
        return list.Count == 0 ? null : $"{Separator}{string.Join(Separator, list)}{Separator}";
    }

    public static IReadOnlyList<string> Parse(string? stored) =>
        string.IsNullOrEmpty(stored) ? [] : stored.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
}
