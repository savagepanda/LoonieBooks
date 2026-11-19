using System.Globalization;
using System.Text;
using MapleLedger.Core.Investments;

namespace MapleLedger.Core.Import;

/// <summary>The columns of a holdings report, as found or as the user picked them. Null means not in the report.</summary>
/// <param name="Account">A column naming which account each row is in, for reports that cover several.</param>
/// <param name="AccountName">A friendlier name for that account (the account type), shown beside it.</param>
public sealed record HoldingColumns(
    int? Symbol = null,
    int? Description = null,
    int? Quantity = null,
    int? Price = null,
    int? MarketValue = null,
    int? BookValue = null,
    int? Currency = null,
    int? Account = null,
    int? AccountName = null);

/// <summary>
/// A holdings report split into cells. <see cref="Headers"/> is the header row, or "Column 1"… when
/// none was found (the columns are then picked by hand).
/// </summary>
/// <param name="AsOf">A date the report states ("As of November 14, 2026"), when it states one.</param>
public sealed record HoldingsTable(IReadOnlyList<string> Headers, IReadOnlyList<string[]> Rows, bool HasHeader, DateOnly? AsOf);

/// <summary>One account in a report that covers several. <see cref="Key"/> is the cell value; <see cref="Label"/> never shows more than the last 4 digits of a number.</summary>
public sealed record HoldingsAccount(string Key, string Label, int Rows);

/// <param name="Skipped">Rows that weren't holdings (totals, section titles), as text, to show the user.</param>
public sealed record HoldingsParseResult(IReadOnlyList<Holding> Holdings, IReadOnlyList<string> Skipped);

/// <summary>
/// Reads a broker's holdings (positions) report, exported as CSV or copied from the website, into
/// <see cref="Holding"/>s. The header row is found by its column names, which vary by broker
/// ("Market Value", "Current Market Value", "Book Cost", "Total Cost"); the user can change any
/// column the guess got wrong. Runs in the browser, so the report never leaves the device.
/// </summary>
public static class HoldingsReader
{
    private const int HeaderSearchLines = 40;

    // Each list is in order of preference; a header matches when it equals an entry once
    // punctuation and case are ignored. Contains lists catch longer names ("Market Value (CAD)").
    private static readonly string[] SymbolNames = ["symbol", "ticker", "security symbol", "equity symbol", "symbol cusip", "fund code", "code", "cusip"];
    private static readonly string[] SymbolContains = ["symbol", "ticker"];
    private static readonly string[] DescriptionNames = ["description", "name", "security name", "security description", "security", "investment name", "investment", "holding", "product"];
    private static readonly string[] DescriptionContains = ["description"];
    private static readonly string[] QuantityNames = ["quantity", "qty", "units", "shares", "open qty", "open quantity", "quantity held", "number of units", "units held", "position"];
    private static readonly string[] QuantityContains = ["quantity", "units"];
    private static readonly string[] PriceNames = ["price", "last price", "market price", "current price", "closing price", "close price", "unit price", "price per unit", "last", "nav", "unit value"];
    private static readonly string[] MarketValueNames = ["market value", "current market value", "market value cad", "total market value", "value", "current value", "total value", "marketvalue"];
    private static readonly string[] MarketValueContains = ["market value", "current value"];
    private static readonly string[] BookValueNames = ["book value", "book value market", "book cost", "total book cost", "total cost", "cost", "book value cad", "cost basis", "adjusted cost base", "acb"];
    private static readonly string[] BookValueContains = ["book value", "book cost"];
    private static readonly string[] CurrencyNames = ["currency", "ccy", "curr", "market value currency", "security currency", "price currency", "market price currency"];
    private static readonly string[] AccountNames = ["account number", "account no", "account", "account id", "acct", "account num"];
    private static readonly string[] AccountNameNames = ["account name", "account type", "account nickname", "account description"];

    private static readonly string[] TotalWords = ["total", "totals", "grand total", "subtotal", "sub total"];

    public static HoldingsTable ReadFile(byte[] content) => ReadText(CsvStatementReader.Decode(content));

    /// <summary>Splits pasted text or a decoded file into cells and finds the header row.</summary>
    public static HoldingsTable ReadText(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Where(l => l.Trim().Length > 0)
            .ToList();
        var tabbed = lines.Count(l => l.Contains('\t')) * 2 >= lines.Count;
        var rows = lines.Select(l => (tabbed ? l.Split('\t') : PasteParser.SplitCsv(l)).Select(c => c.Trim()).ToArray()).ToList();

        var header = -1;
        for (var i = 0; i < Math.Min(rows.Count, HeaderSearchLines); i++)
        {
            var guess = Guess(rows[i]);
            if (guess.MarketValue is not null && (guess.Symbol ?? guess.Description ?? guess.Quantity) is not null)
            {
                header = i;
                break;
            }
        }

        var asOf = FindAsOf(lines);
        if (header < 0)
        {
            var width = rows.Count == 0 ? 0 : rows.Max(r => r.Length);
            return new HoldingsTable([.. Enumerable.Range(1, width).Select(n => $"Column {n}")], rows, false, asOf);
        }

        return new HoldingsTable(rows[header], rows.Skip(header + 1).ToList(), true, asOf);
    }

    /// <summary>Which column holds what, from the header names. Each column is used once.</summary>
    public static HoldingColumns Guess(IReadOnlyList<string> headers)
    {
        var names = headers.Select(Normalize).ToList();
        var used = new HashSet<int>();

        int? Find(string[] exact, string[]? contains = null)
        {
            foreach (var wanted in exact)
            {
                for (var i = 0; i < names.Count; i++)
                {
                    if (names[i] == wanted && used.Add(i)) return i;
                }
            }

            foreach (var wanted in contains ?? [])
            {
                for (var i = 0; i < names.Count; i++)
                {
                    if (!used.Contains(i) && $" {names[i]} ".Contains($" {wanted} ") && !names[i].Contains("currency"))
                    {
                        used.Add(i);
                        return i;
                    }
                }
            }

            return null;
        }

        // Most specific first, so "Market Value" isn't taken as the price and "Book Value Currency" isn't a value.
        var marketValue = Find(MarketValueNames, MarketValueContains);
        var bookValue = Find(BookValueNames, BookValueContains);
        var currency = Find(CurrencyNames);
        var account = Find(AccountNames);
        var accountName = Find(AccountNameNames);
        var symbol = Find(SymbolNames, SymbolContains);
        var quantity = Find(QuantityNames, QuantityContains);
        var price = Find(PriceNames);
        var description = Find(DescriptionNames, DescriptionContains);
        return new HoldingColumns(symbol, description, quantity, price, marketValue, bookValue, currency, account, accountName);
    }

    /// <summary>The accounts a report covers, when it has an account column; empty otherwise.</summary>
    public static IReadOnlyList<HoldingsAccount> Accounts(HoldingsTable table, HoldingColumns columns)
    {
        if (columns.Account is not { } column) return [];
        return table.Rows
            .Where(r => Cell(r, column).Length > 0)
            .GroupBy(r => Cell(r, column))
            .Select(g =>
            {
                var name = columns.AccountName is { } n ? Cell(g.First(), n) : "";
                var key = MaskNumber(g.Key);
                return new HoldingsAccount(g.Key, name.Length > 0 && name != g.Key ? $"{name} {key}" : key, g.Count());
            })
            .ToList();
    }

    /// <summary>
    /// The holdings in the report (only <paramref name="account"/>'s, when given). Rows without a
    /// market value, and total lines, are skipped. A market value is worked out from quantity and
    /// price when the report leaves it out. Holdings whose row doesn't state a currency are in
    /// <paramref name="defaultCurrency"/>, the account's.
    /// </summary>
    public static HoldingsParseResult Parse(HoldingsTable table, HoldingColumns columns, string? account = null, string defaultCurrency = Holding.Cad)
    {
        var holdings = new List<Holding>();
        var skipped = new List<string>();
        foreach (var row in table.Rows)
        {
            if (account is not null && columns.Account is { } accountColumn && Cell(row, accountColumn) != account) continue;

            var symbol = Text(row, columns.Symbol);
            var description = Text(row, columns.Description);
            if (symbol is "--" or "-") symbol = null;

            if (IsTotal(symbol) || IsTotal(description) || IsTotal(row.FirstOrDefault(c => c.Length > 0)))
            {
                skipped.Add(Describe(row));
                continue;
            }

            var quantity = Number(row, columns.Quantity);
            var price = Number(row, columns.Price);
            var marketValue = Number(row, columns.MarketValue) ?? (quantity * price is { } product ? Math.Round(product, 2) : null);
            if (marketValue is null || (symbol is null && description is null))
            {
                skipped.Add(Describe(row));
                continue;
            }

            var bookValue = Number(row, columns.BookValue);
            if (Holding.CashCurrencyOf(symbol, description, bookValue) is { } cashCurrency)
            {
                // Interactive Brokers lists cash as a row per currency ("USD", 3,737.58) priced at the
                // exchange rate into the base currency, with the value in that one. The quantity is
                // the cash itself, in the currency the symbol names.
                holdings.Add(new Holding(Holding.CashSymbol, Clip(description ?? $"{cashCurrency} cash", Holding.MaxDescriptionLength),
                    null, null, quantity ?? marketValue.Value, null, cashCurrency));
                continue;
            }

            // An option's price is per share and a contract covers 100, so a value worked out (or
            // reported) as quantity × price is put right by WithContractValue. A statement that names
            // the ticker only in the description ("… (RDDT)") gets it, and the quantity, from there.
            var isCash = symbol is null && description is not null && description.Contains("cash", StringComparison.OrdinalIgnoreCase);
            holdings.Add(new Holding(
                Clip(isCash ? Holding.CashSymbol : symbol, Holding.MaxSymbolLength),
                Clip(description, Holding.MaxDescriptionLength),
                isCash ? null : quantity,
                isCash ? null : price,
                marketValue.Value,
                bookValue,
                CurrencyOf(row, columns) ?? defaultCurrency).WithListingFromDescription().WithContractValue());
        }

        return new HoldingsParseResult(holdings, skipped);
    }

    /// <summary>A currency code from a cell ("USD", "US$", "C$"), or null when it isn't one.</summary>
    public static string? ParseCurrency(string? text)
    {
        var s = (text ?? "").Trim().ToUpperInvariant();
        return s switch
        {
            "" => null,
            "US$" or "US" or "U.S." or "USD$" => "USD",
            "C$" or "CA$" or "CAN" or "CDN" or "CAD$" or "$" => Holding.Cad,
            _ when s.Length == 3 && s.All(char.IsAsciiLetterUpper) => s,
            _ => null,
        };
    }

    private static string? CurrencyOf(string[] row, HoldingColumns columns)
    {
        if (ParseCurrency(Text(row, columns.Currency)) is { } code) return code;

        // No currency column: some reports print "USD 1,234.56" or "US$1,234.56" in the value itself.
        var value = Text(row, columns.MarketValue) ?? "";
        return value.Contains("USD", StringComparison.OrdinalIgnoreCase) || value.Contains("US$", StringComparison.OrdinalIgnoreCase)
            ? "USD"
            : null;
    }

    // "Positions as of …", "Holdings as at …", a statement's "Period ending …".
    private static readonly string[] AsOfMarkers = ["as of", "as at", "ending", "ended"];

    /// <summary>"As of November 14, 2026" (or "Positions as of 2026-11-14", "Period ending …") in the lines around the table.</summary>
    internal static DateOnly? FindAsOf(IEnumerable<string> lines)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        foreach (var line in lines)
        {
            foreach (var marker in AsOfMarkers)
            {
                var at = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (at < 0) continue;

                var words = line[(at + marker.Length)..].Split([' ', ',', '"', '\t', ':'], StringSplitOptions.RemoveEmptyEntries);
                for (var take = Math.Min(4, words.Length); take >= 1; take--)
                {
                    if (DateParser.TryParse(string.Join(' ', words.Take(take)), DateOrder.Auto, today, out var date)) return date;
                }
            }
        }

        return null;
    }

    private static bool IsTotal(string? text) =>
        text is not null && TotalWords.Any(w => Normalize(text) == w || Normalize(text).StartsWith(w + " ", StringComparison.Ordinal));

    private static decimal? Number(string[] row, int? column) =>
        Text(row, column) is { } text && AmountParser.TryParse(text, out var amount) ? amount.Value : null;

    private static string? Text(string[] row, int? column) =>
        column is { } c && Cell(row, c) is { Length: > 0 } text ? text : null;

    private static string Cell(string[] row, int column) => column < row.Length ? row[column].Trim() : "";

    private static string? Clip(string? text, int length) => text is null ? null : text.Length <= length ? text : text[..length].TrimEnd();

    private static string Describe(string[] row)
    {
        var text = string.Join(" · ", row.Where(c => c.Length > 0));
        return text.Length <= 80 ? text : text[..77] + "...";
    }

    /// <summary>Account numbers show only their last 4 digits; names show as they are.</summary>
    private static string MaskNumber(string value)
    {
        var digits = value.Count(char.IsAsciiDigit);
        return digits >= 5 && digits * 2 >= value.Length ? "••••" + new string([.. value.Where(char.IsAsciiDigit).TakeLast(4)]) : value;
    }

    internal static string Normalize(string header)
    {
        var sb = new StringBuilder(header.Length);
        foreach (var c in header.Trim().ToLower(CultureInfo.InvariantCulture))
        {
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
