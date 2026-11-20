using MapleLedger.Core;
using MapleLedger.Core.Budgets;
using MapleLedger.Core.Categorization;
using MapleLedger.Core.Investments;
using MapleLedger.Core.RealEstate;
using MapleLedger.Core.Reports;
using MapleLedger.Core.Tax;
using MapleLedger.Data;
using MapleLedger.Fdx;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor;

namespace MapleLedger.Web.Client.Services;

/// <summary>
/// The ledger kept in this browser: SQLite (via SqliteWasmBlazor) on the Origin Private File
/// System. Nothing here is sent to the server.
/// </summary>
public sealed class BrowserLedgerStore(
    IDbContextFactory<LedgerDbContext> contexts,
    ISqliteWasmInitializer initializer,
    IDbInitializationStatus status) : ILedgerStore
{
    public StorageLocation Location => StorageLocation.Browser;

    public Task<IReadOnlyList<Account>> GetAccountsAsync(CancellationToken ct = default) => Run(l => l.GetAccountsAsync(ct));

    public Task<Account> CreateAccountAsync(AccountRequest request) => Run(l => l.CreateAccountAsync(request));

    public Task<Account> UpdateAccountAsync(string accountId, AccountRequest request) =>
        Run(async l => await l.UpdateAccountAsync(accountId, request) ?? throw NotFound("account"));

    public Task DeleteAccountAsync(string accountId) => Run(l => l.DeleteAccountAsync(accountId));

    public Task<ImportTransactionsResult> ImportAsync(string accountId, ImportTransactionsRequest request) =>
        Run(async l => await l.ImportAsync(accountId, request) ?? throw NotFound("account"));

    public Task<IReadOnlyList<ImportSummary>> GetImportsAsync(string accountId, CancellationToken ct = default) =>
        Run(l => l.GetImportsAsync(accountId, ct: ct));

    public Task<int> UndoImportAsync(Guid importId) =>
        Run(async l => (await l.UndoImportAsync(importId) ?? throw NotFound("import")).Removed);

    public Task<IReadOnlyList<LedgerTransaction>> SearchTransactionsAsync(TransactionFilter filter, CancellationToken ct = default) =>
        Run(l => l.SearchTransactionsAsync(filter, ct));

    public Task ClassifyAsync(Transaction transaction, ClassifyTransactionRequest request) =>
        Run(async l => await l.ClassifyAsync(transaction.AccountId, transaction.TransactionId, request) ? true : throw NotFound("transaction"));

    public Task<IReadOnlyList<LedgerTransaction>> FindSimilarAsync(Transaction transaction) =>
        Run(async l => await l.FindSimilarAsync(transaction.AccountId, transaction.TransactionId) ?? throw NotFound("transaction"));

    public Task<IReadOnlyList<MerchantChoice>> GetMerchantChoicesAsync(CancellationToken ct = default) => Run(l => l.GetMerchantChoicesAsync(ct));

    public Task<int> MarkReviewedAsync(MarkReviewedRequest request) => Run(l => l.MarkReviewedAsync(request));

    public Task<int> SetTaxManyAsync(SetTaxManyRequest request) => Run(l => l.SetTaxManyAsync(request));

    public Task<IReadOnlyList<PossibleDuplicate>> FindDuplicatesAsync(CancellationToken ct = default) => Run(l => l.FindDuplicatesAsync(ct));

    public Task MergeDuplicateAsync(string accountId, MergeDuplicateRequest request) =>
        Run(async l => await l.MergeDuplicateAsync(accountId, request) ? true : throw NotFound("transaction"));

    public Task KeepBothAsync(string accountId, KeepBothRequest request) =>
        Run(async l => await l.KeepBothAsync(accountId, request) ? true : throw NotFound("transaction"));

    public Task<int> ClassifyManyAsync(string accountId, ClassifyManyRequest request) => Run(l => l.ClassifyManyAsync(accountId, request));

    public Task SetTaxTagAsync(Transaction transaction, TaxTag? tax) =>
        Run(async l => await l.SetTaxTagAsync(transaction.AccountId, transaction.TransactionId, tax) ? true : throw NotFound("transaction"));

    public Task<DashboardSummary> GetDashboardAsync(DateOnly from, DateOnly to, string? category = null, string? subCategory = null, IReadOnlyCollection<string>? accountIds = null, CategoryKind? kind = null, IReadOnlyCollection<string>? tagIds = null, CancellationToken ct = default) =>
        Run(l => l.GetDashboardAsync(from, to, category, subCategory, accountIds, kind, tagIds, ct));

    public Task<IReadOnlyList<MonthlyNetWorth>> GetNetWorthByMonthAsync(DateOnly from, DateOnly to, IReadOnlyCollection<string>? accountIds = null, CancellationToken ct = default) =>
        Run(l => l.GetNetWorthByMonthAsync(from, to, accountIds, ct));

    public Task<IReadOnlyList<MonthlyAccountValue>> GetAccountValueByMonthAsync(string accountId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        Run(l => l.GetAccountValueByMonthAsync(accountId, from, to, ct));

    public Task<TaxReport> GetTaxReportAsync(int year, string? person = null, CancellationToken ct = default) => Run(l => l.GetTaxReportAsync(year, person, ct));

    public Task<TaxReview> GetTaxReviewAsync(int year, CancellationToken ct = default) => Run(l => l.GetTaxReviewAsync(year, ct));

    public Task<MedicalEstimate> GetMedicalEstimateAsync(int year, decimal? netIncome, string? person = null, CancellationToken ct = default) =>
        Run(l => l.GetMedicalEstimateAsync(year, netIncome, person, ct));

    public Task<int> ApplyMedicalWindowAsync(int year, int endMonth, string? person = null) => Run(l => l.ApplyMedicalWindowAsync(year, endMonth, person));

    public Task<string> GetTaxReportCsvAsync(int year, string? person = null) => Run(l => l.GetTaxReportCsvAsync(year, person));

    public Task<int> SetBusinessUseAsync(int year, SetBusinessUseRequest request, string? person = null) =>
        Run(l => l.SetBusinessUseAsync(year, request, person));

    public Task<TaxEstimateSet> GetTaxEstimatesAsync(int year, CancellationToken ct = default) => Run(l => l.GetTaxEstimatesAsync(year, null, ct));

    public Task<IReadOnlyDictionary<string, string>> GetAccountHoldersAsync(CancellationToken ct = default) => Run(l => l.GetAccountHoldersAsync(ct));

    public Task SetAccountHolderAsync(string accountId, string? personId) =>
        Run(async l => await l.SetAccountHolderAsync(accountId, personId) ? true : throw NotFound("account"));

    public Task SetEarnedByAsync(Transaction transaction, string? personId) =>
        Run(async l => await l.SetEarnedByAsync(transaction.AccountId, transaction.TransactionId, personId) ? true : throw NotFound("transaction"));

    public Task<TaxSituationEntry> SaveTaxSituationAsync(int year, string personId, TaxSituation situation) =>
        Run(l => l.SaveTaxSituationAsync(year, personId, situation));

    public Task<IReadOnlyList<HoldingsSnapshot>> GetHoldingsAsync(CancellationToken ct = default) => Run(l => l.GetHoldingsAsync(ct: ct));

    public Task<ImportHoldingsResult> ImportHoldingsAsync(string accountId, ImportHoldingsRequest request) =>
        Run(async l => await l.ImportHoldingsAsync(accountId, request) ?? throw NotFound("account"));

    public Task DeleteHoldingsAsync(string accountId) => Run(l => l.DeleteHoldingsAsync(accountId));

    public Task<ApplyQuotesResult> ApplyQuotesAsync(ApplyQuotesRequest request) => Run(l => l.ApplyQuotesAsync(request));

    public Task<IReadOnlyList<PriceReading>> GetPriceHistoryAsync(CancellationToken ct = default) => Run(l => l.GetPriceHistoryAsync(ct));

    public Task<ApplyPriceHistoryResult> ApplyPriceHistoryAsync(ApplyPriceHistoryRequest request) => Run(l => l.ApplyPriceHistoryAsync(request));

    public Task<IReadOnlyList<ExchangeRate>> GetExchangeRatesAsync(CancellationToken ct = default) => Run(l => l.GetExchangeRatesAsync(ct));

    public Task<ExchangeRate> SetExchangeRateAsync(ExchangeRate rate) => Run(l => l.SetExchangeRateAsync(rate));

    public Task<IReadOnlyList<MonthlyExchangeRate>> GetMonthlyExchangeRatesAsync(CancellationToken ct = default) => Run(l => l.GetMonthlyExchangeRatesAsync(ct));

    public Task<ApplyMonthlyExchangeRatesResult> ApplyMonthlyExchangeRatesAsync(ApplyMonthlyExchangeRatesRequest request) => Run(l => l.ApplyMonthlyExchangeRatesAsync(request));

    public Task<BudgetPlan> GetBudgetPlanAsync(int year, CancellationToken ct = default) => Run(l => l.GetBudgetPlanAsync(year, ct));

    public Task<IReadOnlyList<BudgetTarget>> SetBudgetAsync(int year, SetBudgetRequest request) => Run(l => l.SetBudgetAsync(year, request));

    public Task<IReadOnlyList<BudgetMonth>> GetBudgetMonthsAsync(int fromYear, int toYear, CancellationToken ct = default) => Run(l => l.GetBudgetMonthsAsync(fromYear, toYear, ct));

    public Task<IReadOnlyList<RealEstateProperty>> GetPropertiesAsync(CancellationToken ct = default) => Run(l => l.GetPropertiesAsync(ct));

    public Task<RealEstateProperty> CreatePropertyAsync(PropertyRequest request) => Run(l => l.CreatePropertyAsync(request));

    public Task<RealEstateProperty> UpdatePropertyAsync(string id, PropertyRequest request) =>
        Run(async l => await l.UpdatePropertyAsync(id, request) ?? throw NotFound("property"));

    public Task DeletePropertyAsync(string id) => Run(l => l.DeletePropertyAsync(id));

    public Task<IReadOnlyList<HpiReading>> GetHpiReadingsAsync(CancellationToken ct = default) => Run(l => l.GetHpiReadingsAsync(ct));

    public Task<HpiReading> SetHpiReadingAsync(HpiReading reading) => Run(l => l.SetHpiReadingAsync(reading));

    public Task DeleteHpiReadingAsync(string area, DateOnly month) => Run(l => l.DeleteHpiReadingAsync(area, month));

    public Task<ApplyHpiResult> ApplyHpiAsync(ApplyHpiRequest request) => Run(l => l.ApplyHpiAsync(request));

    public Task<IReadOnlyList<HouseholdMember>> GetHouseholdAsync(CancellationToken ct = default) => Run(l => l.GetHouseholdAsync(ct));

    public Task<HouseholdMember> AddHouseholdMemberAsync(string name) => Run(l => l.AddHouseholdMemberAsync(name));

    public Task<HouseholdMember> RenameHouseholdMemberAsync(string id, string name) =>
        Run(async l => await l.RenameHouseholdMemberAsync(id, name) ?? throw NotFound("person"));

    public Task DeleteHouseholdMemberAsync(string id) => Run(l => l.DeleteHouseholdMemberAsync(id));

    public Task<IReadOnlyList<LedgerTag>> GetTagsAsync(CancellationToken ct = default) => Run(l => l.GetTagsAsync(ct));

    public Task<IReadOnlyList<TagUsage>> GetTagUsageAsync(CancellationToken ct = default) => Run(l => l.GetTagUsageAsync(ct));

    public Task<LedgerTag> AddTagAsync(string name) => Run(l => l.AddTagAsync(name));

    public Task<LedgerTag> RenameTagAsync(string id, string name) =>
        Run(async l => await l.RenameTagAsync(id, name) ?? throw NotFound("tag"));

    public Task DeleteTagAsync(string id) => Run(l => l.DeleteTagAsync(id));

    public Task SetTagsAsync(Transaction transaction, IReadOnlyList<string> tagIds) =>
        Run(async l => await l.SetTagsAsync(transaction.AccountId, transaction.TransactionId, tagIds) ? true : throw NotFound("transaction"));

    public Task<int> TagManyAsync(TagManyRequest request) => Run(l => l.TagManyAsync(request));

    public Task<IReadOnlyList<CustomCategory>> GetCustomCategoriesAsync(CancellationToken ct = default) => Run(l => l.GetCustomCategoriesAsync(ct));

    public Task<CustomCategory> AddCustomCategoryAsync(CustomCategoryRequest request) => Run(l => l.AddCustomCategoryAsync(request));

    public Task DeleteCustomCategoryAsync(string id) => Run(l => l.DeleteCustomCategoryAsync(id));

    public Task<IReadOnlyList<ReceiptInfo>> GetReceiptsAsync(Transaction transaction) =>
        Run(l => l.GetReceiptsAsync(transaction.AccountId, transaction.TransactionId));

    public Task<ReceiptInfo> AddReceiptAsync(Transaction transaction, UploadReceiptRequest request) =>
        Run(async l => await l.AddReceiptAsync(transaction.AccountId, transaction.TransactionId, request) ?? throw NotFound("transaction"));

    public Task<byte[]> GetReceiptContentAsync(ReceiptInfo receipt) =>
        Run(async l => (await l.GetReceiptAsync(receipt.Id) ?? throw NotFound("receipt")).Content);

    public Task DeleteReceiptAsync(ReceiptInfo receipt) => Run(l => l.DeleteReceiptAsync(receipt.Id));

    public Task<IReadOnlyList<ReceiptMatch>> FindReceiptMatchesAsync(ReceiptFields receipt) =>
        Run(l => l.FindReceiptMatchesAsync(receipt, Periods.Today));

    public Task<LedgerBackup> ExportAsync() => Run(l => l.ExportAsync());

    public Task<RestoreResult> RestoreAsync(LedgerBackup backup) => Run(l => l.RestoreAsync(backup));

    public Task<ClearLedgerResult> ClearAsync() => Run(l => l.ClearAsync());

    private async Task<T> Run<T>(Func<LedgerService, Task<T>> operation)
    {
        try
        {
            // Idempotent: starts the SQLite worker and applies migrations the first time only.
            await initializer.InitializeAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            throw new LedgerStoreException("Couldn't open the ledger stored in this browser. Reload the page to try again.", ex);
        }

        // A failed start is reported through the status rather than thrown.
        if (status.State != DbInitState.READY)
        {
            throw new LedgerStoreException(status.State switch
            {
                DbInitState.TAB_LOCKED => "LoonieBooks is open in another tab, and only one tab at a time can use the ledger stored in this browser. Close the other tab, then reload this one.",
                DbInitState.SCHEMA_INCOMPATIBLE => "The ledger stored in this browser was made by a different version of LoonieBooks and can't be opened.",
                _ => "Couldn't open the ledger stored in this browser. Reload the page to try again.",
            });
        }

        await using var db = await contexts.CreateDbContextAsync();
        db.OwnerId = LedgerDbContext.LocalOwner;
        try
        {
            return await operation(new LedgerService(db));
        }
        catch (LedgerValidationException ex)
        {
            throw new LedgerStoreException(ex.Message, ex);
        }
    }

    private async Task Run(Func<LedgerService, Task> operation) => await Run(async l => { await operation(l); return true; });

    private static LedgerStoreException NotFound(string what) => new($"That {what} no longer exists.");
}
