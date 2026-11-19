using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MapleLedger.Core.RealEstate;

/// <summary>
/// One region's house price index in the feed: its name, the places it's known by, and its index
/// for each month from <see cref="From"/> (null for a month without one).
/// </summary>
/// <param name="Kind">"country", "province" or "area".</param>
/// <param name="Places">Cities and towns the region covers besides its name, e.g. Oakville and Milton.</param>
public sealed record HpiRegion(string Id, string Name, string Kind, string? Province, IReadOnlyList<string> Places, DateOnly From, IReadOnlyList<decimal?> Index)
{
    /// <summary>The last month with an index.</summary>
    public DateOnly? Through => Index.Select((v, i) => (v, i)).LastOrDefault(p => p.v is > 0) is { v: not null } last ? From.AddMonths(last.i) : null;

    /// <summary>The months that have an index, as readings for <paramref name="area"/>.</summary>
    public IEnumerable<HpiReading> Readings(string area) =>
        Index.Select((v, i) => (v, i)).Where(p => p.v is > 0).Select(p => new HpiReading(area, From.AddMonths(p.i), p.v!.Value));

    /// <summary>The name, with its province when it's an area: "Oakville Milton, ON".</summary>
    public string Label => Kind == "area" && Province is { Length: > 0 } p ? $"{Name}, {p}" : Name;
}

/// <summary>Every region in a feed document, and what it says about itself.</summary>
/// <param name="Through">The newest month any region has an index for.</param>
public sealed class HpiBook(IReadOnlyList<HpiRegion> regions, string? source, string? index, DateOnly? through)
{
    public static HpiBook Empty { get; } = new([], null, null, null);

    public IReadOnlyList<HpiRegion> Regions { get; } = regions;
    public string? Source { get; } = source;

    /// <summary>Which index the feed carries, e.g. "CREA MLS® Home Price Index, composite".</summary>
    public string? IndexName { get; } = index;

    public DateOnly? Through { get; } = through;
    public int Count => Regions.Count;

    /// <summary>
    /// The region a property's city or area belongs to, or null when it can't be told. In order:
    /// the region's name ("Oakville Milton", "Ontario"), its name without words like "and District"
    /// or "CMA" ("Montréal"), then a place it lists ("Oakville"). Case, accents, punctuation and a
    /// trailing province (", ON") don't matter. A name two regions share matches neither.
    /// </summary>
    public HpiRegion? Find(string? cityOrArea)
    {
        var key = Key(cityOrArea);
        if (key.Length == 0) return null;

        return One(Regions.Where(r => Key(r.Name) == key))
            ?? One(Regions.Where(r => Short(Key(r.Name)) == key))
            ?? One(Regions.Where(r => r.Places.Any(p => Key(p) == key)));

        static HpiRegion? One(IEnumerable<HpiRegion> matches)
        {
            var list = matches.Take(2).ToList();
            return list.Count == 1 ? list[0] : null;
        }
    }

    private static readonly string[] Suffixes = [" and district", " and area", " district", " region", " area", " cma"];

    private static string Short(string key)
    {
        foreach (var suffix in Suffixes)
        {
            if (key.EndsWith(suffix, StringComparison.Ordinal)) return key[..^suffix.Length];
        }

        return key;
    }

    private static readonly HashSet<string> ProvinceCodes = ["ab", "bc", "mb", "nb", "nl", "ns", "nt", "nu", "on", "pe", "qc", "sk", "yt"];

    /// <summary>Lower case without accents or punctuation, and without a trailing province code.</summary>
    internal static string Key(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (c is '\'' or '’') continue; // St. John's
            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        var words = builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 1 && ProvinceCodes.Contains(words[^1])) words.RemoveAt(words.Count - 1);
        return string.Join(' ', words);
    }
}

/// <summary>
/// Reads ownquity.com's house price index feed (version 1): every region's CREA MLS® Home Price
/// Index, month by month, in one document. LoonieBooks downloads all of it and finds a property's
/// region here, so where the properties are is never sent.
/// </summary>
public static class OwnquityHpi
{
    public const string DefaultUrl = "https://ownquity.com/data/hpi/v1/all.json";

    /// <summary>A series longer than this (a century of months) isn't the feed.</summary>
    public const int MaxMonths = 1200;

    /// <summary>
    /// The regions in a feed document. Members are found by name, so one added later doesn't break
    /// this. A region without an id, a name, a first month or an index is skipped, and so is a
    /// month whose index isn't a positive number; a document that isn't the feed gives an empty book.
    /// </summary>
    public static HpiBook Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("v", out var v) || !v.TryGetInt32(out var version) || version != 1
                || !root.TryGetProperty("regions", out var rows) || rows.ValueKind != JsonValueKind.Array)
            {
                return HpiBook.Empty;
            }

            var regions = new List<HpiRegion>();
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                if (Text(row, "id") is not { } id || Text(row, "name") is not { } name) continue;
                if (Month(Text(row, "from")) is not { } from) continue;
                if (!row.TryGetProperty("hpi", out var hpi) || hpi.ValueKind != JsonValueKind.Array || hpi.GetArrayLength() is 0 or > MaxMonths) continue;

                var index = hpi.EnumerateArray()
                    .Select(x => x.ValueKind == JsonValueKind.Number && x.TryGetDecimal(out var d) && d is > 0 and <= HpiReading.MaxIndex ? d : (decimal?)null)
                    .ToList();
                if (index.All(x => x is null)) continue;

                var places = row.TryGetProperty("places", out var p) && p.ValueKind == JsonValueKind.Array
                    ? p.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0).ToList()
                    : [];
                regions.Add(new HpiRegion(id, name, Text(row, "kind") ?? "area", Text(row, "province"), places, from, index));
            }

            var through = regions.Select(r => r.Through).Max();
            return new HpiBook(regions, Text(root, "source"), Text(root, "index"), through);
        }
        catch (JsonException)
        {
            return HpiBook.Empty;
        }
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && p.GetString()?.Trim() is { Length: > 0 } s ? s : null;

    private static DateOnly? Month(string? yyyyMm) =>
        DateOnly.TryParseExact(yyyyMm + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d.Year >= 1900 ? d : null;
}

/// <summary>An area's index, month by month from <see cref="From"/>, to save as its readings.</summary>
/// <param name="Area">The property's city or area as entered: the readings are kept under it.</param>
/// <param name="Index">One value per month from <see cref="From"/>; null for a month without one.</param>
public sealed record HpiSeries(string Area, DateOnly From, IReadOnlyList<decimal?> Index);

/// <summary>
/// Areas whose readings are to be the series given, replacing whatever readings each had (an area
/// carried by the feed is never a mix of the feed's index and one typed in).
/// </summary>
public sealed record ApplyHpiRequest(IReadOnlyList<HpiSeries> Series)
{
    public const int MaxSeries = 50;
}

/// <param name="Areas">Areas whose readings changed.</param>
/// <param name="Readings">Readings added, changed or removed.</param>
public sealed record ApplyHpiResult(int Areas, int Readings);
