using MapleLedger.Core.Categorization;
using MapleLedger.Core.Investments;
using MapleLedger.Core.Tax;
using MapleLedger.Fdx;

namespace MapleLedger.Core.Import;

/// <summary>One account of the sample household, with what goes into it.</summary>
/// <param name="Transactions">As pasted from the bank's website, oldest first; categorized on import like any paste.</param>
/// <param name="Holdings">An investment account's holdings, as of the day the sample is made.</param>
public sealed record SampleAccount(AccountRequest Account, IReadOnlyList<ParsedTransaction> Transactions, IReadOnlyList<Holding> Holdings);

/// <summary>
/// A made-up household for trying LoonieBooks without entering anything: a couple renting in Toronto
/// with a child in daycare, a chequing account, a credit card, savings, an RRSP and a TFSA, and a
/// year of transactions ending on the day it's made (so it always looks recent). Merchants are ones
/// the built-in rules know, so categories and tax suggestions show up, plus a few they don't, so
/// there's something to categorize. Nobody's real data. The same seed gives the same household.
/// </summary>
public static class SampleHousehold
{
    public const int Months = 12;

    private const string ChequingName = "Everyday chequing";
    private const string ChequingLast4 = "4471";
    private const decimal SavingsTransfer = 400.00m;

    /// <summary>The trip's merchants, tagged Vacation when the sample is loaded.</summary>
    private static readonly string[] TripMerchants = ["AIR CANADA", "FAIRMONT BANFF", "BANFF GONDOLA", "BANFF AVE BREWING", "PARKS CANADA BANFF"];

    public static bool IsTrip(string? description) =>
        description is not null && TripMerchants.Any(m => description.StartsWith(m, StringComparison.Ordinal));

    /// <summary>True when the ledger holds the sample household (its chequing account is there).</summary>
    public static bool IsLoaded(IEnumerable<Account> accounts) =>
        accounts.Any(a => a.Nickname == ChequingName && a.AccountNumberDisplay?.EndsWith(ChequingLast4, StringComparison.Ordinal) == true);

    public static IReadOnlyList<SampleAccount> Create(DateOnly today)
    {
        var months = MonthsEnding(today);
        var visa = VisaRows(months, today);
        var asOf = new DateTimeOffset(today.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);

        var chequing = ChequingRows(months, today, visa);
        var savings = SavingsRows(months, today);

        return
        [
            new(new AccountRequest(ChequingName, "TD Canada Trust", AccountCategory.DepositAccount, AccountType.Checking, ChequingLast4,
                    3200m + chequing.Sum(t => t.SignedAmount), asOf, null),
                Numbered(chequing), []),
            // A card's purchases are debits and payments credits; what's owed is a positive balance.
            new(new AccountRequest("Cash back Visa", "TD Canada Trust", AccountCategory.LocAccount, AccountType.CreditCard, "9012",
                    -visa.Sum(t => t.SignedAmount), asOf, 8000m),
                Numbered(visa), []),
            new(new AccountRequest("High interest savings", "TD Canada Trust", AccountCategory.DepositAccount, AccountType.Savings, "7730",
                    6000m + savings.Sum(t => t.SignedAmount), asOf, null),
                Numbered(savings), []),
            new(new AccountRequest("RRSP", "Wealthsimple", AccountCategory.InvestmentAccount, AccountType.Brokerage, null, null, null, null),
                [],
                [
                    Fund("XEQT", "iShares Core Equity ETF Portfolio", 612m, 38.40m, 19_850m, asOf),
                    Fund("VFV", "Vanguard S&P 500 Index ETF", 48m, 162.10m, 6_420m, asOf),
                    Fund("ZAG", "BMO Aggregate Bond Index ETF", 290m, 14.05m, 4_280m, asOf),
                    new Holding(Holding.CashSymbol, "Cash", null, null, 312.55m, null, PriceAsOf: asOf),
                ]),
            new(new AccountRequest("TFSA", "Wealthsimple", AccountCategory.InvestmentAccount, AccountType.Brokerage, null, null, null, null),
                [],
                [
                    Fund("XEQT", "iShares Core Equity ETF Portfolio", 455m, 38.40m, 15_600m, asOf),
                    new Holding(Holding.CashSymbol, "Cash", null, null, 1_045.20m, null, PriceAsOf: asOf),
                ]),
        ];
    }

    /// <summary>
    /// The account's transactions as an import into <paramref name="accountId"/> makes them: categorized
    /// and given tax suggestions by the built-in rules, with the trip tagged Vacation.
    /// </summary>
    public static IReadOnlyList<LedgerTransaction> Transactions(SampleAccount sample, string accountId, TransactionCategorizer categorizer, TaxSuggester taxSuggester)
    {
        var vacation = DefaultTags.All.Single(t => t.Name == "Vacation").Id;
        return LedgerTransactionFactory.Create(accountId, sample.Transactions, categorizer, taxSuggester)
            .Select(t => OwnChoices.FirstOrDefault(c => t.Transaction.Description?.StartsWith(c.Merchant, StringComparison.Ordinal) == true) is { Merchant: not null } choice
                ? t with { Transaction = t.Transaction with { Category = choice.Category, SubCategory = choice.SubCategory }, CategoryIsManual = true }
                : t)
            .Select(t => IsTrip(t.Transaction.Description) ? t with { Tags = [vacation] } : t)
            .ToList();
    }

    /// <summary>
    /// Merchants the built-in rules don't know that the household has already categorized, as if they'd
    /// been using LoonieBooks for a while. The café is left for whoever's trying it.
    /// </summary>
    private static readonly (string Merchant, string Category, string SubCategory)[] OwnChoices =
    [
        ("FAIRMONT BANFF", Categories.Travel, "Lodging"),
        ("BANFF GONDOLA", Categories.Entertainment, "Events"),
        ("BANFF AVE BREWING", Categories.FoodAndDining, "Restaurants"),
        ("PARKS CANADA BANFF", Categories.Travel, "Travel Booking"),
        ("TFR FROM CHEQUING", Categories.Transfer, "Account Transfer"),
    ];

    private static Holding Fund(string symbol, string name, decimal units, decimal price, decimal bookValue, DateTimeOffset asOf) =>
        new(symbol, name, units, price, Math.Round(units * price, 2), bookValue, PriceAsOf: asOf);

    /// <summary>The first day of each of the last <see cref="Months"/> months, this one last.</summary>
    private static List<DateOnly> MonthsEnding(DateOnly today)
    {
        var first = new DateOnly(today.Year, today.Month, 1).AddMonths(1 - Months);
        return Enumerable.Range(0, Months).Select(first.AddMonths).ToList();
    }

    private static IReadOnlyList<ParsedTransaction> Numbered(List<ParsedTransaction> rows) =>
        rows.OrderBy(r => r.Date).Select((r, i) => r with { SourceLine = i + 1 }).ToList();

    private sealed class Rows(DateOnly today) : List<ParsedTransaction>
    {
        /// <summary>Adds a row on <paramref name="day"/> of <paramref name="month"/>, unless that's after today or the month is short.</summary>
        public void Add(DateOnly month, int day, string description, decimal signedAmount)
        {
            if (day > DateTime.DaysInMonth(month.Year, month.Month)) return;
            var date = new DateOnly(month.Year, month.Month, day);
            if (date > today) return;
            var amount = Math.Round(Math.Abs(signedAmount), 2);
            Add(new ParsedTransaction(0, date, null, description, amount, signedAmount >= 0 ? DebitCreditMemo.Credit : DebitCreditMemo.Debit, null));
        }
    }

    private static decimal Between(Random rnd, double low, double high) => (decimal)(low + (high - low) * rnd.NextDouble());

    private static T Pick<T>(Random rnd, params T[] choices) => choices[rnd.Next(choices.Length)];

    private static List<ParsedTransaction> VisaRows(List<DateOnly> months, DateOnly today)
    {
        var rnd = new Random(11);
        var rows = new Rows(today);
        for (var i = 0; i < months.Count; i++)
        {
            var m = months[i];
            foreach (var day in new[] { 2, 9, 16, 23, 29 })
                rows.Add(m, day, Pick(rnd, "LOBLAWS #1043 TORONTO ON", "NO FRILLS #3381 TORONTO ON"), -Between(rnd, 70, 190));
            foreach (var day in new[] { 4, 7, 11, 18, 25 })
                rows.Add(m, day, Pick(rnd, "TIM HORTONS #2211", "STARBUCKS 04456"), -Between(rnd, 3, 8));
            foreach (var day in new[] { 6, 20 })
                rows.Add(m, day, "PETRO CANADA 51123", -Between(rnd, 52, 78));
            rows.Add(m, 9, "NETFLIX.COM", -20.99m);
            rows.Add(m, 14, "SPOTIFY P2F1A9", -11.99m);
            rows.Add(m, 13, "AMAZON.CA*RT5Y2", -Between(rnd, 18, 95));
            rows.Add(m, 27, "AMAZON.CA*K81HZ", -Between(rnd, 18, 95));
            rows.Add(m, 19, "UBER EATS TORONTO", -Between(rnd, 28, 52));
            rows.Add(m, 10, "SHOPPERS DRUG MART #0874", -Between(rnd, 14, 62));
            rows.Add(m, 1, "GOODLIFE FITNESS", -59.99m);
            foreach (var day in new[] { 12, 26 })
                rows.Add(m, day, $"THE HOLLOW TREE CAFE #{Pick(rnd, 12, 14)} TORONTO ON", -Between(rnd, 9, 24));

            switch (i)
            {
                case 2:
                    rows.Add(m, 14, "BRIGHT SMILES DENTAL", -245.00m);
                    rows.Add(m, 22, "CINEPLEX ENTERTAINMENT", -38.50m);
                    break;
                case 5:
                    rows.Add(m, 17, "CANADIAN TIRE #0412", -186.40m);
                    break;
                case 7: // The trip
                    rows.Add(m, 10, "AIR CANADA 0142", -642.30m);
                    rows.Add(m, 12, "FAIRMONT BANFF SPRINGS", -1212.75m);
                    rows.Add(m, 13, "BANFF GONDOLA", -129.00m);
                    rows.Add(m, 13, "BANFF AVE BREWING CO", -86.40m);
                    rows.Add(m, 14, "PARKS CANADA BANFF", -21.00m);
                    break;
                case 9:
                    rows.Add(m, 3, "CITY PHYSIO CLINIC", -95.00m);
                    rows.Add(m, 17, "CITY PHYSIO CLINIC", -95.00m);
                    rows.Add(m, 8, "IKEA NORTH YORK", -312.44m);
                    break;
                case 10:
                    rows.Add(m, 21, "BRIGHT SMILES DENTAL", -180.00m);
                    rows.Add(m, 24, "CANADIAN TIRE #0412", -64.18m);
                    break;
            }
        }

        // The card is paid in full on the 21st for the month before.
        for (var i = 1; i < months.Count; i++)
        {
            var prev = months[i - 1];
            var spent = -rows.Where(r => r.Date.Year == prev.Year && r.Date.Month == prev.Month && r.Direction == DebitCreditMemo.Debit).Sum(r => r.SignedAmount);
            rows.Add(months[i], 21, "PAYMENT - THANK YOU", spent);
        }

        return rows;
    }

    private static List<ParsedTransaction> ChequingRows(List<DateOnly> months, DateOnly today, List<ParsedTransaction> visa)
    {
        var rnd = new Random(5);
        var rows = new Rows(today);
        for (var i = 0; i < months.Count; i++)
        {
            var m = months[i];
            rows.Add(m, 1, "RENT MAPLEVIEW PROPERTIES", -1850.00m);
            rows.Add(m, 3, "SUNSHINE DAYCARE INC", -1150.00m);
            rows.Add(m, 8, "TORONTO HYDRO", -Between(rnd, 68, 112));
            rows.Add(m, 10, "ENBRIDGE GAS", -Between(rnd, 38, 92));
            rows.Add(m, 12, "ROGERS WIRELESS", -92.18m);
            rows.Add(m, 13, "PAYROLL DEPOSIT NORTHWIND LTD", 3186.40m);
            rows.Add(m, 28, "PAYROLL DEPOSIT NORTHWIND LTD", 3186.40m);
            rows.Add(m, 22, "TFR TO SAVINGS 7730", -SavingsTransfer);
            rows.Add(m, 25, "RRSP CONTRIB WEALTHSIMPLE", -500.00m);
            rows.Add(m, 26, "TFSA CONTRIB WEALTHSIMPLE", -250.00m);
            if (i % 3 == 1) rows.Add(m, 5, "GST CANADA", 129.50m);
            rows.Add(m, 20, "CCB CANADA", 455.00m);
            if (i is 4 or 11) rows.Add(m, 18, "CANADAHELPS DONATION", i == 4 ? -75.00m : -150.00m);
        }

        foreach (var payment in visa.Where(r => r.Direction == DebitCreditMemo.Credit))
            rows.Add(new DateOnly(payment.Date.Year, payment.Date.Month, 1), payment.Date.Day, "TD VISA PAYMENT", -payment.Amount);

        return rows;
    }

    private static List<ParsedTransaction> SavingsRows(List<DateOnly> months, DateOnly today)
    {
        var rows = new Rows(today);
        var balance = 6000m;
        foreach (var m in months)
        {
            rows.Add(m, 22, "TFR FROM CHEQUING 4471", SavingsTransfer);
            var interest = Math.Round(balance * 0.025m / 12, 2);
            rows.Add(m, 28, "INTEREST PAID", interest);
            balance += rows.Where(r => r.Date.Year == m.Year && r.Date.Month == m.Month).Sum(r => r.SignedAmount);
        }

        return rows;
    }
}
