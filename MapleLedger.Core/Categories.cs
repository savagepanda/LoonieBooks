using MapleLedger.Fdx;

namespace MapleLedger.Core;

public enum CategoryKind
{
    Income,
    Expense,

    /// <summary>Money moving between the user's own accounts. Excluded from income and spending.</summary>
    Transfer,
}

/// <summary>A top-level category and the sub-categories that can detail it.</summary>
public sealed record CategoryNode(string Name, IReadOnlyList<string> SubCategories);

/// <summary>
/// The two-level category taxonomy: the top level is written into the FDX <c>category</c> field and
/// the optional detail into <c>subCategory</c>. A transaction can be filed at either level.
/// </summary>
public static class Categories
{
    public const string Income = "Income";
    public const string FoodAndDining = "Food & Dining";
    public const string AutoAndTransport = "Auto & Transport";
    public const string BillsAndUtilities = "Bills & Utilities";
    public const string Shopping = "Shopping";
    public const string Entertainment = "Entertainment";
    public const string HealthAndFitness = "Health & Fitness";
    public const string Home = "Home";
    public const string Insurance = "Insurance";
    public const string Travel = "Travel";
    public const string Education = "Education";
    public const string KidsAndFamily = "Kids & Family";
    public const string GiftsAndDonations = "Gifts & Donations";
    public const string FeesAndCharges = "Fees & Charges";
    public const string Taxes = "Taxes";
    public const string CashAndAtm = "Cash & ATM";
    public const string Transfer = "Transfer";
    public const string Uncategorized = "Uncategorized";

    // Sub-categories referred to in code.
    public const string Salary = "Salary";
    public const string BusinessIncome = "Business Income";
    public const string RentalIncome = "Rental Income";
    public const string OtherIncome = "Other Income";
    public const string Gas = "Gas";
    public const string CarLease = "Car Lease";
    public const string Parking = "Parking";
    public const string Taxi = "Taxi";
    public const string PropertyTax = "Property Tax";

    /// <summary>In a sub-category filter: only transactions filed at the top level, with no detail.</summary>
    public const string NoSubCategory = "(none)";

    public static IReadOnlyList<CategoryNode> Tree { get; } =
    [
        new(Income, [Salary, BusinessIncome, RentalIncome, OtherIncome]),
        new(FoodAndDining, ["Groceries", "Restaurants", "Coffee Shops", "Fast Food", "Food Delivery"]),
        new(AutoAndTransport, [Gas, CarLease, Parking, Taxi, "Public Transit", "Tolls"]),
        new(BillsAndUtilities, ["Electricity", "Gas & Heating", "Mobile & Internet"]),
        new(Shopping, ["Clothing", "Electronics", "General Merchandise", "Online", "Sporting Goods"]),
        new(Entertainment, ["Events", "Movies", "Streaming", "Subscriptions"]),
        new(HealthAndFitness, ["Pharmacy", "Dentist", "Medical", "Vision", "Physiotherapy", "Gym"]),
        new(Home, ["Mortgage", "Rent", "Home Improvement", "Furnishings", "Moving"]),
        new(Insurance, ["Home & Auto", "Life & Health"]),
        new(Travel, ["Air Travel", "Lodging", "Rail", "Travel Booking"]),
        new(Education, ["Tuition"]),
        new(KidsAndFamily, ["Child Care"]),
        new(GiftsAndDonations, ["Charity", "Gifts"]),
        new(FeesAndCharges, ["Service Fee", "Interest Charged", "NSF / Overdraft", "Professional Dues"]),
        new(Taxes, ["Income Tax", PropertyTax]),
        new(CashAndAtm, ["Cash Withdrawal"]),
        new(Transfer, ["Account Transfer", "Credit Card Payment", "Interac e-Transfer", "Investment Contribution"]),
        new(Uncategorized, []),
    ];

    /// <summary>The top-level categories.</summary>
    public static IReadOnlyList<string> All { get; } = [.. Tree.Select(n => n.Name)];

    private static readonly Dictionary<string, CategoryNode> ByName = Tree.ToDictionary(n => n.Name);

    /// <summary>The sub-categories of a top-level category; empty for an unknown one.</summary>
    public static IReadOnlyList<string> SubCategoriesOf(string? category) =>
        category is not null && ByName.TryGetValue(category, out var node) ? node.SubCategories : [];

    /// <summary>A known top-level category, with no sub-category or one of its own.</summary>
    public static bool IsValid(string? category, string? subCategory) => IsValid(Tree, category, subCategory);

    /// <summary>A top-level category of <paramref name="tree"/>, with no sub-category or one of its own.</summary>
    public static bool IsValid(IReadOnlyList<CategoryNode> tree, string? category, string? subCategory) =>
        Find(tree, category) is { } node && (subCategory is null || node.SubCategories.Contains(subCategory));

    public static CategoryNode? Find(IReadOnlyList<CategoryNode> tree, string? category) =>
        category is null ? null : tree.FirstOrDefault(n => n.Name == category);

    /// <summary>
    /// The built-in tree with the user's own categories added: details after the built-in ones of
    /// their category, and new top-level categories just before Uncategorized, each alphabetically.
    /// </summary>
    public static IReadOnlyList<CategoryNode> WithCustom(IEnumerable<CustomCategory> custom)
    {
        var list = custom.ToList();
        if (list.Count == 0) return Tree;

        List<string> DetailsOf(string category) =>
            [.. list.Where(c => c.Category == category && c.SubCategory is not null).Select(c => c.SubCategory!).Order(StringComparer.OrdinalIgnoreCase)];

        // A detail added before it became built-in (Income › Rental Income) isn't listed twice.
        var nodes = Tree.Where(n => n.Name != Uncategorized)
            .Select(n => n with { SubCategories = [.. n.SubCategories, .. DetailsOf(n.Name).Where(d => !n.SubCategories.Contains(d, StringComparer.OrdinalIgnoreCase))] })
            .ToList();
        foreach (var top in list.Where(c => c.SubCategory is null && !ByName.ContainsKey(c.Category)).OrderBy(c => c.Category, StringComparer.OrdinalIgnoreCase))
        {
            nodes.Add(new CategoryNode(top.Category, DetailsOf(top.Category)));
        }

        nodes.Add(ByName[Uncategorized]);
        return nodes;
    }

    /// <summary>"Auto &amp; Transport › Gas", or just the category when it has no detail.</summary>
    public static string Label(string? category, string? subCategory) =>
        string.IsNullOrEmpty(subCategory) ? category ?? Uncategorized : $"{category ?? Uncategorized} › {subCategory}";

    /// <summary>
    /// Maps categories found in older backups onto the current taxonomy: income detail (paycheques,
    /// benefits, interest) becomes Income › Salary or Other Income; the short-lived top-level Salary,
    /// Business Income, Other Income, Gas, Car Lease, Parking, Taxi and Property Tax become details of
    /// Income, Auto &amp; Transport and Taxes; and a detail that doesn't belong to its category is dropped.
    /// </summary>
    /// <param name="tree">The tree to check against, with the user's own categories; the built-in one by default.</param>
    public static (string? Category, string? SubCategory) Normalize(string? category, string? subCategory, IReadOnlyList<CategoryNode>? tree = null)
    {
        tree ??= Tree;
        (category, subCategory) = (category, subCategory) switch
        {
            (Income, "Paycheque") => (Income, Salary),
            (Income, "Government Benefits" or "Tax Refund" or "Interest" or "Dividends") => (Income, OtherIncome),
            (Salary or BusinessIncome or OtherIncome, _) => (Income, category),
            (AutoAndTransport, "Rideshare") => (AutoAndTransport, Taxi),
            (Gas or CarLease or Parking or Taxi, _) => (AutoAndTransport, category),
            (PropertyTax, _) => (Taxes, PropertyTax),
            _ => (category, subCategory),
        };

        return Find(tree, category) is { } node && subCategory is not null && !node.SubCategories.Contains(subCategory)
            ? (category, null)
            : (category, subCategory);
    }

    /// <summary>
    /// Uncategorized money is counted by direction: unexplained credits as income and
    /// unexplained debits as spending, so totals still balance before the user tidies up.
    /// </summary>
    public static CategoryKind KindOf(string? category, DebitCreditMemo direction) => category switch
    {
        Income => CategoryKind.Income,
        Transfer => CategoryKind.Transfer,
        null or "" or Uncategorized => direction == DebitCreditMemo.Credit ? CategoryKind.Income : CategoryKind.Expense,
        _ => CategoryKind.Expense,
    };

    /// <summary>
    /// Whether a category's money counts as income or spending by its direction (see
    /// <see cref="KindOf"/>), so it shows in both lists and a filter needs the direction too.
    /// </summary>
    public static bool KindDependsOnDirection(string? category) => category is null or "" or Uncategorized;
}
