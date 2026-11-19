using System.Globalization;
using System.Text;

namespace MapleLedger.Core.Import;

/// <summary>An explicit credit/debit marker printed next to an amount, e.g. "12.50 CR".</summary>
public enum AmountMarker
{
    None,
    Credit,
    Debit,
}

/// <param name="Value">Signed value: negative when printed with a minus sign or in parentheses.</param>
/// <param name="LooksLikeMoney">True when the text had a currency symbol or exactly two decimals, which distinguishes amounts from reference numbers.</param>
public readonly record struct ParsedAmount(decimal Value, AmountMarker Marker, bool LooksLikeMoney);

/// <summary>
/// Parses amounts as Canadian banks print them: "$1,234.56", "-$12.00", "$-12.00", "(12.00)",
/// "12.00 CR", and French-Canadian "1 234,56 $".
/// </summary>
public static class AmountParser
{
    public static bool TryParse(string? text, out ParsedAmount amount)
    {
        amount = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim();
        var marker = AmountMarker.None;
        var negative = false;
        var hadCurrency = false;

        if (EndsWithWord(ref s, "CR")) marker = AmountMarker.Credit;
        else if (EndsWithWord(ref s, "DR")) marker = AmountMarker.Debit;

        foreach (var code in (string[])["CAD", "USD", "C$", "US$", "CA$"])
        {
            if (s.StartsWith(code, StringComparison.OrdinalIgnoreCase)) { s = s[code.Length..].Trim(); hadCurrency = true; }
            if (s.EndsWith(code, StringComparison.OrdinalIgnoreCase)) { s = s[..^code.Length].Trim(); hadCurrency = true; }
        }

        if (s.StartsWith('(') && s.EndsWith(')'))
        {
            negative = true;
            s = s[1..^1];
        }

        var digits = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case >= '0' and <= '9' or '.' or ',':
                    digits.Append(c);
                    break;
                case '$':
                    hadCurrency = true;
                    break;
                case '-' or '−' or '–': // hyphen, minus sign, en dash
                    if (digits.Length > 0 && !IsTrailing(s, c))
                    {
                        return false; // a dash between digits: a date or an account number, not an amount
                    }
                    negative = true;
                    break;
                case '+':
                    break;
                case ' ' or ' ' or ' ' or ' ': // spaces used as thousands separators
                    break;
                default:
                    return false;
            }
        }

        var number = NormalizeSeparators(digits.ToString());
        if (number is null || !decimal.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        var decimals = number.Contains('.') ? number.Length - number.IndexOf('.') - 1 : 0;
        amount = new ParsedAmount(negative ? -value : value, marker, hadCurrency || decimals == 2);
        return true;
    }

    private static bool EndsWithWord(ref string s, string word)
    {
        if (s.Length > word.Length && s.EndsWith(word, StringComparison.OrdinalIgnoreCase)
            && !char.IsLetter(s[^(word.Length + 1)]))
        {
            s = s[..^word.Length].Trim();
            return true;
        }

        return false;
    }

    private static bool IsTrailing(string s, char c) => s.TrimEnd().EndsWith(c);

    /// <summary>Returns the number with '.' as the only decimal separator and no grouping, or null if malformed.</summary>
    private static string? NormalizeSeparators(string n)
    {
        if (n.Length == 0 || !n.Any(char.IsDigit))
        {
            return null;
        }

        var lastDot = n.LastIndexOf('.');
        var lastComma = n.LastIndexOf(',');
        char? decimalSeparator = null;

        if (lastDot >= 0 && lastComma >= 0)
        {
            decimalSeparator = lastDot > lastComma ? '.' : ',';
        }
        else if (lastComma >= 0)
        {
            // "12,50" is a French decimal; "1,234" and "1,234,567" are English grouping.
            var fraction = n.Length - lastComma - 1;
            decimalSeparator = n.Count(c => c == ',') == 1 && fraction is 1 or 2 ? ',' : null;
        }
        else if (lastDot >= 0)
        {
            decimalSeparator = n.Count(c => c == '.') == 1 ? '.' : null;
        }

        var sb = new StringBuilder(n.Length);
        for (var i = 0; i < n.Length; i++)
        {
            var c = n[i];
            if (char.IsDigit(c)) sb.Append(c);
            else if (c == decimalSeparator && i == (c == '.' ? lastDot : lastComma)) sb.Append('.');
            else if (c == decimalSeparator) return null;
        }

        return sb.ToString();
    }
}
