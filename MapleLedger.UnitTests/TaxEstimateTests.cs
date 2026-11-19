using MapleLedger.Core.Tax;

namespace MapleLedger.UnitTests;

public class TaxEstimateTests
{
    private static TaxClaims Claims(params (string Code, decimal Amount)[] claims) =>
        new(claims.ToDictionary(c => c.Code, c => c.Amount, StringComparer.OrdinalIgnoreCase));

    private static TaxEstimate Estimate(TaxInputs situation, TaxClaims? claims = null) =>
        TaxEstimator.Estimate(2026, situation, claims ?? TaxClaims.None) ?? throw new InvalidOperationException("No estimate");

    [Fact]
    public void OntarioSalaryMatchesAHandCalculation()
    {
        // CPP 4,230.45 + CPP2 416 and EI 1,123.07 are worked out from the salary. The enhanced part
        // (711.00 + 416) is deducted, so net income is 98,873.
        var e = Estimate(new TaxInputs("ON", 1985, EmploymentIncome: 100_000m, IncomeTaxDeducted: 20_000m));

        Assert.Equal(98_873m, e.NetIncome);
        // 16,464.97 on brackets less 14% of BPA 16,452, CPP base 3,519.45, EI 1,123.07 and CEA 1,501.
        Assert.Equal(13_301.60m, e.FederalTax);
        // 6,837.35 less 5.05% credits (890.39) = 5,946.96, + surtax 25.79 + Health Premium 750.
        Assert.Equal(6_722.75m, e.ProvincialTax);
        Assert.Equal(20_024.35m, e.TotalPayable);
        Assert.Equal(24.35m, e.Balance);
        Assert.False(e.IsRefund);
        // 20.5% federal + 9.15% Ontario × 1.2 surtax.
        Assert.Equal(0.3148m, e.MarginalRate);
        Assert.NotNull(e.RrspSavingsPer1000);
    }

    // Take-home pay of a $100,000 Ontario salary: less income tax 20,024.35, CPP 4,230.45 + 416 and EI 1,123.07.
    private const decimal OntarioNetOf100k = 74_206.13m;

    private static TaxEstimate FromIncome(TaxSituation situation, IncomeReceived income, TaxClaims? claims = null) =>
        TaxEstimator.EstimateFromIncome(2026, situation, income, claims ?? TaxClaims.None) ?? throw new InvalidOperationException("No estimate");

    private static IncomeReceived Received(decimal salary = 0, decimal business = 0, decimal rental = 0, decimal other = 0, decimal factor = 1) =>
        new(salary, business, rental, other, new DateOnly(2026, 12, 31), factor);

    [Fact]
    public void NetPayWorksBackToTheSalary()
    {
        Assert.InRange(TaxEstimator.GrossUp(TaxRates.For(2026)!, "ON", OntarioNetOf100k), 99_999.99m, 100_000.01m);
        Assert.Equal(0m, TaxEstimator.GrossUp(TaxRates.For(2026)!, "ON", 0));
    }

    [Fact]
    public void WithheldSalaryAloneLeavesAboutNothingToPay()
    {
        var e = FromIncome(new TaxSituation("ON"), Received(OntarioNetOf100k));

        Assert.InRange(e.Balance, -1m, 1m);
        Assert.InRange(e.TotalPaid, 20_023m, 20_026m);
        Assert.Contains(e.Notes, n => n.Contains("worked back from CA$74,206.13 of net pay"));

        // An RRSP contribution comes back at the marginal rate: 20.5% + 9.15%, plus Ontario surtax on the part still above its threshold.
        var withRrsp = FromIncome(new TaxSituation("ON"), Received(OntarioNetOf100k), Claims((TaxCategories.Rrsp, 5_000m)));
        Assert.True(withRrsp.IsRefund);
        Assert.InRange(-withRrsp.Balance, 1_500m, 1_520m);
    }

    [Fact]
    public void PayrollPlansComeOffNetPayAndTheMatchIsABenefit()
    {
        // $100,000 salary: 15% ESPP and 5% group RRSP come off the pay, the 5% match is on the T4,
        // and the employer withholds on $105,000 less the $10,000 going into the RRSP.
        var withheld = Estimate(new TaxInputs("ON", EmploymentIncome: 105_000m, GroupRrspContributions: 5_000m, EmployerRrspMatch: 5_000m)).TotalPayable;
        var net = 100_000m - 15_000m - 5_000m - withheld - (4_230.45m + 416m + 1_123.07m);
        var plans = new TaxSituation("ON", EsppPercent: 15, RrspPercent: 5, RrspMatchPercent: 5);

        Assert.InRange(TaxEstimator.GrossUp(TaxRates.For(2026)!, "ON", net, plans), 99_999.99m, 100_000.01m);

        var e = FromIncome(plans, Received(net));
        Assert.InRange(Assert.Single(e.Income, l => l.Label.StartsWith("Salary")).Amount, 99_999.99m, 100_000.01m);
        Assert.Equal(5_000m, Assert.Single(e.Income, l => l.Label.Contains("RRSP match")).Amount);
        Assert.InRange(e.TotalIncome, 104_999.99m, 105_000.01m);
        Assert.InRange(Assert.Single(e.Deductions, d => d.Reference == "Line 20800").Amount, 9_999.99m, 10_000.01m);
        Assert.InRange(e.Balance, -1m, 1m);
        Assert.Contains(e.Notes, n => n.Contains("after CA$15,000.00 to the ESPP (15%) and CA$5,000.00 to the group RRSP (5%)"));
        Assert.Contains(e.Notes, n => n.Contains("taxable benefit"));

        // Without the plans, the same deposits look like a much smaller salary.
        Assert.True(FromIncome(new TaxSituation("ON"), Received(net)).TotalIncome < 80_000m);

        // An ESPP match is a taxable benefit, but nothing is deducted for it.
        var matched = FromIncome(plans with { EsppMatchPercent = 50 }, Received(net));
        Assert.Contains(matched.Income, l => l.Label.Contains("ESPP contribution") && l.Amount > 7_000m);
        Assert.True(matched.TotalIncome > e.TotalIncome + 7_000m);
    }

    [Fact]
    public void APensionComesOffPayIsNotABenefitAndLowersRrspRoom()
    {
        // $100,000 salary: 5% to the pension comes off the pay before tax, and the employer's 5% isn't on the T4.
        var withheld = Estimate(new TaxInputs("ON", EmploymentIncome: 100_000m, RppContributions: 5_000m)).TotalPayable;
        var net = 100_000m - 5_000m - withheld - (4_230.45m + 416m + 1_123.07m);
        var plans = new TaxSituation("ON", PensionPercent: 5, PensionMatchPercent: 5);

        Assert.InRange(TaxEstimator.GrossUp(TaxRates.For(2026)!, "ON", net, plans), 99_999.99m, 100_000.01m);

        var e = FromIncome(plans, Received(net));
        Assert.InRange(e.TotalIncome, 99_999.99m, 100_000.01m);
        Assert.InRange(Assert.Single(e.Deductions, d => d.Reference == "Line 20700").Amount, 4_999.99m, 5_000.01m);
        Assert.DoesNotContain(e.Deductions, d => d.Reference == "Line 20800");
        Assert.InRange(e.Balance, -1m, 1m);
        Assert.Contains(e.Notes, n => n.Contains("to the pension plan (5%)"));
        Assert.Contains(e.Notes, n => n.Contains("isn't taxable income") && n.Contains("pension adjustment of about CA$10,000.00") && n.Contains("2027"));
        Assert.DoesNotContain(e.Notes, n => n.StartsWith("Your pension adjustment of"));

        // Both sides' contributions are the pension adjustment, which comes off 18% of earned income.
        var room = e.Rrsp!;
        Assert.InRange(room.PensionAdjustment, 9_999.98m, 10_000.02m);
        Assert.True(room.LimitEstimated);
        Assert.InRange(room.Limit, 7_999.97m, 8_000.03m);
        Assert.Equal(room.Limit, room.NextYearRoom);
        Assert.Equal(room.Limit, room.Left);

        // A pension adjustment from the T4 (a defined benefit plan) replaces the contributions.
        var defined = FromIncome(plans with { PensionAdjustment = 12_000m }, Received(net)).Rrsp!;
        Assert.Equal(12_000m, defined.PensionAdjustment);
        Assert.InRange(defined.Limit, 5_999.97m, 6_000.03m);

        // The notice of assessment's limit already has last year's taken off; only next year's room changes.
        var entered = FromIncome(plans with { RrspDeductionLimit = 20_000m }, Received(net)).Rrsp!;
        Assert.Equal(20_000m, entered.Limit);
        Assert.InRange(entered.NextYearRoom!.Value, 7_999.97m, 8_000.03m);

        // A pension adjustment above the room leaves none, not less than none.
        Assert.Equal(0m, FromIncome(plans with { PensionAdjustment = 50_000m }, Received(net)).Rrsp!.NextYearRoom);
    }

    [Fact]
    public void RrspRoomIsEstimatedFromEarnedIncomeWithoutALimit()
    {
        var plans = new TaxSituation("ON", RrspPercent: 5, RrspMatchPercent: 5);
        var net = 70_000m;
        var e = FromIncome(plans, Received(net), Claims((TaxCategories.Rrsp, 2_000m)));
        var room = e.Rrsp!;
        var employment = e.Income.Where(l => l.Reference == "Line 10100").Sum(l => l.Amount);

        Assert.True(room.LimitEstimated);
        Assert.Equal(33_810m, room.DollarLimit);
        Assert.Equal(Math.Round(employment * 0.18m, 2), room.Limit);
        Assert.Equal(2_000m, room.Tagged);
        Assert.Equal(room.Limit - room.Payroll - room.EmployerMatch - 2_000m, room.Left);
        Assert.Equal(room.Left, e.RrspRoomLeft);
        Assert.Equal(2027, room.NextYear);
        Assert.Equal(room.Limit, room.NextYearRoom);

        // Using the room saves roughly the marginal rate on it; the part above the bracket floor is all at that rate.
        Assert.InRange(room.SavingsIfUsed, room.Left * 0.2m, room.Left * e.MarginalRate + 1);
        // Taxable income is well inside its brackets, so all of the room comes off at the marginal rate.
        Assert.Equal(room.Left, room.AtMarginalRate);

        // At $120,000, only the part above the federal 26% bracket at $117,045 does.
        var near = FromIncome(new TaxSituation("ON", EmployerWithholds: false), Received(120_000m)).Rrsp!;
        Assert.Equal(21_600m, near.Limit);
        Assert.Equal(2_955m, near.AtMarginalRate);

        // High earners are capped at the dollar limit.
        Assert.Equal(33_810m, FromIncome(new TaxSituation("ON"), Received(300_000m)).Rrsp!.Limit);
    }

    [Fact]
    public void AnEnteredRrspLimitReplacesTheEstimateAndCapsTheDeduction()
    {
        var e = FromIncome(new TaxSituation("BC", RrspPercent: 5, RrspDeductionLimit: 40_000m), Received(80_000m), Claims((TaxCategories.Rrsp, 10_000m)));
        var room = e.Rrsp!;
        Assert.False(room.LimitEstimated);
        Assert.Equal(40_000m, room.Limit);
        Assert.Equal(40_000m - room.Payroll - 10_000m, room.Left);

        var over = FromIncome(new TaxSituation("BC", RrspDeductionLimit: 5_000m), Received(80_000m), Claims((TaxCategories.Rrsp, 6_000m)));
        Assert.Equal(0m, over.Rrsp!.Left);
        Assert.Equal(1_000m, over.Rrsp.Over);
        Assert.Equal(0m, over.Rrsp.SavingsIfUsed);
        Assert.Equal(5_000m, Assert.Single(over.Deductions, d => d.Reference == "Line 20800").Amount);
        Assert.Contains(over.Notes, n => n.Contains("over the deduction limit you entered"));
    }

    [Fact]
    public void WithoutWithholdingDepositsAreTaxedAsReceived()
    {
        var e = FromIncome(new TaxSituation("AB", EmployerWithholds: false), Received(salary: 50_000m, business: 30_000m, rental: 12_000m, other: 900m),
            Claims((TaxCategories.Rental, 2_000m)));

        Assert.Equal(50_000m, Assert.Single(e.Income, l => l.Reference == "Line 10100").Amount);
        Assert.Equal(10_000m, Assert.Single(e.Income, l => l.Reference == "Line 12600").Amount);
        Assert.Equal(30_000m, Assert.Single(e.Income, l => l.Reference == "Line 13500").Amount);
        Assert.Empty(e.Paid);
        Assert.Equal(e.TotalPayable, e.Balance);
        Assert.DoesNotContain(e.Federal, l => l.Label == "EI premiums");
        Assert.Contains(e.Notes, n => n.Contains("CA$900.00") && n.Contains("isn't counted"));
    }

    [Fact]
    public void RentalIncomeHasNoTaxTakenOffAndCanBringInstalments()
    {
        var withheld = FromIncome(new TaxSituation("ON"), Received(OntarioNetOf100k));
        Assert.Empty(withheld.NotWithheld);
        Assert.DoesNotContain(withheld.Notes, n => n.Contains("instalments"));

        var e = FromIncome(new TaxSituation("ON"), Received(OntarioNetOf100k, rental: 30_000m), Claims((TaxCategories.Rental, 5_000m)));
        Assert.Equal(25_000m, e.NotWithheldTotal);
        Assert.Equal("rental income", TaxEstimator.NotWithheldKinds(e.NotWithheld));
        Assert.True(e.Balance > TaxRates.InstalmentThreshold);
        Assert.Contains(e.Notes, n => n.Contains("No tax is taken off the CA$25,000.00 of rental income") && n.Contains("April 30, 2027"));
        Assert.Contains(e.Notes, n => n.Contains("quarterly instalments") && n.Contains("2025 or 2024"));

        var small = FromIncome(new TaxSituation("ON"), Received(OntarioNetOf100k, rental: 2_000m));
        Assert.Contains(small.Notes, n => n.Contains("No tax is taken off"));
        Assert.DoesNotContain(small.Notes, n => n.Contains("instalments"));
    }

    [Fact]
    public void RentalSummaryShowsRentsExpensesAndTheTaxTheExpensesSave()
    {
        var e = FromIncome(new TaxSituation("ON"), Received(OntarioNetOf100k, rental: 30_000m), Claims((TaxCategories.Rental, 5_000m)));
        var rental = e.Rental!;
        Assert.Equal((30_000m, 5_000m, 25_000m), (rental.Rents, rental.Expenses, rental.Net));
        Assert.Null(rental.Share);
        Assert.False(rental.Projected);

        // The expenses save tax at the marginal rate: their own estimate without them owes that much more.
        var without = FromIncome(new TaxSituation("ON"), Received(OntarioNetOf100k, rental: 30_000m));
        Assert.Equal(without.Balance - e.Balance, rental.TaxSaved);
        Assert.InRange(rental.TaxSaved, 5_000m * 0.29m, 5_000m * 0.45m);
        Assert.Equal(0m, without.Rental!.TaxSaved);

        // More expenses than rent: a loss that comes off the salary.
        var loss = FromIncome(new TaxSituation("ON"), Received(OntarioNetOf100k, rental: 10_000m), Claims((TaxCategories.Rental, 14_000m)));
        Assert.Equal(-4_000m, loss.Rental!.Net);
        Assert.Equal(-4_000m, Assert.Single(loss.Income, l => l.Reference == "Line 12600").Amount);

        Assert.Null(FromIncome(new TaxSituation("ON"), Received(OntarioNetOf100k)).Rental);
        var split = FromIncome(new TaxSituation("ON"), new IncomeReceived(0, 0, 12_000m, 0, new DateOnly(2026, 12, 31), SplitRental: 24_000m, RentalShare: 0.5m));
        Assert.Equal(0.5m, split.Rental!.Share);
    }

    [Fact]
    public void ASplitRentShowsTheCoOwnersShare()
    {
        var income = new IncomeReceived(0, 0, 12_000m, 0, new DateOnly(2026, 12, 31), SplitRental: 24_000m, RentalShare: 0.5m);
        var e = FromIncome(new TaxSituation("ON"), income);

        Assert.Equal("Net rental income, with your 50% share of joint property (rents less expenses)", Assert.Single(e.Income, l => l.Reference == "Line 12600").Label);
        Assert.Contains(e.Notes, n => n.Contains("your 50% share of CA$24,000.00 in rent split between co-owners") && n.Contains("T776"));
    }

    [Fact]
    public void RentalSharesFillWhatsLeftEqually()
    {
        Assert.Equal([0.5m, 0.5m], TaxSituations.RentalShares([("a", null), ("b", null)]).Values);
        Assert.Equal([0.6m, 0.4m, 0m], TaxSituations.RentalShares([("a", 60m), ("b", null), ("kid", 0m)]).Values);
        Assert.Equal([0.7m, 0.15m, 0.15m], TaxSituations.RentalShares([("a", 70m), ("b", null), ("c", null)]).Values);
        Assert.Equal([1m, 0m], TaxSituations.RentalShares([("a", 100m), ("b", null)]).Values);
    }

    [Fact]
    public void TheYearInProgressIsProjected()
    {
        var e = FromIncome(new TaxSituation("ON"), Received(OntarioNetOf100k / 2, factor: 2));

        Assert.InRange(Assert.Single(e.Income, l => l.Reference == "Line 10100").Amount, 99_999m, 100_001m);
        Assert.Contains(e.Notes, n => n.Contains("projected"));
    }

    [Fact]
    public void QuebecAndUnknownYearsHaveNoEstimate()
    {
        Assert.Null(TaxEstimator.Estimate(2026, new TaxInputs("QC", EmploymentIncome: 50_000m), TaxClaims.None));
        Assert.Null(TaxEstimator.Estimate(2026, new TaxInputs(null, EmploymentIncome: 50_000m), TaxClaims.None));
        Assert.Null(TaxEstimator.Estimate(1999, new TaxInputs("ON", EmploymentIncome: 50_000m), TaxClaims.None));
    }

    [Fact]
    public void RrspCombinesTaggedPayrollAndMatchUpToTheLimit()
    {
        var situation = new TaxInputs("BC", EmploymentIncome: 90_000m, GroupRrspContributions: 3_000m, EmployerRrspMatch: 1_500m, RrspDeductionLimit: 10_000m);
        var e = Estimate(situation, Claims((TaxCategories.Rrsp, 7_000m)));

        var rrsp = Assert.Single(e.Deductions, d => d.Reference == "Line 20800");
        Assert.Equal(10_000m, rrsp.Amount);
        Assert.Equal(0m, e.RrspRoomLeft);
        Assert.Null(e.RrspSavingsPer1000);
        Assert.Contains(e.Notes, n => n.Contains("CA$1,500.00 of RRSP contributions"));

        // The match is income too when it isn't already on the T4.
        var off = Estimate(situation with { EmployerMatchOnT4 = false, RrspDeductionLimit = null }, Claims((TaxCategories.Rrsp, 7_000m)));
        Assert.Equal(91_500m, off.TotalIncome);
        Assert.Equal(11_500m, Assert.Single(off.Deductions, d => d.Reference == "Line 20800").Amount);
    }

    [Fact]
    public void RrspDeductionLowersTaxAtTheMarginalRate()
    {
        var without = Estimate(new TaxInputs("AB", EmploymentIncome: 130_000m));
        var with = Estimate(new TaxInputs("AB", EmploymentIncome: 130_000m), Claims((TaxCategories.Rrsp, 5_000m)));

        // 26% federal + 10% Alberta on the whole $5,000.
        Assert.Equal(1_800m, without.TotalPayable - with.TotalPayable);
        Assert.Equal(360m, with.RrspSavingsPer1000);
    }

    [Fact]
    public void SelfEmployedPayBothCppSharesAndDeductHalf()
    {
        var e = Estimate(new TaxInputs("AB", SelfEmploymentIncome: 60_000m, OtherBusinessExpenses: 4_000m), Claims((TaxCategories.Business, 6_000m)));

        Assert.Equal(50_000m, Assert.Single(e.Income).Amount);
        // 11.9% of (50,000 − 3,500).
        Assert.Equal(5_533.50m, Assert.Single(e.Other).Amount);
        Assert.Equal(2_766.75m, Assert.Single(e.Deductions, d => d.Reference == "Line 22200").Amount);
        Assert.Equal(465m, Assert.Single(e.Deductions, d => d.Reference == "Line 22215").Amount);
        Assert.Equal(46_768.25m, e.NetIncome);
    }

    [Fact]
    public void CreditsComeFromTaggedClaims()
    {
        var e = Estimate(new TaxInputs("ON", EmploymentIncome: 60_000m),
            Claims((TaxCategories.Medical, 3_000m), (TaxCategories.Charitable, 1_200m), (TaxCategories.Political, 400m)));

        // Medical over the lesser of 3% of net income and $2,890.
        var medical = Assert.Single(e.Federal, l => l.Reference == "Line 33099");
        Assert.Equal(-Math.Round((3_000m - 1_783.05m) * 0.14m, 2), medical.Amount);
        // 14% of the first $200, 29% of the rest.
        Assert.Equal(-(28m + 290m), Assert.Single(e.Federal, l => l.Reference == "Line 34900").Amount);
        Assert.Equal(-300m, Assert.Single(e.Federal, l => l.Reference == "Line 41000").Amount);
        Assert.Contains(e.Provincial, l => l.Label == "Donations and gifts" && l.Amount == -(10.10m + 111.60m));
    }

    [Fact]
    public void SeniorsGetAgeAndPensionAmountsAndRepayOas()
    {
        var e = Estimate(new TaxInputs("NS", BirthYear: 1955, PensionIncome: 90_000m, OasBenefits: 8_000m, CppBenefits: 12_000m));

        Assert.Equal(110_000m, e.TotalIncome);
        // 15% of (110,000 − 95,323).
        Assert.Equal(2_201.55m, Assert.Single(e.Other, l => l.Reference == "Line 42200").Amount);
        Assert.Equal(107_798.45m, e.NetIncome);
        // The federal age amount is gone by $107,819… almost: 9,208 − 15% × (107,798.45 − 46,432).
        Assert.Contains(e.Federal, l => l.Reference == "Line 30100");
        Assert.Equal(-280m, Assert.Single(e.Federal, l => l.Reference == "Line 31400").Amount);
    }

    [Fact]
    public void BasicPersonalAmountsPhaseOut()
    {
        var federal = TaxRates.For(2026)!.Federal;
        Assert.Equal(16_452m, federal.BasicPersonalFor(181_440m));
        Assert.Equal(14_829m, federal.BasicPersonalFor(300_000m));
        Assert.InRange(federal.BasicPersonalFor(220_000m), 14_829m, 16_452m);

        var manitoba = TaxRates.For(2026)!.Province("MB")!;
        Assert.Equal(7_890m, manitoba.BasicPersonalFor(300_000m));
        Assert.Equal(0m, manitoba.BasicPersonalFor(450_000m));
    }

    [Fact]
    public void OntarioHealthPremiumAndPoliticalCreditFollowTheirTables()
    {
        Assert.Equal(0m, TaxEstimator.OntarioHealthPremium(20_000m));
        Assert.Equal(300m, TaxEstimator.OntarioHealthPremium(36_000m));
        Assert.Equal(600m, TaxEstimator.OntarioHealthPremium(60_000m));
        Assert.Equal(900m, TaxEstimator.OntarioHealthPremium(250_000m));

        Assert.Equal(300m, TaxEstimator.PoliticalCredit(400m));
        Assert.Equal(475m, TaxEstimator.PoliticalCredit(750m));
        Assert.Equal(650m, TaxEstimator.PoliticalCredit(5_000m));
    }

    [Fact]
    public void EveryProvinceExceptQuebecIsAvailable()
    {
        Assert.Equal(12, TaxRates.ProvinceNames.Count);
        Assert.DoesNotContain(TaxRates.ProvinceNames, p => p.Code == "QC");
        foreach (var (code, _) in TaxRates.ProvinceNames)
        {
            var e = Estimate(new TaxInputs(code, EmploymentIncome: 75_000m));
            Assert.InRange(e.TotalPayable, 10_000m, 20_000m);
        }
    }
}
