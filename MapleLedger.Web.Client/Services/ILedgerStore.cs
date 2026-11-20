using MapleLedger.Core;
using MapleLedger.Core.Budgets;
using MapleLedger.Core.Categorization;
using MapleLedger.Core.Investments;
using MapleLedger.Core.RealEstate;
using MapleLedger.Core.Reports;
using MapleLedger.Core.Tax;
using MapleLedger.Fdx;

namespace MapleLedger.Web.Client.Services;

public enum StorageLocation
{
    /// <summary>SQLite in this browser's private storage. The default, and the only option without premium.</summary>
    Browser,

    /// <summary>The LoonieBooks server's database (SQL Server, or the desktop app's SQLite file). Premium users only.</summary>
    Cloud,
}

/// <summary>A ledger operation failed; <see cref="Exception.Message"/> is safe to show the user.</summary>
public sealed class LedgerStoreException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Where the pages read and write the ledger. Both implementations run the same
/// <c>LedgerService</c>: one in the browser against SQLite, one on the server behind the API.
/// </summary>
public interface ILedgerStore
{
    StorageLocation Location { get; }

    Task<IReadOnlyList<Account>> GetAccountsAsync(CancellationToken ct = default);
    Task<Account> CreateAccountAsync(AccountRequest request);
    Task<Account> UpdateAccountAsync(string accountId, AccountRequest request);
    Task DeleteAccountAsync(string accountId);

    Task<ImportTransactionsResult> ImportAsync(string accountId, ImportTransactionsRequest request);
    Task<IReadOnlyList<ImportSummary>> GetImportsAsync(string accountId, CancellationToken ct = default);

    /// <summary>Removes the transactions an import added; returns how many.</summary>
    Task<int> UndoImportAsync(Guid importId);
    Task<IReadOnlyList<LedgerTransaction>> SearchTransactionsAsync(TransactionFilter filter, CancellationToken ct = default);
    Task ClassifyAsync(Transaction transaction, ClassifyTransactionRequest request);

    /// <summary>The account's other transactions from the same merchant, newest first.</summary>
    Task<IReadOnlyList<LedgerTransaction>> FindSimilarAsync(Transaction transaction);

    /// <summary>What the user chose for each merchant, to categorize and tag new imports the same way.</summary>
    Task<IReadOnlyList<MerchantChoice>> GetMerchantChoicesAsync(CancellationToken ct = default);

    /// <summary>Confirms the categories of several transactions as they are; returns how many weren't reviewed yet.</summary>
    Task<int> MarkReviewedAsync(MarkReviewedRequest request);

    /// <summary>Gives several transactions one tax treatment, or marks them not claimable; returns how many changed.</summary>
    Task<int> SetTaxManyAsync(SetTaxManyRequest request);

    /// <summary>Transactions that look like one imported twice (e.g. from a PDF and a CSV), newest first.</summary>
    Task<IReadOnlyList<PossibleDuplicate>> FindDuplicatesAsync(CancellationToken ct = default);

    /// <summary>Deletes one of a duplicate pair, moving its receipts and choices onto the other.</summary>
    Task MergeDuplicateAsync(string accountId, MergeDuplicateRequest request);

    /// <summary>Stops flagging two look-alike transactions as duplicates.</summary>
    Task KeepBothAsync(string accountId, KeepBothRequest request);

    /// <summary>Sets one category on several transactions of an account; returns how many changed.</summary>
    Task<int> ClassifyManyAsync(string accountId, ClassifyManyRequest request);

    /// <summary>Changes only the tax tag (null clears it), leaving the category alone.</summary>
    Task SetTaxTagAsync(Transaction transaction, TaxTag? tax);

    /// <param name="tagIds">Only transactions with any of these tags; null or empty for all.</param>
    /// <param name="category">Only this category (and <paramref name="subCategory"/> detail, or <see cref="Categories.NoSubCategory"/>); null for everything.</param>
    Task<DashboardSummary> GetDashboardAsync(DateOnly from, DateOnly to, string? category = null, string? subCategory = null, IReadOnlyCollection<string>? accountIds = null, CategoryKind? kind = null, IReadOnlyCollection<string>? tagIds = null, CancellationToken ct = default);

    /// <summary>Net worth at the end of each month, worked back from today's balances; follows the account filter, not tags.</summary>
    Task<IReadOnlyList<MonthlyNetWorth>> GetNetWorthByMonthAsync(DateOnly from, DateOnly to, IReadOnlyCollection<string>? accountIds = null, CancellationToken ct = default);

    /// <summary>One account's balance at the end of each month, in its own currency; empty when it has no balance set.</summary>
    Task<IReadOnlyList<MonthlyAccountValue>> GetAccountValueByMonthAsync(string accountId, DateOnly from, DateOnly to, CancellationToken ct = default);
    /// <param name="person">Whose claims: null for everyone, a household member id, or <see cref="TaxReportBuilder.Unassigned"/>.</param>
    Task<TaxReport> GetTaxReportAsync(int year, string? person = null, CancellationToken ct = default);
    Task<TaxReview> GetTaxReviewAsync(int year, CancellationToken ct = default);
    Task<MedicalEstimate> GetMedicalEstimateAsync(int year, decimal? netIncome, string? person = null, CancellationToken ct = default);

    /// <summary>Claims the medical expenses of one 12-month window for the year; returns how many transactions moved.</summary>
    Task<int> ApplyMedicalWindowAsync(int year, int endMonth, string? person = null);
    Task<string> GetTaxReportCsvAsync(int year, string? person = null);

    /// <summary>Sets the business-use share on a year's claims of one tax category; returns how many changed.</summary>
    Task<int> SetBusinessUseAsync(int year, SetBusinessUseRequest request, string? person = null);

    /// <summary>Everyone's tax estimate for the year, from their slips and their tagged claims.</summary>
    Task<TaxEstimateSet> GetTaxEstimatesAsync(int year, CancellationToken ct = default);

    /// <summary>Saves one person's province and withholding for the year.</summary>
    Task<TaxSituationEntry> SaveTaxSituationAsync(int year, string personId, TaxSituation situation);

    /// <summary>Whose each account is: account id to household member id. Shared accounts aren't listed.</summary>
    Task<IReadOnlyDictionary<string, string>> GetAccountHoldersAsync(CancellationToken ct = default);

    /// <summary>Sets whose an account is, or makes it shared (null).</summary>
    Task SetAccountHolderAsync(string accountId, string? personId);

    /// <summary>Sets whose income a deposit in a shared account is, or clears it (null).</summary>
    Task SetEarnedByAsync(Transaction transaction, string? personId);

    /// <summary>Each investment account's latest holdings snapshot.</summary>
    Task<IReadOnlyList<HoldingsSnapshot>> GetHoldingsAsync(CancellationToken ct = default);

    /// <summary>Replaces an investment account's holdings with a new snapshot.</summary>
    Task<ImportHoldingsResult> ImportHoldingsAsync(string accountId, ImportHoldingsRequest request);

    /// <summary>Removes an account's holdings snapshot.</summary>
    Task DeleteHoldingsAsync(string accountId);

    /// <summary>Prices holdings from the quote feed; balances that were their holdings' total follow.</summary>
    Task<ApplyQuotesResult> ApplyQuotesAsync(ApplyQuotesRequest request);

    /// <summary>The month-end closes saved for the holdings' listings.</summary>
    Task<IReadOnlyList<PriceReading>> GetPriceHistoryAsync(CancellationToken ct = default);

    /// <summary>Saves month-end closes from the monthly price history feed, for net worth by month.</summary>
    Task<ApplyPriceHistoryResult> ApplyPriceHistoryAsync(ApplyPriceHistoryRequest request);

    /// <summary>The ledger's rate for each currency it has one for.</summary>
    Task<IReadOnlyList<ExchangeRate>> GetExchangeRatesAsync(CancellationToken ct = default);

    /// <summary>Sets the ledger's rate for a currency, revaluing the holdings in it.</summary>
    Task<ExchangeRate> SetExchangeRateAsync(ExchangeRate rate);

    /// <summary>The Bank of Canada's monthly average rates saved for the accounts' currencies.</summary>
    Task<IReadOnlyList<MonthlyExchangeRate>> GetMonthlyExchangeRatesAsync(CancellationToken ct = default);

    /// <summary>Saves monthly average rates, which spending in other currencies is converted at.</summary>
    Task<ApplyMonthlyExchangeRatesResult> ApplyMonthlyExchangeRatesAsync(ApplyMonthlyExchangeRatesRequest request);

    /// <summary>The year's budget beside its spending, last year's budget and the suggestions.</summary>
    Task<BudgetPlan> GetBudgetPlanAsync(int year, CancellationToken ct = default);

    /// <summary>Sets (or, with no amount, removes) lines of the year's budget; returns the year's lines.</summary>
    Task<IReadOnlyList<BudgetTarget>> SetBudgetAsync(int year, SetBudgetRequest request);

    /// <summary>Each month of the years, up to this one: every budget line's share and the month's spending.</summary>
    Task<IReadOnlyList<BudgetMonth>> GetBudgetMonthsAsync(int fromYear, int toYear, CancellationToken ct = default);

    Task<IReadOnlyList<RealEstateProperty>> GetPropertiesAsync(CancellationToken ct = default);
    Task<RealEstateProperty> CreatePropertyAsync(PropertyRequest request);
    Task<RealEstateProperty> UpdatePropertyAsync(string id, PropertyRequest request);
    Task DeletePropertyAsync(string id);

    /// <summary>The house price index readings, by area then month.</summary>
    Task<IReadOnlyList<HpiReading>> GetHpiReadingsAsync(CancellationToken ct = default);

    /// <summary>Records an area's index for a month, replacing any reading for that month.</summary>
    Task<HpiReading> SetHpiReadingAsync(HpiReading reading);
    Task DeleteHpiReadingAsync(string area, DateOnly month);

    /// <summary>Makes each area's readings the house price index feed's series for it.</summary>
    Task<ApplyHpiResult> ApplyHpiAsync(ApplyHpiRequest request);

    Task<IReadOnlyList<HouseholdMember>> GetHouseholdAsync(CancellationToken ct = default);
    Task<HouseholdMember> AddHouseholdMemberAsync(string name);
    Task<HouseholdMember> RenameHouseholdMemberAsync(string id, string name);

    /// <summary>Removes a person; their claims become unassigned.</summary>
    Task DeleteHouseholdMemberAsync(string id);

    /// <summary>The categories this ledger added to the built-in tree.</summary>
    Task<IReadOnlyList<CustomCategory>> GetCustomCategoriesAsync(CancellationToken ct = default);
    Task<CustomCategory> AddCustomCategoryAsync(CustomCategoryRequest request);

    /// <summary>Removes an added category; its transactions move up to the parent category, or to Uncategorized.</summary>
    Task DeleteCustomCategoryAsync(string id);

    /// <summary>The ledger's tags, by name.</summary>
    Task<IReadOnlyList<LedgerTag>> GetTagsAsync(CancellationToken ct = default);

    /// <summary>Every tag with how many transactions have it.</summary>
    Task<IReadOnlyList<TagUsage>> GetTagUsageAsync(CancellationToken ct = default);
    Task<LedgerTag> AddTagAsync(string name);
    Task<LedgerTag> RenameTagAsync(string id, string name);

    /// <summary>Deletes a tag and takes it off its transactions.</summary>
    Task DeleteTagAsync(string id);

    /// <summary>Replaces a transaction's tags.</summary>
    Task SetTagsAsync(Transaction transaction, IReadOnlyList<string> tagIds);

    /// <summary>Adds a tag to several transactions, or takes it off them; returns how many changed.</summary>
    Task<int> TagManyAsync(TagManyRequest request);

    Task<IReadOnlyList<ReceiptInfo>> GetReceiptsAsync(Transaction transaction);
    Task<ReceiptInfo> AddReceiptAsync(Transaction transaction, UploadReceiptRequest request);
    Task<byte[]> GetReceiptContentAsync(ReceiptInfo receipt);
    Task DeleteReceiptAsync(ReceiptInfo receipt);

    /// <summary>Spending a receipt may belong to, best first.</summary>
    Task<IReadOnlyList<ReceiptMatch>> FindReceiptMatchesAsync(ReceiptFields receipt);

    Task<LedgerBackup> ExportAsync();

    /// <summary>Merges a backup into this ledger; anything already present is skipped.</summary>
    Task<RestoreResult> RestoreAsync(LedgerBackup backup);

    /// <summary>Deletes the whole ledger in this store, to start over.</summary>
    Task<ClearLedgerResult> ClearAsync();
}
