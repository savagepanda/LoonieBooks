namespace MapleLedger.Web.Client.Services;

public sealed record Period(string Key, string Label, DateOnly From, DateOnly To);

public static class Periods
{
    public static IReadOnlyList<Period> All(DateOnly today)
    {
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        return
        [
            new("this-month", "This month", monthStart, today),
            new("last-month", "Last month", monthStart.AddMonths(-1), monthStart.AddDays(-1)),
            new("3m", "Last 3 months", monthStart.AddMonths(-2), today),
            new("12m", "Last 12 months", monthStart.AddMonths(-11), today),
            new("ytd", "Year to date", new DateOnly(today.Year, 1, 1), today),
            new("last-year", $"{today.Year - 1}", new DateOnly(today.Year - 1, 1, 1), new DateOnly(today.Year - 1, 12, 31)),
        ];
    }

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>Until the April filing deadline, the tax year you're working on is last year.</summary>
    public static int DefaultTaxYear => Today.Month <= 4 ? Today.Year - 1 : Today.Year;

    /// <summary>The tax years offered in pickers, newest first.</summary>
    public static IEnumerable<int> TaxYears => Enumerable.Range(Today.Year - 5, 6).Reverse();
}
