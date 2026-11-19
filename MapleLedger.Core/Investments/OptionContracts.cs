using System.Globalization;
using System.Text.RegularExpressions;

namespace MapleLedger.Core.Investments;

public enum OptionRight { Call, Put }

/// <summary>
/// A listed equity option, read from the symbol a broker's report gives it. One contract covers
/// <see cref="SharesPerContract"/> shares, and reports quote its price per share, so a position is
/// worth quantity × price × 100.
/// </summary>
public sealed partial record OptionContract(string Underlying, DateOnly Expiry, decimal Strike, OptionRight Right)
{
    /// <summary>Shares one standard contract covers.</summary>
    public const int SharesPerContract = 100;

    /// <summary>
    /// The contract's OCC symbol, which Yahoo Finance uses: the underlying, the expiry as yyMMdd,
    /// C or P, and the strike in thousandths in eight digits (INTC271217C00035000).
    /// </summary>
    public string OccSymbol =>
        Underlying.Replace("-", "", StringComparison.Ordinal)
        + Expiry.ToString("yyMMdd", CultureInfo.InvariantCulture)
        + (Right == OptionRight.Call ? 'C' : 'P')
        + ((long)Math.Round(Strike * 1000)).ToString("D8", CultureInfo.InvariantCulture);

    /// <summary>How the page names it: INTC Dec 17, 2027 35 call.</summary>
    public string Describe() =>
        $"{Underlying} {Expiry.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)} {Strike.ToString("0.##", CultureInfo.InvariantCulture)} {(Right == OptionRight.Call ? "call" : "put")}";

    // Interactive Brokers: "INTC 17DEC27 35 C" (also "INTC DEC17'27 35 CALL" from its website).
    [GeneratedRegex(@"^(?<u>[A-Z][A-Z0-9.\-]{0,5})\s+(?:(?<d>\d{1,2})(?<m>[A-Z]{3})'?(?<y>\d{2}(?:\d{2})?)|(?<m>[A-Z]{3})(?<d>\d{1,2})'(?<y>\d{2}))\s+(?<k>\d{1,6}(?:\.\d{1,4})?)\s+(?<r>C|P|CALL|PUT)$")]
    private static partial Regex Ibkr();

    // OCC, as Yahoo and many US brokers write it: "INTC271217C00035000", or with the root padded to six.
    [GeneratedRegex(@"^(?<u>[A-Z][A-Z0-9]{0,5})\s*(?<y>\d{2})(?<mm>\d{2})(?<d>\d{2})(?<r>[CP])(?<k>\d{8})$")]
    private static partial Regex Occ();

    // Questrade: "INTC17Dec27C35.00".
    [GeneratedRegex(@"^(?<u>[A-Z][A-Z0-9.]{0,5}?)(?<d>\d{1,2})(?<m>[A-Z]{3})(?<y>\d{2})(?<r>[CP])(?<k>\d{1,6}(?:\.\d{1,4})?)$")]
    private static partial Regex Questrade();

    private static readonly string[] Months = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    /// <summary>The option a symbol names, or null for anything else (a stock, a fund code).</summary>
    public static OptionContract? Parse(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol) || symbol.Length > Holding.MaxSymbolLength) return null;
        var s = string.Join(' ', symbol.Trim().ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

        if (Occ().Match(s) is { Success: true } occ)
        {
            var strike = long.Parse(occ.Groups["k"].Value, CultureInfo.InvariantCulture) / 1000m;
            return Build(occ.Groups["u"].Value, occ.Groups["y"].Value, int.Parse(occ.Groups["mm"].Value, CultureInfo.InvariantCulture), occ.Groups["d"].Value, strike, occ.Groups["r"].Value);
        }

        var named = Ibkr().Match(s) is { Success: true } ibkr ? ibkr : Questrade().Match(s) is { Success: true } qt ? qt : null;
        if (named is null) return null;

        var month = Array.IndexOf(Months, named.Groups["m"].Value) + 1;
        return month > 0 && decimal.TryParse(named.Groups["k"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var k)
            ? Build(named.Groups["u"].Value, named.Groups["y"].Value, month, named.Groups["d"].Value, k, named.Groups["r"].Value)
            : null;
    }

    private static OptionContract? Build(string underlying, string year, int month, string day, decimal strike, string right)
    {
        var y = int.Parse(year, CultureInfo.InvariantCulture);
        if (y < 100) y += 2000;
        var d = int.Parse(day, CultureInfo.InvariantCulture);
        if (month is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, month) || strike <= 0 || strike >= 100_000m) return null;

        var root = underlying.Replace('.', '-').Trim('-');
        return root.Length == 0 ? null : new OptionContract(root, new DateOnly(y, month, d), strike, right.StartsWith('C') ? OptionRight.Call : OptionRight.Put);
    }
}
