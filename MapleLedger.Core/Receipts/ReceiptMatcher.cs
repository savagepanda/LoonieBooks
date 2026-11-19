using MapleLedger.Fdx;

namespace MapleLedger.Core.Receipts;

/// <summary>
/// Ranks spending by how well it fits a receipt. The amount matters most (cards charge exactly
/// the total, plus a tip at restaurants), then the date (card transactions post a day or three
/// later), then the merchant name, which banks abbreviate and OCR mangles.
/// </summary>
public static class ReceiptMatcher
{
    public const int MaxMatches = 5;

    /// <summary>A candidate needs at least this score to be suggested.</summary>
    public const int MinScore = 30;

    /// <summary>How far after the receipt date a charge can post and still be suggested.</summary>
    public const int MaxDaysLater = 14;

    public static IReadOnlyList<ReceiptMatch> Rank(ReceiptFields receipt, IEnumerable<LedgerTransaction> transactions)
    {
        var merchantWords = Words(receipt.Merchant);
        return transactions
            .Where(t => t.Transaction.DebitCreditMemo == DebitCreditMemo.Debit)
            .Select(t => Score(receipt, merchantWords, t))
            .OfType<ReceiptMatch>()
            .Where(m => m.Score >= MinScore)
            .OrderByDescending(m => m.Score)
            .ThenBy(m => Dates(receipt).Select(d => Math.Abs(m.Item.Transaction.EffectiveDate.DayNumber - d.DayNumber)).DefaultIfEmpty(0).Min())
            .Take(MaxMatches)
            .ToList();
    }

    private static ReceiptMatch? Score(ReceiptFields receipt, HashSet<string> merchantWords, LedgerTransaction item)
    {
        var t = item.Transaction;
        var score = 0;
        var reasons = new List<string>();

        if (receipt.Total is { } total and > 0)
        {
            var difference = t.Amount - total;
            if (Math.Abs(difference) < 0.005m)
            {
                score += 50;
                reasons.Add("Same amount");
            }
            else if (difference > 0 && difference <= total * 0.30m && t.Category == Categories.FoodAndDining)
            {
                score += 25;
                reasons.Add(Math.Round(difference / total * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "% more: a tip?");
            }
            else if (Math.Abs(difference) <= total * 0.02m)
            {
                score += 20;
                reasons.Add("Amount within 2%");
            }
            else
            {
                return null; // a known total that doesn't fit rules the transaction out
            }
        }

        var dates = Dates(receipt);
        if (dates.Count > 0)
        {
            // An ambiguous date counts by whichever reading fits this transaction better.
            var best = dates.Select(d => DateScore(t.EffectiveDate.DayNumber - d.DayNumber)).MaxBy(s => s?.Points ?? -1);
            if (best is not { } fit) return null;
            score += fit.Points;
            if (fit.Reason is not null) reasons.Add(fit.Reason);
        }

        var common = merchantWords.Count(w => Words(t.Description).Any(d => d == w || (w.Length >= 4 && d.Length >= 4 && (d.StartsWith(w) || w.StartsWith(d)))));
        if (common > 0)
        {
            score += Math.Min(20, common * 10);
            reasons.Add("Merchant name matches");
        }

        return new ReceiptMatch(item, score, reasons);
    }

    private static List<DateOnly> Dates(ReceiptFields receipt) =>
        new[] { receipt.Date, receipt.OtherDate }.OfType<DateOnly>().Distinct().ToList();

    /// <summary>How well a posting this many days after the receipt fits; null rules it out.</summary>
    private static (int Points, string? Reason)? DateScore(int days) => days switch
    {
        0 => (30, "Same day"),
        -1 => (15, "A day earlier"),
        1 => (25, "Posted a day later"),
        >= 2 and <= 3 => (25, $"Posted {days} days later"),
        >= 4 and <= 7 => (10, $"Posted {days} days later"),
        > 7 and <= MaxDaysLater => (0, null),
        _ => null,
    };

    /// <summary>Distinctive words: three or more letters, and not store boilerplate.</summary>
    private static HashSet<string> Words(string? text) =>
        TextNormalizer.ForMatching(text)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && w.Any(char.IsLetter) && !Boilerplate.Contains(w))
            .ToHashSet();

    private static readonly HashSet<string> Boilerplate =
        ["THE", "AND", "INC", "LTD", "LTEE", "STORE", "STORES", "CANADA", "POS", "PURCHASE", "ACHAT", "PAYMENT", "INTERAC", "VISA", "DEBIT", "WWW", "COM"];
}
