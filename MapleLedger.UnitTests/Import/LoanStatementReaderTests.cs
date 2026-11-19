using MapleLedger.Core.Import;
using StatementPdf = MapleLedger.UnitTests.Import.PdfStatementReaderTests.StatementPdf;

namespace MapleLedger.UnitTests.Import;

public class LoanStatementReaderTests
{
    private static readonly DateOnly Today = new(2026, 11, 18);

    // A TD Home Equity FlexLine statement's lines as PdfPig groups them: words broken apart
    // ("Tot al") or run together ("INTERESTFORTHESTATEMENTPERIOD"), a revolving portion with a
    // credit balance, and a term portion whose payment covered only interest, leaving deferred interest.
    private static readonly string[][] FlexLineWithDeferredInterest =
    [
        [
            "Page1of 3",
            "Your TD Home Equity FlexLine Statement",
            "Statement Period: October 1, 2026 to October 31, 2026",
            "Statement Date: Oct ober 31, 2026",
            "Current Plan Limit: $710,919.61",
            "Credit Limit: $617,500.00",
            "AvailableCredit: $120,129.73",
            "REVOLVINGPORTIONACCOUNTNUMBER:",
            "0564-0000000",
            "PAYMENTDETAILS",
            "Payment Authorized Overdue Minimum",
            "Due Dat e Debit Date Amount ($) Payment 1 ($)",
            "Nov 21/26 Nov 01/26 0.00 0.00",
            "ACTIVITY",
            "Dat e Description Amount ($)",
            "Oct 01 OPENING BALANCE -324.87",
            "Oct 31 CLOSING BALANCE -324.87",
            "Tot al New Advances, Interest & Total Payments/Credit s",
            "Non-Interest Charges $0.00",
            "$0.00",
        ],
        [
            "Account Number: 0564-0000000",
            "INTERESTFORTHESTATEMENTPERIOD",
            "Period Covering TD Prime Variance Annual Interest",
            "Oct 01 - Oct 28 4.450 +0.200 4.650 0.00",
            "Oct 29 - Oct 31 4.700 +0.200 4.900 0.00",
            "Tot al Interest 0.00",
            "TD Prime Rate of 4.700% became effective as of October 29, 2026.",
            "TERM PORTION",
            "ACCOUNTNO. 0564-0000000-01",
            "Opening Principal Balance: $601,114.75",
            "Closing Principal Balance: $591,114.75",
            "Maturity Date: Dec 21/26",
            "DEFERRED INTERESTAMOUNT",
            "Deferred interest is the amount of accrued interest that your payment amount did not cover.",
            "As of October 21, 2026, your total accumulated Deferred Interest amount owing is$9,379.31.",
            "Deferred interest is not currently reflected in your Closing Principal Balance.",
            "PAYM ENT DETAILS",
            "Payment Due Dat e Overdue Amount ($) Payment Due 2 ($)",
            "Nov 21/26 0.00 2,055.63",
            "Date Description Principal Interest 3 Amount ($)",
            "Oct 06 Lump-sum Pyt -10,000.00 0.00 -10,000.00",
            "Oct 21 Pyt 0.00 -2,055.63 -2,055.63",
            "Tot al Principal Paid -10,000.00",
            "Tot al Interest Paid -2,055.63",
            "Tot al Payment s -12,055.63",
        ],
        [
            "INTERESTFORTHESTATEMENTPERIOD 4",
            "Oct 01-Oct 28 4.450 -1.350 3.100 1,409.97",
            "Oct 29-Oct 31 4.700 -1.350 3.350 162.76",
            "Total Interest 1,572.73",
            "4Thismaynot match the Total Interest Paid because of your payment frequency.",
        ],
    ];

    [Fact]
    public void ReadsEachPortionsBalanceAndTheDeferredInterestOnTopOfThePrincipal()
    {
        var statement = LoanStatementReader.Read(FlexLineWithDeferredInterest, Today);

        Assert.NotNull(statement);
        Assert.Equal(new DateOnly(2026, 10, 31), statement.AsOf);
        Assert.Equal(
            [
                new LoanBalancePart("Revolving portion balance", -324.87m),
                new LoanBalancePart("Term portion principal balance", 591_114.75m),
                new LoanBalancePart("Term portion deferred interest", 9_379.31m),
            ],
            statement.BalanceParts);
        // TD's available credit agrees: plan limit 710,919.61 − 591,114.75 + 324.87 = 120,129.73.
        Assert.Equal(600_169.19m, statement.Balance);

        var term = statement.Portions[1];
        Assert.Equal(601_114.75m, term.OpeningBalance);
        Assert.Equal(1_572.73m, term.InterestCharged);
        Assert.Equal(2_055.63m, term.InterestPaid);
        Assert.Equal(0m, statement.Portions[0].InterestCharged);
    }

    [Fact]
    public void ReadsPaymentsSplitIntoPrincipalAndInterestButNotRatePeriodsOrTotals()
    {
        var statement = LoanStatementReader.Read(FlexLineWithDeferredInterest, Today)!;

        Assert.Equal(
            [
                new LoanActivity(new DateOnly(2026, 10, 6), "Lump-sum Pyt", 10_000m, LoanActivityKind.Payment, 10_000m, 0m),
                new LoanActivity(new DateOnly(2026, 10, 21), "Pyt", 2_055.63m, LoanActivityKind.Payment, 0m, 2_055.63m),
            ],
            statement.Activity);
    }

    [Fact]
    public void WithoutDeferredInterestTheBalanceIsThePrincipalLessAnyCredit()
    {
        string[][] pages =
        [
            [
                "Statement Date: October 31, 2026",
                "REVOLVINGPORTIONACCOUNTNUMBER:",
                "Oct 01 OPENING BALANCE -573.04",
                "Oct 31 CLOSING BALANCE -573.04",
                "TERM PORTION",
                "Opening Principal Balance: $998,651.72",
                "Closing Principal Balance: $996,951.09",
                "Oct 07 Pyt -1,700.63 -2,499.37 -4,200.00",
                "Tot al Interest",
                "2,688.55",
            ],
        ];

        var statement = LoanStatementReader.Read(pages, Today)!;

        Assert.Equal(996_378.05m, statement.Balance);
        Assert.All(statement.Portions, p => Assert.Null(p.DeferredInterest));
        Assert.Equal(2_688.55m, statement.Portions[1].InterestCharged);
        var payment = Assert.Single(statement.Activity);
        Assert.Equal((4_200m, 1_700.63m, 2_499.37m), (payment.Amount, payment.Principal!.Value, payment.Interest!.Value));
    }

    [Fact]
    public void AMortgageStatementWithoutPortionsIsOneLoan()
    {
        string[][] pages =
        [
            [
                "Mortgage statement",
                "Statement Period: January 1, 2026 to June 30, 2026",
                "Principal balance: $412,300.18",
                "Jan 15 Regular payment -1,850.00",
                "Feb 15 Late payment fee 25.00",
            ],
        ];

        var statement = LoanStatementReader.Read(pages, Today)!;

        Assert.Equal(new DateOnly(2026, 6, 30), statement.AsOf);
        Assert.Equal([new LoanBalancePart("Principal balance", 412_300.18m)], statement.BalanceParts);
        Assert.Equal(
            [LoanActivityKind.Payment, LoanActivityKind.Charge],
            statement.Activity.Select(a => a.Kind));
    }

    [Fact]
    public void ABankAccountStatementIsNotALoan()
    {
        string[][] pages =
        [
            [
                "Statement Period: October 1, 2026 to October 31, 2026",
                "Oct 01 Opening balance 1,250.00",
                "Oct 03 PAYROLL DEPOSIT 2,400.00 3,650.00",
                "Oct 31 Closing balance 3,650.00",
            ],
        ];

        Assert.Null(LoanStatementReader.Read(pages, Today));
    }

    [Fact]
    public void ReadsTheLabelsFromAPdf()
    {
        var pdf = new StatementPdf();
        pdf.Text(40, 740, "Statement Date: October 31, 2026");
        pdf.Text(40, 700, "TERM PORTION");
        pdf.Text(40, 680, "Closing Principal Balance: $591,114.75");
        pdf.Text(40, 660, "As of October 21, 2026, your total accumulated Deferred Interest amount owing is $9,379.31.");
        pdf.Text(40, 620, "Oct 21");
        pdf.Text(90, 620, "Pyt");
        pdf.Right(400, 620, "0.00");
        pdf.Right(470, 620, "-2,055.63");
        pdf.Right(540, 620, "-2,055.63");

        var statement = LoanStatementReader.Read(pdf.Build("flexline.pdf"), Today);

        Assert.NotNull(statement);
        Assert.Equal(600_494.06m, statement.Balance);
        Assert.Equal(2_055.63m, Assert.Single(statement.Activity).Interest);
    }
}
