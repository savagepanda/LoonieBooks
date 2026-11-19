using System.Globalization;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using MapleLedger.Core;

namespace MapleLedger.ApiService.Receipts;

/// <summary>
/// Reads a receipt photo or PDF with Claude, for premium users who opt in per receipt. Far more
/// reliable than in-browser OCR on crumpled or faded receipts, but the file is sent to Anthropic's
/// API, so the browser asks first. Registered only when an Anthropic API key is configured.
/// </summary>
public sealed class ClaudeReceiptReader(AnthropicClient client, ILogger<ClaudeReceiptReader> logger)
{
    public const string Model = "claude-opus-5-5";

    /// <summary>The formats Claude reads: photos (no HEIC) and PDFs.</summary>
    public static IReadOnlyList<string> ContentTypes { get; } = ["image/jpeg", "image/png", "image/webp", "application/pdf"];

    private const string Instructions =
        "You read shopping receipts for a personal bookkeeping app. Report what the receipt shows; never guess. " +
        "Use null for anything that isn't legible.";

    // Structured output: the response is always this JSON object.
    private static readonly Dictionary<string, JsonElement> Schema = new()
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["properties"] = JsonSerializer.SerializeToElement(new
        {
            total = new
            {
                anyOf = new object[] { new { type = "number" }, new { type = "null" } },
                description = "The final amount paid, including tax (and tip if printed), as a plain number such as 24.72.",
            },
            date = new
            {
                anyOf = new object[] { new { type = "string" }, new { type = "null" } },
                description = "The purchase date as YYYY-MM-DD.",
            },
            merchant = new
            {
                anyOf = new object[] { new { type = "string" }, new { type = "null" } },
                description = "The store or business name as printed at the top of the receipt.",
            },
        }),
        ["required"] = JsonSerializer.SerializeToElement(new[] { "total", "date", "merchant" }),
        ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
    };

    public async Task<ReceiptFields> ReadAsync(string contentType, byte[] content, DateOnly today, CancellationToken ct)
    {
        var data = Convert.ToBase64String(content);
        BetaContentBlockParam file = contentType == "application/pdf"
            ? new BetaRequestDocumentBlock { Source = new BetaBase64PdfSource { Data = data } }
            : new BetaImageBlockParam { Source = new BetaBase64ImageSource { Data = data, MediaType = contentType } };

        var response = await client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = Model,
            MaxTokens = 4096,
            // A refused request is re-served by a fallback model chosen for the refusal's category.
            Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
            Fallbacks = new Default(),
            // Reading three fields off a receipt needs little reasoning.
            OutputConfig = new BetaOutputConfig { Effort = Effort.Low, Format = new BetaJsonOutputFormat { Schema = Schema } },
            System = Instructions,
            Messages =
            [
                new BetaUserMessageParam
                {
                    Content = new List<BetaContentBlockParam>
                    {
                        file,
                        new BetaTextBlockParam
                        {
                            Text = $"Read this receipt. Today is {today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}, so the receipt is from today or earlier.",
                        },
                    },
                },
            ],
        }, ct);

        if (response.StopReason == "refusal" || response.StopReason == "max_tokens")
        {
            logger.LogWarning("Claude did not read the receipt (stop reason {StopReason}).", response.StopReason);
            return new ReceiptFields(null, null, null);
        }

        var json = string.Concat(response.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text));
        return Parse(json, today);
    }

    /// <summary>
    /// Turns the model's JSON into fields, dropping anything implausible: a total of zero or
    /// absurdly large, a date in the future or long past.
    /// </summary>
    public static ReceiptFields Parse(string json, DateOnly today)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            decimal? total = root.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetDecimal(out var amount)
                && amount is > 0 and < 10_000_000 ? Math.Round(amount, 2) : null;

            DateOnly? date = root.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String
                && DateOnly.TryParseExact(d.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                && day <= today.AddDays(1) && day >= today.AddYears(-10) ? day : null;

            var merchant = root.TryGetProperty("merchant", out var m) && m.ValueKind == JsonValueKind.String
                ? TextOf(m.GetString()) : null;

            return new ReceiptFields(total, date, merchant);
        }
        catch (JsonException)
        {
            return new ReceiptFields(null, null, null);
        }
    }

    private static string? TextOf(string? value)
    {
        var text = string.Join(' ', (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length == 0 ? null : text.Length > 80 ? text[..80] : text;
    }
}
