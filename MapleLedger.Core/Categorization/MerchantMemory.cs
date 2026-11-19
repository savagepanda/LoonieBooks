using MapleLedger.Fdx;

namespace MapleLedger.Core.Categorization;

/// <summary>
/// What the ledger remembers about one merchant's money in, or out, of one account (see
/// <see cref="MerchantName.Key"/>): the category the user last chose or confirmed, and the tags
/// on the newest transaction.
/// </summary>
/// <param name="Category">Null when the user hasn't chosen one for this merchant.</param>
/// <param name="ChosenOn">The date of the transaction <paramref name="Category"/> was chosen for.</param>
/// <param name="Tags">Ids of the tags on the merchant's newest transaction; null or empty for none.</param>
public sealed record MerchantChoice(
    string AccountId,
    string MerchantKey,
    DebitCreditMemo Direction,
    string? Category,
    string? SubCategory,
    DateOnly? ChosenOn,
    IReadOnlyList<string>? Tags);

/// <summary>What a new transaction takes from the merchant's earlier ones. Null parts aren't remembered.</summary>
public sealed record MerchantRecollection(CategoryAssignment? Category, IReadOnlyList<string>? Tags);

/// <summary>
/// Remembers the user's choices per merchant, so a new transaction from a merchant they already
/// categorized or tagged gets the same. A category comes from the same account if one was chosen
/// there, else from the newest choice in any account; tags only from the same account, since they
/// often name what the account is for (a rental property's bills).
/// </summary>
public sealed class MerchantMemory
{
    public static readonly MerchantMemory Empty = new([]);

    private readonly Dictionary<(string AccountId, string Key, DebitCreditMemo Direction), MerchantChoice> _inAccount = [];
    private readonly Dictionary<(string Key, DebitCreditMemo Direction), MerchantChoice> _anyAccount = [];

    public MerchantMemory(IEnumerable<MerchantChoice> choices)
    {
        foreach (var choice in choices)
        {
            if (choice.MerchantKey.Length == 0) continue;
            _inAccount[(choice.AccountId, choice.MerchantKey, choice.Direction)] = choice;
            if (choice.Category is not null
                && (!_anyAccount.TryGetValue((choice.MerchantKey, choice.Direction), out var newest) || choice.ChosenOn > newest.ChosenOn))
            {
                _anyAccount[(choice.MerchantKey, choice.Direction)] = choice;
            }
        }
    }

    public bool IsEmpty => _inAccount.Count == 0;

    public MerchantRecollection Recall(string accountId, string? description, DebitCreditMemo direction)
    {
        var key = MerchantName.Key(description);
        if (key.Length == 0) return new(null, null);

        _inAccount.TryGetValue((accountId, key, direction), out var here);
        var categorySource = here?.Category is not null ? here : _anyAccount.GetValueOrDefault((key, direction));
        return new(
            categorySource?.Category is { } category ? new CategoryAssignment(category, categorySource.SubCategory) : null,
            here?.Tags is { Count: > 0 } tags ? tags : null);
    }
}
