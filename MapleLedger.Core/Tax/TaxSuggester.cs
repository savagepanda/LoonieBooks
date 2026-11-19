using MapleLedger.Fdx;
using static MapleLedger.Core.Tax.TaxCategories;

namespace MapleLedger.Core.Tax;

/// <summary>
/// Proposes a tax category for money spent. Suggestions are deliberately broad (every pharmacy
/// purchase is flagged as possibly medical) because the user reviews them before year end; a
/// missed claim costs more than a rejected suggestion.
/// </summary>
public sealed class TaxSuggester
{
    private static readonly Dictionary<string, string> BySubCategory = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Pharmacy"] = Medical,
        ["Dentist"] = Medical,
        ["Physiotherapy"] = Medical,
        ["Vision"] = Medical,
        ["Medical"] = Medical,
        ["Charity"] = Charitable,
        ["Child Care"] = ChildCare,
        ["Tuition"] = Tuition,
        ["Professional Dues"] = UnionDues,
        ["Moving"] = Moving,
    };

    private static readonly (string Pattern, string Code)[] Keywords =
    [
        ("RRSP", Rrsp),
        ("REER", Rrsp),
        ("FHSA", Fhsa),
        ("CELIAPP", Fhsa),
        ("DAYCARE", ChildCare),
        ("GARDERIE", ChildCare),
        ("CHILD CARE", ChildCare),
        ("CHILDCARE", ChildCare),
        ("DAY CAMP", ChildCare),
        ("MONTESSORI", ChildCare),
        ("OPTOMETR", Medical),
        ("OPTICAL", Medical),
        ("DENTAL", Medical),
        ("DENTIST", Medical),
        ("ORTHODONT", Medical),
        ("PHYSIO", Medical),
        ("CHIROPRACT", Medical),
        ("MASSAGE THERAPY", Medical),
        ("PSYCHOLOG", Medical),
        ("CLINIC", Medical),
        ("HOSPITAL", Medical),
        ("PHARMACY", Medical),
        ("CANADAHELPS", Charitable),
        ("DONATION", Charitable),
        ("UNITED WAY", Charitable),
        ("RED CROSS", Charitable),
        ("LIBERAL PARTY", Political),
        ("CONSERVATIVE PARTY", Political),
        ("NDP", Political),
        ("GREEN PARTY", Political),
        ("UNION DUES", UnionDues),
        ("CUPE", UnionDues),
        ("UNIFOR", UnionDues),
        ("PSAC", UnionDues),
        ("OPSEU", UnionDues),
        ("CPA ", UnionDues),
        ("LAW SOCIETY", UnionDues),
        ("U HAUL", Moving),
        ("UHAUL", Moving),
        ("MOVERS", Moving),
        // Loan payments are mostly principal; the suggestion is a reminder to look up the
        // interest on the lender's annual statement.
        ("NSLSC", StudentLoanInterest),
        ("STUDENT LOAN", StudentLoanInterest),
        ("TUITION", Tuition),
        ("UNIVERSITY", Tuition),
        ("COLLEGE", Tuition),
    ];

    public string? Suggest(string? description, DebitCreditMemo direction, string? subCategory)
    {
        if (direction != DebitCreditMemo.Debit)
        {
            return null;
        }

        if (subCategory is not null && BySubCategory.TryGetValue(subCategory, out var code))
        {
            return code;
        }

        // Prefix match on word starts, so "PHYSIO" catches "PHYSIOTHERAPY" and "OPTOMETR" catches
        // "OPTOMETRIST". A trailing space in the pattern ("CPA ") demands the whole word.
        var text = TextNormalizer.ForMatching(description);
        foreach (var (pattern, keywordCode) in Keywords)
        {
            if (text.Contains(" " + pattern, StringComparison.Ordinal))
            {
                return keywordCode;
            }
        }

        return null;
    }
}
