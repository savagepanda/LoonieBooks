namespace MapleLedger.Core;

/// <summary>
/// The tags every ledger starts with, like the built-in category tree: always there, and not
/// renamed or removed. A ledger's own tags join them. Each has a fixed id that is never reused for
/// another tag, as transactions store it.
/// </summary>
public static class DefaultTags
{
    // Ids are the same length as generated ones ("tg-" and 12 characters), which TagList relies on.
    public static IReadOnlyList<LedgerTag> All { get; } =
    [
        new("tg-default-busi", "Business"),
        new("tg-default-home", "Home Office"),
        new("tg-default-move", "Moving"),
        new("tg-default-reim", "Reimbursable"),
        new("tg-default-reno", "Renovation"),
        new("tg-default-rent", "Rental"),
        new("tg-default-shar", "Shared"),
        new("tg-default-vaca", "Vacation"),
    ];

    private static readonly HashSet<string> Ids = All.Select(t => t.Id).ToHashSet();

    public static bool IsDefault(string? id) => id is not null && Ids.Contains(id);

    /// <summary>
    /// A ledger's tags: the built-in ones and its own, by name. A tag of its own with a built-in's
    /// name (made before the built-ins existed) takes that built-in's place.
    /// </summary>
    public static IReadOnlyList<LedgerTag> WithCustom(IEnumerable<LedgerTag> custom)
    {
        var own = custom.ToList();
        return All
            .Where(d => !own.Any(c => string.Equals(c.Name, d.Name, StringComparison.OrdinalIgnoreCase)))
            .Concat(own)
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Id, StringComparer.Ordinal)
            .ToList();
    }
}
