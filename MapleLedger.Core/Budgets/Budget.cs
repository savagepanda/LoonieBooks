using MapleLedger.Fdx;

namespace MapleLedger.Core.Budgets;

/// <summary>
/// What the household means to spend in a category (or one of its details) over a calendar year, in
/// Canadian dollars. A month's budget is a twelfth of it, unless it's <paramref name="Yearly"/>.
/// </summary>
/// <param name="Yearly">Kept for the year as a whole, with no monthly share: for costs that come a few times a
/// year (tuition, tax instalments), which a monthly share would show as over in one month and under in the rest.</param>
public sealed record BudgetTarget(int Year, string Category, string? SubCategory, decimal Amount, bool Yearly = false)
{
    public const decimal MaxAmount = 100_000_000m;

    /// <summary>The most budget lines a ledger keeps, over all its years.</summary>
    public const int MaxCount = 5000;
}

/// <summary>One line of a year's budget to set: an amount for the year, or null (or 0) to remove it.</summary>
/// <param name="Yearly">Whether it's kept for the year only (see <see cref="BudgetTarget.Yearly"/>); null keeps the line's (a new line is monthly).</param>
public sealed record BudgetLineRequest(string Category, string? SubCategory, decimal? Amount, bool? Yearly = null);

public sealed record SetBudgetRequest(IReadOnlyList<BudgetLineRequest> Lines)
{
    public const int MaxLines = 500;
}

/// <summary>
/// The complete months the suggestions are worked out from: last calendar year when the ledger covers
/// all of it, otherwise the latest complete months it has (up to 12), counted as a year.
/// </summary>
public sealed record BudgetBasis(DateOnly From, DateOnly To, int Months)
{
    public bool IsCalendarYear => Months == 12 && From.Month == 1;
}

/// <summary>A category (or a detail of one) on the budget page.</summary>
/// <param name="BasisSpent">Spent over the <see cref="BudgetPlan.Basis"/> months.</param>
/// <param name="Suggested">The basis spending as a year, rounded up to a tidy amount; null without any.</param>
/// <param name="Target">This year's budget; null when it has none.</param>
/// <param name="PreviousTarget">Last year's budget; null when it had none.</param>
/// <param name="Spent">Spent this year so far (all of it, for a past year).</param>
/// <param name="Yearly">This year's budget is for the year only (<see cref="BudgetTarget.Yearly"/>).</param>
/// <param name="PreviousYearly">Last year's was.</param>
/// <param name="SuggestYearly">The spending comes in a few months of the basis only, so a yearly budget fits it better.</param>
/// <param name="SuggestedMonthly">For a monthly budget: the basis spending's average month, rounded up to a tidy amount (a month's, not a year's).</param>
public sealed record BudgetRow(string Category, string? SubCategory, decimal BasisSpent, decimal? Suggested, decimal? Target, decimal? PreviousTarget, decimal Spent,
    bool Yearly = false, bool PreviousYearly = false, bool SuggestYearly = false, decimal? SuggestedMonthly = null)
{
    /// <summary>The suggestion as a year's amount: <see cref="Suggested"/> for a yearly budget, otherwise the tidy month times 12.</summary>
    public decimal? SuggestionFor(bool yearly) => yearly ? Suggested : SuggestedMonthly * 12;
}

/// <summary>A year's budget beside the spending it's measured against and the suggestions for it.</summary>
/// <param name="AsOf">The last day counted: today for this year, the year's last day for a past one.</param>
/// <param name="Basis">What the suggestions are based on; null when the ledger has no complete month to go by.</param>
/// <param name="Rows">Every spending category, each followed by its details that have spending or a budget.</param>
/// <param name="Uncategorized">Uncategorized spending this year so far: it counts against no budget until it's sorted.</param>
public sealed record BudgetPlan(int Year, DateOnly AsOf, BudgetBasis? Basis, IReadOnlyList<BudgetRow> Rows, decimal Uncategorized)
{
    /// <summary>The year's whole budget: each category's own budget, or its details' added up when it has none.</summary>
    public decimal Total => BudgetCalculator.Total(Rows.Select(r => (r.Category, r.SubCategory, r.Target)));

    public decimal PreviousTotal => BudgetCalculator.Total(Rows.Select(r => (r.Category, r.SubCategory, r.PreviousTarget)));
}

/// <summary>One budget line's share of a month: a twelfth of its year's budget, and what was spent.</summary>
/// <param name="Nested">A detail whose category has a budget of its own, which already counts its spending.</param>
/// <param name="Yearly">A budget for the year only (<see cref="BudgetTarget.Yearly"/>): <see cref="Budget"/> is still a twelfth of
/// it, for adding up a year, but it has no monthly share to measure a month against.</param>
public sealed record BudgetLineAmount(string Category, string? SubCategory, decimal Budget, decimal Spent, bool Nested = false, bool Yearly = false);

/// <summary>A month's budget and spending, line by line.</summary>
/// <param name="Unbudgeted">Spending in categories without a budget (uncategorized spending aside).</param>
/// <param name="Uncategorized">Uncategorized spending, which counts against no budget.</param>
public sealed record BudgetMonth(int Year, int Month, IReadOnlyList<BudgetLineAmount> Lines, decimal Unbudgeted, decimal Uncategorized)
{
    public decimal Budget => Lines.Where(l => !l.Nested).Sum(l => l.Budget);
    public decimal Spent => Lines.Where(l => !l.Nested).Sum(l => l.Spent);
}

/// <summary>
/// Budgets measured against spending the way the dashboard counts it: transfers are ignored and
/// refunds reduce spending in their category. Amounts are expected in Canadian dollars already.
/// </summary>
public static class BudgetCalculator
{
    /// <summary>
    /// The months the suggestions for <paramref name="year"/> go by: the calendar year before it when it's
    /// over and the ledger covers all of it, otherwise up to 12 of the latest complete months. A first
    /// month that starts after its first week is left out, as only part of it was imported.
    /// </summary>
    /// <param name="firstTransaction">The ledger's earliest transaction; null when it has none.</param>
    public static BudgetBasis? Basis(int year, DateOnly? firstTransaction, DateOnly today)
    {
        if (firstTransaction is not { } first) return null;
        var firstFull = new DateOnly(first.Year, first.Month, 1);
        if (first.Day > 7) firstFull = firstFull.AddMonths(1);

        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var lastYear = new DateOnly(year - 1, 1, 1);
        if (firstFull <= lastYear && lastYear.AddYears(1) <= thisMonth)
        {
            return new BudgetBasis(lastYear, new DateOnly(year - 1, 12, 31), 12);
        }

        var end = thisMonth; // the first month not counted
        var start = end.AddMonths(-12);
        if (start < firstFull) start = firstFull;
        var months = (end.Year - start.Year) * 12 + end.Month - start.Month;
        return months <= 0 ? null : new BudgetBasis(start, end.AddDays(-1), months);
    }

    /// <summary>Spending over <paramref name="months"/> months as a year, rounded up to a tidy amount; null for none.</summary>
    public static decimal? Suggest(decimal spent, int months)
    {
        if (spent <= 0 || months <= 0) return null;
        var yearly = spent * 12 / months;
        var step = yearly switch
        {
            < 1_000 => 50m,
            < 10_000 => 100m,
            < 50_000 => 500m,
            _ => 1_000m,
        };
        return Math.Min(BudgetTarget.MaxAmount, Math.Ceiling(yearly / step) * step);
    }

    /// <summary>The average month of spending over <paramref name="months"/> months, rounded up to a tidy amount; null for none.</summary>
    public static decimal? SuggestMonthly(decimal spent, int months)
    {
        if (spent <= 0 || months <= 0) return null;
        var monthly = spent / months;
        var step = monthly switch
        {
            < 50 => 5m,
            < 250 => 10m,
            < 1_000 => 25m,
            < 5_000 => 50m,
            _ => 100m,
        };
        return Math.Min(BudgetTarget.MaxAmount / 12, Math.Ceiling(monthly / step) * step);
    }

    /// <summary>A category whose spending is under this share of all spending is minor: listed apart, under Other, until it has a budget.</summary>
    public const decimal MinorShare = 0.01m;

    /// <summary>
    /// The plan's minor categories: without a budget this year or last (on itself or a detail), and with
    /// under <see cref="MinorShare"/> of all the basis spending (this year's spending, without a basis),
    /// none at all included. Budgeting them would mostly add clutter. Without any spending to compare
    /// against (a new ledger), none are: every category is there to budget from scratch.
    /// </summary>
    public static IReadOnlySet<string> MinorCategories(BudgetPlan plan)
    {
        var tops = plan.Rows.Where(r => r.SubCategory is null).ToList();
        decimal Spending(BudgetRow r) => plan.Basis is null ? r.Spent : r.BasisSpent;
        var total = tops.Sum(r => Math.Max(0, Spending(r)));
        var budgeted = plan.Rows.Where(r => r.Target is not null || r.PreviousTarget is not null).Select(r => r.Category).ToHashSet();
        if (total <= 0) return new HashSet<string>();
        return tops
            .Where(r => !budgeted.Contains(r.Category) && Spending(r) < total * MinorShare)
            .Select(r => r.Category)
            .ToHashSet();
    }

    /// <summary>
    /// Spending that came in at most a third of the months (4 of 12, say) is irregular: a yearly budget fits
    /// it better than a monthly share. Under six months there's too little to tell.
    /// </summary>
    public static bool IsIrregular(int monthsWithSpending, int months) =>
        months >= 6 && monthsWithSpending > 0 && monthsWithSpending <= months / 3;

    /// <summary>A whole budget from its lines: each category's own amount, or its details' added up when it has none.</summary>
    public static decimal Total(IEnumerable<(string Category, string? SubCategory, decimal? Amount)> lines)
    {
        var list = lines.Where(l => l.Amount is > 0).ToList();
        var own = list.Where(l => l.SubCategory is null).Select(l => l.Category).ToHashSet();
        return list.Where(l => l.SubCategory is null || !own.Contains(l.Category)).Sum(l => l.Amount!.Value);
    }

    /// <summary>The year's budget page: every spending category of <paramref name="tree"/>, its budgets, spending and suggestion.</summary>
    /// <param name="transactions">Transactions from the start of <paramref name="basis"/> (or the year) to the year's end, in Canadian dollars.</param>
    public static BudgetPlan Plan(int year, DateOnly today, BudgetBasis? basis, IReadOnlyList<CategoryNode> tree, IEnumerable<BudgetTarget> targets, IReadOnlyList<Transaction> transactions)
    {
        var yearStart = new DateOnly(year, 1, 1);
        var yearEnd = new DateOnly(year, 12, 31);
        var asOf = today < yearEnd ? today : yearEnd;
        var spent = asOf < yearStart ? new SpendingTotals([]) : new SpendingTotals(Spending(transactions, yearStart, asOf));
        var basisSpent = basis is null ? new SpendingTotals([]) : new SpendingTotals(Spending(transactions, basis.From, basis.To));
        var all = targets.ToList();
        var now = all.Where(t => t.Year == year).ToDictionary(t => (t.Category, t.SubCategory));
        var before = all.Where(t => t.Year == year - 1).ToDictionary(t => (t.Category, t.SubCategory));

        // Each basis month's spending, to tell spending that comes every month from spending that comes in a few.
        var basisMonths = new List<SpendingTotals>();
        for (var m = basis?.From ?? DateOnly.MaxValue; basis is not null && m <= basis.To; m = m.AddMonths(1))
        {
            basisMonths.Add(new SpendingTotals(Spending(transactions, m, m.AddMonths(1).AddDays(-1))));
        }

        var rows = new List<BudgetRow>();
        foreach (var node in tree.Where(n => IsBudgetable(n.Name)))
        {
            rows.Add(Row(node.Name, null));
            foreach (var sub in node.SubCategories)
            {
                var row = Row(node.Name, sub);
                if (row.BasisSpent != 0 || row.Spent != 0 || row.Target is not null || row.PreviousTarget is not null) rows.Add(row);
            }
        }

        return new BudgetPlan(year, asOf, basis, rows, spent.Uncategorized);

        BudgetRow Row(string category, string? sub)
        {
            var b = basisSpent.Of(category, sub);
            var target = now.GetValueOrDefault((category, sub));
            var previous = before.GetValueOrDefault((category, sub));
            var active = basisMonths.Count(m => m.Of(category, sub) > 0);
            return new BudgetRow(category, sub, b, basis is null ? null : Suggest(b, basis.Months), target?.Amount, previous?.Amount, spent.Of(category, sub),
                target?.Yearly ?? false, previous?.Yearly ?? false, b > 0 && IsIrregular(active, basisMonths.Count),
                basis is null ? null : SuggestMonthly(b, basis.Months));
        }
    }

    /// <summary>
    /// Each month from <paramref name="fromYear"/>'s January to <paramref name="toYear"/>'s December, up
    /// to the month of <paramref name="today"/>: every budget line of its year with a twelfth of the
    /// year's amount and that month's spending.
    /// </summary>
    /// <param name="transactions">The years' transactions, in Canadian dollars.</param>
    public static IReadOnlyList<BudgetMonth> Months(int fromYear, int toYear, DateOnly today, IEnumerable<BudgetTarget> targets, IReadOnlyList<Transaction> transactions)
    {
        var byYear = targets.Where(t => t.Amount > 0).ToLookup(t => t.Year);
        var byMonth = Countable(transactions).ToLookup(t => (t.EffectiveDate.Year, t.EffectiveDate.Month));
        var months = new List<BudgetMonth>();
        for (var m = new DateOnly(fromYear, 1, 1); m.Year <= toYear && m <= today; m = m.AddMonths(1))
        {
            var spending = new SpendingTotals(Spending(byMonth[(m.Year, m.Month)], m, m.AddMonths(1).AddDays(-1)));
            var lines = byYear[m.Year].ToList();
            var own = lines.Where(l => l.SubCategory is null).Select(l => l.Category).ToHashSet();
            var amounts = lines
                .OrderBy(l => l.Category, StringComparer.Ordinal).ThenBy(l => l.SubCategory, StringComparer.Ordinal)
                .Select(l => new BudgetLineAmount(l.Category, l.SubCategory, l.Amount / 12, spending.Of(l.Category, l.SubCategory), l.SubCategory is not null && own.Contains(l.Category), l.Yearly))
                .ToList();

            // Spending no line counts: categories with no budget, and the details without one of a
            // category budgeted only by its details.
            var subs = lines.Where(l => l.SubCategory is not null && !own.Contains(l.Category)).Select(l => (l.Category, l.SubCategory)).ToHashSet();
            var unbudgeted = spending.Lines
                .Where(s => s.Key.Category != Categories.Uncategorized && !own.Contains(s.Key.Category) && !subs.Contains((s.Key.Category, s.Key.SubCategory)))
                .Sum(s => s.Value);
            months.Add(new BudgetMonth(m.Year, m.Month, amounts, unbudgeted, spending.Uncategorized));
        }

        return months;
    }

    /// <summary>Spending categories: everything but income, transfers and uncategorized money.</summary>
    public static bool IsBudgetable(string? category) =>
        category is { Length: > 0 } && category != Categories.Uncategorized && Categories.KindOf(category, DebitCreditMemo.Debit) == CategoryKind.Expense;

    /// <summary>Net spending (debits less refunds) per category and detail (null for none) over [from, to].</summary>
    private static Dictionary<(string Category, string? SubCategory), decimal> Spending(IEnumerable<Transaction> transactions, DateOnly from, DateOnly to) =>
        Countable(transactions)
            .Where(t => t.EffectiveDate >= from && t.EffectiveDate <= to && Categories.KindOf(t.Category, t.DebitCreditMemo) == CategoryKind.Expense)
            .GroupBy(t => (Category: string.IsNullOrEmpty(t.Category) ? Categories.Uncategorized : t.Category, SubCategory: string.IsNullOrEmpty(t.SubCategory) ? null : t.SubCategory))
            .ToDictionary(g => g.Key, g => -g.Sum(t => t.SignedAmount));

    private static IEnumerable<Transaction> Countable(IEnumerable<Transaction> transactions) =>
        transactions.Where(t => t.Status is TransactionStatus.Posted or TransactionStatus.Pending);

    private sealed class SpendingTotals(Dictionary<(string Category, string? SubCategory), decimal> lines)
    {
        public IReadOnlyDictionary<(string Category, string? SubCategory), decimal> Lines => lines;

        /// <summary>A category's spending, all its details included; or one detail's.</summary>
        public decimal Of(string category, string? subCategory) => subCategory is null
            ? lines.Where(l => l.Key.Category == category).Sum(l => l.Value)
            : lines.GetValueOrDefault((category, subCategory));

        public decimal Uncategorized => Of(Categories.Uncategorized, null);
    }
}
