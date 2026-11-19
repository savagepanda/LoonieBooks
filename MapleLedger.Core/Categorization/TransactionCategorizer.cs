using MapleLedger.Fdx;

namespace MapleLedger.Core.Categorization;

/// <summary>
/// A keyword rule. <see cref="Pattern"/> matches whole words in the description after both are
/// upper-cased and stripped of punctuation, so "PETRO-CANADA" matches "PETRO-CANADA #4521".
/// </summary>
public sealed record CategoryRule(string Pattern, string Category, string? SubCategory = null, DebitCreditMemo? AppliesTo = null);

public sealed record CategoryAssignment(string Category, string? SubCategory);

/// <summary>Assigns categories to transactions by the first matching rule.</summary>
public sealed class TransactionCategorizer
{
    private readonly IReadOnlyList<(string NormalizedPattern, CategoryRule Rule)> _rules;

    /// <summary>Uses <see cref="DefaultCanadianRules"/>.</summary>
    public TransactionCategorizer()
        : this(DefaultCanadianRules.Rules)
    {
    }

    // Deliberately IReadOnlyList rather than IEnumerable: a DI container resolves an unregistered
    // IEnumerable<T> as an empty sequence, which would silently discard every rule.
    public TransactionCategorizer(IReadOnlyList<CategoryRule> rules)
    {
        _rules = rules
            .Select(r => (TextNormalizer.ForMatching(r.Pattern), r))
            .Where(r => r.Item1.Length > 2)
            .ToList();
    }

    public CategoryAssignment Categorize(string? description, DebitCreditMemo direction)
    {
        var text = TextNormalizer.ForMatching(description);
        if (text.Length > 2)
        {
            foreach (var (pattern, rule) in _rules)
            {
                if ((rule.AppliesTo is null || rule.AppliesTo == direction) && text.Contains(pattern, StringComparison.Ordinal))
                {
                    return new CategoryAssignment(rule.Category, rule.SubCategory);
                }
            }
        }

        return new CategoryAssignment(Categories.Uncategorized, null);
    }
}
