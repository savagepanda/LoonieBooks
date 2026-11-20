using MapleLedger.Core.Tax;
using MapleLedger.Fdx;

namespace MapleLedger.Core;

// Application API contracts (the /api/* endpoints). The /fdx/* endpoints return pure FDX types;
// these wrap them with data FDX has no place for, such as tax treatment.

/// <summary>An FDX transaction plus LoonieBooks' own annotations.</summary>
/// <param name="Tags">Ids of the <see cref="LedgerTag"/>s on this transaction; null or empty for none.</param>
/// <param name="EarnedBy">
/// For income in a shared account: the household member whose income it is (a member id), for
/// their tax estimate. Null means the account's owner, if it has one.
/// </param>
public sealed record LedgerTransaction(Transaction Transaction, TaxTag? Tax = null, bool CategoryIsManual = false, IReadOnlyList<string>? Tags = null, string? EarnedBy = null);

/// <param name="CurrentBalance">In the account's currency.</param>
/// <param name="CurrencyCode">The account's currency (CAD, USD…); null keeps an existing account's, or CAD for a new one.</param>
public sealed record AccountRequest(
    string Nickname,
    string? InstitutionName,
    AccountCategory AccountCategory,
    AccountType AccountType,
    string? AccountNumberLast4,
    decimal? CurrentBalance,
    DateTimeOffset? BalanceAsOf,
    decimal? CreditLine,
    string? CurrencyCode = null);

public sealed record ImportTransactionsRequest(
    IReadOnlyList<LedgerTransaction> Transactions,
    decimal? EndingBalance = null,
    DateTimeOffset? EndingBalanceAsOf = null,
    string Source = "paste");

public sealed record ImportTransactionsResult(Guid BatchId, int Received, int Added, int Duplicates);

/// <summary>One past import into an account. <paramref name="Remaining"/> of its transactions are still in the ledger.</summary>
public sealed record ImportSummary(Guid Id, string AccountId, string Source, DateTimeOffset ImportedAt, int Added, int Duplicates, int Remaining);

public sealed record UndoImportResult(int Removed);

/// <summary>
/// Two transactions of one account that look like the same one imported twice (see
/// <see cref="Import.DuplicateFinder"/>). Merging keeps <paramref name="Keep"/> by default: the one
/// with receipts or the user's own category and tax choices, else the longer description.
/// </summary>
public sealed record PossibleDuplicate(LedgerTransaction Keep, LedgerTransaction Remove, int DaysApart);

/// <summary>Deletes <see cref="RemoveId"/>, first moving its receipts, category and tax choices onto <see cref="KeepId"/>.</summary>
public sealed record MergeDuplicateRequest(string KeepId, string RemoveId);

/// <summary>Two transactions the user says are both real, so they stop being flagged as duplicates.</summary>
public sealed record KeepBothRequest(string FirstId, string SecondId);

/// <summary>Sets one category on several transactions of an account, e.g. all from one merchant.</summary>
public sealed record ClassifyManyRequest(IReadOnlyList<string> TransactionIds, string Category, string? SubCategory = null);

/// <summary>What "start over" deleted.</summary>
public sealed record ClearLedgerResult(int Accounts, int Transactions, int Receipts, int HouseholdMembers);

public sealed record TransactionFilter(
    DateOnly? From = null,
    DateOnly? To = null,
    string? AccountId = null,
    string? Category = null,
    string? TaxCode = null,
    string? Search = null,
    int Limit = 500,
    // A sub-category of Category, or Categories.NoSubCategory for transactions filed without one.
    string? SubCategory = null,
    // Any of these accounts (with AccountId, both apply); null or empty for every account.
    IReadOnlyList<string>? AccountIds = null,
    // With Category: only what counts as this kind. Matters for Uncategorized, which is income
    // when money comes in and spending when it goes out (Categories.KindOf).
    CategoryKind? Kind = null,
    // Transactions with any of these tags (LedgerTag ids); null or empty for no tag filter.
    IReadOnlyList<string>? TagIds = null,
    // false: only categories nobody has reviewed yet (set by the built-in rules or remembered from
    // the merchant's earlier transactions); true: only ones the user chose or confirmed; null: both.
    bool? Reviewed = null);

/// <summary>
/// Everything in one person's ledger, as a portable file: the browser-storage backup format, and
/// what "move my data to the cloud" uploads.
/// </summary>
/// <param name="Receipts">Attached receipt files (version 2 and later).</param>
/// <param name="Household">The people tax claims are assigned to (version 2 and later).</param>
/// <param name="Categories">Categories the user added (optional; older backups have none).</param>
/// <param name="Tags">The ledger's tags (optional; older backups have none). Transactions refer to them by id.</param>
/// <param name="TaxSituations">Each person's province and withholding per tax year (optional; older backups have none).</param>
/// <param name="AccountHolders">Whose each account is: account id to household member id (optional; shared accounts are left out).</param>
/// <param name="Holdings">Investment accounts' holdings snapshots (optional; older backups have none).</param>
/// <param name="ExchangeRates">The rates holdings in other currencies are valued at (optional; older backups have none).</param>
/// <param name="Properties">Real estate, with the accounts secured by each (optional; older backups have none).</param>
/// <param name="HousePriceIndex">House price index readings that carry the properties' values forward (optional).</param>
/// <param name="Budgets">Each year's budget lines (optional; older backups have none).</param>
public sealed record LedgerBackup(
    int Version,
    DateTimeOffset ExportedAt,
    IReadOnlyList<Account> Accounts,
    IReadOnlyList<LedgerTransaction> Transactions,
    IReadOnlyList<ReceiptFile>? Receipts = null,
    IReadOnlyList<HouseholdMember>? Household = null,
    IReadOnlyList<CustomCategory>? Categories = null,
    IReadOnlyList<LedgerTag>? Tags = null,
    IReadOnlyList<Tax.TaxSituationEntry>? TaxSituations = null,
    IReadOnlyDictionary<string, string>? AccountHolders = null,
    IReadOnlyList<Investments.HoldingsSnapshot>? Holdings = null,
    IReadOnlyList<Investments.ExchangeRate>? ExchangeRates = null,
    IReadOnlyList<RealEstate.RealEstateProperty>? Properties = null,
    IReadOnlyList<RealEstate.HpiReading>? HousePriceIndex = null,
    IReadOnlyList<Budgets.BudgetTarget>? Budgets = null)
{
    public const int CurrentVersion = 2;
}

public sealed record RestoreResult(int AccountsAdded, int TransactionsAdded, int Duplicates, int ReceiptsAdded = 0);

/// <summary>A receipt attached to a transaction, without its content.</summary>
public sealed record ReceiptInfo(Guid Id, string AccountId, string TransactionId, string FileName, string ContentType, long Size, DateTimeOffset UploadedAt);

public sealed record ReceiptFile(ReceiptInfo Info, byte[] Content);

public sealed record UploadReceiptRequest(string FileName, string ContentType, byte[] Content);

/// <summary>What a receipt says, as far as it could be read. Any field can be missing.</summary>
/// <param name="OtherDate">
/// The other reading of an ambiguous numeric date: 02/10/2026 is February 10 on some tills and
/// October 2 on others. <see cref="Date"/> holds the more recent reading.
/// </param>
public sealed record ReceiptFields(decimal? Total, DateOnly? Date, string? Merchant, DateOnly? OtherDate = null);

/// <summary>
/// A "send from phone" session: the phone opens <see cref="UploadUrl"/> (shown as a QR code) and
/// the computer collects the photo with <see cref="PickupToken"/>, which only it knows.
/// </summary>
public sealed record ReceiptRelaySession(string UploadUrl, string PickupToken, DateTimeOffset ExpiresAt, string QrSvg);

/// <summary>
/// "Connect a phone" on a shared desktop install: a device that opens <see cref="Url"/> (shown as a
/// QR code) once before <see cref="ExpiresAt"/> is signed in without the household passcode.
/// </summary>
public sealed record LanPairing(string Url, DateTimeOffset ExpiresAt, string QrSvg);

/// <summary>Whether the server can read receipts with Claude (an Anthropic API key is configured).</summary>
public sealed record ReceiptReaderStatus(bool Available);

/// <summary>A transaction a receipt may belong to, with why it was suggested.</summary>
public sealed record ReceiptMatch(LedgerTransaction Item, int Score, IReadOnlyList<string> Reasons);

/// <summary>The signed-in user as the API sees them.</summary>
public sealed record UserProfile(string Id, string Email, string? DisplayName, bool IsPremium);

/// <summary>Updates how a transaction is classified. A null <see cref="Tax"/> clears the tax tag.</summary>
public sealed record ClassifyTransactionRequest(string Category, string? SubCategory, TaxTag? Tax = null);

/// <summary>Changes only the tax tag. A null <see cref="Tax"/> clears it.</summary>
public sealed record SetTaxTagRequest(TaxTag? Tax);

/// <summary>
/// Claims the medical expenses of the 12-month window ending in <see cref="EndMonth"/> of the tax
/// year, for one person's claims (see <see cref="Tax.TaxReportBuilder.ForPerson"/>) or everyone's.
/// </summary>
public sealed record ApplyMedicalWindowRequest(int EndMonth, string? Person = null);

/// <summary>Someone in the household who claims deductions and credits on their own return.</summary>
public sealed record HouseholdMember(string Id, string Name)
{
    public const int MaxNameLength = 64;
}

public sealed record HouseholdMemberRequest(string Name);

/// <summary>
/// A category the user added: a new top-level category (<see cref="SubCategory"/> null, counted as
/// spending) or a detail under an existing one.
/// </summary>
public sealed record CustomCategory(string Id, string Category, string? SubCategory)
{
    public const int MaxNameLength = 48;
    public const int MaxCount = 200;
}

/// <summary>Adds a top-level category (no <see cref="SubCategory"/>) or a detail under <see cref="Category"/>.</summary>
public sealed record CustomCategoryRequest(string Category, string? SubCategory = null);

/// <summary>
/// A label the user puts on transactions to group them across categories and accounts ("Rental",
/// "Cottage", "Wedding"). A transaction can have several; filters show those with any of the picked ones.
/// </summary>
public sealed record LedgerTag(string Id, string Name)
{
    public const int MaxNameLength = 32;
    public const int MaxCount = 100;
    public const int MaxPerTransaction = 20;
}

/// <summary>Adds a tag, or renames one.</summary>
public sealed record TagRequest(string Name);

/// <summary>A tag and how many transactions have it.</summary>
public sealed record TagUsage(LedgerTag Tag, int Transactions);

/// <summary>Replaces a transaction's tags.</summary>
public sealed record SetTagsRequest(IReadOnlyList<string> TagIds);

/// <summary>One transaction, by its key.</summary>
public sealed record TransactionKey(string AccountId, string TransactionId);

/// <summary>Adds one tag to (or, with <see cref="Remove"/>, takes it off) several transactions, e.g. all of a filtered list.</summary>
public sealed record TagManyRequest(IReadOnlyList<TransactionKey> Transactions, string TagId, bool Remove = false);

/// <summary>
/// Gives several transactions one tax treatment (a <see cref="Tax.TaxCategories"/> code), e.g. all
/// of a filtered list; a null code marks them not claimable.
/// </summary>
public sealed record SetTaxManyRequest(IReadOnlyList<TransactionKey> Transactions, string? TaxCode);

/// <summary>Confirms the categories of several transactions as they are, so they no longer need review.</summary>
public sealed record MarkReviewedRequest(IReadOnlyList<TransactionKey> Transactions);

/// <summary>
/// Sets the business-use share on every claim of one tax category in a year, optionally only those
/// in one spending category (all "Bills &amp; Utilities" at 30%). A null percent means all of it.
/// </summary>
public sealed record SetBusinessUseRequest(string TaxCode, string? Category, decimal? Percent);

public sealed record SetBusinessUseResult(int Changed);

public sealed record ApplyMedicalWindowResult(int Changed);
