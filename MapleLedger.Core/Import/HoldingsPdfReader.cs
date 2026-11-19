using System.Text.RegularExpressions;

namespace MapleLedger.Core.Import;

/// <param name="Table">The holdings tables found, as one table; null when none was found.</param>
/// <param name="HasText">False when the PDF has no text layer (a scanned image).</param>
/// <param name="TableCount">How many holdings tables (header rows) were found.</param>
public sealed record HoldingsPdfResult(HoldingsTable? Table, int PageCount, bool HasText, int TableCount);

/// <summary>
/// Reads the holdings section of a broker's statement saved as PDF (a monthly or quarterly
/// statement). A PDF has no table structure, only words placed on a page, so this looks for a
/// header row whose column names <see cref="HoldingsReader.Guess"/> recognises (a market value and
/// a symbol, description or quantity), including headers printed over two or three lines
/// ("Market" above "Value"), and puts each following word into the column under whose name it
/// sits. A table ends at a large gap, at a line that starts with a date (the activity section) or
/// at another table's header. Section titles such as "U.S. dollar holdings" set the currency of
/// the rows under them when the table has no currency column. The result is a
/// <see cref="HoldingsTable"/> that <see cref="HoldingsReader.Parse"/> reads like a CSV report.
/// Runs in the browser, so the statement never leaves the device.
/// </summary>
public static class HoldingsPdfReader
{
    /// <summary>Words further apart than this many font sizes are in different cells.</summary>
    private const double CellGap = 0.9;

    /// <summary>Header lines further apart than this many font sizes aren't one header.</summary>
    private const double HeaderLineGap = 1.6;

    /// <summary>A gap of more than this many font sizes between rows ends a table.</summary>
    private const double TableGap = 4.0;

    /// <summary>A wrapped description is at most this many font sizes below its row.</summary>
    private const double WrapGap = 1.6;

    private const string CurrencyHeader = "Currency";

    private static readonly Regex Year = new(@"\b(19|20)\d\d\b", RegexOptions.CultureInvariant);

    public static HoldingsPdfResult Read(StatementFile file, DateOnly? today = null) =>
        ReadPages(PdfStatementReader.ReadPdfWords(file), today);

    /// <summary>Reads the holdings tables from words already placed on pages (PDF coordinates: baselines grow upward).</summary>
    public static HoldingsPdfResult ReadPages(IReadOnlyList<IReadOnlyList<StatementWord>> pages, DateOnly? today = null)
    {
        var now = today ?? DateOnly.FromDateTime(DateTime.Today);
        var lines = pages
            .SelectMany((words, page) => PdfStatementReader
                .GroupLines(words.Where(w => !string.IsNullOrWhiteSpace(w.Text) && w.Size > 0).ToList())
                .Select(l => new Line(page, l)))
            .ToList();
        if (lines.Count == 0) return new HoldingsPdfResult(null, pages.Count, false, 0);

        var tables = new List<Table>();
        string? currency = null;
        var i = 0;
        while (i < lines.Count)
        {
            if (FindHeader(lines, i) is { } header)
            {
                var table = new Table(header.Columns);
                tables.Add(table);
                i = ReadRows(lines, i + header.LineCount, table, ref currency);
                continue;
            }

            if (!lines[i].HasNumber && SectionCurrency(lines[i]) is { } code) currency = code;
            i++;
        }

        var asOf = FindAsOf(lines, now);
        if (tables.All(t => t.Rows.Count == 0)) return new HoldingsPdfResult(null, pages.Count, true, tables.Count);
        return new HoldingsPdfResult(Combine(tables, asOf), pages.Count, true, tables.Count);
    }

    // ---------------------------------------------------------------- lines and cells

    private sealed record Cell(string Text, double Left, double Right, bool Numeric, bool Number);

    private sealed class Line
    {
        public Line(int page, List<StatementWord> words)
        {
            Page = page;
            Words = words;
            Baseline = words[0].Baseline;
            Size = words.Max(w => w.Size);
            Text = string.Join(' ', words.Select(w => w.Text));
            Cells = SplitCells(words);
        }

        public int Page { get; }
        public List<StatementWord> Words { get; }
        public double Baseline { get; }
        public double Size { get; }
        public string Text { get; }
        public List<Cell> Cells { get; }
        public bool HasNumber => Cells.Any(c => c.Number);
        public double Left => Words[0].Left;
        public double Right => Words[^1].Right;
    }

    /// <summary>
    /// Words close together form a cell. A cell of numbers only ("100 52.30" in a tightly set
    /// table) is split back into one cell per number, since each belongs in its own column.
    /// </summary>
    private static List<Cell> SplitCells(List<StatementWord> words)
    {
        var groups = new List<List<StatementWord>>();
        foreach (var word in words)
        {
            if (groups.Count > 0 && word.Left - groups[^1][^1].Right <= CellGap * Math.Min(word.Size, groups[^1][^1].Size)) groups[^1].Add(word);
            else groups.Add([word]);
        }

        var cells = new List<Cell>();
        foreach (var group in groups)
        {
            if (group.Count > 1 && group.All(w => IsNumber(w.Text) || IsFiller(w.Text)) && !IsSpacedNumber(group))
            {
                cells.AddRange(group.Select(w => new Cell(w.Text, w.Left, w.Right, true, IsNumber(w.Text))));
            }
            else
            {
                var text = string.Join(' ', group.Select(w => w.Text));
                var number = IsNumber(text);
                cells.Add(new Cell(text, group[0].Left, group[^1].Right, number || IsFiller(text), number));
            }
        }

        return cells;
    }

    /// <summary>"1 234,56" (French-Canadian): one amount whose thousands are separated by spaces.</summary>
    private static bool IsSpacedNumber(List<StatementWord> group) =>
        SpacedLead.IsMatch(group[0].Text) && group.Skip(1).All(w => SpacedThousands.IsMatch(w.Text));

    private static readonly Regex SpacedLead = new(@"^[-(]?\d{1,3}$", RegexOptions.CultureInvariant);
    private static readonly Regex SpacedThousands = new(@"^\d{3}([.,]\d+)?\)?%?$", RegexOptions.CultureInvariant);

    private static bool IsNumber(string text)
    {
        var s = text.Trim().TrimEnd('%').Trim();
        return s.Any(char.IsAsciiDigit) && AmountParser.TryParse(s, out _);
    }

    /// <summary>Placeholders printed in a number column: "-", "—", "N/A".</summary>
    private static bool IsFiller(string text) => text.Trim() is "-" or "—" or "–" or "--" or "N/A" or "n/a" or "NA";

    // ---------------------------------------------------------------- header

    private sealed record Column(string Name, double Left, double Right)
    {
        public double Center => (Left + Right) / 2;
    }

    private sealed record Header(List<Column> Columns, int LineCount);

    /// <summary>
    /// A holdings header starting at <paramref name="start"/>: up to three close lines of text
    /// (a two-line header, or one with names centred between two lines), widest first.
    /// </summary>
    private static Header? FindHeader(List<Line> lines, int start)
    {
        if (lines[start].HasNumber || lines[start].Cells.Count < 2) return null;

        var count = 1;
        while (count < 3 && start + count < lines.Count)
        {
            var next = lines[start + count];
            var previous = lines[start + count - 1];
            if (next.Page != previous.Page || next.HasNumber || previous.Baseline - next.Baseline > HeaderLineGap * Math.Max(next.Size, previous.Size)) break;
            count++;
        }

        for (var n = count; n >= 1; n--)
        {
            var group = lines.Skip(start).Take(n).ToList();
            // A line joined to a header shares its columns: a lone title below it isn't part of it.
            if (n > 1 && group.Skip(1).Any(l => l.Cells.Count < 2 && !l.Cells.Any(c => group[0].Cells.Any(h => Overlap(c.Left, c.Right, h.Left, h.Right) > 0)))) continue;

            var columns = Columns(group);
            var guess = HoldingsReader.Guess(columns.Select(c => c.Name).ToList());
            if (guess.MarketValue is not null && (guess.Symbol ?? guess.Description ?? guess.Quantity) is not null) return new Header(columns, n);
        }

        return null;
    }

    /// <summary>Joins the header lines' cells that sit over each other into one column name, top line first.</summary>
    private static List<Column> Columns(List<Line> group)
    {
        var cells = group.SelectMany((l, row) => l.Cells.Select(c => (Row: row, Cell: c))).OrderBy(x => x.Cell.Left).ToList();
        var clusters = new List<List<(int Row, Cell Cell)>>();
        foreach (var item in cells)
        {
            var into = clusters.FirstOrDefault(k => k.Any(x => Overlap(x.Cell.Left, x.Cell.Right, item.Cell.Left, item.Cell.Right) > 0));
            if (into is null) clusters.Add([item]);
            else into.Add(item);
        }

        return clusters
            .Select(k => new Column(
                string.Join(' ', k.OrderBy(x => x.Row).ThenBy(x => x.Cell.Left).Select(x => x.Cell.Text)),
                k.Min(x => x.Cell.Left),
                k.Max(x => x.Cell.Right)))
            .OrderBy(c => c.Left)
            .ToList();
    }

    private static double Overlap(double left1, double right1, double left2, double right2) =>
        Math.Min(right1, right2) - Math.Max(left1, left2);

    // ---------------------------------------------------------------- rows

    private sealed class Table(List<Column> columns)
    {
        public List<Column> Columns { get; } = columns;
        public List<Row> Rows { get; } = [];
    }

    private sealed class Row(int columns, string? currency, Line line)
    {
        public string[] Cells { get; } = Enumerable.Repeat("", columns).ToArray();
        public string? Currency { get; } = currency;
        public Line Last { get; set; } = line;
        public bool HasNumber { get; init; }
    }

    /// <summary>Reads the rows under a header; returns the index of the first line after the table.</summary>
    private static int ReadRows(List<Line> lines, int start, Table table, ref string? currency)
    {
        var textColumns = TextColumns(table.Columns);
        var tableLeft = table.Columns[0].Left;
        var tableWidth = Math.Max(table.Columns[^1].Right - tableLeft, 1);
        var header = lines[start - 1];
        Line previous = header;
        Row? last = null;
        var pending = new List<Line>();

        var i = start;
        for (; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Page != header.Page) break;
            if (previous.Baseline - line.Baseline > TableGap * Math.Max(line.Size, previous.Size)) break;
            if (StartsWithDate(line)) break;
            if (FindHeader(lines, i) is not null) break;

            if (!line.HasNumber)
            {
                // Paragraphs (notes under the table) and another table's header end it.
                if (line.Words.Count > 8 && (line.Right - line.Left) > 0.6 * tableWidth) break;
                if (line.Cells.Count >= 3 && !line.Cells.All(c => c.Numeric)) break;

                var place = Place(line, table.Columns);
                var textOnly = place.All(p => textColumns.Contains(p.Column));
                if (last is not null && textOnly && pending.Count == 0 && last.Last.Baseline - line.Baseline <= WrapGap * line.Size)
                {
                    // A description (or symbol) that wrapped onto the next line.
                    Append(last, place);
                    last.Last = line;
                }
                else
                {
                    pending.Add(line);
                }

                previous = line;
                continue;
            }

            var cells = Place(line, table.Columns);
            var hasText = cells.Any(c => textColumns.Contains(c.Column) && !c.Cell.Numeric);

            // A name printed on the line above its numbers.
            Line? name = !hasText && pending.Count > 0 && pending[^1].Baseline - line.Baseline <= WrapGap * line.Size ? pending[^1] : null;
            if (name is not null) pending.RemoveAt(pending.Count - 1);

            foreach (var title in pending)
            {
                if (SectionCurrency(title) is { } code) currency = code;
                table.Rows.Add(TitleRow(title, table));
            }

            pending.Clear();
            var row = new Row(table.Columns.Count, currency, line) { HasNumber = true };
            if (name is not null) Append(row, Place(name, table.Columns));
            Append(row, cells);
            table.Rows.Add(row);
            last = row;
            previous = line;
        }

        foreach (var title in pending)
        {
            if (SectionCurrency(title) is { } code) currency = code;
        }

        return i;
    }

    /// <summary>A title line kept as a row of its own, so the user sees it among the rows left out.</summary>
    private static Row TitleRow(Line title, Table table)
    {
        var row = new Row(table.Columns.Count, null, title);
        Append(row, Place(title, table.Columns));
        return row;
    }

    private static HashSet<int> TextColumns(List<Column> columns)
    {
        var guess = HoldingsReader.Guess(columns.Select(c => c.Name).ToList());
        return new[] { guess.Symbol, guess.Description, guess.Currency, guess.Account, guess.AccountName }.OfType<int>().ToHashSet();
    }

    /// <summary>
    /// Which column each cell of a line goes in: the one whose name it sits under most, or else the
    /// nearest. Numbers are right-aligned under their name, text left-aligned.
    /// </summary>
    private static List<(int Column, Cell Cell)> Place(Line line, List<Column> columns)
    {
        var placed = new List<(int, Cell)>();
        foreach (var cell in line.Cells)
        {
            var best = -1;
            var bestScore = double.NegativeInfinity;
            for (var c = 0; c < columns.Count; c++)
            {
                var col = columns[c];
                var overlap = Overlap(cell.Left, cell.Right, col.Left, col.Right);
                var score = overlap > 0
                    ? overlap / Math.Min(cell.Right - cell.Left + 0.01, col.Right - col.Left + 0.01)
                    : -Math.Min(
                        Math.Abs(cell.Numeric ? cell.Right - col.Right : cell.Left - col.Left),
                        Math.Abs((cell.Left + cell.Right) / 2 - col.Center));
                if (score > bestScore)
                {
                    bestScore = score;
                    best = c;
                }
            }

            placed.Add((best, cell));
        }

        return placed;
    }

    private static void Append(Row row, List<(int Column, Cell Cell)> cells)
    {
        foreach (var (column, cell) in cells)
        {
            if (cell.Text is "-" or "—" or "–" or "--") continue;
            row.Cells[column] = row.Cells[column].Length == 0 ? cell.Text : $"{row.Cells[column]} {cell.Text}";
        }
    }

    private static bool StartsWithDate(Line line)
    {
        var words = line.Words.Select(w => w.Text.TrimEnd(',', '.')).ToList();
        if (!words[0].Any(char.IsAsciiDigit) && (words.Count < 2 || !words[1].Any(char.IsAsciiDigit))) return false;
        for (var take = Math.Min(3, words.Count); take >= 1; take--)
        {
            var text = string.Join(' ', words.Take(take));
            if (text.Count(char.IsAsciiDigit) >= 2 && !AmountParser.TryParse(text, out _)
                && DateParser.TryParse(text, DateOrder.Auto, DateOnly.FromDateTime(DateTime.Today), out _)) return true;
        }

        return false;
    }

    // ---------------------------------------------------------------- currency, date, result

    /// <summary>"U.S. dollar holdings", "Canadian dollar account", "(USD)": the currency a section title names.</summary>
    private static string? SectionCurrency(Line line)
    {
        if (line.Words.Count > 8) return null;
        var text = $" {HoldingsReader.Normalize(line.Text.Replace("U.S.", "US", StringComparison.OrdinalIgnoreCase).Replace("US$", "USD", StringComparison.OrdinalIgnoreCase))} ";
        var usd = text.Contains(" us dollar") || text.Contains(" usd ") || text.Contains(" american dollar");
        var cad = text.Contains(" canadian dollar") || text.Contains(" cad ") || text.Contains(" cdn ");
        return usd == cad ? null : usd ? "USD" : "CAD";
    }

    /// <summary>The date the statement states ("as of", "period ending"), else the latest full date on its first page.</summary>
    private static DateOnly? FindAsOf(List<Line> lines, DateOnly today)
    {
        if (HoldingsReader.FindAsOf(lines.Select(l => l.Text)) is { } stated && stated <= today) return stated;

        DateOnly? latest = null;
        foreach (var line in lines.Where(l => l.Page == 0))
        {
            for (var w = 0; w < line.Words.Count; w++)
            {
                if (!Year.IsMatch(line.Words[w].Text)) continue;
                for (var take = 1; take <= 3 && take <= w + 1; take++)
                {
                    var text = string.Join(' ', line.Words.Skip(w + 1 - take).Take(take).Select(x => x.Text.Trim(',', '.', '(', ')')));
                    if (DateParser.TryParse(text, DateOrder.Auto, today, out var date) && date <= today && (latest is null || date > latest)) latest = date;
                }
            }
        }

        return latest;
    }

    /// <summary>
    /// One table from all the tables found: the first one's columns, each later table's columns put
    /// under the same name or the same kind of column, and a currency column from section titles.
    /// </summary>
    private static HoldingsTable Combine(List<Table> tables, DateOnly? asOf)
    {
        var headers = tables[0].Columns.Select(c => c.Name).ToList();
        var guess = HoldingsReader.Guess(headers);
        var addCurrency = guess.Currency is null && tables.SelectMany(t => t.Rows).Any(r => r.Currency is not null);
        var width = headers.Count + (addCurrency ? 1 : 0);
        if (addCurrency) headers.Add(CurrencyHeader);

        var rows = new List<string[]>();
        foreach (var table in tables)
        {
            var map = Map(table.Columns.Select(c => c.Name).ToList(), headers.Take(tables[0].Columns.Count).ToList());
            foreach (var row in table.Rows)
            {
                var cells = Enumerable.Repeat("", width).ToArray();
                for (var c = 0; c < row.Cells.Length; c++)
                {
                    if (map[c] is { } to) cells[to] = row.Cells[c];
                }

                if (addCurrency && row.HasNumber) cells[^1] = row.Currency ?? "";
                if (cells.Any(c => c.Length > 0)) rows.Add(cells);
            }
        }

        return new HoldingsTable(headers, rows, true, asOf);
    }

    /// <summary>Where each of a table's columns goes in the combined table: the same name, else the same kind of column.</summary>
    private static int?[] Map(List<string> from, List<string> to)
    {
        var map = new int?[from.Count];
        var toNames = to.Select(HoldingsReader.Normalize).ToList();
        var taken = new HashSet<int>();
        for (var c = 0; c < from.Count; c++)
        {
            var at = toNames.IndexOf(HoldingsReader.Normalize(from[c]));
            if (at >= 0 && taken.Add(at)) map[c] = at;
        }

        var fromGuess = Fields(HoldingsReader.Guess(from));
        var toGuess = Fields(HoldingsReader.Guess(to));
        for (var f = 0; f < fromGuess.Length; f++)
        {
            if (fromGuess[f] is { } c && map[c] is null && toGuess[f] is { } at && taken.Add(at)) map[c] = at;
        }

        return map;
    }

    private static int?[] Fields(HoldingColumns c) =>
        [c.Symbol, c.Description, c.Quantity, c.Price, c.MarketValue, c.BookValue, c.Currency, c.Account, c.AccountName];
}
