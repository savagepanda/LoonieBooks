using MapleLedger.Core.Investments;
using MapleLedger.Fdx;

namespace MapleLedger.Core.Reports;

public sealed record MonthlyCashFlow(int Year, int Month, decimal Income, decimal Expenses)
{
    public decimal Net => Income - Expenses;
}

/// <summary>A category's total; with <see cref="SubCategory"/> set (or null for "no detail") in a breakdown.</summary>
public sealed record CategoryTotal(string Category, decimal Amount, int TransactionCount, string? SubCategory = null);

/// <param name="Assets">The accounts' balances that are assets (bank and investment accounts).</param>
/// <param name="RealEstate">The properties' estimated values (see <see cref="MapleLedger.Core.RealEstate.PropertyValuation"/>).</param>
public sealed record NetWorthSummary(decimal Assets, decimal Liabilities, decimal RealEstate = 0)
{
    public decimal NetWorth => Assets + RealEstate - Liabilities;
}

/// <summary>Net worth at the end of a month (or on <see cref="AsOf"/>, for the month still under way), in Canadian dollars.</summary>
public sealed record MonthlyNetWorth(int Year, int Month, DateOnly AsOf, decimal Assets, decimal Liabilities, decimal RealEstate = 0)
{
    public decimal NetWorth => Assets + RealEstate - Liabilities;
}

/// <summary>
/// An account's balance at the end of a month (or on <see cref="AsOf"/>, for the month still under way),
/// in the account's own currency: what it holds, or for a loan, line of credit or card, what's owed on it.
/// </summary>
/// <param name="Estimated">Moved by the holdings' month-end prices (see <see cref="HoldingPriceMoves"/>), not only worked back from transactions.</param>
public sealed record MonthlyAccountValue(int Year, int Month, DateOnly AsOf, decimal Value, bool Estimated = false);

public sealed record DashboardSummary(
    DateOnly From,
    DateOnly To,
    decimal Income,
    decimal Expenses,
    IReadOnlyList<MonthlyCashFlow> Monthly,
    IReadOnlyList<CategoryTotal> SpendingByCategory,
    IReadOnlyList<CategoryTotal> IncomeByCategory,
    NetWorthSummary NetWorth,
    IReadOnlyList<LedgerTransaction> RecentTransactions,
    IReadOnlyList<CategoryTotal>? SpendingDetails = null,
    IReadOnlyList<CategoryTotal>? IncomeDetails = null,
    IReadOnlyList<CategoryTotal>? SpendingByAccount = null,
    IReadOnlyList<CategoryTotal>? IncomeByAccount = null)
{
    public decimal Net => Income - Expenses;
}

/// <summary>
/// Income and spending the way a person thinks about them: transfers between their own
/// accounts are ignored, and refunds reduce spending in their category rather than counting as
/// income.
/// </summary>
public static class CashFlowCalculator
{
    public static IReadOnlyList<MonthlyCashFlow> Monthly(IEnumerable<Transaction> transactions, DateOnly from, DateOnly to)
    {
        var byMonth = Countable(transactions)
            .Where(t => t.EffectiveDate >= from && t.EffectiveDate <= to)
            .GroupBy(t => (t.EffectiveDate.Year, t.EffectiveDate.Month))
            .ToDictionary(g => g.Key, g => Totals(g));

        var months = new List<MonthlyCashFlow>();
        for (var m = new DateOnly(from.Year, from.Month, 1); m <= to; m = m.AddMonths(1))
        {
            var (income, expenses) = byMonth.GetValueOrDefault((m.Year, m.Month));
            months.Add(new MonthlyCashFlow(m.Year, m.Month, income, expenses));
        }

        return months;
    }

    public static (decimal Income, decimal Expenses) Totals(IEnumerable<Transaction> transactions)
    {
        decimal income = 0, expenses = 0;
        foreach (var t in Countable(transactions))
        {
            switch (Categories.KindOf(t.Category, t.DebitCreditMemo))
            {
                case CategoryKind.Income:
                    income += t.SignedAmount;
                    break;
                case CategoryKind.Expense:
                    expenses -= t.SignedAmount;
                    break;
            }
        }

        return (income, expenses);
    }

    /// <summary>Net spending per category (debits minus refunds), largest first.</summary>
    public static IReadOnlyList<CategoryTotal> SpendingByCategory(IEnumerable<Transaction> transactions) =>
        ByCategory(transactions, CategoryKind.Expense, sign: -1);

    public static IReadOnlyList<CategoryTotal> IncomeByCategory(IEnumerable<Transaction> transactions) =>
        ByCategory(transactions, CategoryKind.Income, sign: 1);

    /// <summary>Net spending per category and sub-category (null for no detail), largest first.</summary>
    public static IReadOnlyList<CategoryTotal> SpendingDetails(IEnumerable<Transaction> transactions) =>
        BySubCategory(transactions, CategoryKind.Expense, sign: -1);

    public static IReadOnlyList<CategoryTotal> IncomeDetails(IEnumerable<Transaction> transactions) =>
        BySubCategory(transactions, CategoryKind.Income, sign: 1);

    /// <summary>Net spending per account, largest first; <see cref="CategoryTotal.Category"/> holds the account id.</summary>
    public static IReadOnlyList<CategoryTotal> SpendingByAccount(IEnumerable<Transaction> transactions) =>
        ByAccount(transactions, CategoryKind.Expense, sign: -1);

    /// <summary>Income per account, largest first; <see cref="CategoryTotal.Category"/> holds the account id.</summary>
    public static IReadOnlyList<CategoryTotal> IncomeByAccount(IEnumerable<Transaction> transactions) =>
        ByAccount(transactions, CategoryKind.Income, sign: 1);

    /// <summary>
    /// The open accounts' balances added up in Canadian dollars, each converted from its account's
    /// currency at <paramref name="rates"/> (at face value without one).
    /// </summary>
    public static NetWorthSummary NetWorth(IEnumerable<Account> accounts, CurrencyRates? rates = null, decimal realEstate = 0)
    {
        rates ??= CurrencyRates.None;
        decimal assets = 0, liabilities = 0;
        foreach (var a in accounts.Where(a => a.Status != AccountStatus.Closed))
        {
            if (a.IsLiability) liabilities += rates.BalanceInCad(a);
            else assets += rates.BalanceInCad(a);
        }

        return new NetWorthSummary(assets, liabilities, realEstate);
    }

    /// <summary>
    /// Net worth at the end of each month from <paramref name="from"/> to <paramref name="to"/> (the last
    /// month ends on <paramref name="to"/>). Only balances today are known, so each account's balance on
    /// a month's last day is its balance less what its transactions added after that day, up to the date
    /// the balance was set; later months keep that balance. Closed accounts are worth nothing now, so
    /// they count what they held before their last transactions. Investments move with their transactions
    /// and, with <paramref name="marketMoves"/>, with their holdings' month-end prices (see
    /// <see cref="HoldingPriceMoves"/>); without it, market changes aren't known. Every month is converted at today's
    /// <paramref name="rates"/>, and real estate counts at <paramref name="realEstateOn"/> for the
    /// month's last day (its value by its area's house price index then), or at
    /// <paramref name="realEstate"/> throughout without it. The last month matches <see cref="NetWorth"/>.
    /// </summary>
    public static IReadOnlyList<MonthlyNetWorth> MonthlyNetWorth(IEnumerable<Account> accounts, IEnumerable<Transaction> transactions, DateOnly from, DateOnly to, CurrencyRates? rates = null, decimal realEstate = 0, Func<DateOnly, decimal>? realEstateOn = null, IReadOnlyDictionary<string, Func<DateOnly, decimal?>>? marketMoves = null)
    {
        rates ??= CurrencyRates.None;
        var ends = MonthEnds(from, to);
        var assets = new decimal[ends.Count];
        var liabilities = new decimal[ends.Count];
        var byAccount = Countable(transactions).ToLookup(t => t.AccountId ?? "");
        foreach (var a in accounts)
        {
            if (BalancesAt(a, byAccount[a.AccountId], ends) is not { } balances) continue;
            var currency = CurrencyRates.CurrencyOf(a);
            var moves = a.Status != AccountStatus.Closed && !a.IsLiability ? marketMoves?.GetValueOrDefault(a.AccountId) : null;
            for (var i = 0; i < ends.Count; i++)
            {
                var cad = rates.ToCad(balances[i], currency);
                if (a.IsLiability) liabilities[i] += cad;
                else assets[i] += cad + (moves?.Invoke(ends[i].End) ?? 0);
            }
        }

        return ends.Select((e, i) => new MonthlyNetWorth(e.Year, e.Month, e.End, assets[i], liabilities[i], realEstateOn?.Invoke(e.End) ?? realEstate)).ToList();
    }

    /// <summary>
    /// One account's balance at the end of each month, in its own currency, worked back from today's
    /// balance the way <see cref="MonthlyNetWorth"/> does it; for a liability, what's owed. An investment
    /// account also moves with <paramref name="marketMove"/>, its holdings' month-end prices against
    /// today's in Canadian dollars (see <see cref="HoldingPriceMoves"/>), converted back at
    /// <paramref name="rates"/>. Empty when the account has no balance set (nothing to work back from).
    /// </summary>
    public static IReadOnlyList<MonthlyAccountValue> MonthlyAccountValue(Account account, IEnumerable<Transaction> transactions, DateOnly from, DateOnly to, CurrencyRates? rates = null, Func<DateOnly, decimal?>? marketMove = null)
    {
        rates ??= CurrencyRates.None;
        var ends = MonthEnds(from, to);
        if (BalancesAt(account, Countable(transactions).Where(t => t.AccountId == account.AccountId), ends) is not { } balances) return [];
        var moves = account.Status != AccountStatus.Closed && !account.IsLiability ? marketMove : null;
        // The moves are in Canadian dollars; back into the account's currency (at face value without a rate).
        var rate = rates.RateOf(CurrencyRates.CurrencyOf(account)) is { } r and not 0 ? r : 1m;
        return ends.Select((e, i) => moves?.Invoke(e.End) is { } move
            ? new MonthlyAccountValue(e.Year, e.Month, e.End, balances[i] + Math.Round(move / rate, 2), true)
            : new MonthlyAccountValue(e.Year, e.Month, e.End, balances[i])).ToList();
    }

    /// <summary>Each month from <paramref name="from"/>'s to <paramref name="to"/>'s, with its last day (<paramref name="to"/> for the last).</summary>
    private static List<(int Year, int Month, DateOnly End)> MonthEnds(DateOnly from, DateOnly to)
    {
        var ends = new List<(int Year, int Month, DateOnly End)>();
        for (var m = new DateOnly(from.Year, from.Month, 1); m <= to; m = m.AddMonths(1))
        {
            var last = m.AddMonths(1).AddDays(-1);
            ends.Add((m.Year, m.Month, last < to ? last : to));
        }

        return ends;
    }

    /// <summary>
    /// The account's balance on each of <paramref name="ends"/>, in its own currency: today's balance
    /// less what its (countable) transactions added after that day, up to the date the balance was set.
    /// A closed account is worth nothing now; null for an open one without a balance.
    /// </summary>
    private static decimal[]? BalancesAt(Account a, IEnumerable<Transaction> transactions, List<(int Year, int Month, DateOnly End)> ends)
    {
        var closed = a.Status == AccountStatus.Closed;
        if (!closed && a.CurrentBalance is null) return null; // counted as nothing today, so nothing then either
        var balance = closed ? 0 : a.CurrentBalance!.Value;
        var setOn = closed || a.BalanceAsOf is not { } asOf ? DateOnly.MaxValue : DateOnly.FromDateTime(asOf.Date);
        // A credit adds to an asset's balance and pays down what's owed on a liability.
        var sign = a.IsLiability ? -1 : 1;
        var since = transactions.Where(t => t.EffectiveDate <= setOn).ToList();
        return ends.Select(e => balance - sign * since.Where(t => t.EffectiveDate > e.End).Sum(t => t.SignedAmount)).ToArray();
    }

    private static IReadOnlyList<CategoryTotal> ByCategory(IEnumerable<Transaction> transactions, CategoryKind kind, int sign) =>
        Countable(transactions)
            .Where(t => Categories.KindOf(t.Category, t.DebitCreditMemo) == kind)
            .GroupBy(t => string.IsNullOrEmpty(t.Category) ? Categories.Uncategorized : t.Category)
            .Select(g => new CategoryTotal(g.Key, sign * g.Sum(t => t.SignedAmount), g.Count()))
            .Where(c => c.Amount > 0)
            .OrderByDescending(c => c.Amount)
            .ToList();

    private static IReadOnlyList<CategoryTotal> ByAccount(IEnumerable<Transaction> transactions, CategoryKind kind, int sign) =>
        Countable(transactions)
            .Where(t => Categories.KindOf(t.Category, t.DebitCreditMemo) == kind)
            .GroupBy(t => t.AccountId ?? "")
            .Select(g => new CategoryTotal(g.Key, sign * g.Sum(t => t.SignedAmount), g.Count()))
            .Where(c => c.Amount > 0)
            .OrderByDescending(c => c.Amount)
            .ToList();

    private static IReadOnlyList<CategoryTotal> BySubCategory(IEnumerable<Transaction> transactions, CategoryKind kind, int sign) =>
        Countable(transactions)
            .Where(t => Categories.KindOf(t.Category, t.DebitCreditMemo) == kind)
            .GroupBy(t => (Category: string.IsNullOrEmpty(t.Category) ? Categories.Uncategorized : t.Category, Sub: string.IsNullOrEmpty(t.SubCategory) ? null : t.SubCategory))
            .Select(g => new CategoryTotal(g.Key.Category, sign * g.Sum(t => t.SignedAmount), g.Count(), g.Key.Sub))
            .Where(c => c.Amount > 0)
            .OrderByDescending(c => c.Amount)
            .ToList();

    private static IEnumerable<Transaction> Countable(IEnumerable<Transaction> transactions) =>
        transactions.Where(t => t.Status is TransactionStatus.Posted or TransactionStatus.Pending);
}
