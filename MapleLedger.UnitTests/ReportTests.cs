using MapleLedger.Core;
using MapleLedger.Core.Investments;
using MapleLedger.Core.Reports;
using MapleLedger.Core.Tax;
using MapleLedger.Fdx;

namespace MapleLedger.UnitTests;

public class ReportTests
{
    private static int _next;

    private static Transaction Tx(string date, decimal amount, DebitCreditMemo direction, string category, string account = "chq") => new()
    {
        TransactionId = $"t{Interlocked.Increment(ref _next)}",
        AccountId = account,
        TransactionTimestamp = DateTimeOffset.Parse(date + "T00:00:00Z"),
        Amount = amount,
        DebitCreditMemo = direction,
        Category = category,
        Description = category,
    };

    [Fact]
    public void SpendingAndIncomeAddUpByAccount()
    {
        Transaction[] txns =
        [
            Tx("2026-09-01", 3000m, DebitCreditMemo.Credit, Categories.Income),
            Tx("2026-09-02", 80m, DebitCreditMemo.Debit, Categories.FoodAndDining),
            Tx("2026-09-05", 200m, DebitCreditMemo.Debit, Categories.Shopping, "visa"),
            Tx("2026-09-06", 50m, DebitCreditMemo.Credit, Categories.Shopping, "visa"), // refund
            Tx("2026-09-07", 30m, DebitCreditMemo.Debit, Categories.FoodAndDining, "visa"),
            Tx("2026-09-10", 500m, DebitCreditMemo.Debit, Categories.Transfer),
            Tx("2026-09-10", 500m, DebitCreditMemo.Credit, Categories.Transfer, "visa"),
            Tx("2026-09-12", 12m, DebitCreditMemo.Credit, Categories.Income, "savings"),
        ];

        Assert.Equal([new CategoryTotal("visa", 180m, 3), new CategoryTotal("chq", 80m, 1)], CashFlowCalculator.SpendingByAccount(txns));
        Assert.Equal([new CategoryTotal("chq", 3000m, 1), new CategoryTotal("savings", 12m, 1)], CashFlowCalculator.IncomeByAccount(txns));
    }

    [Fact]
    public void TransfersAreExcludedAndRefundsReduceSpending()
    {
        Transaction[] txns =
        [
            Tx("2026-09-01", 3000m, DebitCreditMemo.Credit, Categories.Income),
            Tx("2026-09-05", 200m, DebitCreditMemo.Debit, Categories.Shopping),
            Tx("2026-09-06", 50m, DebitCreditMemo.Credit, Categories.Shopping), // refund
            Tx("2026-09-10", 1000m, DebitCreditMemo.Debit, Categories.Transfer),
            Tx("2026-09-10", 1000m, DebitCreditMemo.Credit, Categories.Transfer, "visa"),
        ];

        var (income, expenses) = CashFlowCalculator.Totals(txns);
        var shopping = Assert.Single(CashFlowCalculator.SpendingByCategory(txns));

        Assert.Equal(3000m, income);
        Assert.Equal(150m, expenses);
        Assert.Equal(new CategoryTotal(Categories.Shopping, 150m, 2), shopping);
    }

    [Fact]
    public void CategoriesTotalAtTheTopLevelAndBreakDownBySubCategory()
    {
        Transaction[] txns =
        [
            Tx("2026-09-01", 3000m, DebitCreditMemo.Credit, Categories.Income) with { SubCategory = Categories.Salary },
            Tx("2026-09-15", 3000m, DebitCreditMemo.Credit, Categories.Income) with { SubCategory = Categories.Salary },
            Tx("2026-09-20", 800m, DebitCreditMemo.Credit, Categories.Income) with { SubCategory = Categories.BusinessIncome },
            Tx("2026-09-21", 100m, DebitCreditMemo.Debit, Categories.Income) with { SubCategory = Categories.BusinessIncome }, // client refund
            Tx("2026-09-22", 40m, DebitCreditMemo.Credit, Categories.Income) with { SubCategory = Categories.OtherIncome },
            Tx("2026-09-23", 25m, DebitCreditMemo.Credit, Categories.Income), // filed at the top level
            Tx("2026-09-24", 60m, DebitCreditMemo.Debit, Categories.AutoAndTransport) with { SubCategory = Categories.Gas },
            Tx("2026-09-25", 15m, DebitCreditMemo.Debit, Categories.AutoAndTransport) with { SubCategory = Categories.Parking },
        ];

        Assert.Equal([new CategoryTotal(Categories.Income, 6765m, 6)], CashFlowCalculator.IncomeByCategory(txns));
        Assert.Equal(
            [new CategoryTotal(Categories.Income, 6000m, 2, Categories.Salary), new CategoryTotal(Categories.Income, 700m, 2, Categories.BusinessIncome),
             new CategoryTotal(Categories.Income, 40m, 1, Categories.OtherIncome), new CategoryTotal(Categories.Income, 25m, 1)],
            CashFlowCalculator.IncomeDetails(txns));
        Assert.Equal([new CategoryTotal(Categories.AutoAndTransport, 75m, 2)], CashFlowCalculator.SpendingByCategory(txns));
        Assert.Equal(
            [new CategoryTotal(Categories.AutoAndTransport, 60m, 1, Categories.Gas), new CategoryTotal(Categories.AutoAndTransport, 15m, 1, Categories.Parking)],
            CashFlowCalculator.SpendingDetails(txns));
    }

    [Fact]
    public void UncategorizedMoneyCountsByDirection()
    {
        var (income, expenses) = CashFlowCalculator.Totals(
        [
            Tx("2026-09-01", 40m, DebitCreditMemo.Credit, Categories.Uncategorized),
            Tx("2026-09-01", 15m, DebitCreditMemo.Debit, Categories.Uncategorized),
        ]);

        Assert.Equal(40m, income);
        Assert.Equal(15m, expenses);
    }

    [Fact]
    public void MonthlyIncludesEmptyMonthsInRange()
    {
        var months = CashFlowCalculator.Monthly(
            [Tx("2026-07-15", 100m, DebitCreditMemo.Debit, Categories.FoodAndDining), Tx("2026-09-15", 500m, DebitCreditMemo.Credit, Categories.Income)],
            new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30));

        Assert.Equal(
            [new MonthlyCashFlow(2026, 7, 0, 100), new MonthlyCashFlow(2026, 8, 0, 0), new MonthlyCashFlow(2026, 9, 500, 0)],
            months);
    }

    [Fact]
    public void NetWorthSubtractsLiabilitiesAndSkipsClosedAccounts()
    {
        Account[] accounts =
        [
            new() { AccountId = "chq", AccountCategory = AccountCategory.DepositAccount, AccountType = AccountType.Checking, CurrentBalance = 5000 },
            new() { AccountId = "visa", AccountCategory = AccountCategory.LocAccount, AccountType = AccountType.CreditCard, CurrentBalance = 1200 },
            new() { AccountId = "old", AccountCategory = AccountCategory.DepositAccount, AccountType = AccountType.Savings, CurrentBalance = 99, Status = AccountStatus.Closed },
        ];

        var summary = CashFlowCalculator.NetWorth(accounts);

        Assert.Equal(new NetWorthSummary(5000, 1200), summary);
        Assert.Equal(3800, summary.NetWorth);
    }

    [Fact]
    public void NetWorthConvertsBalancesFromTheirAccountsCurrencies()
    {
        Account[] accounts =
        [
            new() { AccountId = "chq", AccountCategory = AccountCategory.DepositAccount, AccountType = AccountType.Checking, CurrentBalance = 5000 },
            new() { AccountId = "usd", AccountCategory = AccountCategory.InvestmentAccount, AccountType = AccountType.Brokerage, CurrentBalance = 1000, Currency = new Currency("USD") },
            new() { AccountId = "eur", AccountCategory = AccountCategory.DepositAccount, AccountType = AccountType.Savings, CurrentBalance = 100, Currency = new Currency("EUR") },
        ];
        var rates = new CurrencyRates([new ExchangeRate("USD", 1.4m, new DateOnly(2026, 11, 16))]);

        // EUR has no rate: counted at face value.
        Assert.Equal(new NetWorthSummary(5000 + 1400 + 100, 0), CashFlowCalculator.NetWorth(accounts, rates));
        Assert.Equal(new NetWorthSummary(6100, 0), CashFlowCalculator.NetWorth(accounts));
    }

    [Fact]
    public void NetWorthByMonthWorksBackFromTodaysBalances()
    {
        Account[] accounts =
        [
            // Balance set on Nov 10; a later transaction (not in the balance yet) is left out.
            new() { AccountId = "chq", AccountCategory = AccountCategory.DepositAccount, AccountType = AccountType.Checking, CurrentBalance = 5000, BalanceAsOf = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero) },
            new() { AccountId = "visa", AccountCategory = AccountCategory.LocAccount, AccountType = AccountType.CreditCard, CurrentBalance = 300 },
            // Closed: worth nothing now, but held what was moved out of it in October.
            new() { AccountId = "old", AccountCategory = AccountCategory.DepositAccount, AccountType = AccountType.Savings, CurrentBalance = 99, Status = AccountStatus.Closed },
            // No balance: counts as nothing, then as now.
            new() { AccountId = "none", AccountCategory = AccountCategory.DepositAccount, AccountType = AccountType.Savings },
        ];
        Transaction[] transactions =
        [
            Tx("2026-10-05", 2000, DebitCreditMemo.Credit, Categories.Transfer, "chq"),
            Tx("2026-10-05", 2000, DebitCreditMemo.Debit, Categories.Transfer, "old"),
            Tx("2026-11-02", 3000, DebitCreditMemo.Credit, Categories.Income, "chq"),
            Tx("2026-11-12", 400, DebitCreditMemo.Debit, Categories.Uncategorized, "chq"),
            Tx("2026-11-03", 300, DebitCreditMemo.Debit, Categories.Uncategorized, "visa"),
            Tx("2026-11-04", 500, DebitCreditMemo.Credit, Categories.Transfer, "none"),
        ];

        var months = CashFlowCalculator.MonthlyNetWorth(accounts, transactions, new DateOnly(2026, 9, 15), new DateOnly(2026, 11, 18), realEstate: 100_000);

        Assert.Equal([new DateOnly(2026, 9, 30), new DateOnly(2026, 10, 31), new DateOnly(2026, 11, 18)], months.Select(m => m.AsOf));
        // Sept: chequing before both credits (0), the closed account before its 2,000 went out, the card before its charge.
        Assert.Equal(new MonthlyNetWorth(2026, 9, new DateOnly(2026, 9, 30), 2000, 0, 100_000), months[0]);
        Assert.Equal(new MonthlyNetWorth(2026, 10, new DateOnly(2026, 10, 31), 2000, 0, 100_000), months[1]);
        // The last month matches the Net worth tile.
        Assert.Equal(CashFlowCalculator.NetWorth(accounts, realEstate: 100_000).NetWorth, months[2].NetWorth);
        Assert.Equal(104_700, months[2].NetWorth);
    }

    [Fact]
    public void NetWorthByMonthConvertsAtTodaysRates()
    {
        Account[] accounts = [new() { AccountId = "usd", AccountCategory = AccountCategory.DepositAccount, AccountType = AccountType.Checking, CurrentBalance = 1000, Currency = new Currency("USD") }];
        var rates = new CurrencyRates([new ExchangeRate("USD", 1.4m, new DateOnly(2026, 11, 16))]);

        var months = CashFlowCalculator.MonthlyNetWorth(accounts, [Tx("2026-11-02", 100, DebitCreditMemo.Credit, Categories.Income, "usd")], new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 18), rates);

        Assert.Equal([1260m, 1400m], months.Select(m => m.Assets));
    }

    [Fact]
    public void AccountValueByMonthStaysInTheAccountsCurrency()
    {
        var usd = new Account { AccountId = "usd", AccountCategory = AccountCategory.DepositAccount, AccountType = AccountType.Checking, CurrentBalance = 1000, Currency = new Currency("USD") };
        var rates = new CurrencyRates([new ExchangeRate("USD", 1.4m, new DateOnly(2026, 11, 16))]);
        Transaction[] transactions =
        [
            Tx("2026-11-02", 100, DebitCreditMemo.Credit, Categories.Income, "usd"),
            Tx("2026-11-03", 999, DebitCreditMemo.Credit, Categories.Income, "other"), // another account's
        ];

        var months = CashFlowCalculator.MonthlyAccountValue(usd, transactions, new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 18), rates);

        Assert.Equal([new MonthlyAccountValue(2026, 10, new DateOnly(2026, 10, 31), 900), new MonthlyAccountValue(2026, 11, new DateOnly(2026, 11, 18), 1000)], months);
    }

    [Fact]
    public void AccountValueByMonthIsWhatsOwedOnALiability()
    {
        var visa = new Account { AccountId = "visa", AccountCategory = AccountCategory.LocAccount, AccountType = AccountType.CreditCard, CurrentBalance = 300 };
        Transaction[] transactions = [Tx("2026-11-03", 300, DebitCreditMemo.Debit, Categories.Uncategorized, "visa")];

        // A market move never applies to what's owed.
        var months = CashFlowCalculator.MonthlyAccountValue(visa, transactions, new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 18), marketMove: _ => 1_000_000m);

        Assert.Equal([0m, 300m], months.Select(m => m.Value));
    }

    [Fact]
    public void AccountValueByMonthMovesAnInvestmentAccountInItsOwnCurrency_AsAnEstimate()
    {
        var tfsa = new Account { AccountId = "tfsa", AccountCategory = AccountCategory.InvestmentAccount, AccountType = AccountType.Brokerage, CurrentBalance = 5000, Currency = new Currency("USD") };
        var rates = new CurrencyRates([new ExchangeRate("USD", 1.4m, new DateOnly(2026, 11, 16))]);

        // The move is in Canadian dollars: -140 CAD is -100 USD.
        // No close for November (the month of its price): that month isn't estimated.
        var months = CashFlowCalculator.MonthlyAccountValue(tfsa, [], new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 18), rates, end => end.Month == 10 ? -140m : null);

        Assert.Equal([4900m, 5000m], months.Select(m => m.Value));
        Assert.Equal([true, false], months.Select(m => m.Estimated));
    }

    [Fact]
    public void AccountValueByMonthNeedsABalance()
    {
        var none = new Account { AccountId = "none", AccountCategory = AccountCategory.DepositAccount, AccountType = AccountType.Savings };
        Assert.Empty(CashFlowCalculator.MonthlyAccountValue(none, [], new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 18)));
    }

    [Theory]
    [InlineData(1234.5, "CAD", "CA$1,234.50")]
    [InlineData(1234.5, "USD", "US$1,234.50")]
    [InlineData(-5, "USD", "-US$5.00")]
    [InlineData(1234.5, "EUR", "EUR 1,234.50")]
    [InlineData(-0.001, "CAD", "CA$0.00")]
    public void EveryAmountNamesItsCurrency(double amount, string currency, string expected) =>
        Assert.Equal(expected, Money.Format((decimal)amount, currency));

    [Theory]
    [InlineData(44.03, "USD", "US$44.03")]
    [InlineData(0.0525, "CAD", "CA$0.0525")]
    [InlineData(1234.5, "EUR", "EUR 1,234.50")]
    public void PricesKeepUpToFourDecimals(double price, string currency, string expected) =>
        Assert.Equal(expected, Money.FormatPrice((decimal)price, currency));

    [Fact]
    public void HoldingsAddUpInTheAccountsCurrency()
    {
        Holding[] holdings = [new("XEQT", null, null, null, 2800m, null), new("AAPL", null, null, null, 1000m, null, "USD", 1.4m)];

        Assert.Equal(4200m, Holding.TotalIn(holdings, "CAD", 1m));
        Assert.Equal(3000m, Holding.TotalIn(holdings, "USD", 1.4m)); // 2800 CAD is 2000 USD
        Assert.Null(Holding.TotalIn(holdings, "USD", null));
        Assert.Equal(1000m, Holding.TotalIn([holdings[1]], "USD", null)); // nothing to convert
    }

    [Fact]
    public void TaxReportSeparatesConfirmedFromSuggestedAndDropsRejected()
    {
        LedgerTransaction[] items =
        [
            new(Tx("2026-03-01", 120m, DebitCreditMemo.Debit, Categories.HealthAndFitness), new TaxTag(TaxCategories.Medical, TaxTagStatus.Confirmed)),
            new(Tx("2026-03-02", 20m, DebitCreditMemo.Credit, Categories.HealthAndFitness), new TaxTag(TaxCategories.Medical, TaxTagStatus.Confirmed)),
            new(Tx("2026-04-01", 35m, DebitCreditMemo.Debit, Categories.HealthAndFitness), new TaxTag(TaxCategories.Medical, TaxTagStatus.Suggested)),
            new(Tx("2026-05-01", 999m, DebitCreditMemo.Debit, Categories.HealthAndFitness), new TaxTag(TaxCategories.Medical, TaxTagStatus.Rejected)),
            new(Tx("2025-12-31", 50m, DebitCreditMemo.Debit, Categories.GiftsAndDonations), new TaxTag(TaxCategories.Charitable, TaxTagStatus.Confirmed)),
            new(Tx("2026-06-01", 10m, DebitCreditMemo.Debit, Categories.FoodAndDining)),
        ];

        var report = TaxReportBuilder.Build(2026, items);

        var medical = Assert.Single(report.Categories);
        Assert.Equal(TaxCategories.Medical, medical.Category.Code);
        Assert.Equal(100m, medical.Confirmed);
        Assert.Equal(35m, medical.Suggested);
        Assert.Equal(3, medical.Transactions.Count);
        Assert.Equal(1, report.PendingReview);
    }

    [Fact]
    public void TaxCsvEscapesAndNeutralizesFormulas()
    {
        var item = new LedgerTransaction(
            Tx("2026-03-01", 120m, DebitCreditMemo.Debit, Categories.HealthAndFitness) with { Description = "=HYPERLINK(\"x\"), DR. SMITH" },
            new TaxTag(TaxCategories.Medical, TaxTagStatus.Confirmed));

        var csv = TaxReportBuilder.ToCsv(TaxReportBuilder.Build(2026, [item]), new Dictionary<string, string> { ["chq"] = "Chequing" });

        var line = csv.Split(Environment.NewLine)[1];
        Assert.Equal("2026,Medical expenses,Line 33099,Confirmed,2026-03-01,Chequing,\"'=HYPERLINK(\"\"x\"\"), DR. SMITH\",120.00,,,,,120.00", line);
    }

    [Theory]
    [InlineData(1234.5, "CA$1,234.50")]
    [InlineData(-12, "-CA$12.00")]
    public void FormatsCanadianDollars(double amount, string expected) => Assert.Equal(expected, Money.Format((decimal)amount));

    [Fact]
    public void WholeAndSignedAmountsNameTheCurrencyToo()
    {
        Assert.Equal("CA$1,235", Money.FormatWhole(1234.6m));
        Assert.Equal("+CA$5.00", Money.FormatSigned(5m));
        Assert.Equal("-CA$5.00", Money.FormatSigned(-5m));
    }

    [Theory]
    [InlineData(999_999.4, "CA$999,999")]
    [InlineData(1_008_686, "CA$1.01M")]
    [InlineData(3_573_807, "CA$3.57M")]
    [InlineData(-2_434_879, "-CA$2.43M")]
    [InlineData(999_996_000, "CA$1.00B")]
    [InlineData(12_345_678_901, "CA$12.35B")]
    public void AmountsFromAMillionUpAreShortened(double amount, string expected) => Assert.Equal(expected, Money.FormatWhole((decimal)amount));

    [Fact]
    public void ShorteningAppliesToEveryCurrencyButNotToPrices()
    {
        Assert.Equal("CA$999,999.99", Money.Format(999_999.99m));
        Assert.Equal("US$1.23M", Money.Format(1_234_567.89m, "USD"));
        Assert.Equal("+EUR 2.00M", "+" + Money.Format(2_000_000m, "EUR"));
        Assert.Equal("+CA$1.50M", Money.FormatSigned(1_500_000m));
        Assert.Equal("US$1,234,567.50", Money.FormatPrice(1_234_567.5m, "USD"));
    }
}
