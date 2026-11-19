using MapleLedger.Core;

namespace MapleLedger.Web.Client.Services;

/// <summary>
/// A category at either level: a top-level category alone, or with one of its sub-categories.
/// <paramref name="Kind"/> says which side of the dashboard it was picked from (income or
/// spending), which narrows Uncategorized to its money in or out.
/// </summary>
public sealed record CategoryChoice(string Category, string? SubCategory = null, CategoryKind? Kind = null)
{
    public string Label => Categories.Label(Category, SubCategory);
}
