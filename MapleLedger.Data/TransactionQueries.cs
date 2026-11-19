using MapleLedger.Core;
using MapleLedger.Core.Tax;
using MapleLedger.Fdx;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace MapleLedger.Data;

public static class TransactionQueries
{
    /// <summary>Transactions whose effective date falls in [from, to].</summary>
    public static IQueryable<Transaction> Between(this IQueryable<Transaction> query, DateOnly? from, DateOnly? to)
    {
        if (from is { } f)
        {
            var start = new DateTimeOffset(f.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(t => (t.TransactionTimestamp ?? t.PostedTimestamp) >= start);
        }

        if (to is { } t2)
        {
            var end = new DateTimeOffset(t2.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(t => (t.TransactionTimestamp ?? t.PostedTimestamp) < end);
        }

        return query;
    }

    /// <summary>Transactions with any of these tags; null or none is no filter.</summary>
    public static IQueryable<Transaction> WithAnyTag(this IQueryable<Transaction> query, IReadOnlyCollection<string>? tagIds)
    {
        var markers = (tagIds ?? []).Where(id => !string.IsNullOrEmpty(id)).Distinct().Select(TagList.Marker).ToList();
        if (markers.Count == 0) return query;

        // tags LIKE '%|a|%' OR tags LIKE '%|b|%' ...: built by hand, as neither provider translates
        // "any of these substrings" over a list. Each marker goes in as a parameter.
        var t = Expression.Parameter(typeof(Transaction), "t");
        var tags = Expression.Call(typeof(EF), nameof(EF.Property), [typeof(string)], t, Expression.Constant(LedgerDbContext.Tags));
        var contains = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;
        var any = markers
            .Select(marker => (Expression)Expression.AndAlso(
                Expression.NotEqual(tags, Expression.Constant(null, typeof(string))),
                Expression.Call(tags, contains, Expression.Property(Expression.Constant(new Captured(marker)), nameof(Captured.Value)))))
            .Aggregate(Expression.OrElse);

        return query.Where(Expression.Lambda<Func<Transaction, bool>>(any, t));
    }

    /// <summary>Holds a value the way a lambda's closure does, so EF sends it as a parameter.</summary>
    private sealed record Captured(string Value);

    public static IQueryable<Transaction> NewestFirst(this IQueryable<Transaction> query) =>
        query.OrderByDescending(t => t.TransactionTimestamp ?? t.PostedTimestamp).ThenBy(t => t.TransactionId);

    /// <summary>Materializes transactions together with their shadow-property annotations.</summary>
    public static async Task<List<LedgerTransaction>> ToLedgerTransactionsAsync(this IQueryable<Transaction> query, CancellationToken cancellationToken)
    {
        var rows = await query
            .Select(t => new
            {
                Transaction = t,
                TaxCode = EF.Property<string?>(t, LedgerDbContext.TaxCode),
                TaxStatus = EF.Property<TaxTagStatus?>(t, LedgerDbContext.TaxStatus),
                TaxNote = EF.Property<string?>(t, LedgerDbContext.TaxNote),
                TaxClaimYear = EF.Property<int?>(t, LedgerDbContext.TaxClaimYear),
                TaxClaimedBy = EF.Property<string?>(t, LedgerDbContext.TaxClaimedBy),
                TaxBusinessUsePercent = EF.Property<decimal?>(t, LedgerDbContext.TaxBusinessUsePercent),
                CategoryIsManual = EF.Property<bool>(t, LedgerDbContext.CategoryIsManual),
                Tags = EF.Property<string?>(t, LedgerDbContext.Tags),
                EarnedBy = EF.Property<string?>(t, LedgerDbContext.EarnedBy),
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new LedgerTransaction(
                r.Transaction,
                r.TaxCode is not null && r.TaxStatus is { } status ? new TaxTag(r.TaxCode, status, r.TaxNote, r.TaxClaimYear, r.TaxClaimedBy, r.TaxBusinessUsePercent) : null,
                r.CategoryIsManual,
                r.Tags is null ? null : TagList.Parse(r.Tags),
                r.EarnedBy))
            .ToList();
    }
}
