using System.Text;

namespace MapleLedger.Core.Import;

/// <param name="Text">Text for <see cref="PasteParser"/>, the same as if the user had pasted it.</param>
/// <param name="RowCount">Lines that hold a date, i.e. likely transactions.</param>
public sealed record CsvTextResult(string Text, int FileCount, int RowCount);

/// <summary>A CSV file that can't be read, with a message fit to show the user.</summary>
public sealed class CsvStatementException(string message) : Exception(message);

/// <summary>
/// Reads transaction exports saved as CSV (or tab-separated text) by a bank's website. The text goes
/// to <see cref="PasteParser"/> unchanged, so the export is read exactly like pasted CSV. When several
/// files are chosen (e.g. one per month), they're joined into one table: the first file keeps its
/// header, and each later file's title and header lines (everything above its first dated row) are
/// dropped so they don't show up as rows. Runs in the browser, so the file never leaves the device.
/// </summary>
public static class CsvStatementReader
{
    public static CsvTextResult Read(IEnumerable<StatementFile> files, DateOnly? today = null)
    {
        var reference = today ?? DateOnly.FromDateTime(DateTime.Today);
        var output = new StringBuilder();
        int fileCount = 0, rowCount = 0;
        foreach (var file in files)
        {
            var lines = Decode(file.Content)
                .Replace("\r\n", "\n").Replace('\r', '\n')
                .Split('\n')
                .Select(l => l.TrimEnd())
                .Where(l => l.Trim().Length > 0)
                .ToList();

            var dated = lines.Select(l => HasDate(l, reference)).ToList();
            var firstData = dated.IndexOf(true);
            if (firstData < 0)
            {
                throw new CsvStatementException($"No dates found in {file.Name}. Choose the transaction export (CSV) from your bank.");
            }

            foreach (var line in lines.Skip(fileCount == 0 ? 0 : firstData))
            {
                output.Append(line).Append('\n');
            }

            rowCount += dated.Count(d => d);
            fileCount++;
        }

        return new CsvTextResult(output.ToString(), fileCount, rowCount);
    }

    private static bool HasDate(string line, DateOnly reference) =>
        (line.Contains('\t') ? line.Split('\t') : PasteParser.SplitCsv(line))
            .Any(c => DateParser.TryParse(c.Trim(), DateOrder.Auto, reference, out _));

    /// <summary>
    /// Bank exports are UTF-8 (sometimes with a byte-order mark), UTF-16 when saved as "Unicode text"
    /// by Excel, or Windows-1252 from older systems. Latin-1 matches Windows-1252 for accented
    /// letters, which is what French descriptions need.
    /// </summary>
    internal static string Decode(byte[] bytes)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..]) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes is [0xFF, 0xFE, ..]) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes is [0xFE, 0xFF, ..]) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }
}
