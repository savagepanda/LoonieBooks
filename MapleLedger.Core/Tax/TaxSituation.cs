namespace MapleLedger.Core.Tax;

/// <summary>
/// What one person sets for the tax estimate: where they live, and whether their employer takes
/// tax off their pay. Everything else comes from the ledger: income from Salary, Business Income
/// and Rental Income deposits, deductions and credits from tagged claims. It's for a high-level
/// picture, not for filing.
/// </summary>
/// <param name="Province">Province or territory of residence on December 31 (see <see cref="TaxRates.ProvinceNames"/>).</param>
/// <param name="EmployerWithholds">
/// Whether Salary deposits are net pay, with income tax, CPP and EI already taken off. When they
/// are, the estimate works back to the gross salary; when not, a deposit is taxed as received.
/// </param>
/// <param name="EsppPercent">Share of salary put into an employee share purchase plan through payroll, in percent (after tax).</param>
/// <param name="EsppMatchPercent">What the employer adds to the ESPP, as a percent of the person's own contributions (a taxable benefit).</param>
/// <param name="RrspPercent">Share of salary the person puts into a group RRSP through payroll, in percent.</param>
/// <param name="RrspMatchPercent">What the employer puts into the group RRSP, as a percent of salary (a taxable benefit, deducted as an RRSP contribution).</param>
/// <param name="RrspDeductionLimit">
/// The RRSP deduction limit for the year from the notice of assessment; blank to estimate it from
/// the year's pay. Only for the year it's saved for, never carried to a later year.
/// </param>
/// <param name="PensionPercent">
/// Share of salary the person puts into a registered pension plan through payroll, in percent
/// (deducted on line 20700, before tax is withheld).
/// </param>
/// <param name="PensionMatchPercent">
/// What the employer puts into the registered pension plan, as a percent of salary. Not a taxable
/// benefit, but it lowers RRSP room through the pension adjustment.
/// </param>
/// <param name="PensionAdjustment">
/// The pension adjustment (T4 box 52), for a defined benefit plan or to use the exact figure;
/// blank to take both sides' contributions as it, as for a defined contribution plan.
/// </param>
/// <param name="RentalSharePercent">
/// The person's share of jointly owned rental property, in percent: their part of the rental
/// deposits split between co-owners (<see cref="TaxSituations.Split"/>) and of the rental expenses
/// no one claims. Blank to share what others haven't taken equally.
/// </param>
public sealed record TaxSituation(
    string? Province = null,
    bool EmployerWithholds = true,
    decimal? EsppPercent = null,
    decimal? EsppMatchPercent = null,
    decimal? RrspPercent = null,
    decimal? RrspMatchPercent = null,
    decimal? RrspDeductionLimit = null,
    decimal? PensionPercent = null,
    decimal? PensionMatchPercent = null,
    decimal? PensionAdjustment = null,
    decimal? RentalSharePercent = null)
{
    /// <summary>Whether any payroll plan (ESPP, group RRSP or pension) is set.</summary>
    public bool HasPayrollPlans => EsppPercent > 0 || RrspPercent > 0 || RrspMatchPercent > 0
        || PensionPercent > 0 || PensionMatchPercent > 0 || PensionAdjustment > 0;
}

/// <summary>
/// RRSP room for the year: the deduction limit (from the notice of assessment, or estimated), what
/// already goes in, and what more would save. A planning aid, not advice.
/// </summary>
/// <param name="Limit">The deduction limit for the year.</param>
/// <param name="LimitEstimated">
/// The limit is 18% of this year's earned income up to <see cref="DollarLimit"/>, less this year's
/// pension adjustment, as if last year's pay and pension were similar, without room carried forward
/// from earlier years.
/// </param>
/// <param name="Payroll">The person's own group RRSP contributions through payroll.</param>
/// <param name="EmployerMatch">The employer's group RRSP contributions.</param>
/// <param name="Tagged">Contributions tagged RRSP in the ledger.</param>
/// <param name="Left">Limit not used yet; never negative.</param>
/// <param name="SavingsIfUsed">How much less tax contributing all of <see cref="Left"/> would mean.</param>
/// <param name="AtMarginalRate">
/// How much of <see cref="Left"/> comes off income taxed at the current marginal rate, before
/// taxable income drops into a lower bracket.
/// </param>
/// <param name="NextYearRoom">
/// New room this year's earned income adds for next year: 18% up to next year's dollar limit, less
/// this year's <see cref="PensionAdjustment"/>.
/// </param>
/// <param name="PensionAdjustment">This year's pension adjustment, which comes off next year's room.</param>
public sealed record RrspRoom(
    decimal Limit,
    bool LimitEstimated,
    decimal DollarLimit,
    decimal Payroll,
    decimal EmployerMatch,
    decimal Tagged,
    decimal Left,
    decimal SavingsIfUsed,
    decimal AtMarginalRate,
    int NextYear,
    decimal? NextYearRoom,
    decimal PensionAdjustment = 0)
{
    public decimal Contributed => Payroll + EmployerMatch + Tagged;

    public decimal Over => Math.Max(0, Contributed - Limit);
}

/// <summary>One person's situation for a year.</summary>
/// <param name="PersonId">A household member id, or <see cref="TaxSituations.Self"/> when there's no household.</param>
public sealed record TaxSituationEntry(int Year, string PersonId, TaxSituation Situation);

public static class TaxSituations
{
    /// <summary>The person id used when the ledger has no household: whoever keeps it.</summary>
    public const string Self = "self";

    /// <summary>
    /// Whose income a rental deposit is when it's split between co-owners, each by their
    /// <see cref="TaxSituation.RentalSharePercent"/>, as each owner reports their share on Form T776.
    /// </summary>
    public const string Split = "split";

    /// <summary>
    /// Each person's share of jointly owned rental property, as a fraction: the percent they set,
    /// with what's left of 100% shared equally by those who left theirs blank.
    /// </summary>
    public static IReadOnlyDictionary<string, decimal> RentalShares(IEnumerable<(string PersonId, decimal? Percent)> people)
    {
        var list = people.ToList();
        var blanks = list.Count(p => p.Percent is null);
        var each = blanks == 0 ? 0 : Math.Max(0, 100 - list.Sum(p => p.Percent ?? 0)) / blanks;
        return list.ToDictionary(p => p.PersonId, p => (p.Percent ?? each) / 100m);
    }
}

/// <summary>
/// Income one person received in the year, from the ledger's income deposits.
/// </summary>
/// <param name="Salary">Income › Salary deposits: net pay when the employer withholds.</param>
/// <param name="Business">Income › Business Income deposits, before expenses.</param>
/// <param name="Rental">Income › Rental Income deposits, before expenses, including the person's share of split ones.</param>
/// <param name="Other">Other income (Income › Other Income or no detail), which isn't counted.</param>
/// <param name="ReceivedTo">The last day counted: today for the year in progress, December 31 otherwise.</param>
/// <param name="ProjectionFactor">
/// What the amounts are multiplied by to cover the whole year: the year's days over the days so far
/// for the year in progress, 1 for a finished year.
/// </param>
/// <param name="SplitRental">All the rental deposits split between co-owners, of which <see cref="RentalShare"/> is in <see cref="Rental"/>.</param>
/// <param name="RentalShare">
/// The person's share of jointly owned rental property, as a fraction, when the household splits
/// rental income; null otherwise.
/// </param>
public sealed record IncomeReceived(
    decimal Salary,
    decimal Business,
    decimal Rental,
    decimal Other,
    DateOnly ReceivedTo,
    decimal ProjectionFactor = 1,
    decimal SplitRental = 0,
    decimal? RentalShare = null)
{
    public bool IsProjected => ProjectionFactor != 1;

    public static IncomeReceived None(int year) => new(0, 0, 0, 0, new DateOnly(year, 12, 31));
}

/// <summary>
/// The detailed amounts the estimator works from, in T1 terms. <see cref="TaxEstimator.EstimateFromIncome"/>
/// fills them from the ledger; tests and future detail can set them directly. Blank amounts count
/// as zero, except CPP and EI, which are worked out from employment income.
/// </summary>
/// <param name="EmploymentIncome">Gross employment income (T4 box 14).</param>
/// <param name="CppContributions">T4 box 16; blank to work out from employment income.</param>
/// <param name="Cpp2Contributions">T4 box 16A; blank to work out.</param>
/// <param name="EiPremiums">T4 box 18; blank to work out.</param>
/// <param name="EmployerRrspMatch">An employer's group RRSP contributions, deducted as RRSP contributions.</param>
/// <param name="EmployerMatchOnT4">Whether the match is already in <see cref="EmploymentIncome"/>.</param>
/// <param name="RrspDeductionLimit">The RRSP deduction limit; blank to not cap.</param>
/// <param name="CapitalGains">Net capital gains before the 50% inclusion; may be negative.</param>
/// <param name="IncomeTaxDeducted">Income tax already taken off at source.</param>
public sealed record TaxInputs(
    string? Province = null,
    int? BirthYear = null,
    decimal? EmploymentIncome = null,
    decimal? CppContributions = null,
    decimal? Cpp2Contributions = null,
    decimal? EiPremiums = null,
    decimal? RppContributions = null,
    decimal? UnionDues = null,
    decimal? PensionAdjustment = null,
    decimal? IncomeTaxDeducted = null,
    decimal? GroupRrspContributions = null,
    decimal? EmployerRrspMatch = null,
    bool EmployerMatchOnT4 = true,
    decimal? RrspDeductionLimit = null,
    decimal? PensionIncome = null,
    decimal? RrifIncome = null,
    decimal? RrspWithdrawals = null,
    decimal? CppBenefits = null,
    decimal? OasBenefits = null,
    decimal? EiBenefits = null,
    decimal? InterestIncome = null,
    decimal? EligibleDividends = null,
    decimal? OtherDividends = null,
    decimal? CapitalGains = null,
    decimal? SelfEmploymentIncome = null,
    decimal? OtherBusinessExpenses = null,
    decimal? RentalIncome = null,
    decimal? OtherRentalExpenses = null,
    decimal? OtherIncome = null,
    decimal? InstalmentsPaid = null);

/// <param name="Name">The household member's name, or "You" when there's no household.</param>
/// <param name="Saved">Whether the situation was saved (otherwise it's the default, or carried from an earlier year).</param>
/// <param name="Estimate">Null until a province is chosen, or when LoonieBooks has no rates for the year.</param>
/// <param name="Income">The income counted for the person, from the ledger.</param>
/// <param name="Claims">The person's confirmed claims for the year, by tax category code.</param>
public sealed record PersonTaxEstimate(
    string PersonId,
    string Name,
    TaxSituation Situation,
    bool Saved,
    TaxEstimate? Estimate,
    IncomeReceived Income,
    IReadOnlyDictionary<string, decimal> Claims);

/// <summary>Everyone's estimate for a year: the household's members, or just the person keeping the ledger.</summary>
/// <param name="RatesAvailable">Whether LoonieBooks has federal and provincial rates for <see cref="Year"/>.</param>
/// <param name="UnassignedClaims">Confirmed claims assigned to no one, which no estimate counts.</param>
/// <param name="SharedIncome">
/// Salary, business and rental deposits in accounts that belong to no one (shared), newest first,
/// with the person each is assigned to, if any (or <see cref="TaxSituations.Split"/>). Only with a household.
/// </param>
public sealed record TaxEstimateSet(
    int Year,
    bool RatesAvailable,
    IReadOnlyList<PersonTaxEstimate> People,
    int UnassignedClaims = 0,
    decimal UnassignedAmount = 0,
    IReadOnlyList<LedgerTransaction>? SharedIncome = null)
{
    public const int MaxSharedIncome = 500;

    /// <summary>Shared-account income not assigned to anyone, which no estimate counts.</summary>
    public IReadOnlyList<LedgerTransaction> UnassignedIncome => (SharedIncome ?? []).Where(t => t.EarnedBy is null).ToList();
}

/// <summary>Sets or clears (null) whose an account is, or whose income a deposit is.</summary>
public sealed record SetPersonRequest(string? PersonId);
