using System.Text.RegularExpressions;
using MapleLedger.Core.Import;

namespace MapleLedger.Core.Receipts;

/// <summary>
/// Pulls the total, date and merchant out of a receipt's text (from OCR, so expect noise). Every
/// field is a best guess for the user to check, never something to rely on blindly.
/// </summary>
public static partial class ReceiptText
{
    public static ReceiptFields Parse(string? text, DateOnly today)
    {
        var lines = (text ?? "")
            .Split('\n')
            .Select(l => TextNormalizer.CollapseWhitespace(l))
            .Where(l => l.Length > 0)
            .ToList();

        var (date, otherDate) = FindDate(lines, today);
        return new ReceiptFields(FindTotal(lines), date, FindMerchant(lines), otherDate);
    }

    // ---------------------------------------------------------------- total

    private static decimal? FindTotal(List<string> lines)
    {
        // 1. A "total" line (but not a subtotal, tax or savings line). Grand totals are the largest.
        var totals = lines.Where(l => TotalLine().IsMatch(l) && !NotTheTotal().IsMatch(l)).SelectMany(Amounts).Where(a => a > 0).ToList();
        if (totals.Count > 0) return totals.Max();

        // 2. What was charged to a card.
        var paid = lines.Where(l => PaymentLine().IsMatch(l) && !NotTheTotal().IsMatch(l)).SelectMany(Amounts).Where(a => a > 0).ToList();
        if (paid.Count > 0) return paid.Max();

        // 3. The largest amount anywhere, ignoring cash handed over and change given back.
        var any = lines.Where(l => !CashLine().IsMatch(l)).SelectMany(Amounts).Where(a => a > 0).ToList();
        return any.Count > 0 ? any.Max() : null;
    }

    /// <summary>Money-looking numbers on a line ("12.34", "$1,234.56", "12,34 $"), left to right.</summary>
    public static IEnumerable<decimal> Amounts(string line)
    {
        foreach (Match m in MoneyToken().Matches(line))
        {
            if (AmountParser.TryParse(m.Value, out var amount) && amount.LooksLikeMoney) yield return Math.Abs(amount.Value);
        }
    }

    // ---------------------------------------------------------------- date

    private static (DateOnly? Date, DateOnly? OtherDate) FindDate(List<string> lines, DateOnly today)
    {
        // Every way each date on the receipt can be read, keeping only past (and fairly recent) dates.
        var readingsPerToken = new List<List<DateOnly>>();
        foreach (var line in lines)
        {
            if (ExpiryLine().IsMatch(line)) continue; // card expiry dates

            foreach (Match m in DateToken().Matches(line))
            {
                var token = m.Value.Trim().TrimEnd(',', '.');
                var readings = new List<DateOnly>();
                foreach (var order in new[] { DateOrder.MonthDayYear, DateOrder.DayMonthYear })
                {
                    if (DateParser.TryParse(token, order, today, out var date)) readings.Add(date);
                }

                // Some tills print year-first with a two-digit year: 26/02/10.
                var ymd = ShortYearFirst().Match(token);
                if (ymd.Success && DateParser.TryParse($"20{ymd.Groups[1].Value}-{ymd.Groups[2].Value}-{ymd.Groups[3].Value}", DateOrder.Auto, today, out var yearFirst))
                {
                    readings.Add(yearFirst);
                }

                readings = readings.Where(d => d <= today.AddDays(1) && d >= today.AddYears(-3)).Distinct().OrderDescending().ToList();
                if (readings.Count > 0) readingsPerToken.Add(readings);
            }
        }

        // A receipt is usually recent, so prefer the latest date; keep its other reading, if any,
        // for matching and for the user to switch to.
        var best = readingsPerToken.OrderByDescending(r => r[0]).FirstOrDefault();
        return best is null ? (null, null) : (best[0], best.Count > 1 ? best[^1] : null);
    }

    // ---------------------------------------------------------------- merchant

    private static string? FindMerchant(List<string> lines)
    {
        foreach (var line in lines.Take(6))
        {
            var letters = line.Count(char.IsLetter);
            if (letters < 3 || letters < line.Length / 2) continue;      // mostly symbols or numbers: noise, a phone number
            if (char.IsDigit(line[0])) continue;                          // a street address
            if (NotAMerchant().IsMatch(line)) continue;                   // "WELCOME", "RECEIPT", ...
            return line.Length > 60 ? line[..60].Trim() : line;
        }

        return null;
    }

    // ---------------------------------------------------------------- patterns

    // OCR often reads O as 0 in "TOTAL".
    [GeneratedRegex(@"\bT[O0]TAL\b|\bMONTANT\b|\bAMOUNT DUE\b|\bBALANCE DUE\b|\bA PAYER\b|\bÀ PAYER\b", RegexOptions.IgnoreCase)]
    private static partial Regex TotalLine();

    [GeneratedRegex(@"SUB\s*-?\s*T[O0]TAL|SOUS\s*-?\s*T[O0]TAL|\bTAX|\bTPS\b|\bTVQ\b|\bTVH\b|\bHST\b|\bGST\b|\bPST\b|\bQST\b|SAVING|ECONOMI|DISCOUNT|RABAIS|\bITEMS?\b|\bARTICLES?\b|POINTS", RegexOptions.IgnoreCase)]
    private static partial Regex NotTheTotal();

    [GeneratedRegex(@"\bVISA\b|MASTERCARD|\bMC\b|\bAMEX\b|AMERICAN EXPRESS|\bDEBIT\b|\bDÉBIT\b|INTERAC|\bCREDIT\b|\bCRÉDIT\b|APPROVED|APPROUV", RegexOptions.IgnoreCase)]
    private static partial Regex PaymentLine();

    [GeneratedRegex(@"\bCASH\b|COMPTANT|ESPÈCES|ESPECES|\bCHANGE\b|MONNAIE|TENDER", RegexOptions.IgnoreCase)]
    private static partial Regex CashLine();

    [GeneratedRegex(@"\bEXP\b|EXPIR", RegexOptions.IgnoreCase)]
    private static partial Regex ExpiryLine();

    [GeneratedRegex(@"WELCOME|BIENVENUE|RECEIPT|REÇU|RECU|INVOICE|FACTURE|CUSTOMER COPY|COPIE|^THANK|^MERCI", RegexOptions.IgnoreCase)]
    private static partial Regex NotAMerchant();

    // "1,234.56", "1 234,56 $" and plain "1500.00"; never part of a longer number such as a
    // dotted date (2026.09.10) or a percentage.
    [GeneratedRegex(@"(?<![\d.,])\$?\s?(?:\d{1,3}(?:[ ,]\d{3})+|\d+)[.,]\d{2}(?:\s?\$)?(?![\d%]|[.,]\d)")]
    private static partial Regex MoneyToken();

    [GeneratedRegex(@"\b\d{4}[-/.]\d{1,2}[-/.]\d{1,2}\b|\b\d{1,2}[-/.]\d{1,2}[-/.]\d{2,4}\b|\b\d{1,2}(?:er)?[\s-]?\p{L}{3,9}\.?[\s,-]+\d{2,4}\b|\b\p{L}{3,9}\.?\s+\d{1,2}(?:st|nd|rd|th)?,?\s+\d{4}\b")]
    private static partial Regex DateToken();

    [GeneratedRegex(@"^(\d{2})[-/.](\d{1,2})[-/.](\d{1,2})$")]
    private static partial Regex ShortYearFirst();
}
