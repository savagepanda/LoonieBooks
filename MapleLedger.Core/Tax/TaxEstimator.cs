namespace MapleLedger.Core.Tax;

/// <summary>One line of an estimate: an income, deduction, credit or tax amount, with the T1 line it relates to.</summary>
/// <param name="FromLedger">The amount comes from tagged transactions rather than from the person's slips.</param>
public sealed record TaxEstimateLine(string Label, decimal Amount, string? Reference = null, bool FromLedger = false);

/// <summary>
/// An estimate of one person's tax for a year from their income (<see cref="TaxInputs"/>) and
/// the claims tagged in the ledger. It is a planning aid with CRA line references, not a return
/// and not tax advice: it leaves out many credits, carry-forwards and special cases.
/// </summary>
/// <param name="Federal">Federal tax on taxable income, then each credit (negative).</param>
/// <param name="Provincial">Provincial or territorial tax, credits (negative), surtax, reductions and premiums.</param>
/// <param name="Other">Other amounts payable on the return: CPP on self-employment, OAS recovery.</param>
/// <param name="Paid">Tax already paid (deducted at source, instalments) and refundable credits.</param>
/// <param name="Balance">What's left to pay when filing; negative for a refund.</param>
/// <param name="MarginalRate">Combined federal and provincial tax on the next dollar of ordinary income.</param>
/// <param name="RrspSavingsPer1000">
/// How much less tax an extra $1,000 RRSP deduction would mean; null when the deduction limit is
/// already used.
/// </param>
/// <param name="RrspRoomLeft">Deduction limit not used this year, when the limit was entered.</param>
public sealed record TaxEstimate(
    int Year,
    string ProvinceCode,
    string ProvinceName,
    IReadOnlyList<TaxEstimateLine> Income,
    decimal TotalIncome,
    IReadOnlyList<TaxEstimateLine> Deductions,
    decimal NetIncome,
    decimal TaxableIncome,
    IReadOnlyList<TaxEstimateLine> Federal,
    decimal FederalTax,
    IReadOnlyList<TaxEstimateLine> Provincial,
    decimal ProvincialTax,
    IReadOnlyList<TaxEstimateLine> Other,
    decimal TotalPayable,
    IReadOnlyList<TaxEstimateLine> Paid,
    decimal TotalPaid,
    decimal Balance,
    decimal MarginalRate,
    decimal? RrspSavingsPer1000,
    decimal? RrspRoomLeft,
    IReadOnlyList<string> Notes)
{
    public bool IsRefund => Balance < 0;

    /// <summary>RRSP room for the year, when the estimate comes from the ledger's income.</summary>
    public RrspRoom? Rrsp { get; init; }

    /// <summary>
    /// Income lines no tax was taken off, when the estimate comes from the ledger's income: net
    /// rental and business income, and salary from an employer that doesn't withhold.
    /// </summary>
    public IReadOnlyList<TaxEstimateLine> NotWithheld { get; init; } = [];

    /// <summary>Income with no tax taken off, in total.</summary>
    public decimal NotWithheldTotal => NotWithheld.Sum(l => l.Amount);

    /// <summary>How rents and rental expenses make up net rental income (line 12600); null with neither.</summary>
    public RentalSummary? Rental { get; init; }

    /// <summary>Income tax (federal and provincial) as a share of total income.</summary>
    public decimal AverageRate => TotalIncome > 0 ? (FederalTax + ProvincialTax) / TotalIncome : 0;
}

/// <summary>Net rental income (line 12600, Form T776) in its parts.</summary>
/// <param name="Rents">Rents counted, before expenses (projected to December 31 when <see cref="Projected"/>).</param>
/// <param name="Expenses">Rental expenses claimed: the tagged ones, after business-use shares.</param>
/// <param name="Net">Rents less expenses; a loss when negative, which comes off other income.</param>
/// <param name="TaxSaved">How much more tax there would be without the expenses.</param>
/// <param name="Projected">Whether the rents are projected to December 31 (the expenses never are).</param>
/// <param name="Share">The person's share of jointly owned property, when rent is split between co-owners.</param>
public sealed record RentalSummary(decimal Rents, decimal Expenses, decimal Net, decimal TaxSaved, bool Projected = false, decimal? Share = null);

/// <summary>The confirmed claims of one person for one year, by tax category code, plus what's still waiting for review.</summary>
public sealed record TaxClaims(IReadOnlyDictionary<string, decimal> Confirmed, int Pending = 0, decimal PendingAmount = 0)
{
    public static TaxClaims None { get; } = new(new Dictionary<string, decimal>());

    public decimal this[string code] => Confirmed.TryGetValue(code, out var amount) ? amount : 0;

    public static TaxClaims From(TaxReport report) => new(
        report.Categories.ToDictionary(c => c.Category.Code, c => c.Confirmed, StringComparer.OrdinalIgnoreCase),
        report.PendingReview,
        report.Categories.Sum(c => c.Suggested));
}

public static class TaxEstimator
{
    /// <summary>
    /// Estimates the tax for <paramref name="year"/>. Returns null when LoonieBooks has no rates
    /// for the year or no province was chosen.
    /// </summary>
    public static TaxEstimate? Estimate(int year, TaxInputs situation, TaxClaims claims)
    {
        if (TaxRates.For(year) is not { } rates || rates.Province(situation.Province) is null) return null;

        var estimate = Compute(rates, situation, claims, extraIncome: 0, extraRrsp: 0);
        var higher = Compute(rates, situation, claims, extraIncome: 1_000m, extraRrsp: 0);
        var marginal = Math.Round((higher.TotalPayable - estimate.TotalPayable) / 1_000m, 4);

        decimal? savings = null;
        if (estimate.RrspRoomLeft is null or >= 1_000m)
        {
            var lower = Compute(rates, situation, claims, extraIncome: 0, extraRrsp: 1_000m);
            savings = Math.Round(estimate.TotalPayable - lower.TotalPayable, 2);
        }

        var rents = situation.RentalIncome ?? 0;
        var rentalExpenses = claims[TaxCategories.Rental] + (situation.OtherRentalExpenses ?? 0);
        if (rents != 0 || rentalExpenses != 0)
        {
            var withoutExpenses = Compute(
                rates,
                situation with { OtherRentalExpenses = null },
                claims with { Confirmed = claims.Confirmed.Where(c => !string.Equals(c.Key, TaxCategories.Rental, StringComparison.OrdinalIgnoreCase)).ToDictionary() },
                extraIncome: 0,
                extraRrsp: 0);
            estimate = estimate with
            {
                Rental = new RentalSummary(rents, rentalExpenses, rents - rentalExpenses, Math.Round(withoutExpenses.Balance - estimate.Balance, 2)),
            };
        }

        return estimate with { MarginalRate = marginal, RrspSavingsPer1000 = savings };
    }

    /// <summary>
    /// Estimates the tax for <paramref name="year"/> from the income deposits in the ledger. Net
    /// pay is worked back to a gross salary (the pay that leaves that much after income tax, CPP,
    /// EI and any ESPP, group RRSP and pension contributions), and the employer is taken to have withheld
    /// the tax on that salary alone; claims, business and rental income then move the balance from
    /// there. For the year in progress the income is projected to December 31.
    /// </summary>
    public static TaxEstimate? EstimateFromIncome(int year, TaxSituation situation, IncomeReceived income, TaxClaims claims)
    {
        if (TaxRates.For(year) is not { } rates || rates.Province(situation.Province) is null) return null;

        var factor = income.ProjectionFactor;
        decimal Year(decimal amount) => Math.Round(amount * factor, 2, MidpointRounding.AwayFromZero);
        var province = situation.Province!;
        var salary = Year(income.Salary);
        var withholds = situation.EmployerWithholds && salary > 0;
        // Payroll plans need an employer taking things off the pay.
        var plans = withholds ? PayrollPlans.From(situation) : PayrollPlans.None;
        var pay = Payroll(withholds ? GrossUp(rates, province, salary, situation) : salary, plans);
        var withheld = withholds ? Withheld(rates, province, pay) : 0;
        // Defined contribution: both sides' contributions; a defined benefit plan's comes from the T4.
        var pensionAdjustment = withholds ? situation.PensionAdjustment ?? pay.Pension + pay.PensionMatch : 0;

        var inputs = new TaxInputs(
            province,
            EmploymentIncome: pay.Employment,
            // Without withholding, nothing came off the pay: no CPP or EI either.
            CppContributions: withholds ? null : 0,
            Cpp2Contributions: withholds ? null : 0,
            EiPremiums: withholds ? null : 0,
            IncomeTaxDeducted: withheld,
            RppContributions: pay.Pension,
            PensionAdjustment: pensionAdjustment,
            GroupRrspContributions: pay.Rrsp,
            EmployerRrspMatch: pay.RrspMatch,
            EmployerMatchOnT4: true,
            RrspDeductionLimit: situation.RrspDeductionLimit,
            SelfEmploymentIncome: Year(income.Business),
            RentalIncome: Year(income.Rental));
        if (Estimate(year, inputs, claims) is not { } estimate) return null;

        var receivedTo = income.ReceivedTo.ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture);
        var projected = income.IsProjected ? $" (projected from income to {receivedTo})" : "";
        var notes = estimate.Notes
            .Where(n => !n.StartsWith("CPP and EI weren't entered", StringComparison.Ordinal)
                && !n.StartsWith("RRSP contributions aren't checked", StringComparison.Ordinal)
                && !n.StartsWith("Your pension adjustment of", StringComparison.Ordinal))
            .ToList();
        var first = new List<string>();
        if (withholds)
        {
            var taken = new List<string>();
            if (pay.Espp > 0) taken.Add($"{Money.Format(pay.Espp)} to the ESPP ({Percent(plans.Espp)})");
            if (pay.Rrsp > 0) taken.Add($"{Money.Format(pay.Rrsp)} to the group RRSP ({Percent(plans.Rrsp)})");
            if (pay.Pension > 0) taken.Add($"{Money.Format(pay.Pension)} to the pension plan ({Percent(plans.Pension)})");
            var others = pay.Pension > 0 || plans.PensionMatch > 0 || situation.PensionAdjustment > 0 ? "benefits" : "a pension or benefits";
            first.Add(taken.Count == 0
                ? $"Salary of {Money.Format(pay.Salary)} is worked back from {Money.Format(salary)} of net pay{projected}. Payroll deductions such as a group RRSP, ESPP, pension or benefits would make it higher; add the plans you're in under Household."
                : $"Salary of {Money.Format(pay.Salary)} is worked back from {Money.Format(salary)} of net pay{projected}, after {Join(taken)}. Other payroll deductions such as {others} would make it higher.");

            var benefits = new List<string>();
            if (pay.RrspMatch > 0) benefits.Add($"{Money.Format(pay.RrspMatch)} RRSP match ({Percent(plans.RrspMatch)} of salary)");
            if (pay.EsppMatch > 0) benefits.Add($"{Money.Format(pay.EsppMatch)} ESPP contribution");
            if (benefits.Count > 0)
            {
                first.Add($"The employer's {string.Join(" and ", benefits)} {(benefits.Count == 1 ? "is a taxable benefit" : "are taxable benefits")} included in employment income"
                    + (pay.RrspMatch > 0 ? "; the RRSP match is also deducted as an RRSP contribution." : "."));
            }

            var beforeTax = new List<string>();
            if (pay.Rrsp + pay.RrspMatch > 0) beforeTax.Add("group RRSP");
            if (pay.Pension > 0) beforeTax.Add("pension");
            if (beforeTax.Count > 0)
            {
                first.Add($"The employer is assumed to withhold tax after the {Join(beforeTax)} contributions, as most do.");
            }

            if (pensionAdjustment > 0 || pay.PensionMatch > 0)
            {
                var parts = new List<string>();
                if (pay.Pension > 0) parts.Add($"Your {Money.Format(pay.Pension)} of pension contributions is deducted on line 20700.");
                if (pay.PensionMatch > 0) parts.Add($"The employer's {Money.Format(pay.PensionMatch)} ({Percent(plans.PensionMatch)} of salary) isn't taxable income.");
                parts.Add(situation.PensionAdjustment is null
                    ? $"Together they make a pension adjustment of about {Money.Format(pensionAdjustment)}, which lowers your RRSP room for {year + 1}; T4 box 52 has the exact figure."
                    : $"The pension adjustment of {Money.Format(pensionAdjustment)} you entered lowers your RRSP room for {year + 1}.");
                first.Add(string.Join(" ", parts));
            }

            if (pay.Espp > 0)
            {
                first.Add("ESPP contributions come from after-tax pay and aren't deductible. Selling the shares later can mean a capital gain or loss, which isn't counted here.");
            }
        }
        else if (salary > 0)
        {
            first.Add($"Salary deposits are taxed as received{projected}, with no tax, CPP or EI taken off by an employer.");
        }
        else if (income.IsProjected)
        {
            first.Add($"Income is projected to December 31 from what arrived to {receivedTo}.");
        }

        notes.InsertRange(0, first);

        if (income.RentalShare is { } share && income.SplitRental != 0)
        {
            notes.Add($"Rental income includes your {Percent(share)} share of {Money.Format(Year(income.SplitRental))} in rent split between co-owners{projected}, "
                + "and rental expenses no one claimed are shared the same way; each co-owner reports their share on Form T776.");
        }

        if (income.Other != 0)
        {
            notes.Add($"Other income of {Money.Format(income.Other)} (refunds, benefits, interest…) isn't counted.");
        }

        // Employment income in its parts: the salary, then the employer's contributions.
        var incomeLines = new List<TaxEstimateLine>();
        foreach (var line in estimate.Income)
        {
            if (line.Reference == "Line 10100")
            {
                incomeLines.Add(new(withholds ? "Salary, before deductions (worked back from net pay)" : "Salary", pay.Salary, line.Reference, FromLedger: true));
                if (pay.RrspMatch > 0) incomeLines.Add(new("Employer's group RRSP match (taxable benefit)", pay.RrspMatch, line.Reference));
                if (pay.EsppMatch > 0) incomeLines.Add(new("Employer's ESPP contribution (taxable benefit)", pay.EsppMatch, line.Reference));
            }
            else if (line.Reference == "Line 12600" && income.RentalShare is { } ownShare && income.SplitRental != 0)
            {
                incomeLines.Add(line with { Label = $"Net rental income, with your {Percent(ownShare)} share of joint property (rents less expenses)", FromLedger = true });
            }
            else
            {
                incomeLines.Add(line.Reference is "Line 13500" or "Line 12600" ? line with { FromLedger = true } : line);
            }
        }

        // Rental and business income come in whole, and so does salary when nothing is withheld.
        var notWithheld = incomeLines
            .Where(l => l.Amount > 0 && (l.Reference is "Line 12600" or "Line 13500" || (!withholds && l.Reference == "Line 10100")))
            .ToList();
        var notWithheldTotal = notWithheld.Sum(l => l.Amount);
        if (notWithheldTotal > 0 && estimate.Balance > 0)
        {
            notes.Add($"No tax is taken off the {Money.Format(notWithheldTotal)} of {NotWithheldKinds(notWithheld)}, so the tax on it is part of the balance owing, due April 30, {year + 1}.");
            if (estimate.Balance > TaxRates.InstalmentThreshold)
            {
                notes.Add($"With more than {Money.FormatWhole(TaxRates.InstalmentThreshold)} owing, CRA expects quarterly instalments (March 15, June 15, September 15 and December 15) "
                    + $"when {year - 1} or {year - 2} was also over it, and sends an instalment reminder with the amounts.");
            }
        }

        var room = RoomFor(rates, situation, inputs, claims, estimate, pay, pensionAdjustment);
        if (room is { LimitEstimated: true, Over: > 0 })
        {
            notes.Add($"RRSP contributions are {Money.Format(room.Over)} over the estimated limit. Room carried forward from earlier years may cover it; your notice of assessment shows your actual limit.");
        }

        return estimate with
        {
            Income = incomeLines,
            Deductions = estimate.Deductions
                .Select(l => l.Reference == "Line 20700" ? l with { Label = "Registered pension plan contributions from your pay" } : l)
                .ToList(),
            Paid = estimate.Paid
                .Select(l => l.Reference == "Line 43700" ? l with { Label = "Tax your employer likely withheld" } : l)
                .ToList(),
            Notes = notes,
            RrspRoomLeft = room?.Left ?? estimate.RrspRoomLeft,
            Rrsp = room,
            NotWithheld = notWithheld,
            Rental = estimate.Rental is { } rental
                ? rental with { Projected = income.IsProjected && income.Rental != 0, Share = income.SplitRental != 0 ? income.RentalShare : null }
                : null,
        };
    }

    /// <summary>"rental income", "business and rental income", and so on, for income lines no tax was taken off.</summary>
    public static string NotWithheldKinds(IEnumerable<TaxEstimateLine> lines)
    {
        var kinds = lines.Select(l => l.Reference switch
            {
                "Line 10100" => "salary",
                "Line 13500" => "business",
                _ => "rental",
            })
            .Distinct()
            .ToList();
        return kinds is ["salary"] ? "salary" : Join(kinds) + " income";
    }

    /// <summary>
    /// RRSP room for the year: the deduction limit entered, or else 18% of the year's earned income
    /// up to the dollar limit less the pension adjustment; what's already going in; and what using
    /// the rest would save.
    /// </summary>
    private static RrspRoom? RoomFor(TaxYearRates rates, TaxSituation situation, TaxInputs inputs, TaxClaims claims, TaxEstimate estimate, Pay pay, decimal pensionAdjustment)
    {
        // Earned income: employment income with its benefits, and net business and rental income.
        var earned = Math.Max(0, estimate.Income.Where(l => l.Reference is "Line 10100" or "Line 13500" or "Line 12600").Sum(l => l.Amount));
        // A pension adjustment comes off the next year's room; for an estimated limit, last year's is taken to be the same.
        decimal? RoomUpTo(decimal? dollarLimit) => dollarLimit is { } cap
            ? Math.Max(0, Math.Round(Math.Min(earned * TaxRates.RrspEarnedIncomeRate, cap), 2, MidpointRounding.AwayFromZero) - pensionAdjustment)
            : null;

        var dollarLimit = TaxRates.RrspDollarLimit(rates.Year);
        if ((situation.RrspDeductionLimit ?? RoomUpTo(dollarLimit)) is not { } limit) return null;

        var tagged = claims[TaxCategories.Rrsp];
        var left = Math.Max(0, limit - pay.Rrsp - pay.RrspMatch - tagged);
        var savings = left > 0
            ? Math.Round(estimate.TotalPayable - Compute(rates, inputs, claims, extraIncome: 0, extraRrsp: left).TotalPayable, 2)
            : 0;

        // How far taxable income is above the start of the federal or provincial bracket its next dollar is taxed in.
        var taxable = estimate.TaxableIncome;
        decimal BracketStart(JurisdictionRates j) => j.Brackets.Where(b => b.From < taxable).Select(b => b.From).DefaultIfEmpty(0).Max();
        var floor = Math.Max(BracketStart(rates.Federal), BracketStart(rates.Province(situation.Province)!));

        return new RrspRoom(
            limit,
            LimitEstimated: situation.RrspDeductionLimit is null,
            DollarLimit: dollarLimit ?? limit,
            pay.Rrsp,
            pay.RrspMatch,
            tagged,
            left,
            savings,
            AtMarginalRate: Math.Min(left, Math.Max(0, taxable - floor)),
            NextYear: rates.Year + 1,
            NextYearRoom: RoomUpTo(TaxRates.RrspDollarLimit(rates.Year + 1)),
            PensionAdjustment: pensionAdjustment);
    }

    private static string Join(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Concat(items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];

    private static string Percent(decimal fraction) => (fraction * 100).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// A situation's payroll plans as fractions: ESPP, group RRSP and pension of salary, the ESPP
    /// match of the ESPP contributions.
    /// </summary>
    private sealed record PayrollPlans(decimal Espp, decimal EsppMatch, decimal Rrsp, decimal RrspMatch, decimal Pension, decimal PensionMatch)
    {
        public static PayrollPlans None { get; } = new(0, 0, 0, 0, 0, 0);

        public static PayrollPlans From(TaxSituation? s) => s is null
            ? None
            : new((s.EsppPercent ?? 0) / 100m, (s.EsppMatchPercent ?? 0) / 100m, (s.RrspPercent ?? 0) / 100m, (s.RrspMatchPercent ?? 0) / 100m,
                (s.PensionPercent ?? 0) / 100m, (s.PensionMatchPercent ?? 0) / 100m);
    }

    /// <summary>A salary and what the payroll plans take off it or add to it.</summary>
    private sealed record Pay(decimal Salary, decimal Espp, decimal EsppMatch, decimal Rrsp, decimal RrspMatch, decimal Pension, decimal PensionMatch)
    {
        /// <summary>
        /// Employment income on the T4 (box 14): the salary and the employer's ESPP and RRSP
        /// contributions. The employer's pension contributions aren't taxable.
        /// </summary>
        public decimal Employment => Salary + EsppMatch + RrspMatch;

        /// <summary>What the person's own plan contributions take off their pay.</summary>
        public decimal Contributions => Espp + Rrsp + Pension;
    }

    private static Pay Payroll(decimal salary, PayrollPlans plans)
    {
        static decimal R(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
        var espp = R(salary * plans.Espp);
        return new Pay(salary, espp, R(espp * plans.EsppMatch), R(salary * plans.Rrsp), R(salary * plans.RrspMatch), R(salary * plans.Pension), R(salary * plans.PensionMatch));
    }

    /// <summary>Tax withheld from pay: on the employment income, less the group RRSP and pension contributions.</summary>
    private static decimal Withheld(TaxYearRates rates, string province, Pay pay) =>
        Compute(rates, new TaxInputs(province, EmploymentIncome: pay.Employment, RppContributions: pay.Pension, GroupRrspContributions: pay.Rrsp, EmployerRrspMatch: pay.RrspMatch), TaxClaims.None, 0, 0).TotalPayable;

    /// <summary>CPP, CPP2 and EI taken off employment income, as an employee.</summary>
    private static decimal PayrollContributions(TaxYearRates rates, decimal gross)
    {
        var cpp = rates.Cpp;
        return Math.Max(0, Math.Min(gross, cpp.MaxPensionable) - cpp.BasicExemption) * cpp.EmployeeRate
            + Math.Max(0, Math.Min(gross, cpp.MaxAdditionalPensionable) - cpp.MaxPensionable) * cpp.SecondAdditionalRate
            + Math.Min(gross, rates.Ei.MaxInsurable) * rates.Ei.Rate;
    }

    /// <summary>
    /// The salary that leaves <paramref name="net"/> after income tax, CPP, EI and the person's
    /// own ESPP, group RRSP and pension contributions in <paramref name="plans"/>.
    /// </summary>
    public static decimal GrossUp(TaxYearRates rates, string province, decimal net, TaxSituation? plans = null)
    {
        if (net <= 0) return net;
        var p = PayrollPlans.From(plans);
        decimal NetOf(decimal salary)
        {
            var pay = Payroll(salary, p);
            return salary - pay.Contributions - Withheld(rates, province, pay) - PayrollContributions(rates, pay.Employment);
        }

        // Take-home pay rises with salary (no rate reaches 100%), so halving the interval converges.
        decimal low = net, high = net * 2 + 20_000m;
        for (var i = 0; i < 20 && NetOf(high) < net; i++) high *= 2;
        for (var i = 0; i < 80 && high - low > 0.005m; i++)
        {
            var mid = (low + high) / 2;
            if (NetOf(mid) < net) low = mid; else high = mid;
        }

        return Math.Round(high, 2, MidpointRounding.AwayFromZero);
    }

    private static TaxEstimate Compute(TaxYearRates rates, TaxInputs s, TaxClaims claims, decimal extraIncome, decimal extraRrsp)
    {
        var federal = rates.Federal;
        var province = rates.Province(s.Province)!;
        var notes = new List<string>();
        static decimal V(decimal? value) => value ?? 0;
        static decimal R(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
        var age = s.BirthYear is { } born ? rates.Year - born : (int?)null;
        var senior = age >= 65;

        // ------------------------------------------------------------ income
        var income = new List<TaxEstimateLine>();
        void AddIncome(string label, decimal amount, string reference, bool fromLedger = false)
        {
            if (amount != 0) income.Add(new(label, R(amount), reference, fromLedger));
        }

        var match = V(s.EmployerRrspMatch);
        var employment = V(s.EmploymentIncome) + (s.EmployerMatchOnT4 ? 0 : match);
        AddIncome("Employment income (T4 box 14)", V(s.EmploymentIncome), "Line 10100");
        if (!s.EmployerMatchOnT4) AddIncome("Employer RRSP match not on your T4", match, "Line 10100");
        AddIncome("Old Age Security", V(s.OasBenefits), "Line 11300");
        AddIncome("CPP or QPP benefits", V(s.CppBenefits), "Line 11400");
        AddIncome("Pension and RRIF income", V(s.PensionIncome) + V(s.RrifIncome), "Line 11500");
        AddIncome("Employment Insurance benefits", V(s.EiBenefits), "Line 11900");
        var eligibleGrossed = V(s.EligibleDividends) * TaxYearRates.EligibleDividendGrossUp;
        var otherGrossed = V(s.OtherDividends) * TaxYearRates.OtherDividendGrossUp;
        AddIncome("Eligible dividends, grossed up 38%", eligibleGrossed, "Line 12000");
        AddIncome("Other dividends, grossed up 15%", otherGrossed, "Line 12010");
        AddIncome("Interest and other investment income", V(s.InterestIncome), "Line 12100");

        var rentalExpenses = claims[TaxCategories.Rental] + V(s.OtherRentalExpenses);
        if (V(s.RentalIncome) != 0 || rentalExpenses != 0)
        {
            AddIncome("Net rental income (rents less expenses)", V(s.RentalIncome) - rentalExpenses, "Line 12600", claims[TaxCategories.Rental] != 0);
        }

        if (V(s.CapitalGains) > 0)
        {
            AddIncome("Taxable capital gains (50%)", V(s.CapitalGains) * TaxYearRates.CapitalGainsInclusion, "Line 12700");
        }
        else if (V(s.CapitalGains) < 0)
        {
            notes.Add("A net capital loss isn't deducted from other income; it can be carried back 3 years or forward against future gains.");
        }

        AddIncome("RRSP withdrawals", V(s.RrspWithdrawals), "Line 12900");
        AddIncome("Other income", V(s.OtherIncome) + extraIncome, "Line 13000");

        var businessExpenses = claims[TaxCategories.Business] + V(s.OtherBusinessExpenses);
        var businessNet = V(s.SelfEmploymentIncome) - businessExpenses;
        if (V(s.SelfEmploymentIncome) != 0 || businessExpenses != 0)
        {
            AddIncome("Net self-employment income (revenue less expenses)", businessNet, "Line 13500", claims[TaxCategories.Business] != 0);
        }

        var totalIncome = income.Sum(l => l.Amount);

        // ------------------------------------------------------------ CPP and EI
        var cpp = rates.Cpp;
        var cppPaid = s.CppContributions ?? R(Math.Max(0, Math.Min(employment, cpp.MaxPensionable) - cpp.BasicExemption) * cpp.EmployeeRate);
        var cpp2Paid = s.Cpp2Contributions ?? R(Math.Max(0, Math.Min(employment, cpp.MaxAdditionalPensionable) - cpp.MaxPensionable) * cpp.SecondAdditionalRate);
        var eiPaid = s.EiPremiums ?? R(Math.Min(employment, rates.Ei.MaxInsurable) * rates.Ei.Rate);
        if (employment > 0 && (s.CppContributions is null || s.EiPremiums is null))
        {
            notes.Add("CPP and EI weren't entered, so they're worked out from employment income; use T4 boxes 16, 16A and 18 for exact amounts.");
        }

        var cppBase = cppPaid * cpp.BaseRate / cpp.EmployeeRate;
        var cppEnhanced = cppPaid - cppBase + cpp2Paid;

        // Self-employed people pay both the employee and the employer share on business income.
        decimal seFirst = 0, seSecond = 0;
        if (businessNet > 0)
        {
            var earnings = employment + businessNet;
            seFirst = 2 * Math.Max(0, Math.Max(0, Math.Min(earnings, cpp.MaxPensionable) - cpp.BasicExemption) * cpp.EmployeeRate - cppPaid);
            seSecond = 2 * Math.Max(0, Math.Max(0, Math.Min(earnings, cpp.MaxAdditionalPensionable) - cpp.MaxPensionable) * cpp.SecondAdditionalRate - cpp2Paid);
        }

        var seEmployerHalf = (seFirst + seSecond) / 2;
        var seBase = seFirst / 2 * cpp.BaseRate / cpp.EmployeeRate;
        var seEnhanced = seFirst / 2 - seBase + seSecond / 2;

        // ------------------------------------------------------------ deductions
        var deductions = new List<TaxEstimateLine>();
        void AddDeduction(string label, decimal amount, string reference, bool fromLedger = false)
        {
            if (amount != 0) deductions.Add(new(label, R(amount), reference, fromLedger));
        }

        AddDeduction("Registered pension plan contributions (T4 box 20)", V(s.RppContributions), "Line 20700");

        var rrspTagged = claims[TaxCategories.Rrsp];
        var rrspContributed = rrspTagged + V(s.GroupRrspContributions) + match;
        var rrspDeducted = s.RrspDeductionLimit is { } limit ? Math.Min(rrspContributed, limit) : rrspContributed;
        decimal? rrspRoomLeft = s.RrspDeductionLimit is { } room ? Math.Max(0, room - rrspDeducted) : null;
        AddDeduction("RRSP contributions (tagged, payroll and employer match)", rrspDeducted + extraRrsp, "Line 20800", rrspTagged != 0);
        if (rrspContributed > rrspDeducted)
        {
            notes.Add($"{Money.Format(rrspContributed - rrspDeducted)} of RRSP contributions is over the deduction limit you entered and isn't deducted; it can be carried forward (over-contributions above CA$2,000 are taxed 1% a month).");
        }
        else if (s.RrspDeductionLimit is null && rrspContributed > 0)
        {
            notes.Add("RRSP contributions aren't checked against your deduction limit; enter the limit from your notice of assessment to cap them.");
        }

        var fhsa = claims[TaxCategories.Fhsa];
        AddDeduction("FHSA contributions", fhsa, "Line 20805", true);
        if (fhsa > 8_000m) notes.Add("FHSA contributions over CA$8,000 need room carried forward from last year (and CA$40,000 lifetime).");

        AddDeduction("Union and professional dues", V(s.UnionDues) + claims[TaxCategories.UnionDues], "Line 21200", claims[TaxCategories.UnionDues] != 0);
        AddDeduction("Child care expenses", claims[TaxCategories.ChildCare], "Line 21400", true);
        AddDeduction("Disability supports", claims[TaxCategories.DisabilitySupports], "Line 21500", true);
        AddDeduction("Moving expenses", claims[TaxCategories.Moving], "Line 21900", true);
        AddDeduction("Support payments made", claims[TaxCategories.SupportPayments], "Line 22000", true);
        AddDeduction("Carrying charges and interest", claims[TaxCategories.CarryingCharges], "Line 22100", true);
        AddDeduction("CPP on self-employment, employer's share", seEmployerHalf, "Line 22200");
        AddDeduction("Enhanced CPP contributions", cppEnhanced + seEnhanced, "Line 22215");
        AddDeduction("Employment expenses", claims[TaxCategories.Employment], "Line 22900", true);

        var netBeforeRecovery = Math.Max(0, totalIncome - deductions.Sum(l => l.Amount));
        var oasRecovery = R(Math.Min(V(s.OasBenefits), Math.Max(0, netBeforeRecovery - rates.OasRecoveryThreshold) * TaxYearRates.OasRecoveryRate));
        AddDeduction("Old Age Security repayment", oasRecovery, "Line 23500");

        var netIncome = R(Math.Max(0, totalIncome - deductions.Sum(l => l.Amount)));
        var taxable = netIncome;

        // ------------------------------------------------------------ credits, per jurisdiction
        var medical = claims[TaxCategories.Medical];
        var donations = Math.Min(claims[TaxCategories.Charitable], netIncome * TaxYearRates.DonationIncomeLimit);
        if (claims[TaxCategories.Charitable] > donations) notes.Add("Donations above 75% of net income can be carried forward up to 5 years.");
        var eligiblePension = V(s.PensionIncome) + (senior ? V(s.RrifIncome) : 0);
        if (s.BirthYear is null && (V(s.OasBenefits) > 0 || V(s.RrifIncome) > 0 || V(s.CppBenefits) > 0))
        {
            notes.Add("Enter a birth year so the age amount and the pension amount for RRIF income can apply.");
        }

        List<TaxEstimateLine> Credits(JurisdictionRates j, bool isFederal, out decimal basicTax)
        {
            var lines = new List<TaxEstimateLine> { new($"Tax on {Money.Format(taxable)} taxable income", R(j.TaxOn(taxable))) };
            string? F(string reference) => isFederal ? reference : null;
            void Credit(string label, decimal baseAmount, string? reference, bool fromLedger = false, decimal? rate = null)
            {
                if (baseAmount > 0) lines.Add(new(label, -R(baseAmount * (rate ?? j.CreditRate)), reference, fromLedger));
            }

            Credit("Basic personal amount", j.BasicPersonalFor(netIncome), F("Line 30000"));
            if (senior) Credit("Age amount", j.AgeAmountFor(netIncome), F("Line 30100"));
            Credit("CPP contributions", cppBase + seBase, F("Lines 30800, 31000"));
            Credit("EI premiums", eiPaid, F("Line 31200"));
            if (j.EmploymentAmount > 0) Credit("Canada employment amount", Math.Min(j.EmploymentAmount, employment), F("Line 31260"));
            if (isFederal)
            {
                Credit("Home accessibility expenses", Math.Min(claims[TaxCategories.HomeAccessibility], TaxYearRates.HomeAccessibilityLimit), "Line 31285", true);
                Credit("Adoption expenses", claims[TaxCategories.Adoption], "Line 31300", true);
            }

            Credit("Pension income amount", Math.Min(j.PensionAmount, eligiblePension), F("Line 31400"));
            Credit("Student loan interest", claims[TaxCategories.StudentLoanInterest], F("Line 31900"), true);
            if (isFederal) Credit("Tuition fees", claims[TaxCategories.Tuition], "Line 32300", true);

            var medicalThreshold = Math.Min(R(netIncome * 0.03m), j.MedicalThreshold);
            Credit($"Medical expenses over {Money.Format(medicalThreshold)}", medical - medicalThreshold, F("Line 33099"), true);

            if (donations > 0)
            {
                var first = Math.Min(200m, donations);
                var rest = donations - first;
                var atTop = j.DonationTopRate is null ? 0 : Math.Min(rest, Math.Max(0, taxable - j.TopBracketStart));
                var credit = first * j.DonationLowRate + atTop * (j.DonationTopRate ?? 0) + (rest - atTop) * j.DonationHighRate;
                lines.Add(new("Donations and gifts", -R(credit), F("Line 34900"), true));
            }

            var dividendCredit = eligibleGrossed * j.EligibleDividendCredit + otherGrossed * j.OtherDividendCredit;
            if (dividendCredit > 0) lines.Add(new("Dividend tax credit", -R(dividendCredit), F("Line 40425")));

            basicTax = Math.Max(0, lines.Sum(l => l.Amount));
            return lines;
        }

        var federalLines = Credits(federal, isFederal: true, out var federalBasic);
        var political = PoliticalCredit(claims[TaxCategories.Political]);
        if (political > 0)
        {
            var used = Math.Min(federalBasic, political);
            federalLines.Add(new("Federal political contribution credit", -R(used), "Line 41000", true));
            federalBasic -= used;
        }

        var federalTax = R(federalBasic);

        var provincialLines = Credits(province, isFederal: false, out var provincialBasic);
        var provincialTax = provincialBasic;
        if (province.Code == "ON")
        {
            var on = rates.Ontario;
            var surtax = R(Math.Max(0, provincialBasic - on.SurtaxThreshold1) * on.SurtaxRate1 + Math.Max(0, provincialBasic - on.SurtaxThreshold2) * on.SurtaxRate2);
            if (surtax > 0) provincialLines.Add(new("Ontario surtax", surtax));
            var withSurtax = provincialBasic + surtax;
            var reduction = R(Math.Min(withSurtax, Math.Max(0, 2 * on.BasicReduction - withSurtax)));
            if (reduction > 0) provincialLines.Add(new("Ontario tax reduction", -reduction));
            var premium = OntarioHealthPremium(taxable);
            if (premium > 0) provincialLines.Add(new("Ontario Health Premium", premium));
            provincialTax = withSurtax - reduction + premium;
        }
        else if (province.Code == "BC")
        {
            var bc = rates.BritishColumbia;
            var reduction = R(Math.Min(provincialBasic, Math.Max(0, bc.Amount - Math.Max(0, netIncome - bc.Threshold) * bc.Rate)));
            if (reduction > 0) provincialLines.Add(new("BC tax reduction", -reduction));
            provincialTax -= reduction;
        }

        provincialTax = R(provincialTax);

        // ------------------------------------------------------------ totals
        var other = new List<TaxEstimateLine>();
        if (seFirst + seSecond > 0) other.Add(new("CPP contributions on self-employment", R(seFirst + seSecond), "Line 42100"));
        if (oasRecovery > 0) other.Add(new("Old Age Security recovery", oasRecovery, "Line 42200"));
        var totalPayable = federalTax + provincialTax + other.Sum(l => l.Amount);

        var paid = new List<TaxEstimateLine>();
        if (V(s.IncomeTaxDeducted) > 0) paid.Add(new("Income tax deducted on your slips", R(V(s.IncomeTaxDeducted)), "Line 43700"));
        if (V(s.InstalmentsPaid) > 0) paid.Add(new("Instalments paid", R(V(s.InstalmentsPaid)), "Line 47600"));
        var multigenerational = R(Math.Min(claims[TaxCategories.MultigenerationalRenovation], TaxYearRates.MultigenerationalLimit) * federal.CreditRate);
        if (multigenerational > 0) paid.Add(new("Multigenerational home renovation credit", multigenerational, "Line 45355", true));
        var educator = R(Math.Min(claims[TaxCategories.EducatorSupplies], TaxYearRates.EducatorSuppliesLimit) * TaxYearRates.EducatorSuppliesRate);
        if (educator > 0) paid.Add(new("Educator school supply credit", educator, "Line 46900", true));
        var totalPaid = paid.Sum(l => l.Amount);

        if (claims.Pending > 0)
        {
            notes.Add($"{claims.Pending} suggested claim(s) ({Money.Format(claims.PendingAmount)}) are waiting for review and aren't counted.");
        }

        if (V(s.PensionAdjustment) > 0)
        {
            notes.Add($"Your pension adjustment of {Money.Format(V(s.PensionAdjustment))} doesn't change this year's tax; it lowers next year's RRSP room.");
        }

        return new TaxEstimate(
            rates.Year,
            province.Code,
            province.Name,
            income,
            totalIncome,
            deductions,
            netIncome,
            taxable,
            federalLines,
            federalTax,
            provincialLines,
            provincialTax,
            other,
            totalPayable,
            paid,
            totalPaid,
            totalPayable - totalPaid,
            MarginalRate: 0,
            RrspSavingsPer1000: null,
            rrspRoomLeft,
            notes);
    }

    /// <summary>75% of the first $400, 50% of the next $350 and a third of the next $525: at most $650.</summary>
    public static decimal PoliticalCredit(decimal contributions)
    {
        if (contributions <= 0) return 0;
        var credit = Math.Min(contributions, 400m) * 0.75m
            + Math.Clamp(contributions - 400m, 0, 350m) * 0.5m
            + Math.Clamp(contributions - 750m, 0, 525m) / 3m;
        return Math.Min(650m, Math.Round(credit, 2, MidpointRounding.AwayFromZero));
    }

    /// <summary>The Ontario Health Premium, by taxable income: from $0 at $20,000 to $900 above $200,600.</summary>
    public static decimal OntarioHealthPremium(decimal taxable)
    {
        decimal premium = taxable switch
        {
            <= 20_000m => 0,
            <= 36_000m => Math.Min(300m, (taxable - 20_000m) * 0.06m),
            <= 48_000m => Math.Min(450m, 300m + (taxable - 36_000m) * 0.06m),
            <= 72_000m => Math.Min(600m, 450m + (taxable - 48_000m) * 0.25m),
            <= 200_000m => Math.Min(750m, 600m + (taxable - 72_000m) * 0.25m),
            _ => Math.Min(900m, 750m + (taxable - 200_000m) * 0.25m),
        };
        return Math.Round(premium, 2, MidpointRounding.AwayFromZero);
    }
}
