using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace MapleLedger.Core.Receipts;

/// <summary>Text from an emailed or downloaded PDF receipt, which has real text and needs no OCR.</summary>
public static class ReceiptPdf
{
    /// <summary>The first few pages' text in reading order; empty for scanned (image-only) PDFs.</summary>
    public static string ReadText(byte[] content, int maxPages = 3)
    {
        try
        {
            using var pdf = PdfDocument.Open(content);
            return string.Join('\n', pdf.GetPages().Take(maxPages).Select(page => ContentOrderTextExtractor.GetText(page)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ""; // encrypted or damaged: the user types the details instead
        }
    }
}
