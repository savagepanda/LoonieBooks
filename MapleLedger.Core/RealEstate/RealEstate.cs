using MapleLedger.Core.Investments;
using MapleLedger.Fdx;

namespace MapleLedger.Core.RealEstate;

/// <summary>
/// A home or other real estate the household owns, with what it's worth and the loans secured by
/// it (mortgages, home equity lines), so its equity can be worked out.
/// </summary>
/// <param name="City">The city or area the property is in: the area whose house price index carries its value forward.</param>
/// <param name="Value">What it was worth at <paramref name="ValueAsOf"/> (a purchase price, an appraisal, an assessment).</param>
/// <param name="AccountIds">The loan and line-of-credit accounts secured by it.</param>
public sealed record RealEstateProperty(string Id, string Name, string? City, decimal? Value, DateOnly? ValueAsOf, IReadOnlyList<string> AccountIds)
{
    public const int MaxNameLength = 64;
    public const int MaxCityLength = 64;
    public const decimal MaxValue = 10_000_000_000m;
}

/// <param name="ValueAsOf">When the value was right; today when left out.</param>
/// <param name="AccountIds">The loan and line-of-credit accounts secured by it; each is moved off any other property.</param>
public sealed record PropertyRequest(string Name, string? City, decimal? Value, DateOnly? ValueAsOf, IReadOnlyList<string>? AccountIds);

/// <summary>
/// A house price index reading for an area and month: from ownquity.com's feed of the CREA MLS®
/// Home Price Index (<see cref="OwnquityHpi"/>), or, in older ledgers, typed in. Either the index
/// or the benchmark price works, as long as one area's readings all use the same one: only their
/// ratio is used.
/// </summary>
public sealed record HpiReading(string Area, DateOnly Month, decimal Index)
{
    public const int MaxAreaLength = 64;
    public const decimal MaxIndex = 1_000_000_000m;

    public static bool SameArea(string? a, string? b) =>
        a is not null && b is not null && string.Equals(TextNormalizer.CollapseWhitespace(a.Trim()), TextNormalizer.CollapseWhitespace(b.Trim()), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// What a property is estimated to be worth. With <see cref="From"/> and <see cref="To"/>, its
/// value was carried by its area's house price index: value × To / From. To is usually after From
/// (the value carried forward to today), but is before it for a month before the value's date.
/// </summary>
public sealed record PropertyEstimate(decimal Value, DateOnly AsOf, HpiReading? From = null, HpiReading? To = null)
{
    public bool ByIndex => From is not null && To is not null;

    /// <summary>The index's change from the value's month to <see cref="To"/>'s, e.g. 0.12 for 12% up.</summary>
    public decimal? IndexChange => ByIndex ? To!.Index / From!.Index - 1 : null;
}

/// <summary>A property's estimated value, what's owed on the loans secured by it, and the difference.</summary>
/// <param name="Value">Null when the property has no value yet.</param>
/// <param name="Loans">Each loan secured by it, with its balance in Canadian dollars.</param>
public sealed record PropertyEquity(RealEstateProperty Property, PropertyEstimate? Value, IReadOnlyList<(Account Account, decimal Owed)> Loans)
{
    public decimal Owed => Loans.Sum(l => l.Owed);

    public decimal? Equity => Value is { } v ? v.Value - Owed : null;

    /// <summary>What's owed as a share of the value (loan-to-value), e.g. 0.65.</summary>
    public decimal? LoanToValue => Value is { Value: > 0 } v ? Owed / v.Value : null;
}

public static class PropertyValuation
{
    /// <summary>A name, city or area as stored: trimmed, with runs of spaces made one.</summary>
    public static string CleanText(string? text) => TextNormalizer.CollapseWhitespace((text ?? "").Trim());

    /// <summary>
    /// The property's value carried forward to the latest index reading for its area: its value
    /// times the latest reading over the reading for the month its value was set (or the closest
    /// earlier one). Without readings on both sides, its value as entered.
    /// </summary>
    public static PropertyEstimate? Estimate(RealEstateProperty property, IEnumerable<HpiReading> readings) =>
        EstimateOn(property, readings, null);

    /// <summary>
    /// What the property was worth on <paramref name="on"/> by its area's index: its value times the
    /// reading for that month (or the closest earlier one) over the reading for the month its value
    /// was set. That carries the value back as well as forward, so net worth by month follows the
    /// market. Null <paramref name="on"/> is the latest reading. Without readings on both sides, or
    /// with both in the same month, its value as entered.
    /// </summary>
    public static PropertyEstimate? EstimateOn(RealEstateProperty property, IEnumerable<HpiReading> readings, DateOnly? on)
    {
        if (property is not { Value: { } value, ValueAsOf: { } asOf }) return null;

        var area = readings.Where(r => HpiReading.SameArea(r.Area, property.City) && r.Index > 0).OrderBy(r => r.Month).ToList();
        var month = new DateOnly(asOf.Year, asOf.Month, 1);
        var from = area.LastOrDefault(r => r.Month <= month);
        var to = on is { } day ? area.LastOrDefault(r => r.Month <= new DateOnly(day.Year, day.Month, 1)) : area.LastOrDefault();
        if (from is null || to is null || to.Month == from.Month) return new PropertyEstimate(value, asOf);

        return new PropertyEstimate(Math.Round(value * to.Index / from.Index, 2, MidpointRounding.AwayFromZero), to.Month, from, to);
    }

    /// <summary>
    /// Each property's equity: its estimated value less the balances of the open accounts secured
    /// by it, in Canadian dollars at <paramref name="rates"/>.
    /// </summary>
    public static IReadOnlyList<PropertyEquity> Equities(IEnumerable<RealEstateProperty> properties, IEnumerable<HpiReading> readings, IEnumerable<Account> accounts, CurrencyRates? rates = null)
    {
        rates ??= CurrencyRates.None;
        var readingList = readings.ToList();
        var byId = accounts.Where(a => a.Status != AccountStatus.Closed).ToDictionary(a => a.AccountId);
        return properties
            .Select(p => new PropertyEquity(
                p,
                Estimate(p, readingList),
                p.AccountIds.Where(byId.ContainsKey).Select(id => (byId[id], rates.BalanceInCad(byId[id]))).ToList()))
            .ToList();
    }

    /// <summary>The properties' estimated values added up (ones without a value count as nothing).</summary>
    public static decimal TotalValue(IEnumerable<RealEstateProperty> properties, IEnumerable<HpiReading> readings) =>
        TotalValueOn(properties, readings, null);

    /// <summary>The properties' values on <paramref name="on"/> added up (see <see cref="EstimateOn"/>); null is the latest.</summary>
    public static decimal TotalValueOn(IEnumerable<RealEstateProperty> properties, IEnumerable<HpiReading> readings, DateOnly? on)
    {
        var readingList = readings.ToList();
        return properties.Sum(p => EstimateOn(p, readingList, on)?.Value ?? 0);
    }
}
