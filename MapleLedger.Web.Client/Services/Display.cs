using System.Globalization;

namespace MapleLedger.Web.Client.Services;

/// <summary>
/// Display helpers with fixed, culture-independent formats: the WebAssembly runtime only loads
/// culture data for the browser's own locale, and output should not change with it.
/// </summary>
public static class Display
{
    /// <summary>yyyy-MM-dd, for query strings and &lt;input type="date"&gt;.</summary>
    public static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static bool TryParseIso(string? text, out DateOnly date) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    public static string Date(DateOnly date) => date.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);

    public static string ShortDate(DateOnly date) => date.ToString("MMM d", CultureInfo.InvariantCulture);

    /// <summary>
    /// A moment in this device's time zone: "2:05 PM" today, "Nov 17, 2:05 PM" another day this
    /// year, with the year further back.
    /// </summary>
    public static string Moment(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        var now = DateTimeOffset.Now;
        var format = local.Date == now.Date ? "h:mm tt" : local.Year == now.Year ? "MMM d, h:mm tt" : "MMM d, yyyy, h:mm tt";
        return local.ToString(format, CultureInfo.InvariantCulture);
    }

    public static string Month(int year, int month) => new DateOnly(year, month, 1).ToString("MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>SVG coordinates must use '.' as the decimal separator whatever the culture.</summary>
    public static string Svg(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
