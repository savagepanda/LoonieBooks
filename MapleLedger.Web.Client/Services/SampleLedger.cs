using MapleLedger.Core;
using MapleLedger.Core.Budgets;
using MapleLedger.Core.Categorization;
using MapleLedger.Core.Import;
using MapleLedger.Core.Tax;

namespace MapleLedger.Web.Client.Services;

/// <summary>Fills an empty ledger with the made-up <see cref="SampleHousehold"/>, and a budget for this year, for the browser demo.</summary>
public sealed class SampleLedger(ILedgerStore store, TransactionCategorizer categorizer, TaxSuggester taxSuggester)
{
    public async Task LoadAsync()
    {
        // Holdings can't be dated after the ledger's today, which is UTC: the earlier of the two dates.
        var today = DateOnly.FromDateTime(DateTime.Now < DateTime.UtcNow ? DateTime.Now : DateTime.UtcNow);
        foreach (var sample in SampleHousehold.Create(today))
        {
            var account = await store.CreateAccountAsync(sample.Account);
            var transactions = SampleHousehold.Transactions(sample, account.AccountId, categorizer, taxSuggester);
            if (transactions.Count > 0)
            {
                await store.ImportAsync(account.AccountId, new ImportTransactionsRequest(transactions, Source: "sample"));
            }

            if (sample.Holdings.Count > 0)
            {
                await store.ImportHoldingsAsync(account.AccountId, new(today, sample.Holdings));
            }
        }

        // This year's budget from the household's own spending, as "Use suggestions" sets it, so the
        // Budget page and the dashboard's budget charts have something to show.
        var plan = await store.GetBudgetPlanAsync(today.Year);
        if (BudgetCalculator.SuggestedLines(plan) is { Count: > 0 } lines)
        {
            await store.SetBudgetAsync(today.Year, new SetBudgetRequest(lines));
        }
    }
}
