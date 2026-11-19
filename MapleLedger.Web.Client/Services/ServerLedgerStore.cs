using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MapleLedger.Core;
using MapleLedger.Core.Categorization;
using MapleLedger.Core.Investments;
using MapleLedger.Core.RealEstate;
using MapleLedger.Core.Reports;
using MapleLedger.Core.Tax;
using MapleLedger.Data;
using MapleLedger.Fdx;

namespace MapleLedger.Web.Client.Services;

/// <summary>
/// The ledger kept on the LoonieBooks server (premium users). Calls go to the web host, which
/// forwards /api and /fdx to the API with the signed-in user's identity. Accounts are read through
/// the FDX surface, the same way they will be read from a bank's FDX API.
/// </summary>
public sealed class ServerLedgerStore(HttpClient http) : ILedgerStore
{
    /// <summary>The web host rejects state-changing API calls without this header (CSRF defence).</summary>
    public const string RequestHeader = "X-MapleLedger";

    public StorageLocation Location => StorageLocation.Cloud;

    public async Task<IReadOnlyList<Account>> GetAccountsAsync(CancellationToken ct = default) =>
        (await Get<AccountsResponse>("fdx/v6/accounts?limit=1000", ct)).Accounts;

    public async Task<Account> CreateAccountAsync(AccountRequest request) =>
        await Read<Account>(await Send(HttpMethod.Post, "api/accounts", request));

    public async Task<Account> UpdateAccountAsync(string accountId, AccountRequest request) =>
        await Read<Account>(await Send(HttpMethod.Put, $"api/accounts/{Uri.EscapeDataString(accountId)}", request));

    public async Task DeleteAccountAsync(string accountId) =>
        await EnsureSuccess(await Send(HttpMethod.Delete, $"api/accounts/{Uri.EscapeDataString(accountId)}"));

    public async Task<ImportTransactionsResult> ImportAsync(string accountId, ImportTransactionsRequest request) =>
        await Read<ImportTransactionsResult>(await Send(HttpMethod.Post, $"api/accounts/{Uri.EscapeDataString(accountId)}/imports", request));

    public async Task<IReadOnlyList<ImportSummary>> GetImportsAsync(string accountId, CancellationToken ct = default) =>
        await Get<List<ImportSummary>>($"api/accounts/{Uri.EscapeDataString(accountId)}/imports", ct);

    public async Task<int> UndoImportAsync(Guid importId) =>
        (await Read<UndoImportResult>(await Send(HttpMethod.Delete, $"api/imports/{importId}"))).Removed;

    public async Task<IReadOnlyList<LedgerTransaction>> SearchTransactionsAsync(TransactionFilter filter, CancellationToken ct = default)
    {
        var query = new List<string> { $"limit={filter.Limit}" };
        if (filter.From is { } from) query.Add($"from={Date(from)}");
        if (filter.To is { } to) query.Add($"to={Date(to)}");
        if (!string.IsNullOrEmpty(filter.AccountId)) query.Add($"accountId={Uri.EscapeDataString(filter.AccountId)}");
        query.AddRange(AccountQuery(filter.AccountIds));
        query.AddRange(TagQuery(filter.TagIds));
        if (!string.IsNullOrEmpty(filter.Category)) query.Add($"category={Uri.EscapeDataString(filter.Category)}");
        if (!string.IsNullOrEmpty(filter.SubCategory)) query.Add($"subCategory={Uri.EscapeDataString(filter.SubCategory)}");
        if (filter.Kind is { } kind) query.Add($"kind={kind}");
        if (!string.IsNullOrEmpty(filter.TaxCode)) query.Add($"taxCode={Uri.EscapeDataString(filter.TaxCode)}");
        if (filter.Reviewed is { } reviewed) query.Add($"reviewed={(reviewed ? "true" : "false")}");
        if (!string.IsNullOrWhiteSpace(filter.Search)) query.Add($"search={Uri.EscapeDataString(filter.Search.Trim())}");
        return await Get<List<LedgerTransaction>>("api/transactions?" + string.Join('&', query), ct);
    }

    public async Task<IReadOnlyList<LedgerTransaction>> FindSimilarAsync(Transaction transaction) =>
        await Get<List<LedgerTransaction>>($"{TransactionUrl(transaction)}/similar", CancellationToken.None);

    public async Task<IReadOnlyList<MerchantChoice>> GetMerchantChoicesAsync(CancellationToken ct = default) =>
        await Get<List<MerchantChoice>>("api/merchant-choices", ct);

    public async Task<int> MarkReviewedAsync(MarkReviewedRequest request) =>
        await Read<int>(await Send(HttpMethod.Put, "api/transactions/reviewed", request));

    public async Task<int> SetTaxManyAsync(SetTaxManyRequest request) =>
        await Read<int>(await Send(HttpMethod.Put, "api/transactions/tax", request));

    public async Task<IReadOnlyList<PossibleDuplicate>> FindDuplicatesAsync(CancellationToken ct = default) =>
        await Get<List<PossibleDuplicate>>("api/duplicates", ct);

    public async Task MergeDuplicateAsync(string accountId, MergeDuplicateRequest request) =>
        await EnsureSuccess(await Send(HttpMethod.Post, $"api/accounts/{Uri.EscapeDataString(accountId)}/duplicates/merge", request));

    public async Task KeepBothAsync(string accountId, KeepBothRequest request) =>
        await EnsureSuccess(await Send(HttpMethod.Post, $"api/accounts/{Uri.EscapeDataString(accountId)}/duplicates/keep-both", request));

    public async Task<int> ClassifyManyAsync(string accountId, ClassifyManyRequest request) =>
        await Read<int>(await Send(HttpMethod.Put, $"api/accounts/{Uri.EscapeDataString(accountId)}/classification", request));

    public async Task ClassifyAsync(Transaction transaction, ClassifyTransactionRequest request) =>
        await EnsureSuccess(await Send(
            HttpMethod.Put,
            $"{TransactionUrl(transaction)}/classification",
            request));

    public async Task SetTaxTagAsync(Transaction transaction, TaxTag? tax) =>
        await EnsureSuccess(await Send(
            HttpMethod.Put,
            $"{TransactionUrl(transaction)}/tax",
            new SetTaxTagRequest(tax)));

    public Task<DashboardSummary> GetDashboardAsync(DateOnly from, DateOnly to, string? category = null, string? subCategory = null, IReadOnlyCollection<string>? accountIds = null, CategoryKind? kind = null, IReadOnlyCollection<string>? tagIds = null, CancellationToken ct = default)
    {
        var url = $"api/reports/dashboard?from={Date(from)}&to={Date(to)}";
        if (kind is { } k) url += $"&kind={k}";
        if (!string.IsNullOrEmpty(category)) url += $"&category={Uri.EscapeDataString(category)}";
        if (!string.IsNullOrEmpty(subCategory)) url += $"&subCategory={Uri.EscapeDataString(subCategory)}";
        foreach (var account in AccountQuery(accountIds)) url += $"&{account}";
        foreach (var tag in TagQuery(tagIds)) url += $"&{tag}";
        return Get<DashboardSummary>(url, ct);
    }

    public Task<IReadOnlyList<MonthlyNetWorth>> GetNetWorthByMonthAsync(DateOnly from, DateOnly to, IReadOnlyCollection<string>? accountIds = null, CancellationToken ct = default)
    {
        var url = $"api/reports/net-worth?from={Date(from)}&to={Date(to)}";
        foreach (var account in AccountQuery(accountIds)) url += $"&{account}";
        return Get<IReadOnlyList<MonthlyNetWorth>>(url, ct);
    }

    public Task<IReadOnlyList<MonthlyAccountValue>> GetAccountValueByMonthAsync(string accountId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        Get<IReadOnlyList<MonthlyAccountValue>>($"api/reports/account-value/{Uri.EscapeDataString(accountId)}?from={Date(from)}&to={Date(to)}", ct);

    /// <summary>One <c>accountId=</c> per account; the API reads them as "any of".</summary>
    private static IEnumerable<string> AccountQuery(IEnumerable<string>? accountIds) =>
        (accountIds ?? []).Where(id => !string.IsNullOrEmpty(id)).Distinct().Select(id => $"accountId={Uri.EscapeDataString(id)}");

    private static IEnumerable<string> TagQuery(IEnumerable<string>? tagIds) =>
        (tagIds ?? []).Where(id => !string.IsNullOrEmpty(id)).Distinct().Select(id => $"tag={Uri.EscapeDataString(id)}");

    public Task<TaxReport> GetTaxReportAsync(int year, string? person = null, CancellationToken ct = default) =>
        Get<TaxReport>($"api/reports/tax/{year}{PersonQuery(person, "?")}", ct);

    public Task<TaxReview> GetTaxReviewAsync(int year, CancellationToken ct = default) =>
        Get<TaxReview>($"api/reports/tax/{year}/review", ct);

    public Task<MedicalEstimate> GetMedicalEstimateAsync(int year, decimal? netIncome, string? person = null, CancellationToken ct = default) =>
        Get<MedicalEstimate>(
            $"api/reports/tax/{year}/medical?netIncome={netIncome?.ToString(CultureInfo.InvariantCulture)}{PersonQuery(person, "&")}", ct);

    public async Task<int> ApplyMedicalWindowAsync(int year, int endMonth, string? person = null) =>
        (await Read<ApplyMedicalWindowResult>(await Send(HttpMethod.Post, $"api/reports/tax/{year}/medical/window", new ApplyMedicalWindowRequest(endMonth, person)))).Changed;

    public async Task<string> GetTaxReportCsvAsync(int year, string? person = null)
    {
        using var response = await http.GetAsync($"api/reports/tax/{year}/csv{PersonQuery(person, "?")}");
        await EnsureSuccess(response);
        return (await response.Content.ReadAsStringAsync()).TrimStart('﻿');
    }

    public Task<IReadOnlyList<ReceiptInfo>> GetReceiptsAsync(Transaction transaction) =>
        Get<IReadOnlyList<ReceiptInfo>>($"{TransactionUrl(transaction)}/receipts", CancellationToken.None);

    public async Task<ReceiptInfo> AddReceiptAsync(Transaction transaction, UploadReceiptRequest request) =>
        await Read<ReceiptInfo>(await Send(HttpMethod.Post, $"{TransactionUrl(transaction)}/receipts", request));

    public async Task<byte[]> GetReceiptContentAsync(ReceiptInfo receipt)
    {
        using var response = await http.GetAsync($"api/receipts/{receipt.Id}");
        await EnsureSuccess(response);
        return await response.Content.ReadAsByteArrayAsync();
    }

    public async Task DeleteReceiptAsync(ReceiptInfo receipt) =>
        await EnsureSuccess(await Send(HttpMethod.Delete, $"api/receipts/{receipt.Id}"));

    // A POST only to carry the fields in a body; it changes nothing.
    public async Task<IReadOnlyList<ReceiptMatch>> FindReceiptMatchesAsync(ReceiptFields receipt) =>
        await Read<List<ReceiptMatch>>(await Send(HttpMethod.Post, "api/receipts/match", receipt));

    public async Task<int> SetBusinessUseAsync(int year, SetBusinessUseRequest request, string? person = null) =>
        (await Read<SetBusinessUseResult>(await Send(HttpMethod.Post, $"api/reports/tax/{year}/business-use{PersonQuery(person, "?")}", request))).Changed;

    public Task<TaxEstimateSet> GetTaxEstimatesAsync(int year, CancellationToken ct = default) =>
        Get<TaxEstimateSet>($"api/reports/tax/{year}/estimate", ct);

    public Task<IReadOnlyDictionary<string, string>> GetAccountHoldersAsync(CancellationToken ct = default) =>
        Get<IReadOnlyDictionary<string, string>>("api/accounts/holders", ct);

    public async Task SetAccountHolderAsync(string accountId, string? personId) =>
        await EnsureSuccess(await Send(HttpMethod.Put, $"api/accounts/{Uri.EscapeDataString(accountId)}/holder", new SetPersonRequest(personId)));

    public async Task SetEarnedByAsync(Transaction transaction, string? personId) =>
        await EnsureSuccess(await Send(HttpMethod.Put, $"api/accounts/{Uri.EscapeDataString(transaction.AccountId)}/transactions/{Uri.EscapeDataString(transaction.TransactionId)}/earned-by", new SetPersonRequest(personId)));

    public async Task<TaxSituationEntry> SaveTaxSituationAsync(int year, string personId, TaxSituation situation) =>
        await Read<TaxSituationEntry>(await Send(HttpMethod.Put, $"api/reports/tax/{year}/situations/{Uri.EscapeDataString(personId)}", situation));

    public Task<IReadOnlyList<HoldingsSnapshot>> GetHoldingsAsync(CancellationToken ct = default) =>
        Get<IReadOnlyList<HoldingsSnapshot>>("api/holdings", ct);

    public async Task<ImportHoldingsResult> ImportHoldingsAsync(string accountId, ImportHoldingsRequest request) =>
        await Read<ImportHoldingsResult>(await Send(HttpMethod.Put, $"api/accounts/{Uri.EscapeDataString(accountId)}/holdings", request));

    public async Task DeleteHoldingsAsync(string accountId) =>
        await EnsureSuccess(await Send(HttpMethod.Delete, $"api/accounts/{Uri.EscapeDataString(accountId)}/holdings"));

    public async Task<ApplyQuotesResult> ApplyQuotesAsync(ApplyQuotesRequest request) =>
        await Read<ApplyQuotesResult>(await Send(HttpMethod.Post, "api/holdings/quotes", request));

    public Task<IReadOnlyList<PriceReading>> GetPriceHistoryAsync(CancellationToken ct = default) =>
        Get<IReadOnlyList<PriceReading>>("api/holdings/price-history", ct);

    public async Task<ApplyPriceHistoryResult> ApplyPriceHistoryAsync(ApplyPriceHistoryRequest request) =>
        await Read<ApplyPriceHistoryResult>(await Send(HttpMethod.Post, "api/holdings/price-history", request));

    public Task<IReadOnlyList<ExchangeRate>> GetExchangeRatesAsync(CancellationToken ct = default) =>
        Get<IReadOnlyList<ExchangeRate>>("api/exchange-rates", ct);

    public async Task<ExchangeRate> SetExchangeRateAsync(ExchangeRate rate) =>
        await Read<ExchangeRate>(await Send(HttpMethod.Put, $"api/exchange-rates/{Uri.EscapeDataString(rate.Currency)}", rate));

    public Task<IReadOnlyList<RealEstateProperty>> GetPropertiesAsync(CancellationToken ct = default) =>
        Get<IReadOnlyList<RealEstateProperty>>("api/properties", ct);

    public async Task<RealEstateProperty> CreatePropertyAsync(PropertyRequest request) =>
        await Read<RealEstateProperty>(await Send(HttpMethod.Post, "api/properties", request));

    public async Task<RealEstateProperty> UpdatePropertyAsync(string id, PropertyRequest request) =>
        await Read<RealEstateProperty>(await Send(HttpMethod.Put, $"api/properties/{Uri.EscapeDataString(id)}", request));

    public async Task DeletePropertyAsync(string id) =>
        await EnsureSuccess(await Send(HttpMethod.Delete, $"api/properties/{Uri.EscapeDataString(id)}"));

    public Task<IReadOnlyList<HpiReading>> GetHpiReadingsAsync(CancellationToken ct = default) =>
        Get<IReadOnlyList<HpiReading>>("api/house-price-index", ct);

    public async Task<HpiReading> SetHpiReadingAsync(HpiReading reading) =>
        await Read<HpiReading>(await Send(HttpMethod.Put, "api/house-price-index", reading));

    public async Task DeleteHpiReadingAsync(string area, DateOnly month) =>
        await EnsureSuccess(await Send(HttpMethod.Delete, $"api/house-price-index?area={Uri.EscapeDataString(area)}&month={month:yyyy-MM-dd}"));

    public async Task<ApplyHpiResult> ApplyHpiAsync(ApplyHpiRequest request) =>
        await Read<ApplyHpiResult>(await Send(HttpMethod.Post, "api/house-price-index/feed", request));

    public Task<IReadOnlyList<HouseholdMember>> GetHouseholdAsync(CancellationToken ct = default) =>
        Get<IReadOnlyList<HouseholdMember>>("api/household", ct);

    public async Task<HouseholdMember> AddHouseholdMemberAsync(string name) =>
        await Read<HouseholdMember>(await Send(HttpMethod.Post, "api/household", new HouseholdMemberRequest(name)));

    public async Task<HouseholdMember> RenameHouseholdMemberAsync(string id, string name) =>
        await Read<HouseholdMember>(await Send(HttpMethod.Put, $"api/household/{Uri.EscapeDataString(id)}", new HouseholdMemberRequest(name)));

    public async Task DeleteHouseholdMemberAsync(string id) =>
        await EnsureSuccess(await Send(HttpMethod.Delete, $"api/household/{Uri.EscapeDataString(id)}"));

    public Task<IReadOnlyList<LedgerTag>> GetTagsAsync(CancellationToken ct = default) =>
        Get<IReadOnlyList<LedgerTag>>("api/tags", ct);

    public Task<IReadOnlyList<TagUsage>> GetTagUsageAsync(CancellationToken ct = default) =>
        Get<IReadOnlyList<TagUsage>>("api/tags/usage", ct);

    public async Task<LedgerTag> AddTagAsync(string name) =>
        await Read<LedgerTag>(await Send(HttpMethod.Post, "api/tags", new TagRequest(name)));

    public async Task<LedgerTag> RenameTagAsync(string id, string name) =>
        await Read<LedgerTag>(await Send(HttpMethod.Put, $"api/tags/{Uri.EscapeDataString(id)}", new TagRequest(name)));

    public async Task DeleteTagAsync(string id) =>
        await EnsureSuccess(await Send(HttpMethod.Delete, $"api/tags/{Uri.EscapeDataString(id)}"));

    public async Task SetTagsAsync(Transaction transaction, IReadOnlyList<string> tagIds) =>
        await EnsureSuccess(await Send(HttpMethod.Put, $"{TransactionUrl(transaction)}/tags", new SetTagsRequest(tagIds)));

    public async Task<int> TagManyAsync(TagManyRequest request) =>
        await Read<int>(await Send(HttpMethod.Put, "api/tagging", request));

    public Task<IReadOnlyList<CustomCategory>> GetCustomCategoriesAsync(CancellationToken ct = default) =>
        Get<IReadOnlyList<CustomCategory>>("api/categories", ct);

    public async Task<CustomCategory> AddCustomCategoryAsync(CustomCategoryRequest request) =>
        await Read<CustomCategory>(await Send(HttpMethod.Post, "api/categories", request));

    public async Task DeleteCustomCategoryAsync(string id) =>
        await EnsureSuccess(await Send(HttpMethod.Delete, $"api/categories/{Uri.EscapeDataString(id)}"));

    public Task<LedgerBackup> ExportAsync() => Get<LedgerBackup>("api/backup", CancellationToken.None);

    /// <summary>
    /// Uploads in chunks so a large browser ledger doesn't become one enormous request. Every chunk
    /// carries all the accounts; the server only adds the ones it doesn't have yet. Receipts go
    /// last, once the transactions they belong to are there.
    /// </summary>
    public async Task<ClearLedgerResult> ClearAsync() =>
        await Read<ClearLedgerResult>(await Send(HttpMethod.Delete, "api/ledger"));

    public async Task<RestoreResult> RestoreAsync(LedgerBackup backup)
    {
        int accounts = 0, added = 0, duplicates = 0, receipts = 0;
        var chunks = backup.Transactions.Count == 0 ? [[]] : backup.Transactions.Chunk(LedgerService.MaxImportSize).ToList();
        foreach (var chunk in chunks)
        {
            var result = await Read<RestoreResult>(await Send(HttpMethod.Post, "api/backup", backup with { Transactions = chunk, Receipts = null }));
            accounts += result.AccountsAdded;
            added += result.TransactionsAdded;
            duplicates += result.Duplicates;
        }

        foreach (var chunk in ReceiptChunks(backup.Receipts ?? []))
        {
            var result = await Read<RestoreResult>(await Send(HttpMethod.Post, "api/backup", backup with { Transactions = [], Receipts = chunk }));
            receipts += result.ReceiptsAdded;
        }

        return new RestoreResult(accounts, added, duplicates, receipts);
    }

    /// <summary>Groups receipts into requests well under the web host's 30 MB request limit (base64 adds a third).</summary>
    private static IEnumerable<List<ReceiptFile>> ReceiptChunks(IReadOnlyList<ReceiptFile> receipts)
    {
        const long maxBytes = 12 * 1024 * 1024;
        var chunk = new List<ReceiptFile>();
        long size = 0;
        foreach (var receipt in receipts)
        {
            if (chunk.Count > 0 && size + receipt.Content.Length > maxBytes)
            {
                yield return chunk;
                (chunk, size) = ([], 0);
            }

            chunk.Add(receipt);
            size += receipt.Content.Length;
        }

        if (chunk.Count > 0) yield return chunk;
    }

    private static string PersonQuery(string? person, string separator) =>
        string.IsNullOrEmpty(person) ? "" : $"{separator}person={Uri.EscapeDataString(person)}";

    private static string TransactionUrl(Transaction t) =>
        $"api/accounts/{Uri.EscapeDataString(t.AccountId)}/transactions/{Uri.EscapeDataString(t.TransactionId)}";

    private static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private async Task<HttpResponseMessage> Send(HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add(RequestHeader, "1");
        if (body is not null) request.Content = JsonContent.Create(body);
        return await http.SendAsync(request);
    }

    private async Task<T> Get<T>(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        return await Read<T>(response);
    }

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        await EnsureSuccess(response);
        return await response.Content.ReadFromJsonAsync<T>() ?? throw new LedgerStoreException("The server returned an empty response.");
    }

    /// <summary>Throws a <see cref="LedgerStoreException"/> with a readable message for any failed response.</summary>
    internal static async Task EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;

        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Your sign-in has expired. Reload the page to sign in again.",
            _ => $"The server returned {(int)response.StatusCode} {response.ReasonPhrase}.",
        };

        try
        {
            // Validation failures come back as ProblemDetails with an "errors" map; others with a title/detail.
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (problem.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                message = string.Join(" ", errors.EnumerateObject().SelectMany(e => e.Value.EnumerateArray().Select(v => v.GetString())));
            }
            else if (problem.RootElement.TryGetProperty("detail", out var detail) && detail.GetString() is { Length: > 0 } text)
            {
                message = text;
            }
            else if (problem.RootElement.TryGetProperty("title", out var title))
            {
                message = title.GetString() ?? message;
            }
        }
        catch (JsonException)
        {
            // Not JSON; keep the status-code message.
        }

        throw new LedgerStoreException(message);
    }
}
