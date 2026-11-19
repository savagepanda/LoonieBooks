using System.Globalization;
using System.Text;

namespace MapleLedger.Core;

internal static class TextNormalizer
{
    /// <summary>
    /// Upper-cases, strips accents, maps punctuation to single spaces and pads both ends with a
    /// space, so callers can match whole words with <c>Contains(" WORD ")</c>.
    /// </summary>
    public static string ForMatching(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length + 2).Append(' ');
        var lastWasSpace = true;
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue; // "DÉPÔT" -> "DEPOT"
            }

            if (char.IsLetterOrDigit(c) || c == '&')
            {
                sb.Append(char.ToUpperInvariant(c));
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }

        if (!lastWasSpace)
        {
            sb.Append(' ');
        }

        return sb.ToString();
    }

    /// <summary>Trims and collapses runs of whitespace to a single space.</summary>
    public static string CollapseWhitespace(string? text) =>
        string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
