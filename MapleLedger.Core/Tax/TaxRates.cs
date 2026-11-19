namespace MapleLedger.Core.Tax;

/// <summary>A tax rate that applies to income above <see cref="From"/>, up to the next bracket.</summary>
public sealed record TaxBracket(decimal From, decimal Rate);

/// <summary>
/// The basic personal amount falls in a straight line from the full amount at net income
/// <see cref="From"/> to <see cref="Minimum"/> at <see cref="To"/> (the federal amount for high
/// incomes, Manitoba's to zero).
/// </summary>
public sealed record PersonalAmountPhaseOut(decimal From, decimal To, decimal Minimum);

/// <summary>
/// The rates and credit amounts of one jurisdiction (federal, or a province or territory) for one
/// year. Credit amounts are multiplied by <see cref="CreditRate"/>; dividend credits are a share of
/// the grossed-up (taxable) dividend.
/// </summary>
/// <param name="DonationTopRate">
/// The higher donation credit rate on gifts over $200, to the extent income is taxed in the top
/// bracket (federal 33%, BC 20.5%).
/// </param>
public sealed record JurisdictionRates(
    string Code,
    string Name,
    IReadOnlyList<TaxBracket> Brackets,
    decimal CreditRate,
    decimal BasicPersonalAmount,
    PersonalAmountPhaseOut? BasicPersonalPhaseOut,
    decimal AgeAmount,
    decimal AgeThreshold,
    decimal PensionAmount,
    decimal EmploymentAmount,
    decimal MedicalThreshold,
    decimal DonationLowRate,
    decimal DonationHighRate,
    decimal? DonationTopRate,
    decimal EligibleDividendCredit,
    decimal OtherDividendCredit)
{
    /// <summary>Tax on <paramref name="income"/> before credits.</summary>
    public decimal TaxOn(decimal income)
    {
        var tax = 0m;
        for (var i = 0; i < Brackets.Count; i++)
        {
            var from = Brackets[i].From;
            if (income <= from) break;
            var to = i + 1 < Brackets.Count ? Math.Min(income, Brackets[i + 1].From) : income;
            tax += (to - from) * Brackets[i].Rate;
        }

        return tax;
    }

    /// <summary>The rate on the next dollar of taxable income.</summary>
    public decimal RateAt(decimal income) => Brackets.Last(b => income >= b.From).Rate;

    public decimal TopBracketStart => Brackets[^1].From;

    public decimal BasicPersonalFor(decimal netIncome)
    {
        if (BasicPersonalPhaseOut is not { } phase || netIncome <= phase.From) return BasicPersonalAmount;
        if (netIncome >= phase.To) return phase.Minimum;
        return BasicPersonalAmount - (netIncome - phase.From) * (BasicPersonalAmount - phase.Minimum) / (phase.To - phase.From);
    }

    /// <summary>The age amount for someone 65 or older: reduced by 15% of net income above the threshold.</summary>
    public decimal AgeAmountFor(decimal netIncome) => Math.Max(0, AgeAmount - Math.Max(0, netIncome - AgeThreshold) * 0.15m);
}

/// <param name="MaxPensionable">Year's maximum pensionable earnings (YMPE).</param>
/// <param name="MaxAdditionalPensionable">Year's additional maximum pensionable earnings (YAMPE), the CPP2 ceiling.</param>
/// <param name="BaseRate">The employee's base rate, which earns the non-refundable credit.</param>
/// <param name="FirstAdditionalRate">The employee's first additional (enhanced) rate, which is deducted instead.</param>
/// <param name="SecondAdditionalRate">The employee's CPP2 rate, deducted.</param>
public sealed record CppRates(decimal MaxPensionable, decimal MaxAdditionalPensionable, decimal BasicExemption, decimal BaseRate, decimal FirstAdditionalRate, decimal SecondAdditionalRate)
{
    public decimal EmployeeRate => BaseRate + FirstAdditionalRate;
}

public sealed record EiRates(decimal MaxInsurable, decimal Rate);

/// <summary>Ontario's surtax, tax reduction and Health Premium.</summary>
public sealed record OntarioRules(decimal SurtaxThreshold1, decimal SurtaxRate1, decimal SurtaxThreshold2, decimal SurtaxRate2, decimal BasicReduction);

/// <summary>BC's tax reduction for low incomes: the full amount up to the threshold, less a share of net income above it.</summary>
public sealed record BcReduction(decimal Amount, decimal Threshold, decimal Rate);

public sealed record TaxYearRates(
    int Year,
    JurisdictionRates Federal,
    IReadOnlyList<JurisdictionRates> Provinces,
    CppRates Cpp,
    EiRates Ei,
    decimal OasRecoveryThreshold,
    OntarioRules Ontario,
    BcReduction BritishColumbia)
{
    public const decimal CapitalGainsInclusion = 0.5m;
    public const decimal EligibleDividendGrossUp = 1.38m;
    public const decimal OtherDividendGrossUp = 1.15m;
    public const decimal OasRecoveryRate = 0.15m;
    public const decimal HomeAccessibilityLimit = 20_000m;
    public const decimal EducatorSuppliesLimit = 1_000m;
    public const decimal EducatorSuppliesRate = 0.25m;
    public const decimal MultigenerationalLimit = 50_000m;

    /// <summary>Gifts can be claimed up to 75% of net income.</summary>
    public const decimal DonationIncomeLimit = 0.75m;

    public JurisdictionRates? Province(string? code) => Provinces.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Federal and provincial rates for the years LoonieBooks can estimate. Quebec files its own
/// return and isn't included.
/// </summary>
/// <remarks>
/// 2026 sources: CRA "Canadian income tax rates for individuals" (brackets), T4127 Payroll
/// Deductions Formulas 122nd and 123rd editions (CPP, EI, Ontario and BC rules, full-year BC
/// 5.60%, NL $13,094 and PE 20% top bracket), CRA indexation page (federal amounts), and
/// TaxTips.ca 2026 tables (provincial credit amounts, donation and dividend credit rates).
/// </remarks>
public static class TaxRates
{
    private static readonly Dictionary<int, TaxYearRates> ByYear = new() { [2026] = Rates2026() };

    public static IReadOnlyCollection<int> Years => ByYear.Keys;

    public static TaxYearRates? For(int year) => ByYear.GetValueOrDefault(year);

    /// <summary>RRSP room is 18% of the previous year's earned income, up to the year's dollar limit.</summary>
    public const decimal RrspEarnedIncomeRate = 0.18m;

    /// <summary>
    /// Net tax owing above which CRA expects quarterly instalments, when the year before or the one
    /// before that was also above it ($1,800 in Quebec, which LoonieBooks doesn't estimate).
    /// </summary>
    public const decimal InstalmentThreshold = 3_000m;

    // CRA "MP, DB, RRSP, DPSP, ALDA, TFSA limits, YMPE and the YAMPE".
    private static readonly Dictionary<int, decimal> RrspDollarLimits = new()
    {
        [2024] = 31_560m,
        [2025] = 32_490m,
        [2026] = 33_810m,
        [2027] = 35_390m,
    };

    /// <summary>The RRSP dollar limit for a year, when CRA has published it.</summary>
    public static decimal? RrspDollarLimit(int year) => RrspDollarLimits.TryGetValue(year, out var limit) ? limit : null;

    /// <summary>Province and territory codes in name order, for pickers.</summary>
    public static IReadOnlyList<(string Code, string Name)> ProvinceNames { get; } =
        Rates2026().Provinces.OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => (p.Code, p.Name)).ToList();

    public static bool IsProvince(string? code) => ProvinceNames.Any(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<TaxBracket> B(params (decimal From, decimal RatePercent)[] brackets) =>
        brackets.Select(b => new TaxBracket(b.From, b.RatePercent / 100m)).ToList();

    private static TaxYearRates Rates2026()
    {
        var federalPhaseOut = new PersonalAmountPhaseOut(181_440m, 258_482m, 14_829m);
        var federal = new JurisdictionRates("CA", "Federal",
            B((0, 14), (58_523, 20.5m), (117_045, 26), (181_440, 29), (258_482, 33)),
            CreditRate: 0.14m, BasicPersonalAmount: 16_452m, federalPhaseOut,
            AgeAmount: 9_208m, AgeThreshold: 46_432m, PensionAmount: 2_000m, EmploymentAmount: 1_501m,
            MedicalThreshold: 2_890m, DonationLowRate: 0.14m, DonationHighRate: 0.29m, DonationTopRate: 0.33m,
            EligibleDividendCredit: 0.150198m, OtherDividendCredit: 0.090301m);

        JurisdictionRates P(string code, string name, IReadOnlyList<TaxBracket> brackets, decimal creditPercent, decimal bpa,
            decimal age, decimal ageThreshold, decimal pension, decimal medical, decimal donationLowPercent, decimal donationHighPercent,
            decimal eligiblePercent, decimal otherPercent, PersonalAmountPhaseOut? phaseOut = null, decimal employment = 0,
            decimal? donationTopPercent = null) =>
            new(code, name, brackets, creditPercent / 100m, bpa, phaseOut, age, ageThreshold, pension, employment, medical,
                donationLowPercent / 100m, donationHighPercent / 100m, donationTopPercent / 100m, eligiblePercent / 100m, otherPercent / 100m);

        List<JurisdictionRates> provinces =
        [
            // Alberta's 8% bracket is new for 2025; credits use 8% (a supplemental credit for very
            // large credit claims isn't modelled). The first $200 of gifts earns 60%.
            P("AB", "Alberta", B((0, 8), (61_200, 10), (154_259, 12), (185_111, 13), (246_813, 14), (370_220, 15)),
                8, 22_769, 6_345, 47_234, 1_753, 2_942, 60, 21, 8.12m, 2.18m),
            // BC raised its lowest rate to 5.60% for all of 2026 (Budget 2026).
            P("BC", "British Columbia", B((0, 5.6m), (50_363, 7.7m), (100_728, 10.5m), (115_648, 12.29m), (140_430, 14.7m), (190_405, 16.8m), (265_545, 20.5m)),
                5.6m, 13_216, 5_927, 44_119, 1_000, 2_748, 5.6m, 16.8m, 12, 1.96m, donationTopPercent: 20.5m),
            // Manitoba froze indexation, and phases its basic personal amount out between $200,000 and $400,000.
            P("MB", "Manitoba", B((0, 10.8m), (47_000, 12.75m), (100_000, 17.4m)),
                10.8m, 15_780, 3_728, 27_749, 1_000, 1_728, 10.8m, 17.4m, 8, 0.7835m, new PersonalAmountPhaseOut(200_000m, 400_000m, 0m)),
            P("NB", "New Brunswick", B((0, 9.4m), (52_333, 14), (104_666, 16), (193_861, 19.5m)),
                9.4m, 13_664, 6_158, 45_844, 1_000, 2_854, 9.4m, 17.95m, 14, 2.75m),
            // NL's 2026 budget raised the basic personal amount to $13,094 for the year.
            P("NL", "Newfoundland and Labrador", B((0, 8.7m), (44_678, 14.5m), (89_354, 15.8m), (159_528, 17.8m), (223_340, 19.8m), (285_319, 20.8m), (570_638, 21.3m), (1_141_275, 21.8m)),
                8.7m, 13_094, 7_142, 39_138, 1_000, 2_437, 8.7m, 21.8m, 6.3m, 3.2m),
            P("NS", "Nova Scotia", B((0, 8.79m), (30_995, 14.95m), (61_991, 16.67m), (97_417, 17.5m), (157_124, 21)),
                8.79m, 11_932, 5_826, 30_828, 1_173, 1_637, 8.79m, 21, 8.85m, 1.5m),
            P("NT", "Northwest Territories", B((0, 5.9m), (53_003, 8.6m), (106_009, 12.2m), (172_346, 14.05m)),
                5.9m, 18_198, 8_902, 46_432, 1_000, 2_890, 5.9m, 14.05m, 11.5m, 6),
            P("NU", "Nunavut", B((0, 4), (55_801, 7), (111_602, 9), (181_439, 11.5m)),
                4, 19_659, 12_550, 46_432, 2_000, 2_890, 4, 11.5m, 5.51m, 2.61m),
            P("ON", "Ontario", B((0, 5.05m), (53_891, 9.15m), (107_785, 11.16m), (150_000, 12.16m), (220_000, 13.16m)),
                5.05m, 12_989, 6_342, 47_210, 1_796, 2_940, 5.05m, 11.16m, 10, 2.9863m),
            // PEI added a 20% bracket over $200,000 for 2026; its credits stay at 9.65%.
            P("PE", "Prince Edward Island", B((0, 9.5m), (33_928, 13.47m), (65_820, 16.6m), (106_890, 17.62m), (142_520, 19), (200_000, 20)),
                9.65m, 15_000, 6_510, 36_600, 1_000, 1_678, 9.5m, 19, 10.5m, 1.3m),
            P("SK", "Saskatchewan", B((0, 10.5m), (54_532, 12.5m), (155_805, 14.5m)),
                10.5m, 20_381, 5_901, 43_927, 1_000, 2_736, 10.5m, 14.5m, 11, 2.519m),
            // Yukon follows the federal basic personal amount and Canada employment amount.
            P("YT", "Yukon", B((0, 6.4m), (58_523, 9), (117_045, 10.9m), (181_440, 12.8m), (500_000, 15)),
                6.4m, 16_452, 9_208, 46_432, 2_000, 2_890, 6.4m, 12.8m, 12.02m, 0.67m, federalPhaseOut, employment: 1_501),
        ];

        return new TaxYearRates(
            2026,
            federal,
            provinces,
            new CppRates(MaxPensionable: 74_600m, MaxAdditionalPensionable: 85_000m, BasicExemption: 3_500m, BaseRate: 0.0495m, FirstAdditionalRate: 0.01m, SecondAdditionalRate: 0.04m),
            new EiRates(MaxInsurable: 68_900m, Rate: 0.0163m),
            OasRecoveryThreshold: 95_323m,
            new OntarioRules(5_818m, 0.20m, 7_446m, 0.36m, BasicReduction: 300m),
            new BcReduction(690m, 25_570m, 0.0356m));
    }
}
