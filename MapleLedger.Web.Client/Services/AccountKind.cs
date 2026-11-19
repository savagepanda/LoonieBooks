using MapleLedger.Fdx;

namespace MapleLedger.Web.Client.Services;

/// <summary>The account kinds offered when adding an account, mapped onto FDX category/type.</summary>
public sealed record AccountKind(string Label, AccountCategory Category, AccountType Type)
{
    public static IReadOnlyList<AccountKind> All { get; } =
    [
        new("Chequing", AccountCategory.DepositAccount, AccountType.Checking),
        new("Savings", AccountCategory.DepositAccount, AccountType.Savings),
        new("GIC / term deposit", AccountCategory.DepositAccount, AccountType.Cd),
        new("Credit card", AccountCategory.LocAccount, AccountType.CreditCard),
        new("Line of credit", AccountCategory.LocAccount, AccountType.LineOfCredit),
        new("Home equity line of credit", AccountCategory.LocAccount, AccountType.HomeLineOfCredit),
        new("Mortgage", AccountCategory.LoanAccount, AccountType.Mortgage),
        new("Car loan", AccountCategory.LoanAccount, AccountType.AutoLoan),
        new("Student loan", AccountCategory.LoanAccount, AccountType.StudentLoan),
        new("Other loan", AccountCategory.LoanAccount, AccountType.Loan),
        new("Investment (RRSP, TFSA, FHSA, non-registered)", AccountCategory.InvestmentAccount, AccountType.Brokerage),
    ];

    public static string LabelFor(Account account) =>
        All.FirstOrDefault(k => k.Category == account.AccountCategory && k.Type == account.AccountType)?.Label ?? account.AccountType.ToString();

    public static IReadOnlyList<string> CanadianInstitutions { get; } =
    [
        "RBC Royal Bank", "TD Canada Trust", "Scotiabank", "BMO", "CIBC", "National Bank", "Desjardins",
        "Tangerine", "Simplii Financial", "EQ Bank", "HSBC Canada", "ATB Financial", "Coast Capital",
        "Meridian", "PC Financial", "Rogers Bank", "American Express", "Wealthsimple", "Questrade",
    ];
}
