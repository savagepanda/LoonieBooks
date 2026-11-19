using System.Globalization;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Exceptions;

namespace MapleLedger.Core.Import;

/// <summary>A statement file (PDF or CSV) chosen by the user, read into memory.</summary>
public sealed record StatementFile(string Name, byte[] Content);

public enum PdfTextLayout
{
    /// <summary>The PDF had no text layer, typically a scanned image.</summary>
    NoText,

    /// <summary>A transaction table was found and rebuilt as tab-separated rows.</summary>
    Table,

    /// <summary>No table was found; the text is the PDF's lines, as copying from a viewer would give.</summary>
    Lines,
}

/// <summary>
/// A word on a statement page and where it is. <paramref name="Baseline"/> grows upward, as in a
/// PDF: for an image, pass the negated pixel row. <paramref name="Size"/> is the font size (or
/// line height) in the same units, which sets how wide a gap separates two cells.
/// </summary>
public sealed record StatementWord(string Text, double Left, double Right, double Baseline, double Size);

/// <summary>The words of one statement read some other way than from a PDF (e.g. by OCR), page by page.</summary>
public sealed record StatementWords(string Name, IReadOnlyList<IReadOnlyList<StatementWord>> Pages);

/// <param name="Text">Text for <see cref="PasteParser"/>, the same as if the user had pasted it.</param>
public sealed record PdfTextResult(string Text, PdfTextLayout Layout, int PageCount, int RowCount);

/// <summary>A PDF that can't be read, with a message fit to show the user.</summary>
public sealed class PdfStatementException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Reads bank and card statements saved as PDF. A PDF has no table structure, only words placed on
/// a page, so this rebuilds the transaction table from the words' positions: rows start with a date,
/// and amounts line up in columns (withdrawals, deposits, balance) by their right edges. It handles
/// dates printed once per day, descriptions that wrap onto a second line, and dates without a year.
/// The output is tab-separated text that <see cref="PasteParser"/> reads like a copied table.
/// Runs in the browser, so the statement never leaves the user's device. Words read from a photo or
/// screenshot by OCR (<see cref="ReadWords"/>) go through the same steps.
/// </summary>
public static class PdfStatementReader
{
    /// <summary>Words further apart than this many font sizes are in different cells.</summary>
    private const double CellGap = 0.9;

    /// <summary>How far apart, in font sizes, right edges can be and still be one column.</summary>
    private const double ColumnTolerance = 1.2;

    private static readonly string[] AmountSuffixes = ["$", "CR", "DR", "cr", "dr"];

    // Summary lines inside a statement's table that carry an amount but are not transactions.
    private static readonly string[] SummaryWords = ["TOTAL", "TOTALS", "SUBTOTAL", "SOUS", "FORWARD", "REPORTE", "OPENING", "CLOSING", "PREVIOUS", "SOLDE", "BALANCE"];

    // Words that can sit between summary words: "Total of the period", "Solde de fermeture".
    private static readonly string[] SummaryFillers = ["THE", "OF", "NEW", "DE", "DU", "DES", "LE", "LA", "NOUVEAU", "ANCIEN", "FERMETURE", "OUVERTURE"];

    public static PdfTextResult Read(IEnumerable<StatementFile> files, DateOnly? today = null) =>
        Read(files.Select(ReadPdfWords), today);

    /// <summary>
    /// Rebuilds the transaction table from words already placed on pages, e.g. by OCR. Unlike PDF
    /// pages, each picture has its own scale and framing, so each one's table is read on its own
    /// and their columns are lined up by where they sit across the table (see <see cref="Merge"/>).
    /// </summary>
    public static PdfTextResult ReadWords(IEnumerable<StatementWords> documents, DateOnly? today = null)
    {
        var now = today ?? DateOnly.FromDateTime(DateTime.Today);
        var pictures = documents
            .Select((d, i) => ReadDocument(d.Pages.Select(p => p.Where(w => !string.IsNullOrWhiteSpace(w.Text) && w.Size > 0).ToList()).ToList(), i, now))
            .ToList();
        var pageCount = pictures.Sum(p => p.PageCount);
        var lines = pictures.SelectMany(p => p.Lines).ToList();
        if (lines.Count == 0)
        {
            return new PdfTextResult(string.Empty, PdfTextLayout.NoText, pageCount, 0);
        }

        var tables = pictures.Select(p => ReadTable(p.Lines)).OfType<Table>().ToList();
        if (tables.Count == 0)
        {
            return new PdfTextResult(string.Join('\n', lines.Select(l => string.Join(' ', l.Words.Select(w => w.Text)))), PdfTextLayout.Lines, pageCount, 0);
        }

        var (rows, header) = Merge(tables);
        return new PdfTextResult(Format(rows, header), PdfTextLayout.Table, pageCount, rows.Count);
    }

    /// <summary>One picture's table, with each amount column's position across it: 0 at the dates, 1 at the rightmost column.</summary>
    private sealed record Table(List<Row> Rows, double[] Positions, string[]? Header);

    private static Table? ReadTable(List<Line> lines)
    {
        lines = AlignMarkedAmounts(lines);
        var columns = FindAmountColumns(lines);
        if (columns.Count == 0) return null;
        var rows = BuildRows(lines, columns);
        if (rows.Count == 0) return null;

        var dated = lines.Where(l => l.Dates.Count > 0 && l.Amounts.Count > 0).Select(l => l.Words[0].Left).Order().ToList();
        var left = dated.Count > 0 ? dated[dated.Count / 2] : lines.Min(l => l.Words[0].Left);
        var width = Math.Max(columns.Max(c => c.MaxRight) - left, 1);
        return new Table(rows, columns.Select(c => (c.MaxRight - left) / width).ToArray(), FindHeader(lines, columns));
    }

    /// <summary>
    /// Puts several pictures' rows into one table. The table with the most columns (a page with no
    /// deposits has one fewer) sets the columns; each other table's columns go to the nearest one.
    /// </summary>
    private static (List<Row> Rows, string[]? Header) Merge(List<Table> tables)
    {
        var reference = tables.OrderByDescending(t => t.Positions.Length).ThenByDescending(t => t.Header is not null).First();
        var count = reference.Positions.Length;
        var rows = new List<Row>();
        foreach (var table in tables)
        {
            var map = new int[table.Positions.Length];
            var taken = new HashSet<int>();
            foreach (var i in Enumerable.Range(0, map.Length).OrderBy(i => Nearest(reference.Positions, table.Positions[i], taken).Distance))
            {
                (map[i], _) = Nearest(reference.Positions, table.Positions[i], taken);
                taken.Add(map[i]);
            }

            foreach (var row in table.Rows)
            {
                var merged = new Row(row.Dates, count);
                merged.Description.Append(row.Description);
                for (var c = 0; c < row.Amounts.Length; c++) merged.Amounts[map[c]] = row.Amounts[c];
                rows.Add(merged);
            }
        }

        var header = reference.Header ?? tables.FirstOrDefault(t => t.Header is not null && t.Positions.Length == count)?.Header;
        return (rows, header);
    }

    private static (int Index, double Distance) Nearest(double[] positions, double position, HashSet<int> taken) =>
        Enumerable.Range(0, positions.Length)
            .Where(i => !taken.Contains(i))
            .Select(i => (Index: i, Distance: Math.Abs(positions[i] - position)))
            .OrderBy(x => x.Distance)
            .First();

    private static PdfTextResult Read(IEnumerable<List<List<StatementWord>>> pagesByDocument, DateOnly? today)
    {
        var now = today ?? DateOnly.FromDateTime(DateTime.Today);
        var documents = pagesByDocument.Select((pages, i) => ReadDocument(pages, i, now, splitRegions: true)).ToList();
        var lines = documents.SelectMany(d => d.Lines).ToList();
        var pageCount = documents.Sum(d => d.PageCount);

        if (lines.Count == 0)
        {
            return new PdfTextResult(string.Empty, PdfTextLayout.NoText, pageCount, 0);
        }

        lines = AlignMarkedAmounts(lines);
        var columns = FindAmountColumns(lines);
        if (columns.Count > 0)
        {
            var rows = BuildRows(lines, columns);
            if (rows.Count > 0)
            {
                return new PdfTextResult(Format(rows, FindHeader(lines, columns)), PdfTextLayout.Table, pageCount, rows.Count);
            }
        }

        var text = string.Join('\n', lines.Select(l => string.Join(' ', l.Words.Select(w => w.Text))));
        return new PdfTextResult(text, PdfTextLayout.Lines, pageCount, 0);
    }

    // ---------------------------------------------------------------- reading words

    /// <param name="NumberRight">Where the number ends, before a marker such as "CR" or "$".</param>
    private sealed record Money(int FirstWord, string Text, double Right, double NumberRight);

    /// <summary>A line of words, with any leading dates and trailing amounts already picked out.</summary>
    private sealed record Line(int Document, int Page, double Baseline, double Size, List<StatementWord> Words, List<string> Dates, int DateWords, List<Money> Amounts)
    {
        public string TextBetween(int from, int to) => string.Join(' ', Words.Skip(from).Take(to - from).Select(w => w.Text));
    }

    private sealed record Document(int PageCount, List<Line> Lines);

    /// <summary>The words on each page of a PDF; throws <see cref="PdfStatementException"/> when it can't be read.</summary>
    internal static List<List<StatementWord>> ReadPdfWords(StatementFile file)
    {
        var pages = new List<List<StatementWord>>();
        var content = file.Content;
        PdfEncryption encryption;
        try
        {
            encryption = PdfDecryptor.Decrypt(file.Content, out content);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A file this can't make sense of goes to PdfPig as it is, which reports it properly.
            encryption = PdfEncryption.Unsupported;
            content = file.Content;
        }

        if (encryption == PdfEncryption.NeedsPassword)
        {
            throw new PdfStatementException(PasswordMessage(file));
        }

        try
        {
            using var pdf = PdfDocument.Open(content);
            foreach (var page in pdf.GetPages())
            {
                pages.Add(page.GetWords()
                    .Where(w => w.TextOrientation == TextOrientation.Horizontal && !string.IsNullOrWhiteSpace(w.Text) && w.Letters.Count > 0)
                    .Select(ToBox)
                    .ToList());
            }
        }
        catch (PdfDocumentEncryptedException ex)
        {
            throw new PdfStatementException(PasswordMessage(file), ex);
        }
        catch (Exception ex) when (encryption == PdfEncryption.Unsupported && IsMissingCrypto(ex))
        {
            // AES-256 (revisions 5 and 6) and other schemes: the browser's .NET has no AES for PdfPig to use.
            throw new PdfStatementException($"{file.Name} is encrypted in a way LoonieBooks can't read in the browser. Open it, print it to PDF (e.g. Microsoft Print to PDF), and choose that copy.", ex);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or PdfStatementException))
        {
            throw new PdfStatementException($"{file.Name} couldn't be read as a PDF.", ex);
        }

        return pages;
    }

    private static string PasswordMessage(StatementFile file) =>
        $"{file.Name} is password-protected. Open it in a PDF viewer, save or print a copy without the password, and choose that copy.";

    private static bool IsMissingCrypto(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is System.Security.Cryptography.CryptographicException or PlatformNotSupportedException) return true;
        }

        return false;
    }

    private static Document ReadDocument(List<List<StatementWord>> pages, int index, DateOnly today, bool splitRegions = false)
    {
        var grouped = pages.Select((words, page) => (page, lines: splitRegions ? GroupRegions(words) : GroupLines(words))).ToList();
        if (splitRegions) grouped = AlignPages(grouped);

        // Statements print dates without a year ("Sep 28"). Infer the year from the statement's own
        // dates (its period or due date), not from today, so last year's statements land in last year.
        var reference = LatestFullDate(grouped.SelectMany(p => p.lines), today) ?? today;

        var lines = grouped
            .SelectMany(p => p.lines.Select(words => Analyse(words, index, p.page, reference)))
            .ToList();
        return new Document(pages.Count, lines);
    }

    private static StatementWord ToBox(Word word)
    {
        var size = word.Letters.Max(l => l.PointSize);
        if (size < 3)
        {
            // Some generators set a 1pt font and scale it with the text matrix.
            size = Math.Max(word.BoundingBox.Height, 1) * 1.4;
        }

        return new StatementWord(word.Text, word.BoundingBox.Left, word.BoundingBox.Right, word.Letters[0].StartBaseLine.Y, size);
    }

    /// <summary>Groups words into lines, top to bottom (PDF y grows upward), each sorted left to right.</summary>
    internal static List<List<StatementWord>> GroupLines(List<StatementWord> words)
    {
        var lines = new List<List<StatementWord>>();
        foreach (var word in words.OrderByDescending(w => w.Baseline).ThenBy(w => w.Left))
        {
            if (lines.Count > 0 && Math.Abs(lines[^1][0].Baseline - word.Baseline) <= 0.4 * Math.Min(lines[^1][0].Size, word.Size))
            {
                lines[^1].Add(word);
            }
            else
            {
                lines.Add([word]);
            }
        }

        foreach (var line in lines)
        {
            line.Sort((a, b) => a.Left.CompareTo(b.Left));
        }

        return lines;
    }

    /// <summary>
    /// Like <see cref="GroupLines"/>, but keeps a box beside the transaction table (a card statement's
    /// payment information, credit limit and interest rates) out of the table's rows. Lines of the
    /// two often sit within a point of each other, so grouping by baseline alone would join a
    /// transaction to the box's text. Within the band of rows that start with a date, the page is
    /// cut into strips at the vertical gaps no word crosses. Every cell of a table row shares its
    /// date's baseline exactly, while the box's lines have their own: a strip whose lines mostly
    /// don't line up with anything to its left starts a separate region, grouped into lines on its
    /// own. Only for PDFs, whose baselines are exact; OCR'd pictures wobble by a pixel or two.
    /// </summary>
    private static List<List<StatementWord>> GroupRegions(List<StatementWord> words)
    {
        var lines = GroupLines(words);
        var dated = lines.Where(StartsWithDate).ToList();
        if (dated.Count < 2) return lines;

        var size = dated.SelectMany(l => l).Select(w => w.Size).Order().ElementAt(dated.Sum(l => l.Count) / 2);
        double top = dated.Max(l => l[0].Baseline) + size, bottom = dated.Min(l => l[0].Baseline) - 3 * size;
        var band = words.Where(w => w.Baseline <= top && w.Baseline >= bottom).ToList();

        // Strips: runs of x that words in the band cover, split where no word crosses.
        var strips = new List<(double Left, double Right)>();
        foreach (var w in band.OrderBy(w => w.Left))
        {
            if (strips.Count > 0 && w.Left <= strips[^1].Right + 0.8 * size) strips[^1] = (strips[^1].Left, Math.Max(strips[^1].Right, w.Right));
            else strips.Add((w.Left, w.Right));
        }

        if (strips.Count < 2) return lines;

        int StripOf(StatementWord w) => strips.FindIndex(s => w.Left >= s.Left - 0.01 && w.Right <= s.Right + 0.01);
        var byStrip = band.GroupBy(StripOf).ToDictionary(g => g.Key, g => g.ToList());
        var tolerance = 0.1 * size;

        var regionOf = new int[strips.Count];
        var leftBaselines = new List<double>(byStrip.GetValueOrDefault(0, []).Select(w => w.Baseline));
        for (var i = 1; i < strips.Count; i++)
        {
            var strip = byStrip.GetValueOrDefault(i, []);
            var baselines = Distinct(strip.Select(w => w.Baseline), tolerance);
            var unaligned = baselines.Count(b => !leftBaselines.Any(l => Math.Abs(l - b) <= tolerance));
            regionOf[i] = regionOf[i - 1] + (unaligned >= 3 && unaligned * 2 > baselines.Count ? 1 : 0);
            leftBaselines.AddRange(strip.Select(w => w.Baseline));
        }

        if (regionOf[^1] == 0) return lines;

        // Above the band (title, column headings), each region of the band, then below it.
        var inBand = band.ToHashSet();
        var result = GroupLines(words.Where(w => !inBand.Contains(w) && w.Baseline > top).ToList());
        foreach (var region in band.GroupBy(w => regionOf[StripOf(w)]).OrderBy(g => g.Key))
        {
            result.AddRange(GroupLines(region.ToList()));
        }

        result.AddRange(GroupLines(words.Where(w => !inBand.Contains(w) && w.Baseline < bottom).ToList()));
        return result;
    }

    /// <summary>
    /// Statements printed for two-sided paper alternate their margins, so the same table sits
    /// further left on some pages (TD's page 3 is 47pt left of page 1). Amount columns are found
    /// by x across all pages, so shift each page to put its date column where the first one's is.
    /// </summary>
    private static List<(int Page, List<List<StatementWord>> Lines)> AlignPages(List<(int Page, List<List<StatementWord>> Lines)> pages)
    {
        double? DateColumn(List<List<StatementWord>> lines)
        {
            var lefts = lines.Where(StartsWithDate).Select(l => l[0].Left).Order().ToList();
            return lefts.Count > 0 ? lefts[lefts.Count / 2] : null;
        }

        var reference = pages.Select(p => DateColumn(p.Lines)).FirstOrDefault(x => x is not null);
        if (reference is null) return pages;

        return pages.Select(p =>
        {
            var shift = reference.Value - (DateColumn(p.Lines) ?? reference.Value);
            return Math.Abs(shift) < 0.5
                ? p
                : (p.Page, p.Lines.Select(l => l.Select(w => w with { Left = w.Left + shift, Right = w.Right + shift }).ToList()).ToList());
        }).ToList();
    }

    private static bool StartsWithDate(List<StatementWord> line)
    {
        for (var n = Math.Min(3, line.Count); n >= 1; n--)
        {
            if (DateParser.TryParse(string.Join(' ', line.Take(n).Select(w => w.Text)), DateOrder.Auto, new DateOnly(2000, 6, 15), out _)) return true;
        }

        return false;
    }

    private static List<double> Distinct(IEnumerable<double> values, double tolerance)
    {
        var result = new List<double>();
        foreach (var v in values.Order())
        {
            if (result.Count == 0 || v - result[^1] > tolerance) result.Add(v);
        }

        return result;
    }

    private static DateOnly? LatestFullDate(IEnumerable<List<StatementWord>> lines, DateOnly today)
    {
        // A far-future date is a promotion's expiry, not the statement period.
        var limit = today.AddDays(60);
        DateOnly? latest = null;
        foreach (var line in lines)
        {
            for (var i = 0; i < line.Count; i++)
            {
                for (var n = 1; n <= 3 && i + n <= line.Count; n++)
                {
                    var text = string.Join(' ', line.Skip(i).Take(n).Select(w => w.Text)).TrimEnd(',', '.', ';', ')').TrimStart('(');
                    if (HasYear(text, out var date) && date <= limit && (latest is null || date > latest))
                    {
                        latest = date;
                    }
                }
            }
        }

        return latest;
    }

    /// <summary>True when the text is a date that states its year: the year doesn't depend on the reference date.</summary>
    private static bool HasYear(string text, out DateOnly date) =>
        DateParser.TryParse(text, DateOrder.Auto, new DateOnly(2000, 6, 15), out date)
        && DateParser.TryParse(text, DateOrder.Auto, new DateOnly(2100, 6, 15), out var other)
        && date == other;

    private static Line Analyse(List<StatementWord> words, int document, int page, DateOnly reference)
    {
        var size = words.Select(w => w.Size).Order().ElementAt(words.Count / 2);

        // Up to two leading dates: transaction and posting.
        var dates = new List<string>();
        var used = 0;
        while (dates.Count < 2 && used < words.Count)
        {
            var taken = false;
            for (var n = Math.Min(3, words.Count - used); n >= 1; n--)
            {
                var candidate = string.Join(' ', words.Skip(used).Take(n).Select(w => w.Text));
                if (DateParser.TryParse(candidate, DateOrder.Auto, reference, out var date))
                {
                    // Write an inferred year out, so the importer doesn't infer it again from today.
                    dates.Add(HasYear(candidate, out _) ? candidate : date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    used += n;
                    taken = true;
                    break;
                }
            }

            if (!taken) break;
        }

        return new Line(document, page, words[0].Baseline, size, words, dates, used, TrailingAmounts(words, used, size));
    }

    /// <summary>Picks amounts off the end of a line, each in its own cell (separated by a wide gap).</summary>
    private static List<Money> TrailingAmounts(List<StatementWord> words, int firstAllowed, double size)
    {
        var cells = new List<(int Start, int End)>();
        var start = 0;
        for (var i = 1; i <= words.Count; i++)
        {
            if (i == words.Count || words[i].Left - words[i - 1].Right > CellGap * size)
            {
                cells.Add((start, i));
                start = i;
            }
        }

        var amounts = new List<Money>();
        for (var c = cells.Count - 1; c >= 0 && amounts.Count < 4; c--)
        {
            var (s, e) = cells[c];
            s = Math.Max(s, firstAllowed);
            if (s >= e) break;

            // The longest run of trailing words that reads as one amount: "1 234,56 $", "12.50 CR".
            var found = -1;
            for (var k = Math.Max(s, e - 3); k < e; k++)
            {
                if (IsAmount(words, k, e))
                {
                    found = k;
                    break;
                }
            }

            if (found < 0) break;
            var last = e - 1;
            while (last > found && AmountSuffixes.Contains(words[last].Text)) last--;
            amounts.Insert(0, new Money(found, string.Join(' ', words.Skip(found).Take(e - found).Select(w => w.Text)), words[e - 1].Right, words[last].Right));
            if (found > s) break; // the rest of the cell is description
        }

        return amounts;
    }

    private static bool IsAmount(List<StatementWord> words, int from, int to)
    {
        // Extra words must be a suffix or a French thousands group ("234,56"), not separate numbers.
        for (var i = from + 1; i < to; i++)
        {
            var w = words[i].Text;
            var group = w.Length >= 3 && w.Take(3).All(char.IsAsciiDigit) && (w.Length == 3 || w[3] is ',' or '.');
            if (!group && !AmountSuffixes.Contains(w)) return false;
        }

        var text = string.Join(' ', words.Skip(from).Take(to - from).Select(w => w.Text));
        return AmountParser.TryParse(text, out var amount) && amount.LooksLikeMoney;
    }

    // ---------------------------------------------------------------- columns

    /// <summary>
    /// Some statements line up numbers and print a marker past the column ("13.00 CR" at BMO), so
    /// the marked amounts' right edges would make a column of their own. When the marked amounts'
    /// numbers end where the unmarked amounts do more often than the whole amounts do, line them
    /// up by the number instead.
    /// </summary>
    private static List<Line> AlignMarkedAmounts(List<Line> lines)
    {
        var dated = lines.Where(l => l.Dates.Count > 0 && l.Amounts.Count > 0).ToList();
        if (dated.Count == 0) return lines;

        var tolerance = ColumnTolerance * dated.Select(l => l.Size).Order().ElementAt(dated.Count / 2);
        var all = dated.SelectMany(l => l.Amounts).ToList();
        var plain = all.Where(a => a.NumberRight == a.Right).Select(a => a.Right).ToList();
        var marked = all.Where(a => a.NumberRight != a.Right).ToList();
        if (plain.Count == 0 || marked.Count == 0) return lines;

        bool Aligned(double right) => plain.Any(p => Math.Abs(p - right) <= tolerance);
        if (marked.Count(a => Aligned(a.NumberRight)) <= marked.Count(a => Aligned(a.Right))) return lines;

        return lines.Select(l => l with { Amounts = l.Amounts.Select(a => a with { Right = a.NumberRight }).ToList() }).ToList();
    }

    private sealed record Column(double Left, double MinRight, double MaxRight, double Tolerance, int Count);

    /// <summary>
    /// Finds the amount columns from the rows that start with a date: their amounts' right edges
    /// cluster where the statement's columns are, since numbers are printed right-aligned. An
    /// amount that ends a description ("Goods and Services Tax, HST.13 X $70.00") is not a column:
    /// other rows' descriptions run through where it sits, which they never do through a column.
    /// </summary>
    private static List<Column> FindAmountColumns(List<Line> lines)
    {
        var dated = lines.Where(l => l.Dates.Count > 0 && l.Amounts.Count > 0).ToList();
        if (dated.Count == 0) return [];

        var size = dated.Select(l => l.Size).Order().ElementAt(dated.Count / 2);
        var tolerance = ColumnTolerance * size;
        // A lone amount in its own spot is noise (a note, a misprint), unless the statement is tiny.
        var seeds = Cluster(dated, tolerance, dated.Count >= 4 ? 2 : 1);

        // Rows without a date (banks that print it once per day) can be the only ones using a
        // column, say deposits. Count every line that shares at least one column with the dated rows.
        var rows = lines.Where(l => l.Amounts.Any(a => ColumnOf(seeds, a.Right) >= 0)).ToList();
        return Cluster(rows, tolerance, 1).Where(c => c.Count > rows.Count(l => DescriptionCrosses(l, c))).ToList();
    }

    /// <summary>True when a word of the line that is neither a date nor an amount overlaps the column.</summary>
    private static bool DescriptionCrosses(Line line, Column column)
    {
        var amountWords = line.Amounts.SelectMany(a => Enumerable.Range(a.FirstWord, a.Text.Split(' ').Length)).ToHashSet();
        for (var i = line.DateWords; i < line.Words.Count; i++)
        {
            if (!amountWords.Contains(i) && line.Words[i].Right > column.Left && line.Words[i].Left < column.MaxRight) return true;
        }

        return false;
    }

    private static List<Column> Cluster(List<Line> lines, double tolerance, int minimum)
    {
        var edges = lines
            .SelectMany(l => l.Amounts.Select(a => (a.Right, Left: l.Words[a.FirstWord].Left)))
            .OrderBy(e => e.Right)
            .ToList();

        var clusters = new List<List<(double Right, double Left)>>();
        foreach (var edge in edges)
        {
            if (clusters.Count > 0 && edge.Right - clusters[^1][^1].Right <= tolerance) clusters[^1].Add(edge);
            else clusters.Add([edge]);
        }

        return clusters
            .Where(c => c.Count >= minimum)
            .Select(c => new Column(c.Min(e => e.Left), c.Min(e => e.Right), c.Max(e => e.Right), tolerance, c.Count))
            .ToList();
    }

    private static int ColumnOf(List<Column> columns, double right)
    {
        var best = -1;
        var distance = double.MaxValue;
        for (var i = 0; i < columns.Count; i++)
        {
            var c = columns[i];
            var d = right >= c.MinRight && right <= c.MaxRight ? 0 : Math.Min(Math.Abs(right - c.MinRight), Math.Abs(right - c.MaxRight));
            if (d <= c.Tolerance && d < distance)
            {
                (best, distance) = (i, d);
            }
        }

        return best;
    }

    // ---------------------------------------------------------------- rows

    private sealed class Row(List<string> dates, int columnCount)
    {
        public List<string> Dates { get; } = dates;
        public StringBuilder Description { get; } = new();
        public string[] Amounts { get; } = Enumerable.Repeat(string.Empty, columnCount).ToArray();
    }

    private static List<Row> BuildRows(List<Line> lines, List<Column> columns)
    {
        var rows = new List<Row>();
        Row? last = null;
        Line? previous = null;          // the line last added to a row
        double descriptionLeft = 0;     // where the last row's description starts
        List<string>? dates = null;     // carried forward: some banks print the date once per day
        (Line Line, string Text)? pending = null; // a dated line whose amounts are on the next line
        var document = -1;

        foreach (var line in lines)
        {
            if (line.Document != document)
            {
                (document, last, previous, dates, pending) = (line.Document, null, null, null, null);
            }

            // Amounts count only if they sit in a column; the rest of the line is description.
            var amounts = new List<(int Column, Money Money)>();
            for (var i = line.Amounts.Count - 1; i >= 0; i--)
            {
                var column = ColumnOf(columns, line.Amounts[i].Right);
                if (column < 0 || amounts.Any(a => a.Column == column)) break;
                amounts.Insert(0, (column, line.Amounts[i]));
            }

            var descriptionEnd = amounts.Count > 0 ? amounts[0].Money.FirstWord : line.Words.Count;
            var description = line.TextBetween(line.DateWords, descriptionEnd);
            // Directly below: a page's regions come one after another, so the next line can be higher up.
            bool Below(Line above) => above.Page == line.Page && above.Baseline > line.Baseline && above.Baseline - line.Baseline <= 1.8 * line.Size;
            var follows = previous is { } p && Below(p);

            if (amounts.Count > 0)
            {
                List<string> rowDates;
                var prefix = string.Empty;
                if (line.Dates.Count > 0 && IsSummaryOnly(description))
                {
                    // "Opening balance", "Closing totals": dated like a transaction, but not one.
                    (last, previous, pending, dates) = (null, null, null, line.Dates);
                    continue;
                }

                if (line.Dates.Count > 0)
                {
                    rowDates = line.Dates;
                }
                else if (pending is { } pend && Below(pend.Line))
                {
                    (rowDates, prefix) = (pend.Line.Dates, pend.Text);
                }
                else if (dates is not null && description.Length > 0 && !IsSummary(description))
                {
                    rowDates = dates;
                }
                else
                {
                    (last, previous, pending) = (null, null, null);
                    continue;
                }

                last = new Row(rowDates, columns.Count);
                last.Description.Append(string.Join(' ', new[] { prefix, description }.Where(s => s.Length > 0)));
                foreach (var (column, money) in amounts) last.Amounts[column] = money.Text;
                rows.Add(last);

                descriptionLeft = line.DateWords < line.Words.Count ? line.Words[line.DateWords].Left : line.Words[0].Left;
                (dates, previous, pending) = (rowDates, line, null);
            }
            else if (line.Dates.Count > 0)
            {
                // A date with no amounts: either the start of a row that continues on the next line,
                // or a heading for the day's transactions.
                (pending, dates, last, previous) = (description.Length > 0 ? (line, description) : null, line.Dates, null, null);
            }
            else if (last is not null && follows && line.Dates.Count == 0 && line.Words[0].Left >= descriptionLeft - line.Size)
            {
                // A description that wraps onto the next line, indented under the first.
                last.Description.Append(' ').Append(description);
                previous = line;
            }
            else
            {
                (last, previous, pending) = (null, null, null);
            }
        }

        return rows;
    }

    private static bool IsSummary(string description)
    {
        var words = TextNormalizer.ForMatching(description).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Any(SummaryWords.Contains);
    }

    /// <summary>A description made only of summary words ("Closing totals"), unlike "BALANCE TRANSFER".</summary>
    private static bool IsSummaryOnly(string description)
    {
        var words = TextNormalizer.ForMatching(description).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Any(SummaryWords.Contains) && words.All(w => SummaryWords.Contains(w) || SummaryFillers.Contains(w));
    }

    // ---------------------------------------------------------------- output

    /// <summary>
    /// Finds the table's column headings so the importer knows withdrawals from deposits. Returns
    /// one heading per amount column, or null unless every column has a recognisable heading.
    /// </summary>
    private static string[]? FindHeader(List<Line> lines, List<Column> columns)
    {
        foreach (var line in lines)
        {
            if (line.Dates.Count > 0) continue;

            var cells = Cells(line);
            if (cells.Count(c => PasteParser.RoleFromHeader(c.Text) != ColumnRole.Ignore) < 2) continue;

            var headings = new string[columns.Count];
            for (var i = 0; i < columns.Count; i++)
            {
                var column = columns[i];
                var cell = cells
                    .Select(c => (c.Text, Overlap: Math.Min(c.Right, column.MaxRight + column.Tolerance) - Math.Max(c.Left, column.Left - column.Tolerance)))
                    .Where(c => c.Overlap > 0 && PasteParser.RoleFromHeader(c.Text) is ColumnRole.Debit or ColumnRole.Credit or ColumnRole.Amount or ColumnRole.Balance)
                    .OrderByDescending(c => c.Overlap)
                    .Select(c => c.Text)
                    .FirstOrDefault();
                if (cell is null) break;
                headings[i] = cell;
            }

            if (headings.All(h => h is not null)) return headings;
        }

        return null;
    }

    private static List<(string Text, double Left, double Right)> Cells(Line line)
    {
        var cells = new List<(string, double, double)>();
        var start = 0;
        for (var i = 1; i <= line.Words.Count; i++)
        {
            if (i == line.Words.Count || line.Words[i].Left - line.Words[i - 1].Right > CellGap * line.Size)
            {
                cells.Add((line.TextBetween(start, i), line.Words[start].Left, line.Words[i - 1].Right));
                start = i;
            }
        }

        return cells;
    }

    private static string Format(List<Row> rows, string[]? header)
    {
        var posted = rows.Any(r => r.Dates.Count > 1);
        var text = new StringBuilder();
        if (header is not null)
        {
            var headings = new List<string> { "Date" };
            if (posted) headings.Add("Posted");
            headings.Add("Description");
            headings.AddRange(header);
            text.AppendJoin('\t', headings).Append('\n');
        }

        foreach (var row in rows)
        {
            var cells = new List<string> { row.Dates[0] };
            if (posted) cells.Add(row.Dates.Count > 1 ? row.Dates[1] : string.Empty);
            cells.Add(TextNormalizer.CollapseWhitespace(row.Description.ToString().Replace('\t', ' ')));
            cells.AddRange(row.Amounts);
            text.AppendJoin('\t', cells).Append('\n');
        }

        return text.ToString();
    }
}
