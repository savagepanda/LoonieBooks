using System.Globalization;

namespace MapleLedger.Core;

/// <summary>
/// Formats money the same way everywhere, without depending on culture data that the WebAssembly
/// runtime may not have loaded. Every amount names its currency so a Canadian dollar is never
/// mistaken for a US one: CA$1,234.56, US$1,234.56, EUR 1,234.56. From a million up, amounts are
/// shortened to two decimals of millions or billions (CA$3.57M, US$1.20B) so they stay readable.
/// </summary>
public static class Money
{
    public const string Cad = "CAD";

    public static string Format(decimal amount) => Format(amount, Cad);

    /// <summary>Whole dollars, for chart labels and summary tiles.</summary>
    public static string FormatWhole(decimal amount) => FormatWhole(amount, Cad);

    /// <summary>Whole units of a currency given by its ISO code.</summary>
    public static string FormatWhole(decimal amount, string currency) => Write(Math.Round(amount, 0), currency, "#,0");

    public static string FormatSigned(decimal amount) => amount > 0 ? "+" + Format(amount) : Format(amount);

    /// <summary>An amount in a currency given by its ISO code.</summary>
    public static string Format(decimal amount, string currency) => Write(amount, currency, "#,0.00");

    /// <summary>A unit price: two to four decimals, so a CA$0.0525 quote isn't rounded away. Never shortened.</summary>
    public static string FormatPrice(decimal price, string currency) => Write(price, currency, "#,0.00##", shorten: false);

    /// <summary>What goes before an amount: "CA$", "US$", or the code and a space ("EUR ").</summary>
    public static string Prefix(string currency) => currency switch
    {
        Cad => "CA$",
        "USD" => "US$",
        _ => currency + " ",
    };

    private static string Write(decimal amount, string currency, string format, bool shorten = true)
    {
        var digits = shorten && Shortened(Math.Abs(amount)) is { } s ? s : Math.Abs(amount).ToString(format, CultureInfo.InvariantCulture);
        var negative = amount < 0 && digits.Any(c => c is >= '1' and <= '9'); // no "-CA$0.00"
        return (negative ? "-" : "") + Prefix(currency) + digits;
    }

    /// <summary>"3.57M" or "1.20B" from a million up (as the amount rounds to cents); null below that.</summary>
    private static string? Shortened(decimal size)
    {
        if (Math.Round(size, 2) < 1_000_000) return null;
        var millions = Math.Round(size / 1_000_000, 2);
        return millions < 1000
            ? millions.ToString("0.00", CultureInfo.InvariantCulture) + "M"
            : Math.Round(size / 1_000_000_000, 2).ToString("#,0.00", CultureInfo.InvariantCulture) + "B";
    }
}
