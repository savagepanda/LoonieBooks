using System.Text.RegularExpressions;

namespace MapleLedger.Core.Import;

public enum DateOrder
{
    /// <summary>Decide from the data; falls back to month-first when every value is ambiguous.</summary>
    Auto,
    MonthDayYear,
    DayMonthYear,
}

/// <summary>
/// Parses the date formats Canadian bank sites display, in English and French, without relying
/// on culture data (which may not be loaded in the WebAssembly runtime).
/// </summary>
public static partial class DateParser
{
    private static readonly Dictionary<string, int> Months = BuildMonthTable();

    public static bool TryParse(string? text, DateOrder order, DateOnly referenceDate, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = Weekday().Replace(text.Trim(), string.Empty);

        var m = IsoDate().Match(s);
        if (m.Success)
        {
            return TryCreate(Int(m, 1), Int(m, 2), Int(m, 3), out date);
        }

        m = NumericDate().Match(s);
        if (m.Success)
        {
            int a = Int(m, 1), b = Int(m, 2), year = ExpandYear(Int(m, 3));
            var dayFirst = order == DateOrder.DayMonthYear || (order == DateOrder.Auto && a > 12);
            return dayFirst ? TryCreate(year, b, a, out date) : TryCreate(year, a, b, out date);
        }

        m = MonthFirst().Match(s);
        if (m.Success && TryMonth(m.Groups[1].Value, out var month))
        {
            return TryCreateWithOptionalYear(m.Groups[3], month, Int(m, 2), referenceDate, out date);
        }

        m = DayFirst().Match(s);
        if (m.Success && TryMonth(m.Groups[2].Value, out month))
        {
            return TryCreateWithOptionalYear(m.Groups[3], month, Int(m, 1), referenceDate, out date);
        }

        return false;
    }

    /// <summary>
    /// Looks at every numeric date in a column: a first component over 12 means day-first, a
    /// second component over 12 means month-first. Returns <see cref="DateOrder.Auto"/> when undecidable.
    /// </summary>
    public static DateOrder DetectOrder(IEnumerable<string> values)
    {
        foreach (var v in values)
        {
            var m = NumericDate().Match(v.Trim());
            if (!m.Success) continue;
            if (Int(m, 1) > 12) return DateOrder.DayMonthYear;
            if (Int(m, 2) > 12) return DateOrder.MonthDayYear;
        }

        return DateOrder.Auto;
    }

    private static bool TryCreateWithOptionalYear(Group yearGroup, int month, int day, DateOnly reference, out DateOnly date)
    {
        if (yearGroup.Success)
        {
            return TryCreate(ExpandYear(int.Parse(yearGroup.Value)), month, day, out date);
        }

        // Statements often omit the year ("Sep 28"). Assume the most recent such date that is not
        // meaningfully in the future, so a December charge viewed in January lands in last year.
        if (!TryCreate(reference.Year, month, day, out date)) return false;
        if (date > reference.AddDays(7)) return TryCreate(reference.Year - 1, month, day, out date);
        return true;
    }

    private static bool TryCreate(int year, int month, int day, out DateOnly date)
    {
        date = default;
        if (year is < 1900 or > 2200 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        date = new DateOnly(year, month, day);
        return true;
    }

    private static int ExpandYear(int year) => year < 100 ? 2000 + year : year;

    private static int Int(Match m, int group) => int.Parse(m.Groups[group].Value);

    private static bool TryMonth(string name, out int month) =>
        Months.TryGetValue(name.Trim().TrimEnd('.').ToLowerInvariant(), out month);

    private static Dictionary<string, int> BuildMonthTable()
    {
        string[][] names =
        [
            ["jan", "january", "janv", "janvier"],
            ["feb", "february", "fev", "févr", "fevr", "février", "fevrier"],
            ["mar", "march", "mars"],
            ["apr", "april", "avr", "avril"],
            ["may", "mai"],
            ["jun", "june", "juin"],
            ["jul", "july", "juil", "juillet"],
            ["aug", "august", "aou", "aoû", "août", "aout"],
            ["sep", "sept", "september", "septembre"],
            ["oct", "october", "octobre"],
            ["nov", "november", "novembre"],
            ["dec", "december", "déc", "décembre", "decembre"],
        ];

        var table = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < names.Length; i++)
        {
            foreach (var n in names[i]) table[n] = i + 1;
        }

        return table;
    }

    // The comma is required: without it "Mar 28" (March) would be read as the French "mar." (Tuesday).
    [GeneratedRegex(@"^(?:mon|tue|wed|thu|fri|sat|sun|lun|mar|mer|jeu|ven|sam|dim)[a-z]*\.?,\s+", RegexOptions.IgnoreCase)]
    private static partial Regex Weekday();

    [GeneratedRegex(@"^(\d{4})[-/.](\d{1,2})[-/.](\d{1,2})(?:[T\s].*)?$")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"^(\d{1,2})[-/.](\d{1,2})[-/.](\d{4}|\d{2})$")]
    private static partial Regex NumericDate();

    [GeneratedRegex(@"^(\p{L}{3,10}\.?)\s+(\d{1,2})(?:st|nd|rd|th)?(?:,?\s+(\d{4}))?$", RegexOptions.IgnoreCase)]
    private static partial Regex MonthFirst();

    [GeneratedRegex(@"^(\d{1,2})(?:er)?[\s\-]+(\p{L}{3,10}\.?)(?:[\s\-,]+(\d{4}|\d{2}))?$", RegexOptions.IgnoreCase)]
    private static partial Regex DayFirst();
}
