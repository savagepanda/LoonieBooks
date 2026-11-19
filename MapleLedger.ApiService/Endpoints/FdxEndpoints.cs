using System.Globalization;
using MapleLedger.Data;
using MapleLedger.Fdx;

namespace MapleLedger.ApiService.Endpoints;

/// <summary>
/// A read-only FDX-shaped surface over the ledger (Accounts and Transactions). The web client
/// reads accounts through it, and it is the seam where a real FDX data provider will plug in once
/// Canadian consumer-driven banking goes live.
/// </summary>
public static class FdxEndpoints
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 1000;

    public static RouteGroupBuilder MapFdxEndpoints(this IEndpointRouteBuilder app)
    {
        var fdx = app.MapGroup("/fdx/v6").WithTags("FDX");

        fdx.MapGet("/accounts", async (LedgerService ledger, string? offset, int? limit, CancellationToken ct) =>
        {
            var (skip, take) = Page(offset, limit);
            var (accounts, total) = await ledger.GetAccountsPageAsync(skip, take, ct);
            return new AccountsResponse(accounts, PageMetadata(skip, take, total));
        })
        .WithName("FdxSearchForAccounts");

        fdx.MapGet("/accounts/{accountId}", async (LedgerService ledger, string accountId, CancellationToken ct) =>
            await ledger.FindAccountAsync(accountId, ct) is { } account
                ? Results.Ok(account)
                : Results.NotFound(FdxError.AccountNotFound(accountId)))
        .WithName("FdxGetAccount");

        fdx.MapGet("/accounts/{accountId}/transactions", async (
            LedgerService ledger, string accountId, DateOnly? startTime, DateOnly? endTime, string? offset, int? limit, CancellationToken ct) =>
        {
            var (skip, take) = Page(offset, limit);
            return await ledger.GetAccountTransactionsPageAsync(accountId, startTime, endTime, skip, take, ct) is { } page
                ? Results.Ok(new TransactionsResponse(page.Transactions, PageMetadata(skip, take, page.Total)))
                : Results.NotFound(FdxError.AccountNotFound(accountId));
        })
        .WithName("FdxSearchForAccountTransactions");

        return fdx;
    }

    private static (int Skip, int Take) Page(string? offset, int? limit) =>
        (int.TryParse(offset, NumberStyles.None, CultureInfo.InvariantCulture, out var skip) ? skip : 0,
         Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit));

    private static PageMetadata PageMetadata(int skip, int take, int total) => new(
        NextOffset: skip + take < total ? (skip + take).ToString(CultureInfo.InvariantCulture) : null,
        PrevOffset: skip > 0 ? Math.Max(0, skip - take).ToString(CultureInfo.InvariantCulture) : null,
        TotalElements: total);
}
