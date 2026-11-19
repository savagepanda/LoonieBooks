using MapleLedger.Core;
using MapleLedger.Core.Investments;
using MapleLedger.Core.Reports;
using MapleLedger.Data;
using MapleLedger.Fdx;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MapleLedger.UnitTests.Investments;

/// <summary>
/// Net worth by month following the holdings' month-end prices, from stocktreemap.com's monthly
/// price history feed (a shared symbol list plus one small file per month).
/// </summary>
public sealed class PriceHistoryTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<LedgerDbContext> _options;
    private readonly List<LedgerDbContext> _contexts = [];

    public PriceHistoryTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<LedgerDbContext>().UseSqlite(_connection).Options;
        using var db = new LedgerDbContext(_options);
        db.Database.Migrate();
    }

    public void Dispose()
    {
        foreach (var db in _contexts) db.Dispose();
        _connection.Dispose();
    }

    private LedgerService For(string owner)
    {
        var db = new LedgerDbContext(_options) { OwnerId = owner };
        _contexts.Add(db);
        return new LedgerService(db);
    }

    private static DateOnly D(int y, int m, int d = 1) => new(y, m, d);

    // ---------------------------------------------------------------- the feed's files

    [Fact]
    public void SymbolsAreFoundByTheirPlaceInTheList()
    {
        var symbols = StockTreeMapHistory.ParseSymbols("""{"v":1,"count":4,"symbols":["AAPL",null,"xeqt.to","SPY"]}""");

        Assert.Equal(4, symbols.Count);
        Assert.Equal(0, symbols.IndexOf("aapl"));
        Assert.Equal(2, symbols.IndexOf("XEQT.TO"));
        Assert.Null(symbols.IndexOf("VFV.TO"));
    }

    [Theory]
    [InlineData("""{"v":2,"symbols":["AAPL"]}""")]
    [InlineData("""{"v":1}""")]
    [InlineData("not json")]
    public void ADocumentThatIsntTheListIsEmpty(string json) =>
        Assert.Equal(0, StockTreeMapHistory.ParseSymbols(json).Count);

    [Fact]
    public void AMonthsClosesAreInListOrder()
    {
        var month = StockTreeMapHistory.ParseMonth("""{"v":1,"month":"2026-10","count":5,"closes":[351.9,null,44.12,0]}""")!;

        Assert.Equal(D(2026, 10), month.Month);
        Assert.Equal(5, month.Count);
        Assert.Equal(351.9m, month.CloseAt(0));
        Assert.Null(month.CloseAt(1));
        Assert.Equal(44.12m, month.CloseAt(2));
        Assert.Null(month.CloseAt(3)); // not a price
        Assert.Null(month.CloseAt(4)); // left off the end: none that month
        Assert.Equal("2026-10.json", StockTreeMapHistory.MonthFile(D(2026, 10)));
    }

    [Theory]
    [InlineData("""{"v":1,"month":"2026-13","count":1,"closes":[]}""")]
    [InlineData("""{"v":1,"month":"2026-10","closes":[]}""")]
    [InlineData("""{"v":1,"count":1,"closes":[]}""")]
    [InlineData("[]")]
    public void ADocumentThatIsntAMonthIsNull(string json) =>
        Assert.Null(StockTreeMapHistory.ParseMonth(json));

    [Fact]
    public void OnlyMonthsThatHaveEndedAreInView_NewestFirst()
    {
        // A 12-month chart on Nov 18: Dec 2025 to Oct 2026 have ended, November hasn't.
        var months = HoldingPriceMoves.MonthsInView(D(2025, 11, 19), D(2026, 11, 18), D(2026, 11, 18), 120);

        Assert.Equal(12, months.Count);
        Assert.Equal(D(2026, 10), months[0]);
        Assert.Equal(D(2025, 11), months[^1]);
        Assert.Equal(3, HoldingPriceMoves.MonthsInView(D(2020, 1), D(2026, 11, 18), D(2026, 11, 18), 3).Count);
        Assert.Empty(HoldingPriceMoves.MonthsInView(D(2026, 11, 2), D(2026, 11, 18), D(2026, 11, 18), 120));
    }

    [Theory]
    [InlineData(40, 41, false)]
    [InlineData(20, 40, true)]  // 2-for-1
    [InlineData(40, 60, true)]  // 3-for-2
    [InlineData(40, 26.67, true)] // 2-for-3 reverse
    [InlineData(100, 10, true)] // 1-for-10 reverse
    [InlineData(40, 52, false)] // a 23% drop in a month: not a split
    public void APriceFarFromLastMonthsCloseLooksLikeASplit(double price, double close, bool split) =>
        Assert.Equal(split, HoldingPriceMoves.LooksSplit((decimal)price, (decimal)close));

    // ---------------------------------------------------------------- the market's part of net worth

    private static readonly HoldingsSnapshot Tfsa = new("tfsa", D(2026, 11, 14),
    [
        new Holding("XEQT", "iShares Core Equity", 100m, 40m, 4000m, 3000m, PriceAsOf: new DateTimeOffset(2026, 11, 18, 21, 0, 0, TimeSpan.Zero), PriceSource: HoldingPriceSource.Quote),
        new Holding("AAPL", "Apple", 5m, 200m, 1000m, 950m, "USD", 1.4m),
        new Holding(Holding.CashSymbol, "Cash", null, null, 500m, null),
    ]);

    private static readonly PriceReading[] Closes =
    [
        new("XEQT", "CAD", D(2026, 10), 38m),
        new("XEQT", "CAD", D(2026, 9), 36m),
        new("AAPL", "USD", D(2026, 10), 180m),
    ];

    [Fact]
    public void HoldingsMoveByTheirMonthEndCloses_InCanadianDollars()
    {
        var rates = new CurrencyRates([new ExchangeRate("USD", 1.4m, D(2026, 11, 1))]);
        var moves = HoldingPriceMoves.ByAccount([Tfsa], Closes, rates)["tfsa"];

        // October: XEQT 100 × (38 − 40) = −200; AAPL 5 × (180 − 200) = −100 USD = −140.
        Assert.Equal(-340m, moves(D(2026, 10, 31)));
        // September: only XEQT has a close (−400); AAPL has none, so it adds nothing.
        Assert.Equal(-400m, moves(D(2026, 9, 30)));
        // November is the month of the current prices, and August has no closes: nothing estimated.
        Assert.Null(moves(D(2026, 11, 18)));
        Assert.Null(moves(D(2026, 8, 31)));
    }

    [Fact]
    public void WithoutClosesNothingMoves() =>
        Assert.Empty(HoldingPriceMoves.ByAccount([Tfsa], [], CurrencyRates.None));

    [Fact]
    public void ACloseOnlyCountsBeforeTheMonthOfTheHoldingsPrice()
    {
        // Priced by an October statement, never by the quote feed: October's close isn't a move.
        HoldingsSnapshot october = new("rrsp", D(2026, 10, 31), [new Holding("XEQT", null, 10m, 40m, 400m, null)]);
        var moves = HoldingPriceMoves.ByAccount([october], Closes, CurrencyRates.None)["rrsp"];

        Assert.Null(moves(D(2026, 10, 31)));
        Assert.Equal(-40m, moves(D(2026, 9, 30)));
    }

    [Fact]
    public void NetWorthByMonthAddsTheMovesToInvestmentAccounts()
    {
        Account[] accounts =
        [
            new() { AccountId = "tfsa", AccountCategory = AccountCategory.InvestmentAccount, AccountType = AccountType.Brokerage, CurrentBalance = 5000 },
            new() { AccountId = "visa", AccountCategory = AccountCategory.LocAccount, AccountType = AccountType.CreditCard, CurrentBalance = 300 },
        ];
        var moves = new Dictionary<string, Func<DateOnly, decimal?>> { ["tfsa"] = end => end.Month == 10 ? -340m : 0m, ["visa"] = _ => 1_000_000m };

        var months = CashFlowCalculator.MonthlyNetWorth(accounts, [], D(2026, 10), D(2026, 11, 18), marketMoves: moves);

        Assert.Equal(4660m, months[0].Assets);
        Assert.Equal(300m, months[0].Liabilities); // a liability isn't moved
        Assert.Equal(5000m, months[1].Assets);    // the last month still matches the Net worth tile
    }

    // ---------------------------------------------------------------- the ledger

    private static AccountRequest Brokerage() =>
        new("TFSA", "Wealthsimple", AccountCategory.InvestmentAccount, AccountType.Brokerage, null, null, null, null);

    [Fact]
    public async Task ClosesAreSavedOncePerSymbolCurrencyAndMonth_AndAreTheOwnersOnly()
    {
        var alice = For("alice");
        var first = await alice.ApplyPriceHistoryAsync(new([new("XEQT", "cad", D(2026, 10, 15), 38m), new("XEQT", "CAD", D(2026, 9), 36m)]));
        Assert.Equal(2, first.Readings);

        // The same again changes nothing; a split-adjusted close replaces the old one.
        Assert.Equal(0, (await For("alice").ApplyPriceHistoryAsync(new([new("xeqt", "CAD", D(2026, 10), 38m)]))).Readings);
        Assert.Equal(1, (await For("alice").ApplyPriceHistoryAsync(new([new("XEQT", "CAD", D(2026, 10), 19m)]))).Readings);

        var saved = await For("alice").GetPriceHistoryAsync();
        Assert.Equal(2, saved.Count);
        Assert.Contains(new PriceReading("XEQT", "CAD", D(2026, 10), 19m), saved);
        Assert.Empty(await For("bob").GetPriceHistoryAsync());
    }

    [Theory]
    [InlineData("", "CAD", 10)]
    [InlineData("XEQT", "CA", 10)]
    [InlineData("XEQT", "CAD", 0)]
    [InlineData("XEQT", "CAD", -1)]
    public async Task AClosesThatCantBeRightIsRefused(string symbol, string currency, double close) =>
        await Assert.ThrowsAsync<LedgerValidationException>(() =>
            For("alice").ApplyPriceHistoryAsync(new([new(symbol, currency, D(2026, 10), (decimal)close)])));

    [Fact]
    public async Task AMonthStillToComeIsRefused() =>
        await Assert.ThrowsAsync<LedgerValidationException>(() =>
            For("alice").ApplyPriceHistoryAsync(new([new("XEQT", "CAD", DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(2), 10m)])));

    [Fact]
    public async Task NetWorthByMonthFollowsTheSavedCloses()
    {
        var alice = For("alice");
        var tfsa = await alice.CreateAccountAsync(Brokerage());
        var lastMonth = HoldingPriceMoves.FirstOfMonth(DateOnly.FromDateTime(DateTime.UtcNow)).AddMonths(-1);
        await alice.ImportHoldingsAsync(tfsa.AccountId, new(DateOnly.FromDateTime(DateTime.UtcNow), [new Holding("XEQT", "iShares Core Equity", 100m, 40m, 4000m, 3000m)]));

        var from = lastMonth;
        var to = DateOnly.FromDateTime(DateTime.UtcNow);
        var before = await For("alice").GetNetWorthByMonthAsync(from, to);
        Assert.Equal(4000m, before[0].Assets); // no closes yet: flat, as before

        await For("alice").ApplyPriceHistoryAsync(new([new("XEQT", "CAD", lastMonth, 35m)]));
        var after = await For("alice").GetNetWorthByMonthAsync(from, to);

        Assert.Equal(3500m, after[0].Assets); // last month at its close
        Assert.Equal(4000m, after[^1].Assets); // this month at today's price, matching the tile
    }

    [Fact]
    public async Task AccountValueByMonthFollowsTheSavedCloses_AndIsTheOwnersOnly()
    {
        var alice = For("alice");
        var tfsa = await alice.CreateAccountAsync(Brokerage());
        var lastMonth = HoldingPriceMoves.FirstOfMonth(DateOnly.FromDateTime(DateTime.UtcNow)).AddMonths(-1);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await alice.ImportHoldingsAsync(tfsa.AccountId, new(today, [new Holding("XEQT", "iShares Core Equity", 100m, 40m, 4000m, 3000m)]));
        await For("alice").ApplyPriceHistoryAsync(new([new("XEQT", "CAD", lastMonth, 35m)]));

        var months = await For("alice").GetAccountValueByMonthAsync(tfsa.AccountId, lastMonth, today);

        Assert.Equal([3500m, 4000m], months.Select(m => m.Value));
        Assert.Empty(await For("bob").GetAccountValueByMonthAsync(tfsa.AccountId, lastMonth, today));
    }

    [Fact]
    public async Task ClearingTheLedgerRemovesItsCloses()
    {
        await For("alice").ApplyPriceHistoryAsync(new([new("XEQT", "CAD", D(2026, 10), 38m)]));
        await For("alice").ClearAsync();
        Assert.Empty(await For("alice").GetPriceHistoryAsync());
    }
}
