namespace MapleLedger.Fdx;

/// <summary>FDX offset-based paging metadata.</summary>
public sealed record PageMetadata(string? NextOffset = null, string? PrevOffset = null, int? TotalElements = null);

public sealed record HateoasLink(string Href, string? Action = null, string? Rel = null);

public sealed record PageMetadataLinks(HateoasLink? Next = null, HateoasLink? Prev = null);

public sealed record AccountsResponse(IReadOnlyList<Account> Accounts, PageMetadata? Page = null, PageMetadataLinks? Links = null);

public sealed record TransactionsResponse(IReadOnlyList<Transaction> Transactions, PageMetadata? Page = null, PageMetadataLinks? Links = null);

/// <summary>FDX error payload.</summary>
public sealed record FdxError(string Code, string Message, string? DebugMessage = null)
{
    public static FdxError AccountNotFound(string accountId) => new("701", "Account not found", $"No account with id '{accountId}'.");
}
