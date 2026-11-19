using System.Text.RegularExpressions;

namespace MapleLedger.Core.Categorization;

/// <summary>
/// Decides when two transactions are from the same merchant, so a category chosen for one can be
/// offered for the others. Banks print the same merchant with different store numbers, towns and
/// reference codes ("PIZZAVILLE #60 BARRIE", "PIZZAVILLE #69 KINGSTON", "E-TRANSFER ***XvQ"), so
/// the name stops at a "#" store number or a "**" masked code, and the key ignores case, accents,
/// punctuation and every word that contains a digit.
/// </summary>
public static partial class MerchantName
{
    /// <summary>The comparable part of a description; empty when nothing but numbers is left.</summary>
    public static string Key(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return string.Empty;

        // What follows a store number or masked code is a town or a reference, not the merchant.
        // When nothing comes before it ("#1 SUSHI"), fall back to the whole description.
        var name = Words(NameEnd().Split(description, 2)[0]);
        return name.Length > 0 ? name : Words(description);
    }

    public static bool Same(string? a, string? b)
    {
        var key = Key(a);
        return key.Length > 0 && key == Key(b);
    }

    private static string Words(string text) =>
        string.Join(' ', TextNormalizer.ForMatching(text)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !w.Any(char.IsAsciiDigit)));

    [GeneratedRegex(@"#|\*{2,}")]
    private static partial Regex NameEnd();
}
