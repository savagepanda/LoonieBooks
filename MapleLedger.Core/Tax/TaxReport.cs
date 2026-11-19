using System.Globalization;
using System.Text;
using MapleLedger.Fdx;

namespace MapleLedger.Core.Tax;

public sealed record TaxCategorySummary(
    TaxCategory Category,
    decimal Confirmed,
    decimal Suggested,
    IReadOnlyList<LedgerTransaction> Transactions);

/// <param name="Categories">Everything claimed for <see cref="Year"/>, whenever it was paid.</param>
/// <param name="RrspFromNextYear">
/// RRSP contributions made in the first 60 days of the next year that are still claimed in that
/// year; each could be deducted for <see cref="Year"/> instead.
/// </param>
/// <param name="ClaimedInOtherYears">Tagged transactions paid in <see cref="Year"/> but claimed for another year.</param>
/// <param name="Receipts">Receipts attached to the transactions in the report.</param>
public sealed record TaxReport(
    int Year,
    IReadOnlyList<TaxCategorySummary> Categories,
    IReadOnlyList<LedgerTransaction> RrspFromNextYear,
    IReadOnlyList<LedgerTransaction> ClaimedInOtherYears,
    IReadOnlyList<ReceiptInfo>? Receipts = null)
{
    public int PendingReview => Categories.Sum(c => c.Transactions.Count(t => t.Tax?.Status == TaxTagStatus.Suggested));

    /// <summary>Confirmed claims with no receipt attached.</summary>
    public int MissingReceipts => Categories.Sum(c => c.Transactions.Count(t => t.Tax?.Status == TaxTagStatus.Confirmed && ReceiptsFor(t.Transaction).Count == 0));

    public IReadOnlyList<ReceiptInfo> ReceiptsFor(Transaction transaction) =>
        Receipts is null ? [] : Receipts.Where(r => r.AccountId == transaction.AccountId && r.TransactionId == transaction.TransactionId).ToList();
}

public static class TaxReportBuilder
{
    /// <summary>
    /// The last day an RRSP contribution can be deducted for <paramref name="year"/>: the 60th day
    /// of the following year (March 1, or February 29 in a leap year).
    /// </summary>
    public static DateOnly RrspDeadline(int year) => new DateOnly(year + 1, 1, 1).AddDays(59);

    /// <summary>The payment dates that can hold claims for <paramref name="year"/>.</summary>
    public static (DateOnly From, DateOnly To) ReportingRange(int year) => (new DateOnly(year - 1, 1, 1), RrspDeadline(year));

    /// <summary>The tax year a tagged transaction is claimed in.</summary>
    public static int ClaimYear(LedgerTransaction item) => item.Tax?.ClaimYear ?? item.Transaction.EffectiveDate.Year;

    /// <summary>
    /// The part of a payment that counts toward its claim: all of it, or the business-use share.
    /// Refunds come out negative.
    /// </summary>
    public static decimal ClaimedAmount(LedgerTransaction item) =>
        item.Tax?.BusinessUsePercent is { } percent && TaxCategories.AllowsBusinessUse(item.Tax.Code)
            ? Math.Round(-item.Transaction.SignedAmount * percent / 100m, 2, MidpointRounding.AwayFromZero)
            : -item.Transaction.SignedAmount;

    /// <summary>The person filter value for claims not assigned to anyone.</summary>
    public const string Unassigned = "unassigned";

    /// <summary>
    /// One person's claims: null keeps everyone's, <see cref="Unassigned"/> keeps claims with no
    /// person, and a member id keeps that member's.
    /// </summary>
    public static IEnumerable<LedgerTransaction> ForPerson(IEnumerable<LedgerTransaction> transactions, string? person) => person switch
    {
        null or "" => transactions,
        Unassigned => transactions.Where(t => t.Tax?.ClaimedBy is null),
        _ => transactions.Where(t => t.Tax?.ClaimedBy == person),
    };

    /// <summary>
    /// Groups the transactions claimed for <paramref name="year"/> by tax category. Rejected tags
    /// are left out; refunds (credits) reduce the category total.
    /// </summary>
    public static TaxReport Build(int year, IEnumerable<LedgerTransaction> transactions, string? person = null)
    {
        var tagged = ForPerson(transactions.Where(t => t.Tax is { Status: not TaxTagStatus.Rejected }), person).ToList();
        var claimed = tagged.Where(t => ClaimYear(t) == year).ToLookup(t => t.Tax!.Code, StringComparer.OrdinalIgnoreCase);

        var summaries = TaxCategories.All
            .Where(c => claimed.Contains(c.Code))
            .Select(c =>
            {
                var items = claimed[c.Code].OrderBy(t => t.Transaction.EffectiveDate).ToList();
                return new TaxCategorySummary(
                    c,
                    Confirmed: items.Where(t => t.Tax!.Status == TaxTagStatus.Confirmed).Sum(ClaimedAmount),
                    Suggested: items.Where(t => t.Tax!.Status == TaxTagStatus.Suggested).Sum(ClaimedAmount),
                    items);
            })
            .ToList();

        var nextYearStart = new DateOnly(year + 1, 1, 1);
        var deadline = RrspDeadline(year);
        var rrspFromNextYear = tagged
            .Where(t => string.Equals(t.Tax!.Code, TaxCategories.Rrsp, StringComparison.OrdinalIgnoreCase)
                && t.Transaction.EffectiveDate >= nextYearStart
                && t.Transaction.EffectiveDate <= deadline
                && ClaimYear(t) != year)
            .OrderBy(t => t.Transaction.EffectiveDate)
            .ToList();

        var claimedElsewhere = tagged
            .Where(t => t.Transaction.EffectiveDate.Year == year && ClaimYear(t) != year)
            .OrderBy(t => t.Transaction.EffectiveDate)
            .ToList();

        return new TaxReport(year, summaries, rrspFromNextYear, claimedElsewhere);
    }

    /// <summary>One row per transaction, for an accountant or for typing into tax software.</summary>
    public static string ToCsv(TaxReport report, IReadOnlyDictionary<string, string> accountNames, IReadOnlyDictionary<string, string>? memberNames = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Tax Year,Tax Category,CRA Reference,Status,Date,Account,Description,Amount,Note,Receipts,Claimed By,Business Use %,Claimed Amount");
        foreach (var summary in report.Categories)
        {
            foreach (var item in summary.Transactions)
            {
                var t = item.Transaction;
                sb.AppendJoin(',',
                    report.Year.ToString(CultureInfo.InvariantCulture),
                    Csv(summary.Category.Name),
                    Csv(summary.Category.CraReference),
                    item.Tax!.Status,
                    t.EffectiveDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Csv(accountNames.GetValueOrDefault(t.AccountId, t.AccountId)),
                    Csv(t.Description),
                    (-t.SignedAmount).ToString("0.00", CultureInfo.InvariantCulture),
                    Csv(item.Tax.Note),
                    Csv(string.Join("; ", report.ReceiptsFor(t).Select(r => r.FileName))),
                    Csv(item.Tax.ClaimedBy is { } member ? memberNames?.GetValueOrDefault(member, member) : null),
                    TaxCategories.AllowsBusinessUse(item.Tax.Code) ? item.Tax.BusinessUsePercent?.ToString("0.##", CultureInfo.InvariantCulture) ?? "" : "",
                    ClaimedAmount(item).ToString("0.00", CultureInfo.InvariantCulture));
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static string Csv(string? value)
    {
        value ??= string.Empty;
        // Leading =, +, - or @ would be run as a formula by spreadsheet apps.
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@') value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}
