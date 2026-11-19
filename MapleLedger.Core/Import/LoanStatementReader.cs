using System.Globalization;
using System.Text.RegularExpressions;

namespace MapleLedger.Core.Import;

public enum LoanActivityKind
{
    /// <summary>Money paid to the loan: lowers what's owed.</summary>
    Payment,

    /// <summary>Money borrowed (an advance on a line of credit): raises what's owed.</summary>
    Advance,

    /// <summary>Interest or another charge added to what's owed.</summary>
    Charge,
}

/// <summary>A row of a loan statement's activity. <paramref name="Amount"/> is positive; <paramref name="Kind"/> says which way it moves the balance.</summary>
/// <param name="Principal">For a payment split into principal and interest, the principal part.</param>
/// <param name="Interest">For a payment split into principal and interest, the interest part.</param>
public sealed record LoanActivity(DateOnly Date, string Description, decimal Amount, LoanActivityKind Kind, decimal? Principal = null, decimal? Interest = null);

/// <summary>
/// One part of a loan statement: the whole loan, or a portion of a home equity line such as TD's
/// FlexLine, which has a revolving portion and one or more term (mortgage) portions.
/// </summary>
/// <param name="ClosingBalance">What's owed at the statement date as printed; negative is a credit in the borrower's favour.</param>
/// <param name="IsPrincipal">The balance was labelled as principal, which deferred interest is on top of.</param>
/// <param name="DeferredInterest">Interest the payments didn't cover, owed on top of the principal.</param>
/// <param name="InterestCharged">Interest for the statement period ("Total interest").</param>
/// <param name="InterestPaid">Interest paid by this period's payments ("Total interest paid").</param>
public sealed record LoanPortion(
    string Name,
    decimal? OpeningBalance,
    decimal? ClosingBalance,
    bool IsPrincipal,
    decimal? DeferredInterest,
    decimal? InterestCharged,
    decimal? InterestPaid,
    IReadOnlyList<LoanActivity> Activity);

/// <summary>An amount that makes up what's owed on a loan statement: a portion's balance, or its deferred interest.</summary>
public sealed record LoanBalancePart(string Label, decimal Amount);

public sealed record LoanStatement(DateOnly AsOf, IReadOnlyList<LoanPortion> Portions)
{
    public IReadOnlyList<LoanActivity> Activity { get; } = [.. Portions.SelectMany(p => p.Activity).OrderBy(a => a.Date)];

    /// <summary>
    /// What's owed, piece by piece: each portion's closing balance, then its deferred interest. A
    /// statement that has deferred interest says it isn't in the principal balance (TD: "not
    /// currently reflected in your Closing Principal Balance"), so it is listed on its own.
    /// </summary>
    public IReadOnlyList<LoanBalancePart> BalanceParts { get; } = [.. Parts(Portions)];

    public decimal Balance => BalanceParts.Sum(p => p.Amount);

    private static IEnumerable<LoanBalancePart> Parts(IReadOnlyList<LoanPortion> portions)
    {
        var named = portions.Count(p => p.ClosingBalance is not null || p.DeferredInterest is not null) > 1;
        foreach (var p in portions)
        {
            var prefix = named ? p.Name + " " : string.Empty;
            if (p.ClosingBalance is { } balance)
            {
                yield return new LoanBalancePart(Capitalize(prefix + (p.IsPrincipal ? "principal balance" : "balance")), balance);
            }

            if (p.DeferredInterest is { } deferred and not 0)
            {
                yield return new LoanBalancePart(Capitalize(prefix + "deferred interest"), deferred);
            }
        }
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}

/// <summary>
/// Reads mortgage and home equity line statements, which print what's owed as labelled figures
/// ("Closing Principal Balance: $996,951.09", "Deferred Interest amount owing is $9,379.31")
/// rather than as a balance column, and split each payment into principal and interest. The
/// statement's labels are matched with spaces removed, since PDFs often break words apart
/// ("Tot al Int erest") or run them together ("INTERESTFORTHESTATEMENTPERIOD").
/// </summary>
public static partial class LoanStatementReader
{
    /// <summary>The loan statement in a PDF, or null when the PDF isn't one (e.g. a bank account's statement).</summary>
    public static LoanStatement? Read(StatementFile file, DateOnly? today = null)
    {
        var pages = PdfStatementReader.ReadPdfWords(file)
            .Select(words => (IReadOnlyList<string>)PdfStatementReader.GroupLines(words).Select(l => string.Join(' ', l.Select(w => w.Text))).ToList())
            .ToList();
        return Read(pages, today);
    }

    /// <summary>Reads a statement from its lines of text, page by page.</summary>
    public static LoanStatement? Read(IReadOnlyList<IReadOnlyList<string>> pages, DateOnly? today = null)
    {
        var now = today ?? DateOnly.FromDateTime(DateTime.Today);
        var lines = pages.SelectMany(p => p).Select(t => new TextLine(t, Compact(t), Amounts(t))).ToList();
        if (lines.Count == 0) return null;

        var asOf = StatementDate(lines, now);
        if (asOf is null) return null;

        var portions = new List<PortionBuilder>();
        var current = new PortionBuilder("Loan");
        var hasPortions = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var next = i + 1 < lines.Count ? lines[i + 1] : null;
            var c = line.Compact;

            var portion = c.IndexOf("PORTION", StringComparison.Ordinal);
            if (portion > 0 && portion <= 20 && c[..portion].All(char.IsAsciiLetterUpper) && line.Text.Where(char.IsLetter).All(char.IsUpper))
            {
                // A heading in capitals, "REVOLVING PORTION ACCOUNT NUMBER:", "TERM PORTION": a new part of the loan starts.
                portions.Add(current);
                current = new PortionBuilder(c[..portion].ToLowerInvariant() + " portion");
                hasPortions = true;
                continue;
            }

            if (c.Contains("OPENINGPRINCIPALBALANCE", StringComparison.Ordinal) || c.Contains("OPENINGBALANCE", StringComparison.Ordinal))
            {
                current.Opening ??= LabelledAmount(line, next);
            }
            else if (c.Contains("PRINCIPALBALANCE", StringComparison.Ordinal))
            {
                if (LabelledAmount(line, next) is { } principal) (current.Closing, current.IsPrincipal) = (principal, true);
            }
            else if (c.Contains("CLOSINGBALANCE", StringComparison.Ordinal))
            {
                if (LabelledAmount(line, next) is { } closing && !current.IsPrincipal) current.Closing = closing;
            }
            else if (c.Contains("DEFERREDINTEREST", StringComparison.Ordinal))
            {
                if (LabelledAmount(line, next, endsWith: ["IS", "OWING"]) is { } deferred) current.Deferred ??= Math.Abs(deferred);
            }
            else if (c.StartsWith("TOTALINTERESTPAID", StringComparison.Ordinal))
            {
                if (LabelledAmount(line, next) is { } paid) current.InterestPaid ??= Math.Abs(paid);
            }
            else if (c.StartsWith("TOTALINTEREST", StringComparison.Ordinal) && !c.Contains("PAID", StringComparison.Ordinal))
            {
                if (LabelledAmount(line, next) is { } charged) current.InterestCharged ??= Math.Abs(charged);
            }
            else if (ActivityRow(line, asOf.Value) is { } activity)
            {
                current.Activity.Add(activity);
            }
        }

        portions.Add(current);
        var found = portions
            .Where(p => p.Closing is not null || p.Deferred is not null || p.Activity.Count > 0)
            .Select(p => p.Build())
            .ToList();

        // Without a principal balance, deferred interest or portions, this is some other statement
        // (a bank account's "Closing balance"), which the table reader handles.
        var isLoan = found.Any(p => p.IsPrincipal || p.DeferredInterest is not null) || (hasPortions && found.Any(p => p.ClosingBalance is not null));
        return isLoan ? new LoanStatement(asOf.Value, found) : null;
    }

    private sealed record TextLine(string Text, string Compact, List<decimal> Amounts);

    private sealed class PortionBuilder(string name)
    {
        public decimal? Opening, Closing, Deferred, InterestCharged, InterestPaid;
        public bool IsPrincipal;
        public List<LoanActivity> Activity { get; } = [];

        public LoanPortion Build() => new(name, Opening, Closing, IsPrincipal, Deferred, InterestCharged, InterestPaid, Activity);
    }

    private static string Compact(string text) => WhiteSpace().Replace(text, string.Empty).ToUpperInvariant();

    /// <summary>
    /// The amount a label gives: the last one on its line, or, when the line ends with the label
    /// ("Total Interest" with the figure printed below), the first one on the next line.
    /// </summary>
    private static decimal? LabelledAmount(TextLine line, TextLine? next, string[]? endsWith = null)
    {
        if (line.Amounts.Count > 0) return line.Amounts[^1];
        var label = line.Compact.TrimEnd(':');
        var open = label.EndsWith("BALANCE", StringComparison.Ordinal) || label.EndsWith("INTEREST", StringComparison.Ordinal)
            || endsWith?.Any(e => label.EndsWith(e, StringComparison.Ordinal)) == true;
        return open && next is { Amounts.Count: > 0 } && next.Compact.Length <= 16 ? next.Amounts[0] : null;
    }

    /// <summary>
    /// A transaction line: a date, a description, then amounts, either one (the total) or three
    /// (principal, interest and total, as TD prints a mortgage payment). Not a rate period
    /// ("Oct 01 - Oct 28 4.450 +0.200 4.650 0.00"), payment-due line or summary.
    /// </summary>
    private static LoanActivity? ActivityRow(TextLine line, DateOnly asOf)
    {
        if (line.Amounts.Count is 0 or > 3) return null;
        var words = line.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var dateWords = 0;
        var date = default(DateOnly);
        for (var n = Math.Min(3, words.Length - 1); n >= 1; n--)
        {
            if (DateParser.TryParse(string.Join(' ', words.Take(n)), DateOrder.Auto, asOf, out date))
            {
                dateWords = n;
                break;
            }
        }

        if (dateWords == 0) return null;

        var rest = words.Skip(dateWords).ToList();
        var descriptionWords = rest.TakeWhile(w => !MoneyPattern().IsMatch(w)).ToList();
        var description = string.Join(' ', descriptionWords);
        if (description.Length == 0 || !char.IsLetter(description[0]) || descriptionWords.Count + line.Amounts.Count < rest.Count) return null;
        if (StartsWithDate(descriptionWords, asOf)) return null;

        var compact = Compact(description);
        if (compact.Contains("BALANCE", StringComparison.Ordinal) || compact.StartsWith("TOTAL", StringComparison.Ordinal)) return null;

        var total = line.Amounts[^1];
        if (total == 0) return null;

        decimal? principal = null, interest = null;
        if (line.Amounts.Count == 3 && Math.Abs(line.Amounts[0]) + Math.Abs(line.Amounts[1]) == Math.Abs(total))
        {
            (principal, interest) = (Math.Abs(line.Amounts[0]), Math.Abs(line.Amounts[1]));
        }

        // Loan statements print payments as negative, lowering what's owed.
        var kind = total < 0 ? LoanActivityKind.Payment
            : compact.Contains("INTEREST", StringComparison.Ordinal) || compact.Contains("FEE", StringComparison.Ordinal) || compact.Contains("CHARGE", StringComparison.Ordinal) ? LoanActivityKind.Charge
            : LoanActivityKind.Advance;
        return new LoanActivity(date, description, Math.Abs(total), kind, principal, interest);
    }

    private static bool StartsWithDate(List<string> words, DateOnly asOf)
    {
        for (var n = Math.Min(3, words.Count); n >= 1; n--)
        {
            if (DateParser.TryParse(string.Join(' ', words.Take(n)), DateOrder.Auto, asOf, out _)) return true;
        }

        return false;
    }

    /// <summary>
    /// The statement date ("Statement Date: October 31, 2026"), else the latest full date printed
    /// (the end of "Statement Period: … to October 31, 2026"). A date far in the future is a
    /// maturity date, not the statement's.
    /// </summary>
    private static DateOnly? StatementDate(List<TextLine> lines, DateOnly today)
    {
        foreach (var line in lines)
        {
            var at = line.Compact.IndexOf("STATEMENTDATE", StringComparison.Ordinal);
            if (at >= 0 && FullDates(line.Compact[(at + "STATEMENTDATE".Length)..]).FirstOrDefault() is { } date && date != default) return date;
        }

        var limit = today.AddDays(60);
        var dates = lines.SelectMany(l => FullDates(l.Compact)).Where(d => d <= limit).ToList();
        return dates.Count > 0 ? dates.Max() : null;
    }

    /// <summary>Dates with the year written out in a compacted line: "OCTOBER31,2026", "OCT.31,2026", "2026-10-31".</summary>
    private static IEnumerable<DateOnly> FullDates(string compact)
    {
        foreach (Match m in WrittenDate().Matches(compact))
        {
            // The month runs into the word before it ("TOJUNE30,2026"): try the longest ending that's a month.
            var letters = m.Groups[1].Value;
            for (var length = Math.Min(9, letters.Length); length >= 3; length--)
            {
                if (DateParser.TryParse($"{letters[^length..]} {m.Groups[2].Value}, {m.Groups[3].Value}", DateOrder.Auto, new DateOnly(2000, 6, 15), out var date))
                {
                    yield return date;
                    break;
                }
            }
        }

        foreach (Match m in IsoDate().Matches(compact))
        {
            if (DateOnly.TryParseExact(m.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) yield return date;
        }
    }

    /// <summary>Every money amount on a line, signed: "$9,379.31", "-1,700.63", "0.00". Rates ("4.450") have three decimals and aren't.</summary>
    private static List<decimal> Amounts(string text)
    {
        var amounts = new List<decimal>();
        foreach (Match m in MoneyPattern().Matches(text))
        {
            var digits = m.Value.Replace("$", string.Empty).Replace(",", string.Empty).Replace("-", string.Empty);
            if (decimal.TryParse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            {
                amounts.Add(m.Value.Contains('-') ? -value : value);
            }
        }

        return amounts;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    [GeneratedRegex(@"(?<![\d,.])-?\$?-?(?:\d{1,3}(?:,\d{3})+|\d+)\.\d{2}(?![\d%])")]
    private static partial Regex MoneyPattern();

    [GeneratedRegex(@"([A-Z]{3,9})\.?(\d{1,2}),(\d{4})")]
    private static partial Regex WrittenDate();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}")]
    private static partial Regex IsoDate();
}
