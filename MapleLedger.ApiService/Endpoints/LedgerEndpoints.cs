using System.Text;
using MapleLedger.ApiService.Users;
using MapleLedger.Core;
using MapleLedger.Core.Budgets;
using MapleLedger.Core.Investments;
using MapleLedger.Core.RealEstate;
using MapleLedger.Core.Tax;
using MapleLedger.Data;

namespace MapleLedger.ApiService.Endpoints;

/// <summary>LoonieBooks' own API: managing accounts, importing, classifying, reporting and backups.</summary>
public static class LedgerEndpoints
{
    public static RouteGroupBuilder MapLedgerEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").WithTags("Ledger");

        api.MapPost("/accounts", async (LedgerService ledger, AccountRequest request, CancellationToken ct) =>
        {
            var account = await ledger.CreateAccountAsync(request, ct);
            return Results.Created($"/fdx/v6/accounts/{account.AccountId}", account);
        });

        api.MapPut("/accounts/{accountId}", async (LedgerService ledger, string accountId, AccountRequest request, CancellationToken ct) =>
            await ledger.UpdateAccountAsync(accountId, request, ct) is { } account ? Results.Ok(account) : Results.NotFound());

        api.MapDelete("/accounts/{accountId}", async (LedgerService ledger, string accountId, CancellationToken ct) =>
            await ledger.DeleteAccountAsync(accountId, ct) ? Results.NoContent() : Results.NotFound());

        api.MapPost("/accounts/{accountId}/imports", async (LedgerService ledger, string accountId, ImportTransactionsRequest request, CancellationToken ct) =>
            await ledger.ImportAsync(accountId, request, ct) is { } result ? Results.Ok(result) : Results.NotFound());

        // Start over: deletes the signed-in user's whole ledger.
        api.MapDelete("/ledger", async (LedgerService ledger, CancellationToken ct) => Results.Ok(await ledger.ClearAsync(ct)));

        api.MapGet("/accounts/{accountId}/imports", (LedgerService ledger, string accountId, CancellationToken ct) =>
            ledger.GetImportsAsync(accountId, ct: ct));

        api.MapDelete("/imports/{id:guid}", async (LedgerService ledger, Guid id, CancellationToken ct) =>
            await ledger.UndoImportAsync(id, ct) is { } result ? Results.Ok(result) : Results.NotFound());

        api.MapGet("/accounts/{accountId}/transactions/{transactionId}/similar",
            async (LedgerService ledger, string accountId, string transactionId, CancellationToken ct) =>
                await ledger.FindSimilarAsync(accountId, transactionId, ct) is { } similar ? Results.Ok(similar) : Results.NotFound());

        api.MapGet("/duplicates", (LedgerService ledger, CancellationToken ct) => ledger.FindDuplicatesAsync(ct));

        api.MapPost("/accounts/{accountId}/duplicates/merge",
            async (LedgerService ledger, string accountId, MergeDuplicateRequest request, CancellationToken ct) =>
                await ledger.MergeDuplicateAsync(accountId, request, ct) ? Results.NoContent() : Results.NotFound());

        api.MapPost("/accounts/{accountId}/duplicates/keep-both",
            async (LedgerService ledger, string accountId, KeepBothRequest request, CancellationToken ct) =>
                await ledger.KeepBothAsync(accountId, request, ct) ? Results.NoContent() : Results.NotFound());

        api.MapPut("/accounts/{accountId}/classification",
            async (LedgerService ledger, string accountId, ClassifyManyRequest request, CancellationToken ct) =>
                Results.Ok(await ledger.ClassifyManyAsync(accountId, request, ct)));

        api.MapPut("/accounts/{accountId}/transactions/{transactionId}/classification",
            async (LedgerService ledger, string accountId, string transactionId, ClassifyTransactionRequest request, CancellationToken ct) =>
                await ledger.ClassifyAsync(accountId, transactionId, request, ct) ? Results.NoContent() : Results.NotFound());

        api.MapPut("/accounts/{accountId}/transactions/{transactionId}/tags",
            async (LedgerService ledger, string accountId, string transactionId, SetTagsRequest request, CancellationToken ct) =>
                await ledger.SetTagsAsync(accountId, transactionId, request.TagIds, ct) ? Results.NoContent() : Results.NotFound());

        api.MapPut("/accounts/{accountId}/transactions/{transactionId}/tax",
            async (LedgerService ledger, string accountId, string transactionId, SetTaxTagRequest request, CancellationToken ct) =>
                await ledger.SetTaxTagAsync(accountId, transactionId, request.Tax, ct) ? Results.NoContent() : Results.NotFound());

        api.MapGet("/accounts/{accountId}/transactions/{transactionId}/receipts",
            (LedgerService ledger, string accountId, string transactionId, CancellationToken ct) => ledger.GetReceiptsAsync(accountId, transactionId, ct));

        api.MapPost("/accounts/{accountId}/transactions/{transactionId}/receipts",
            async (LedgerService ledger, string accountId, string transactionId, UploadReceiptRequest request, CancellationToken ct) =>
                await ledger.AddReceiptAsync(accountId, transactionId, request, ct) is { } receipt
                    ? Results.Created($"/api/receipts/{receipt.Id}", receipt)
                    : Results.NotFound())
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(8 * 1024 * 1024));

        // Always a download, never rendered inline, and never content-sniffed into something else.
        api.MapGet("/receipts/{id:guid}", async (LedgerService ledger, HttpContext context, Guid id, CancellationToken ct) =>
        {
            if (await ledger.GetReceiptAsync(id, ct) is not { } file) return Results.NotFound();
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(file.Content, file.Info.ContentType, file.Info.FileName);
        });

        api.MapPost("/receipts/match", (LedgerService ledger, ReceiptFields receipt, CancellationToken ct) =>
            ledger.FindReceiptMatchesAsync(receipt, ct: ct));

        api.MapDelete("/receipts/{id:guid}", async (LedgerService ledger, Guid id, CancellationToken ct) =>
            await ledger.DeleteReceiptAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        // accountId and tag may repeat: any of those accounts, any of those tags.
        api.MapGet("/transactions", (LedgerService ledger, DateOnly? from, DateOnly? to, string[]? accountId, string? category, string? subCategory, CategoryKind? kind, string? taxCode, string? search, int? limit, string[]? tag, bool? reviewed, CancellationToken ct) =>
            ledger.SearchTransactionsAsync(new TransactionFilter(from, to, null, category, taxCode, search, limit ?? 500, subCategory, accountId, kind, tag, reviewed), ct));

        api.MapPut("/transactions/reviewed", async (LedgerService ledger, MarkReviewedRequest request, CancellationToken ct) =>
            Results.Ok(await ledger.MarkReviewedAsync(request, ct)));

        api.MapPut("/transactions/tax", async (LedgerService ledger, SetTaxManyRequest request, CancellationToken ct) =>
            Results.Ok(await ledger.SetTaxManyAsync(request, ct)));

        api.MapGet("/merchant-choices", (LedgerService ledger, CancellationToken ct) => ledger.GetMerchantChoicesAsync(ct));

        api.MapGet("/reports/dashboard", (LedgerService ledger, DateOnly from, DateOnly to, string? category, string? subCategory, string[]? accountId, CategoryKind? kind, string[]? tag, CancellationToken ct) =>
            ledger.GetDashboardAsync(from, to, category, subCategory, accountId, kind, tag, ct));

        api.MapGet("/reports/net-worth", (LedgerService ledger, DateOnly from, DateOnly to, string[]? accountId, CancellationToken ct) =>
            ledger.GetNetWorthByMonthAsync(from, to, accountId, ct));

        api.MapGet("/reports/account-value/{accountId}", (LedgerService ledger, string accountId, DateOnly from, DateOnly to, CancellationToken ct) =>
            ledger.GetAccountValueByMonthAsync(accountId, from, to, ct));

        api.MapGet("/reports/tax/{year:int}", (LedgerService ledger, int year, string? person, CancellationToken ct) =>
            ledger.GetTaxReportAsync(year, person, ct));

        api.MapGet("/reports/tax/{year:int}/review", (LedgerService ledger, int year, CancellationToken ct) =>
            ledger.GetTaxReviewAsync(year, ct));

        api.MapGet("/reports/tax/{year:int}/medical", (LedgerService ledger, int year, decimal? netIncome, string? person, CancellationToken ct) =>
            ledger.GetMedicalEstimateAsync(year, netIncome, person, ct));

        api.MapPost("/reports/tax/{year:int}/medical/window", async (LedgerService ledger, int year, ApplyMedicalWindowRequest request, CancellationToken ct) =>
            new ApplyMedicalWindowResult(await ledger.ApplyMedicalWindowAsync(year, request.EndMonth, request.Person, ct)));

        api.MapPost("/reports/tax/{year:int}/business-use", async (LedgerService ledger, int year, string? person, SetBusinessUseRequest request, CancellationToken ct) =>
            new SetBusinessUseResult(await ledger.SetBusinessUseAsync(year, request, person, ct)));

        api.MapGet("/reports/tax/{year:int}/estimate", (LedgerService ledger, int year, CancellationToken ct) =>
            ledger.GetTaxEstimatesAsync(year, null, ct));

        api.MapGet("/accounts/holders", (LedgerService ledger, CancellationToken ct) => ledger.GetAccountHoldersAsync(ct));

        api.MapPut("/accounts/{accountId}/holder", async (LedgerService ledger, string accountId, SetPersonRequest request, CancellationToken ct) =>
            await ledger.SetAccountHolderAsync(accountId, request.PersonId, ct) ? Results.NoContent() : Results.NotFound());

        api.MapPut("/accounts/{accountId}/transactions/{transactionId}/earned-by", async (LedgerService ledger, string accountId, string transactionId, SetPersonRequest request, CancellationToken ct) =>
            await ledger.SetEarnedByAsync(accountId, transactionId, request.PersonId, ct) ? Results.NoContent() : Results.NotFound());

        api.MapPut("/reports/tax/{year:int}/situations/{personId}", async (LedgerService ledger, int year, string personId, TaxSituation situation, CancellationToken ct) =>
            Results.Ok(await ledger.SaveTaxSituationAsync(year, personId, situation, ct)));

        api.MapGet("/reports/tax/{year:int}/csv", async (LedgerService ledger, int year, string? person, CancellationToken ct) =>
        {
            var csv = await ledger.GetTaxReportCsvAsync(year, person, ct);
            return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv", $"looniebooks-tax-{year}.csv");
        });

        api.MapGet("/holdings", (LedgerService ledger, CancellationToken ct) => ledger.GetHoldingsAsync(ct: ct));

        // Replaces the account's snapshot.
        api.MapPut("/accounts/{accountId}/holdings", async (LedgerService ledger, string accountId, ImportHoldingsRequest request, CancellationToken ct) =>
            await ledger.ImportHoldingsAsync(accountId, request, ct) is { } result ? Results.Ok(result) : Results.NotFound());

        // No content either way: an account without holdings already has none.
        api.MapDelete("/accounts/{accountId}/holdings", async (LedgerService ledger, string accountId, CancellationToken ct) =>
        {
            await ledger.DeleteHoldingsAsync(accountId, ct);
            return Results.NoContent();
        });

        // Prices from the quote feed, which the browser downloaded and matched to the holdings.
        api.MapPost("/holdings/quotes", async (LedgerService ledger, ApplyQuotesRequest request, CancellationToken ct) =>
            Results.Ok(await ledger.ApplyQuotesAsync(request, ct)));

        // Month-end closes from the monthly price history feed, which the browser downloaded and matched to the holdings.
        api.MapGet("/holdings/price-history", (LedgerService ledger, CancellationToken ct) => ledger.GetPriceHistoryAsync(ct));
        api.MapPost("/holdings/price-history", async (LedgerService ledger, ApplyPriceHistoryRequest request, CancellationToken ct) =>
            Results.Ok(await ledger.ApplyPriceHistoryAsync(request, ct)));

        api.MapGet("/exchange-rates", (LedgerService ledger, CancellationToken ct) => ledger.GetExchangeRatesAsync(ct));

        api.MapPut("/exchange-rates/{currency}", async (LedgerService ledger, string currency, ExchangeRate rate, CancellationToken ct) =>
            Results.Ok(await ledger.SetExchangeRateAsync(rate with { Currency = currency }, ct)));

        // The Bank of Canada's monthly averages, which the browser fetched for the accounts' currencies.
        api.MapGet("/monthly-exchange-rates", (LedgerService ledger, CancellationToken ct) => ledger.GetMonthlyExchangeRatesAsync(ct));
        api.MapPost("/monthly-exchange-rates", async (LedgerService ledger, ApplyMonthlyExchangeRatesRequest request, CancellationToken ct) =>
            Results.Ok(await ledger.ApplyMonthlyExchangeRatesAsync(request, ct)));

        api.MapGet("/budgets/months", (LedgerService ledger, int fromYear, int toYear, CancellationToken ct) => ledger.GetBudgetMonthsAsync(fromYear, toYear, ct));

        api.MapGet("/budgets/{year:int}", (LedgerService ledger, int year, CancellationToken ct) => ledger.GetBudgetPlanAsync(year, ct));

        api.MapPut("/budgets/{year:int}", async (LedgerService ledger, int year, SetBudgetRequest request, CancellationToken ct) =>
            Results.Ok(await ledger.SetBudgetAsync(year, request, ct)));

        api.MapGet("/properties", (LedgerService ledger, CancellationToken ct) => ledger.GetPropertiesAsync(ct));

        api.MapPost("/properties", async (LedgerService ledger, PropertyRequest request, CancellationToken ct) =>
        {
            var property = await ledger.CreatePropertyAsync(request, ct);
            return Results.Created($"/api/properties/{property.Id}", property);
        });

        api.MapPut("/properties/{id}", async (LedgerService ledger, string id, PropertyRequest request, CancellationToken ct) =>
            await ledger.UpdatePropertyAsync(id, request, ct) is { } property ? Results.Ok(property) : Results.NotFound());

        api.MapDelete("/properties/{id}", async (LedgerService ledger, string id, CancellationToken ct) =>
            await ledger.DeletePropertyAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        api.MapGet("/house-price-index", (LedgerService ledger, CancellationToken ct) => ledger.GetHpiReadingsAsync(ct));

        api.MapPut("/house-price-index", async (LedgerService ledger, HpiReading reading, CancellationToken ct) =>
            Results.Ok(await ledger.SetHpiReadingAsync(reading, ct)));

        api.MapDelete("/house-price-index", async (LedgerService ledger, string area, DateOnly month, CancellationToken ct) =>
            await ledger.DeleteHpiReadingAsync(area, month, ct) ? Results.NoContent() : Results.NotFound());

        // The house price index feed's series for the properties' areas, downloaded by the browser.
        api.MapPost("/house-price-index/feed", async (LedgerService ledger, ApplyHpiRequest request, CancellationToken ct) =>
            Results.Ok(await ledger.ApplyHpiAsync(request, ct)));

        api.MapGet("/household", (LedgerService ledger, CancellationToken ct) => ledger.GetHouseholdAsync(ct));

        api.MapPost("/household", async (LedgerService ledger, HouseholdMemberRequest request, CancellationToken ct) =>
        {
            var member = await ledger.AddHouseholdMemberAsync(request.Name, ct);
            return Results.Created($"/api/household/{member.Id}", member);
        });

        api.MapPut("/household/{id}", async (LedgerService ledger, string id, HouseholdMemberRequest request, CancellationToken ct) =>
            await ledger.RenameHouseholdMemberAsync(id, request.Name, ct) is { } member ? Results.Ok(member) : Results.NotFound());

        api.MapDelete("/household/{id}", async (LedgerService ledger, string id, CancellationToken ct) =>
            await ledger.DeleteHouseholdMemberAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        api.MapGet("/categories", (LedgerService ledger, CancellationToken ct) => ledger.GetCustomCategoriesAsync(ct));

        api.MapPost("/categories", async (LedgerService ledger, CustomCategoryRequest request, CancellationToken ct) =>
        {
            var category = await ledger.AddCustomCategoryAsync(request, ct);
            return Results.Created($"/api/categories/{category.Id}", category);
        });

        api.MapDelete("/categories/{id}", async (LedgerService ledger, string id, CancellationToken ct) =>
            await ledger.DeleteCustomCategoryAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        api.MapGet("/tags", (LedgerService ledger, CancellationToken ct) => ledger.GetTagsAsync(ct));

        api.MapGet("/tags/usage", (LedgerService ledger, CancellationToken ct) => ledger.GetTagUsageAsync(ct));

        api.MapPost("/tags", async (LedgerService ledger, TagRequest request, CancellationToken ct) =>
        {
            var tag = await ledger.AddTagAsync(request.Name, ct);
            return Results.Created($"/api/tags/{tag.Id}", tag);
        });

        api.MapPut("/tags/{id}", async (LedgerService ledger, string id, TagRequest request, CancellationToken ct) =>
            await ledger.RenameTagAsync(id, request.Name, ct) is { } tag ? Results.Ok(tag) : Results.NotFound());

        api.MapDelete("/tags/{id}", async (LedgerService ledger, string id, CancellationToken ct) =>
            await ledger.DeleteTagAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        api.MapPut("/tagging", async (LedgerService ledger, TagManyRequest request, CancellationToken ct) =>
            Results.Ok(await ledger.TagManyAsync(request, ct)));

        api.MapGet("/backup", (LedgerService ledger, CancellationToken ct) => ledger.ExportAsync(ct));

        // Merges uploaded data (a backup file, or a browser ledger moving to the cloud) into this user's ledger.
        api.MapPost("/backup", (LedgerService ledger, LedgerBackup backup, CancellationToken ct) => ledger.RestoreAsync(backup, ct))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(100 * 1024 * 1024));

        return api;
    }

    /// <summary>The signed-in user's own profile. Open to every registered user, premium or not.</summary>
    public static IEndpointRouteBuilder MapProfileEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/me", async (UserService users, CurrentUser user, CancellationToken ct) =>
            UserService.ToProfile(await users.SignInAsync(user, ct)))
            .WithTags("Users")
            .RequireAuthorization();

        return app;
    }
}
