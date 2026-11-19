using MapleLedger.Core;
using MapleLedger.Core.Investments;
using MapleLedger.Fdx;

namespace MapleLedger.Web.Client.Services;

public static class AccountCurrency
{
    /// <summary>
    /// The currency an account's amounts are in, for labelling them (transactions don't carry
    /// one); Canadian dollars for an account that isn't in the list.
    /// </summary>
    public static string CurrencyOf(this IEnumerable<Account>? accounts, string accountId) =>
        accounts?.FirstOrDefault(a => a.AccountId == accountId) is { } account ? CurrencyRates.CurrencyOf(account) : Money.Cad;

    /// <summary>
    /// Transactions' amounts added up in each account's currency, Canadian dollars first:
    /// "CA$500.00 + US$20.00". Never one sum across currencies.
    /// </summary>
    public static string Totals(this IEnumerable<Account>? accounts, IEnumerable<Transaction> transactions)
    {
        var sums = transactions
            .GroupBy(t => accounts.CurrencyOf(t.AccountId))
            .OrderBy(g => g.Key == Money.Cad ? 0 : 1).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => Money.Format(g.Sum(t => t.Amount), g.Key))
            .ToList();
        return sums.Count == 0 ? Money.Format(0) : string.Join(" + ", sums);
    }
}
