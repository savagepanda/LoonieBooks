using System.Text.Json.Serialization;

namespace MapleLedger.Core.Tax;

public enum TaxTreatmentKind
{
    Deduction,
    NonRefundableCredit,
    RefundableCredit,
    BusinessExpense,
}

[JsonConverter(typeof(JsonStringEnumConverter<TaxTagStatus>))]
public enum TaxTagStatus
{
    /// <summary>Proposed by a rule; not counted as claimable until the user confirms it.</summary>
    Suggested,
    Confirmed,

    /// <summary>The user said this transaction is not claimable. Kept so the suggestion isn't re-proposed.</summary>
    Rejected,
}

/// <summary>A transaction's tax treatment, independent of its spending category.</summary>
/// <param name="ClaimYear">
/// The tax year the amount is claimed in, when that isn't the calendar year it was paid: an RRSP
/// contribution made in the first 60 days of the next year, or a medical expense in a 12-month
/// window that started the year before. Null means the year of the transaction.
/// </param>
/// <param name="ClaimedBy">The household member who claims it, by id; null when not assigned.</param>
/// <param name="BusinessUsePercent">
/// For business, rental and employment expenses: the share used to earn income, such as 30% of a
/// phone bill or the home-office part of the rent. Null means all of it.
/// </param>
public sealed record TaxTag(
    string Code,
    TaxTagStatus Status,
    string? Note = null,
    int? ClaimYear = null,
    string? ClaimedBy = null,
    decimal? BusinessUsePercent = null);

/// <param name="CraReference">Where the amount goes on a T1 return, e.g. "Line 33099".</param>
public sealed record TaxCategory(string Code, string Name, string CraReference, TaxTreatmentKind Kind, string Guidance);

/// <summary>
/// Canadian personal income tax items that bank transactions commonly support. This is a
/// record-keeping aid, not tax advice: eligibility rules and limits are the CRA's, and receipts
/// are still required.
/// </summary>
/// <remarks>
/// Codes are stored on transactions, so never rename or remove one; add a new code instead.
/// Line numbers and amounts in the guidance were checked against canada.ca for
/// <see cref="ReviewedTaxYear"/>. Recheck them (and bump the year) when the next T1 guide is published.
/// </remarks>
public static class TaxCategories
{
    /// <summary>The tax year whose CRA line numbers and amounts the guidance text reflects.</summary>
    public const int ReviewedTaxYear = 2026;

    public const string Medical = "MEDICAL";
    public const string Charitable = "CHARITABLE";
    public const string Political = "POLITICAL";
    public const string ChildCare = "CHILD_CARE";
    public const string Moving = "MOVING";
    public const string Rrsp = "RRSP";
    public const string Fhsa = "FHSA";
    public const string UnionDues = "UNION_DUES";
    public const string CarryingCharges = "CARRYING_CHARGES";
    public const string SupportPayments = "SUPPORT_PAYMENTS";
    public const string Employment = "EMPLOYMENT";
    public const string DisabilitySupports = "DISABILITY_SUPPORTS";
    public const string HomeAccessibility = "HOME_ACCESSIBILITY";
    public const string MultigenerationalRenovation = "MULTIGENERATIONAL_RENOVATION";
    public const string Adoption = "ADOPTION";
    public const string Tuition = "TUITION";
    public const string StudentLoanInterest = "STUDENT_LOAN_INTEREST";
    public const string EducatorSupplies = "EDUCATOR_SUPPLIES";
    public const string Business = "BUSINESS";
    public const string Rental = "RENTAL";

    public static IReadOnlyList<TaxCategory> All { get; } =
    [
        new(Medical, "Medical expenses", "Line 33099", TaxTreatmentKind.NonRefundableCredit,
            "Prescriptions, dental, vision, physiotherapy and other eligible expenses, for you, your spouse and your dependants. Any 12-month period ending in the tax year; only the amount above the lesser of 3% of net income or CA$2,890 (2026) counts. From 2026, an expense claimed here can't also be claimed as a home accessibility expense."),
        new(Charitable, "Charitable donations", "Line 34900 (Schedule 9)", TaxTreatmentKind.NonRefundableCredit,
            "Registered charities and other qualified donees only. Keep the official donation receipts. Spouses can combine donations, and unclaimed donations can be carried forward up to 5 years."),
        new(Political, "Federal political contributions", "Line 40900 (credit on line 41000)", TaxTreatmentKind.NonRefundableCredit,
            "Contributions to registered federal parties, riding associations and candidates, with official receipts. The federal credit is capped at CA$650. Provincial contributions go on your provincial form instead."),
        new(ChildCare, "Child care expenses", "Line 21400 (Form T778)", TaxTreatmentKind.Deduction,
            "Daycare, day camps and babysitting so you can work or study. Usually claimed by the lower-income spouse. Limits per child: CA$8,000 under 7, CA$5,000 aged 7 to 16, CA$11,000 if eligible for the disability tax credit."),
        new(Moving, "Moving expenses", "Line 21900 (Form T1-M)", TaxTreatmentKind.Deduction,
            "A move that brings you at least 40 km closer to a new job, business or full-time post-secondary school. Deductible only against income earned at the new location."),
        new(Rrsp, "RRSP contributions", "Line 20800 (Schedule 7)", TaxTreatmentKind.Deduction,
            "Up to your RRSP deduction limit (see your notice of assessment). Contributions made in the first 60 days of the following year (to March 1, 2027 for 2026) can also be deducted for this year."),
        new(Fhsa, "FHSA contributions", "Line 20805 (Schedule 15)", TaxTreatmentKind.Deduction,
            "First Home Savings Account contributions: up to CA$8,000 a year plus any unused room carried forward, CA$40,000 lifetime. Unlike an RRSP, only contributions made in the calendar year count for that year."),
        new(UnionDues, "Union and professional dues", "Line 21200", TaxTreatmentKind.Deduction,
            "Annual union, professional or like dues required to keep a professional status recognized by law. Don't count dues already shown on your T4 slip."),
        new(CarryingCharges, "Carrying charges and interest", "Line 22100", TaxTreatmentKind.Deduction,
            "Interest on money borrowed to earn investment income, and investment counsel fees. Not interest on money borrowed for an RRSP, TFSA or FHSA, and not safety deposit box fees."),
        new(SupportPayments, "Support payments made", "Line 22000", TaxTreatmentKind.Deduction,
            "Spousal support paid under a written agreement or court order. Child support under agreements made after April 1997 is not deductible."),
        new(Employment, "Employment expenses", "Line 22900 (Form T777)", TaxTreatmentKind.Deduction,
            "Expenses your employment contract requires you to pay yourself. Requires a signed T2200 (or T2200S for home office) from your employer."),
        new(DisabilitySupports, "Disability supports", "Line 21500 (Form T929)", TaxTreatmentKind.Deduction,
            "Attendant care, sign-language interpretation, devices and similar supports you paid for so you could work or go to school, if you have a physical or mental impairment. Not reimbursed amounts."),
        new(HomeAccessibility, "Home accessibility expenses", "Line 31285", TaxTreatmentKind.NonRefundableCredit,
            "Renovations that make a home safer or more accessible for someone 65 or older or eligible for the disability tax credit. Up to CA$20,000 of expenses a year. From 2026, an expense claimed here can't also be claimed as a medical expense."),
        new(MultigenerationalRenovation, "Multigenerational home renovation", "Line 45355 (Schedule 12)", TaxTreatmentKind.RefundableCredit,
            "Renovations that create a self-contained secondary unit so a senior (65 or older) or an adult eligible for the disability tax credit can live with a relative. Up to CA$50,000 of expenses per qualifying renovation, credited at the lowest federal rate."),
        new(Adoption, "Adoption expenses", "Line 31300", TaxTreatmentKind.NonRefundableCredit,
            "Eligible expenses to adopt a child under 18, claimed in the year the adoption is finalized. Up to CA$19,972 per child (2026)."),
        new(Tuition, "Tuition fees", "Line 32300 (Schedule 11)", TaxTreatmentKind.NonRefundableCredit,
            "Claimed from the T2202 slip (or TL11 for foreign schools); tracking payments here helps reconcile it. Unused amounts can be carried forward or transferred."),
        new(StudentLoanInterest, "Student loan interest", "Line 31900", TaxTreatmentKind.NonRefundableCredit,
            "Only the interest (not principal) on government student loans, from your lender's annual statement. Federal Canada Student Loans have charged no interest since April 2023, so this is usually provincial loan interest. Interest on bank loans or lines of credit doesn't qualify. Unused amounts carry forward 5 years."),
        new(EducatorSupplies, "Educator school supplies", "Line 46900 (expenses on line 46800)", TaxTreatmentKind.RefundableCredit,
            "Teachers and early childhood educators: 25% of up to CA$1,000 of teaching supplies you bought yourself and weren't reimbursed for. Keep the receipts and ask your employer for a certification."),
        new(Business, "Self-employment expenses", "Form T2125", TaxTreatmentKind.BusinessExpense,
            "Expenses incurred to earn business or professional income. Personal-use portions aren't deductible."),
        new(Rental, "Rental property expenses", "Form T776", TaxTreatmentKind.BusinessExpense,
            "Current expenses incurred to earn rental income. Improvements that last are capital costs, not current expenses."),
    ];

    private static readonly Dictionary<string, TaxCategory> ByCode = All.ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);

    public static TaxCategory? Find(string? code) => code is not null && ByCode.TryGetValue(code, out var c) ? c : null;

    /// <summary>
    /// Whether only part of an expense may count: business (T2125) and rental (T776) expenses, and
    /// employment expenses (T777) such as a home office or a car used for work.
    /// </summary>
    public static bool AllowsBusinessUse(string? code) =>
        Find(code) is { } category && (category.Kind == TaxTreatmentKind.BusinessExpense || category.Code == Employment);
}
