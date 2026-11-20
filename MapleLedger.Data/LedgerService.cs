using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MapleLedger.Core;
using MapleLedger.Core.Budgets;
using MapleLedger.Core.Categorization;
using MapleLedger.Core.Import;
using MapleLedger.Core.Investments;
using MapleLedger.Core.RealEstate;
using MapleLedger.Core.Reports;
using MapleLedger.Core.Tax;
using MapleLedger.Fdx;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace MapleLedger.Data;

/// <summary>A request the ledger refuses; <see cref="Errors"/> is keyed by field and safe to show.</summary>
public sealed class LedgerValidationException(IReadOnlyDictionary<string, string[]> errors)
    : Exception(string.Join(" ", errors.Values.SelectMany(v => v)))
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;

    public static LedgerValidationException For(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
}

/// <summary>
/// Every ledger operation, written once and run in two places: by the API against SQL Server for
/// premium users, and in the browser against SQLite for everyone else. The context must already be
/// scoped to its owner (<see cref="LedgerDbContext.OwnerId"/>).
/// </summary>
public sealed class LedgerService(LedgerDbContext db)
{
    public const int MaxImportSize = 5000;
    public const int MaxTransactionsPerQuery = 2000;

    // ---------------------------------------------------------------- accounts

    public async Task<IReadOnlyList<Account>> GetAccountsAsync(CancellationToken ct = default) =>
        await db.Accounts.AsNoTracking().OrderBy(a => a.Nickname).ThenBy(a => a.AccountId).ToListAsync(ct);

    public async Task<(IReadOnlyList<Account> Accounts, int Total)> GetAccountsPageAsync(int skip, int take, CancellationToken ct = default)
    {
        var total = await db.Accounts.CountAsync(ct);
        var accounts = await db.Accounts.AsNoTracking().OrderBy(a => a.Nickname).ThenBy(a => a.AccountId).Skip(skip).Take(take).ToListAsync(ct);
        return (accounts, total);
    }

    public Task<Account?> FindAccountAsync(string accountId, CancellationToken ct = default) =>
        db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.AccountId == accountId, ct);

    public async Task<Account> CreateAccountAsync(AccountRequest request, CancellationToken ct = default)
    {
        Validate(request);
        var account = ToAccount(NewAccountId(), request);
        if (request.CurrencyCode is { } code) account = account with { Currency = new Currency(CurrencyCode(code)) };
        db.Accounts.Add(account);
        db.Entry(account).Property(LedgerDbContext.CreatedAt).CurrentValue = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return account;
    }

    /// <summary>
    /// Updates the account. Its balance is in its currency; changing the currency moves the holdings
    /// that were in the old one to the new one (see <see cref="ChangeHoldingsCurrencyAsync"/>).
    /// Null when the account doesn't exist.
    /// </summary>
    public async Task<Account?> UpdateAccountAsync(string accountId, AccountRequest request, CancellationToken ct = default)
    {
        Validate(request);
        var existing = await db.Accounts.SingleOrDefaultAsync(a => a.AccountId == accountId, ct);
        if (existing is null) return null;

        var currency = request.CurrencyCode is { } code ? CurrencyCode(code) : CurrencyRates.CurrencyOf(existing);
        var updated = ToAccount(accountId, request) with { Status = existing.Status, Currency = (existing.Currency ?? Currency.Cad) with { CurrencyCode = currency } };
        if (currency != CurrencyRates.CurrencyOf(existing)) updated = await ChangeHoldingsCurrencyAsync(existing, updated, ct);

        db.Entry(existing).CurrentValues.SetValues(updated);
        await db.SaveChangesAsync(ct);
        return updated;
    }

    /// <summary>
    /// An account's currency changed: its holdings in the old currency were read as the account's
    /// (their rows didn't say), so they move to the new one, at the ledger's rate for it. A balance
    /// that was the holdings' total becomes their total in the new currency, unless the edit set
    /// another. Not saved here.
    /// </summary>
    private async Task<Account> ChangeHoldingsCurrencyAsync(Account existing, Account updated, CancellationToken ct)
    {
        var rows = await db.Holdings.Where(h => h.AccountId == existing.AccountId).ToListAsync(ct);
        if (rows.Count == 0) return updated;

        var (from, to) = (CurrencyRates.CurrencyOf(existing), CurrencyRates.CurrencyOf(updated));
        var rates = new CurrencyRates(await GetExchangeRatesAsync(ct));
        var before = Holding.TotalIn(rows.Select(ToHolding), from, rates.RateOf(from));
        var toRate = rates.RateOf(to);
        if (toRate is null && rows.Any(h => h.Currency == from))
        {
            throw LedgerValidationException.For("currencyCode", $"There's no exchange rate for {to} yet. Set one on the Holdings page, then change the currency.");
        }

        foreach (var h in rows.Where(h => h.Currency == from))
        {
            h.Currency = to;
            h.ExchangeRate = toRate!.Value;
        }

        var after = Holding.TotalIn(rows.Select(ToHolding), to, toRate);
        var wasTotal = existing.CurrentBalance is { } balance && before is { } total && Math.Abs(balance - total) < 1;
        return wasTotal && after is { } value && updated.CurrentBalance == existing.CurrentBalance
            ? updated with { CurrentBalance = value }
            : updated;
    }

    /// <summary>A stored holding; one imported without symbol and quantity gets them from its description (see <see cref="Holding.WithListingFromDescription"/>).</summary>
    private static Holding ToHolding(HoldingRecord h) =>
        new Holding(h.Symbol, h.Description, h.Quantity, h.Price, h.MarketValue, h.BookValue, h.Currency, h.ExchangeRate, h.PriceAsOf, h.PriceSource).WithListingFromDescription();

    public async Task<bool> DeleteAccountAsync(string accountId, CancellationToken ct = default)
    {
        if (!await db.Accounts.AnyAsync(a => a.AccountId == accountId, ct)) return false;

        // Delete children explicitly rather than relying on the database cascading: SQLite only
        // enforces foreign keys when the connection turns them on.
        await db.Receipts.Where(r => r.AccountId == accountId).ExecuteDeleteAsync(ct);
        await db.Holdings.Where(h => h.AccountId == accountId).ExecuteDeleteAsync(ct);
        await db.Transactions.Where(t => t.AccountId == accountId).ExecuteDeleteAsync(ct);
        await db.ImportBatches.Where(b => b.AccountId == accountId).ExecuteDeleteAsync(ct);
        await db.Accounts.Where(a => a.AccountId == accountId).ExecuteDeleteAsync(ct);
        return true;
    }

    /// <summary>
    /// Deletes this owner's whole ledger (accounts, transactions, imports, receipts, household
    /// members) to start over. The query filters scope every delete to <see cref="LedgerDbContext.OwnerId"/>;
    /// a context without an owner sees, and so deletes, nothing. The user record (premium) stays.
    /// </summary>
    public async Task<ClearLedgerResult> ClearAsync(CancellationToken ct = default)
    {
        if (db.OwnerId is null) return new ClearLedgerResult(0, 0, 0, 0);

        // Children first: SQLite only enforces foreign keys when the connection turns them on.
        var receipts = await db.Receipts.ExecuteDeleteAsync(ct);
        var transactions = await db.Transactions.ExecuteDeleteAsync(ct);
        await db.ImportBatches.ExecuteDeleteAsync(ct);
        await db.Holdings.ExecuteDeleteAsync(ct);
        var accounts = await db.Accounts.ExecuteDeleteAsync(ct);
        var members = await db.HouseholdMembers.ExecuteDeleteAsync(ct);
        await db.CustomCategories.ExecuteDeleteAsync(ct);
        await db.LedgerTags.ExecuteDeleteAsync(ct);
        await db.TaxSituations.ExecuteDeleteAsync(ct);
        await db.ExchangeRates.ExecuteDeleteAsync(ct);
        await db.Properties.ExecuteDeleteAsync(ct);
        await db.HousePriceIndex.ExecuteDeleteAsync(ct);
        await db.PriceHistory.ExecuteDeleteAsync(ct);
        await db.MonthlyExchangeRates.ExecuteDeleteAsync(ct);
        await db.Budgets.ExecuteDeleteAsync(ct);
        return new ClearLedgerResult(accounts, transactions, receipts, members);
    }

    // ---------------------------------------------------------------- transactions

    /// <summary>
    /// Adds transactions, skipping any whose id already exists for the account, so pasting an
    /// overlapping date range is safe. Returns null when the account doesn't exist.
    /// </summary>
    public async Task<ImportTransactionsResult?> ImportAsync(string accountId, ImportTransactionsRequest request, CancellationToken ct = default)
    {
        if (request.Transactions.Count > MaxImportSize)
        {
            throw LedgerValidationException.For("transactions", $"Import at most {MaxImportSize} transactions at a time.");
        }

        if (request.Transactions.Any(t => t.Transaction.Amount < 0 || string.IsNullOrWhiteSpace(t.Transaction.TransactionId) || t.Transaction.TransactionId.Length > 64))
        {
            throw LedgerValidationException.For("transactions", "Every transaction needs an id of at most 64 characters and a non-negative amount.");
        }

        var account = await db.Accounts.SingleOrDefaultAsync(a => a.AccountId == accountId, ct);
        if (account is null) return null;

        var incoming = request.Transactions.DistinctBy(t => t.Transaction.TransactionId).ToList();
        var ids = incoming.Select(t => t.Transaction.TransactionId).ToList();
        var existing = (await db.Transactions
            .Where(t => t.AccountId == accountId && ids.Contains(t.TransactionId))
            .Select(t => t.TransactionId)
            .ToListAsync(ct)).ToHashSet();

        var tree = await GetCategoryTreeAsync(ct);
        var members = incoming.Any(t => t.Tax?.ClaimedBy is not null || t.EarnedBy is not null)
            ? (await db.HouseholdMembers.Select(m => m.Id).ToListAsync(ct)).ToHashSet()
            : [];
        var tags = incoming.Any(t => t.Tags is { Count: > 0 })
            ? (await GetTagsAsync(ct)).Select(g => g.Id).ToHashSet()
            : [];

        var batch = new ImportBatch
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Source = request.Source.Length > 32 ? request.Source[..32] : request.Source,
            ImportedAt = DateTimeOffset.UtcNow,
            Received = request.Transactions.Count,
        };

        foreach (var item in incoming.Where(t => !existing.Contains(t.Transaction.TransactionId)))
        {
            var (category, subCategory) = Categories.Normalize(item.Transaction.Category, item.Transaction.SubCategory, tree);
            var entry = db.Transactions.Add(item.Transaction with { AccountId = accountId, Category = category, SubCategory = subCategory });
            entry.Property(LedgerDbContext.ImportBatchId).CurrentValue = batch.Id;
            entry.Property(LedgerDbContext.CreatedAt).CurrentValue = batch.ImportedAt;
            entry.Property(LedgerDbContext.CategoryIsManual).CurrentValue = item.CategoryIsManual;
            entry.Property(LedgerDbContext.Tags).CurrentValue =
                TagList.Format(item.Tags?.Where(tags.Contains).Distinct().Take(LedgerTag.MaxPerTransaction));
            entry.Property(LedgerDbContext.EarnedBy).CurrentValue = item.EarnedBy is { } earner
                && (members.Contains(earner) || (earner == TaxSituations.Split && subCategory == Categories.RentalIncome && members.Count > 0))
                ? earner
                : null;
            if (item.Tax is { } tax && TaxCategories.Find(tax.Code) is { } taxCategory)
            {
                entry.Property(LedgerDbContext.TaxCode).CurrentValue = taxCategory.Code;
                entry.Property(LedgerDbContext.TaxStatus).CurrentValue = tax.Status;
                entry.Property(LedgerDbContext.TaxNote).CurrentValue = tax.Note is { Length: > 1024 } note ? note[..1024] : tax.Note;
                entry.Property(LedgerDbContext.TaxClaimYear).CurrentValue = IsPlausibleYear(tax.ClaimYear) ? tax.ClaimYear : null;
                entry.Property(LedgerDbContext.TaxClaimedBy).CurrentValue = tax.ClaimedBy is { } member && members.Contains(member) ? member : null;
                entry.Property(LedgerDbContext.TaxBusinessUsePercent).CurrentValue =
                    TaxCategories.AllowsBusinessUse(taxCategory.Code) && tax.BusinessUsePercent is > 0 and <= 100 ? NormalizePercent(tax.BusinessUsePercent) : null;
            }

            batch.Added++;
        }

        batch.Duplicates = batch.Received - batch.Added;

        if (request.EndingBalance is { } balance && request.EndingBalanceAsOf is { } asOf && (account.BalanceAsOf is null || asOf >= account.BalanceAsOf))
        {
            db.Entry(account).CurrentValues.SetValues(account with { CurrentBalance = balance, BalanceAsOf = asOf });
        }

        db.ImportBatches.Add(batch);
        await db.SaveChangesAsync(ct);
        return new ImportTransactionsResult(batch.Id, batch.Received, batch.Added, batch.Duplicates);
    }

    /// <summary>An account's imports, newest first, with how many of each one's transactions are left.</summary>
    public async Task<IReadOnlyList<ImportSummary>> GetImportsAsync(string accountId, int limit = 20, CancellationToken ct = default)
    {
        var batches = await db.ImportBatches.Where(b => b.AccountId == accountId).ToListAsync(ct);
        var remaining = await db.Transactions
            .Where(t => t.AccountId == accountId && EF.Property<Guid?>(t, LedgerDbContext.ImportBatchId) != null)
            .GroupBy(t => EF.Property<Guid?>(t, LedgerDbContext.ImportBatchId))
            .Select(g => new { Batch = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Batch!.Value, g => g.Count, ct);

        // Sorted here: SQLite can't order DateTimeOffset.
        return batches
            .OrderByDescending(b => b.ImportedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(b => new ImportSummary(b.Id, b.AccountId, b.Source, b.ImportedAt, b.Added, b.Duplicates, remaining.GetValueOrDefault(b.Id)))
            .ToList();
    }

    /// <summary>
    /// Removes the transactions one import added (and receipts attached to them), for an import into
    /// the wrong account or of the wrong file. Transactions it skipped as duplicates belong to an
    /// earlier import and stay. The account balance an import set isn't changed back. Returns null
    /// when the import doesn't exist.
    /// </summary>
    public async Task<UndoImportResult?> UndoImportAsync(Guid importId, CancellationToken ct = default)
    {
        var batch = await db.ImportBatches.SingleOrDefaultAsync(b => b.Id == importId, ct);
        if (batch is null) return null;

        var added = db.Transactions.Where(t => t.AccountId == batch.AccountId && EF.Property<Guid?>(t, LedgerDbContext.ImportBatchId) == importId);
        var ids = await added.Select(t => t.TransactionId).ToListAsync(ct);

        // Children first: SQLite only enforces foreign keys when the connection turns them on.
        foreach (var chunk in ids.Chunk(500))
        {
            await db.Receipts.Where(r => r.AccountId == batch.AccountId && chunk.Contains(r.TransactionId)).ExecuteDeleteAsync(ct);
        }

        var removed = await added.ExecuteDeleteAsync(ct);
        await db.ImportBatches.Where(b => b.Id == importId).ExecuteDeleteAsync(ct);
        return new UndoImportResult(removed);
    }

    /// <summary>
    /// The account's other transactions from the same merchant as this one (see <see cref="MerchantName"/>),
    /// newest first. Null when the transaction doesn't exist.
    /// </summary>
    public async Task<IReadOnlyList<LedgerTransaction>?> FindSimilarAsync(string accountId, string transactionId, CancellationToken ct = default)
    {
        var description = await db.Transactions
            .Where(t => t.AccountId == accountId && t.TransactionId == transactionId)
            .Select(t => new { t.Description })
            .SingleOrDefaultAsync(ct);
        if (description is null) return null;

        var key = MerchantName.Key(description.Description);
        if (key.Length == 0) return [];

        // Descriptions vary too much for SQL to match; compare them here, then load the matches.
        var candidates = await db.Transactions
            .Where(t => t.AccountId == accountId && t.TransactionId != transactionId)
            .Select(t => new { t.TransactionId, t.Description })
            .ToListAsync(ct);
        var ids = candidates.Where(c => MerchantName.Key(c.Description) == key).Select(c => c.TransactionId).Take(MaxImportSize).ToList();
        if (ids.Count == 0) return [];

        var matches = new List<LedgerTransaction>();
        foreach (var chunk in ids.Chunk(500))
        {
            matches.AddRange(await db.Transactions.Where(t => t.AccountId == accountId && chunk.Contains(t.TransactionId)).ToLedgerTransactionsAsync(ct));
        }

        return [.. matches.OrderByDescending(m => m.Transaction.EffectiveDate)];
    }

    /// <summary>
    /// What the user chose for each merchant (see <see cref="MerchantMemory"/>), for categorizing and
    /// tagging new imports alike: per account and direction, the category of the newest transaction
    /// they chose or confirmed one for (none when that was Uncategorized), and the newest
    /// transaction's tags.
    /// </summary>
    public async Task<IReadOnlyList<MerchantChoice>> GetMerchantChoicesAsync(CancellationToken ct = default)
    {
        // Descriptions vary too much for SQL to group, and SQLite can't sort the dates: work here.
        var rows = await db.Transactions
            .Select(t => new
            {
                t.AccountId,
                t.Description,
                t.DebitCreditMemo,
                t.TransactionTimestamp,
                t.PostedTimestamp,
                t.Category,
                t.SubCategory,
                Manual = EF.Property<bool>(t, LedgerDbContext.CategoryIsManual),
                Tags = EF.Property<string?>(t, LedgerDbContext.Tags),
            })
            .ToListAsync(ct);

        var choices = new List<MerchantChoice>();
        foreach (var merchant in rows
            .Select(r => new { Row = r, Key = MerchantName.Key(r.Description), Date = r.TransactionTimestamp ?? r.PostedTimestamp ?? DateTimeOffset.MinValue })
            .Where(r => r.Key.Length > 0)
            .GroupBy(r => (r.Row.AccountId, r.Key, r.Row.DebitCreditMemo)))
        {
            var newest = merchant.MaxBy(r => r.Date)!;
            var chosen = merchant.Where(r => r.Row.Manual).MaxBy(r => r.Date);
            var category = chosen?.Row is { Category: { Length: > 0 } c } && c != Categories.Uncategorized ? c : null;
            var tags = newest.Row.Tags is null ? null : TagList.Parse(newest.Row.Tags);
            if (category is null && tags is not { Count: > 0 }) continue;

            choices.Add(new MerchantChoice(
                merchant.Key.AccountId,
                merchant.Key.Key,
                merchant.Key.DebitCreditMemo,
                category,
                category is null || string.IsNullOrEmpty(chosen!.Row.SubCategory) ? null : chosen.Row.SubCategory,
                category is null ? null : DateOnly.FromDateTime(chosen!.Date.UtcDateTime),
                tags is { Count: > 0 } ? tags : null));
        }

        return choices;
    }

    /// <summary>Confirms the categories of several transactions as they are; returns how many weren't reviewed yet.</summary>
    public async Task<int> MarkReviewedAsync(MarkReviewedRequest request, CancellationToken ct = default)
    {
        if (request.Transactions.Count > MaxImportSize)
        {
            throw LedgerValidationException.For("transactions", $"Change at most {MaxImportSize} transactions at a time.");
        }

        var changed = 0;
        foreach (var account in request.Transactions.GroupBy(t => t.AccountId))
        {
            foreach (var chunk in account.Select(t => t.TransactionId).Distinct().Chunk(500))
            {
                changed += await db.Transactions
                    .Where(t => t.AccountId == account.Key && chunk.Contains(t.TransactionId) && !EF.Property<bool>(t, LedgerDbContext.CategoryIsManual))
                    .ExecuteUpdateAsync(s => s.SetProperty(t => EF.Property<bool>(t, LedgerDbContext.CategoryIsManual), true), ct);
            }
        }

        return changed;
    }

    /// <summary>
    /// Transactions that look like one imported twice under different ids, e.g. from a PDF
    /// statement and a CSV export that print the description differently. Newest first.
    /// </summary>
    public async Task<IReadOnlyList<PossibleDuplicate>> FindDuplicatesAsync(CancellationToken ct = default)
    {
        // Amounts and dates can't be compared in SQLite's SQL; match here, then load the matches.
        var rows = await db.Transactions
            .Select(t => new
            {
                t.AccountId,
                t.TransactionId,
                t.TransactionTimestamp,
                t.PostedTimestamp,
                t.DebitCreditMemo,
                t.Amount,
                t.Description,
                Batch = EF.Property<Guid?>(t, LedgerDbContext.ImportBatchId),
                Kept = EF.Property<bool>(t, LedgerDbContext.KeptAsDistinct),
            })
            .ToListAsync(ct);
        var matches = DuplicateFinder.Find(rows.Select(r => new DuplicateCandidate(
            r.AccountId,
            r.TransactionId,
            DateOnly.FromDateTime((r.TransactionTimestamp ?? r.PostedTimestamp ?? DateTimeOffset.MinValue).UtcDateTime),
            r.DebitCreditMemo,
            r.Amount,
            r.Description,
            r.Batch,
            r.Kept)));
        if (matches.Count == 0) return [];

        var transactions = new Dictionary<(string, string), LedgerTransaction>();
        var receipts = new HashSet<(string, string)>();
        foreach (var accountMatches in matches.GroupBy(m => m.First.AccountId))
        {
            var accountId = accountMatches.Key;
            foreach (var chunk in accountMatches.SelectMany(m => new[] { m.First.TransactionId, m.Second.TransactionId }).Chunk(500))
            {
                foreach (var t in await db.Transactions.Where(t => t.AccountId == accountId && chunk.Contains(t.TransactionId)).ToLedgerTransactionsAsync(ct))
                {
                    transactions[(accountId, t.Transaction.TransactionId)] = t;
                }

                var withReceipts = await db.Receipts.Where(r => r.AccountId == accountId && chunk.Contains(r.TransactionId)).Select(r => r.TransactionId).Distinct().ToListAsync(ct);
                receipts.UnionWith(withReceipts.Select(id => (accountId, id)));
            }
        }

        int Worth(LedgerTransaction t) =>
            (receipts.Contains((t.Transaction.AccountId, t.Transaction.TransactionId)) ? 3 : 0)
            + (t.CategoryIsManual ? 1 : 0)
            + (t.Tax is { Status: not TaxTagStatus.Suggested } ? 1 : 0);

        var result = new List<PossibleDuplicate>();
        foreach (var match in matches)
        {
            var a = transactions[(match.First.AccountId, match.First.TransactionId)];
            var b = transactions[(match.Second.AccountId, match.Second.TransactionId)];
            var keepB = Worth(b) > Worth(a) || (Worth(b) == Worth(a) && (b.Transaction.Description?.Length ?? 0) > (a.Transaction.Description?.Length ?? 0));
            result.Add(keepB ? new PossibleDuplicate(b, a, match.DaysApart) : new PossibleDuplicate(a, b, match.DaysApart));
        }

        return result;
    }

    /// <summary>
    /// Resolves a duplicate: deletes <see cref="MergeDuplicateRequest.RemoveId"/> after moving its
    /// receipts onto the kept transaction, and its category and tax choices too when the kept one
    /// has none of its own. Returns false when either transaction doesn't exist.
    /// </summary>
    public async Task<bool> MergeDuplicateAsync(string accountId, MergeDuplicateRequest request, CancellationToken ct = default)
    {
        if (request.KeepId == request.RemoveId)
        {
            throw LedgerValidationException.For("removeId", "A transaction can't be merged into itself.");
        }

        var pair = await db.Transactions
            .Where(t => t.AccountId == accountId && (t.TransactionId == request.KeepId || t.TransactionId == request.RemoveId))
            .ToLedgerTransactionsAsync(ct);
        if (pair.Count != 2) return false;

        var keep = pair.Single(t => t.Transaction.TransactionId == request.KeepId);
        var remove = pair.Single(t => t.Transaction.TransactionId == request.RemoveId);

        var copyCategory = remove.CategoryIsManual && !keep.CategoryIsManual;
        var copyTax = remove.Tax is { Status: not TaxTagStatus.Suggested } && keep.Tax is null or { Status: TaxTagStatus.Suggested };
        if (copyCategory || copyTax)
        {
            await db.Transactions
                .Where(t => t.AccountId == accountId && t.TransactionId == request.KeepId)
                .ExecuteUpdateAsync(s =>
                {
                    if (copyCategory)
                    {
                        s.SetProperty(t => t.Category, remove.Transaction.Category);
                        s.SetProperty(t => t.SubCategory, remove.Transaction.SubCategory);
                        s.SetProperty(t => EF.Property<bool>(t, LedgerDbContext.CategoryIsManual), true);
                    }

                    if (copyTax) SetTax(s, remove.Tax);
                }, ct);
        }

        if (remove.EarnedBy is { } earner && keep.EarnedBy is null)
        {
            await db.Transactions
                .Where(t => t.AccountId == accountId && t.TransactionId == request.KeepId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => EF.Property<string?>(t, LedgerDbContext.EarnedBy), earner), ct);
        }

        if (remove.Tags is { Count: > 0 } removedTags && removedTags.Except(keep.Tags ?? []).Any())
        {
            var merged = TagList.Format((keep.Tags ?? []).Concat(removedTags).Distinct().Take(LedgerTag.MaxPerTransaction));
            await db.Transactions
                .Where(t => t.AccountId == accountId && t.TransactionId == request.KeepId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => EF.Property<string?>(t, LedgerDbContext.Tags), merged), ct);
        }

        await db.Receipts
            .Where(r => r.AccountId == accountId && r.TransactionId == request.RemoveId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.TransactionId, request.KeepId), ct);
        await db.Transactions.Where(t => t.AccountId == accountId && t.TransactionId == request.RemoveId).ExecuteDeleteAsync(ct);
        return true;
    }

    /// <summary>
    /// Marks two look-alike transactions as both real, so <see cref="FindDuplicatesAsync"/> stops
    /// pairing them. Returns false when either doesn't exist.
    /// </summary>
    public async Task<bool> KeepBothAsync(string accountId, KeepBothRequest request, CancellationToken ct = default)
    {
        if (request.FirstId == request.SecondId) return false;

        var ids = new[] { request.FirstId, request.SecondId };
        var query = db.Transactions.Where(t => t.AccountId == accountId && ids.Contains(t.TransactionId));
        if (await query.CountAsync(ct) != 2) return false;

        await query.ExecuteUpdateAsync(s => s.SetProperty(t => EF.Property<bool>(t, LedgerDbContext.KeptAsDistinct), true), ct);
        return true;
    }

    /// <summary>Sets a category (chosen by the user) on several transactions of one account; returns how many changed.</summary>
    public async Task<int> ClassifyManyAsync(string accountId, ClassifyManyRequest request, CancellationToken ct = default)
    {
        await ValidateCategoryAsync(request.Category, request.SubCategory, ct);

        if (request.TransactionIds.Count > MaxImportSize)
        {
            throw LedgerValidationException.For("transactionIds", $"Change at most {MaxImportSize} transactions at a time.");
        }

        var updated = 0;
        foreach (var chunk in request.TransactionIds.Distinct().Chunk(500))
        {
            updated += await db.Transactions
                .Where(t => t.AccountId == accountId && chunk.Contains(t.TransactionId))
                .ExecuteUpdateAsync(s =>
                {
                    s.SetProperty(t => t.Category, request.Category);
                    s.SetProperty(t => t.SubCategory, request.SubCategory);
                    s.SetProperty(t => EF.Property<bool>(t, LedgerDbContext.CategoryIsManual), true);
                }, ct);
        }

        return updated;
    }

    public async Task<bool> ClassifyAsync(string accountId, string transactionId, ClassifyTransactionRequest request, CancellationToken ct = default)
    {
        await ValidateCategoryAsync(request.Category, request.SubCategory, ct);

        var tax = request.Tax is null ? null : ValidateTaxTag(request.Tax);
        await EnsureMemberExistsAsync(tax, ct);

        var updated = await db.Transactions
            .Where(t => t.AccountId == accountId && t.TransactionId == transactionId)
            .ExecuteUpdateAsync(s =>
            {
                s.SetProperty(t => t.Category, request.Category);
                s.SetProperty(t => t.SubCategory, request.SubCategory);
                s.SetProperty(t => EF.Property<bool>(t, LedgerDbContext.CategoryIsManual), true);
                SetTax(s, tax);
            }, ct);

        return updated > 0;
    }

    private async Task ValidateCategoryAsync(string category, string? subCategory, CancellationToken ct)
    {
        var tree = await GetCategoryTreeAsync(ct);
        if (Categories.Find(tree, category) is null)
        {
            throw LedgerValidationException.For("category", $"Unknown category '{category}'.");
        }

        if (!Categories.IsValid(tree, category, subCategory))
        {
            throw LedgerValidationException.For("subCategory", $"'{subCategory}' isn't a sub-category of {category}.");
        }
    }

    /// <summary>Changes only a transaction's tax tag, leaving its category alone. Null clears the tag.</summary>
    public async Task<bool> SetTaxTagAsync(string accountId, string transactionId, TaxTag? tax, CancellationToken ct = default)
    {
        if (tax is not null) tax = ValidateTaxTag(tax);
        await EnsureMemberExistsAsync(tax, ct);

        var updated = await db.Transactions
            .Where(t => t.AccountId == accountId && t.TransactionId == transactionId)
            .ExecuteUpdateAsync(s => SetTax(s, tax), ct);

        return updated > 0;
    }

    /// <summary>
    /// Gives several transactions one tax treatment, e.g. every mortgage payment for a rental
    /// property. Claims of that category already confirmed are left as they are (keeping their
    /// claim year, claimant and business-use share), suggestions of it are confirmed, and the rest
    /// become new claims of it. A null code marks them all not claimable. Returns how many changed.
    /// </summary>
    public async Task<int> SetTaxManyAsync(SetTaxManyRequest request, CancellationToken ct = default)
    {
        if (request.Transactions.Count > MaxImportSize)
        {
            throw LedgerValidationException.For("transactions", $"Change at most {MaxImportSize} transactions at a time.");
        }

        var code = request.TaxCode is null
            ? null
            : TaxCategories.Find(request.TaxCode)?.Code ?? throw LedgerValidationException.For("tax", $"Unknown tax category '{request.TaxCode}'.");

        var changed = 0;
        foreach (var account in request.Transactions.GroupBy(t => t.AccountId))
        {
            foreach (var chunk in account.Select(t => t.TransactionId).Distinct().Chunk(500))
            {
                var rows = db.Transactions.Where(t => t.AccountId == account.Key && chunk.Contains(t.TransactionId));
                if (code is null)
                {
                    // Like clearing one row's tax treatment: rejected, so a rule doesn't suggest it again.
                    changed += await rows
                        .Where(t => EF.Property<string?>(t, LedgerDbContext.TaxCode) != null
                            && EF.Property<TaxTagStatus?>(t, LedgerDbContext.TaxStatus) != TaxTagStatus.Rejected)
                        .ExecuteUpdateAsync(s => s.SetProperty(t => EF.Property<TaxTagStatus?>(t, LedgerDbContext.TaxStatus), TaxTagStatus.Rejected), ct);
                    continue;
                }

                changed += await rows
                    .Where(t => EF.Property<string?>(t, LedgerDbContext.TaxCode) == code
                        && EF.Property<TaxTagStatus?>(t, LedgerDbContext.TaxStatus) == TaxTagStatus.Suggested)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => EF.Property<TaxTagStatus?>(t, LedgerDbContext.TaxStatus), TaxTagStatus.Confirmed), ct);

                // A new claim keeps the transaction's note, as choosing a category for one row does.
                changed += await rows
                    .Where(t => EF.Property<string?>(t, LedgerDbContext.TaxCode) == null
                        || EF.Property<string?>(t, LedgerDbContext.TaxCode) != code
                        || EF.Property<TaxTagStatus?>(t, LedgerDbContext.TaxStatus) != TaxTagStatus.Confirmed)
                    .ExecuteUpdateAsync(s =>
                    {
                        s.SetProperty(t => EF.Property<string?>(t, LedgerDbContext.TaxCode), code);
                        s.SetProperty(t => EF.Property<TaxTagStatus?>(t, LedgerDbContext.TaxStatus), TaxTagStatus.Confirmed);
                        s.SetProperty(t => EF.Property<int?>(t, LedgerDbContext.TaxClaimYear), (int?)null);
                        s.SetProperty(t => EF.Property<string?>(t, LedgerDbContext.TaxClaimedBy), (string?)null);
                        s.SetProperty(t => EF.Property<decimal?>(t, LedgerDbContext.TaxBusinessUsePercent), (decimal?)null);
                    }, ct);
            }
        }

        return changed;
    }

    /// <summary>Writes every part of a tax tag, so no column is left over from a previous tag.</summary>
    private static void SetTax(UpdateSettersBuilder<Transaction> s, TaxTag? tax)
    {
        s.SetProperty(t => EF.Property<string?>(t, LedgerDbContext.TaxCode), tax?.Code);
        s.SetProperty(t => EF.Property<TaxTagStatus?>(t, LedgerDbContext.TaxStatus), tax?.Status);
        s.SetProperty(t => EF.Property<string?>(t, LedgerDbContext.TaxNote), tax?.Note);
        s.SetProperty(t => EF.Property<int?>(t, LedgerDbContext.TaxClaimYear), tax?.ClaimYear);
        s.SetProperty(t => EF.Property<string?>(t, LedgerDbContext.TaxClaimedBy), tax?.ClaimedBy);
        s.SetProperty(t => EF.Property<decimal?>(t, LedgerDbContext.TaxBusinessUsePercent), tax?.BusinessUsePercent);
    }

    private async Task EnsureMemberExistsAsync(TaxTag? tax, CancellationToken ct)
    {
        if (tax?.ClaimedBy is { } member && !await db.HouseholdMembers.AnyAsync(m => m.Id == member, ct))
        {
            throw LedgerValidationException.For("tax", "That household member no longer exists.");
        }
    }

    private static bool IsPlausibleYear(int? year) => year is null or (>= 1990 and <= 2200);

    /// <summary>Checks a tag and returns it with the tax code in its canonical spelling.</summary>
    private static TaxTag ValidateTaxTag(TaxTag tax)
    {
        if (TaxCategories.Find(tax.Code) is not { } category)
        {
            throw LedgerValidationException.For("tax", $"Unknown tax category '{tax.Code}'.");
        }

        if (tax.Note is { Length: > 1024 })
        {
            throw LedgerValidationException.For("tax", "Keep tax notes under 1,024 characters.");
        }

        if (!IsPlausibleYear(tax.ClaimYear))
        {
            throw LedgerValidationException.For("tax", $"{tax.ClaimYear} isn't a tax year LoonieBooks can record.");
        }

        ValidateBusinessUse(category.Code, tax.BusinessUsePercent);
        return tax with { Code = category.Code, BusinessUsePercent = NormalizePercent(tax.BusinessUsePercent) };
    }

    private static void ValidateBusinessUse(string code, decimal? percent)
    {
        if (percent is null) return;

        if (!TaxCategories.AllowsBusinessUse(code))
        {
            throw LedgerValidationException.For("tax", "A business-use percentage only applies to business, rental and employment expenses.");
        }

        if (percent is <= 0 or > 100)
        {
            throw LedgerValidationException.For("tax", "Enter a business-use percentage above 0 and at most 100.");
        }
    }

    /// <summary>Two decimals at most, and 100% is the same as no percentage.</summary>
    private static decimal? NormalizePercent(decimal? percent) => percent switch
    {
        null or 100m => null,
        var p => Math.Round(p.Value, 2),
    };

    public async Task<IReadOnlyList<LedgerTransaction>> SearchTransactionsAsync(TransactionFilter filter, CancellationToken ct = default)
    {
        var query = db.Transactions.Between(filter.From, filter.To);
        if (!string.IsNullOrEmpty(filter.AccountId)) query = query.Where(t => t.AccountId == filter.AccountId);
        query = WhereAccounts(query, filter.AccountIds);
        query = WhereCategory(query, filter.Category, filter.SubCategory, filter.Kind).WithAnyTag(filter.TagIds);
        if (!string.IsNullOrEmpty(filter.TaxCode)) query = query.Where(t => EF.Property<string?>(t, LedgerDbContext.TaxCode) == filter.TaxCode);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            // LIKE, not Contains: on SQLite (the browser and desktop stores) Contains is instr(), which is
            // case-sensitive, so "banff" missed "BANFF GONDOLA". LIKE ignores ASCII case there and follows the
            // (case-insensitive) collation on SQL Server. The user's % and _ are matched literally.
            var pattern = "%" + filter.Search.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
            query = query.Where(t => t.Description != null && EF.Functions.Like(t.Description, pattern, @"\"));
        }
        if (filter.Reviewed is { } reviewed) query = query.Where(t => EF.Property<bool>(t, LedgerDbContext.CategoryIsManual) == reviewed);

        return await query.NewestFirst().Take(Math.Clamp(filter.Limit, 1, MaxTransactionsPerQuery)).ToLedgerTransactionsAsync(ct);
    }

    /// <summary>Any of the accounts; null or none is no filter.</summary>
    private static IQueryable<Transaction> WhereAccounts(IQueryable<Transaction> query, IReadOnlyCollection<string>? accountIds)
    {
        if (accountIds is not { Count: > 0 }) return query;
        var ids = accountIds.Distinct().ToList();
        return query.Where(t => ids.Contains(t.AccountId));
    }

    /// <summary>
    /// A category, narrowed to one of its details or (<see cref="Categories.NoSubCategory"/>) to none; no category is no filter.
    /// With <paramref name="kind"/>, Uncategorized means only its money in (income) or out (spending).
    /// </summary>
    private static IQueryable<Transaction> WhereCategory(IQueryable<Transaction> query, string? category, string? subCategory, CategoryKind? kind = null)
    {
        if (string.IsNullOrEmpty(category)) return query;
        if (Categories.KindDependsOnDirection(category))
        {
            // Filed as Uncategorized or not filed at all: the dashboard counts both as Uncategorized.
            query = query.Where(t => t.Category == null || t.Category == "" || t.Category == Categories.Uncategorized);
            if (kind is CategoryKind.Income) query = query.Where(t => t.DebitCreditMemo == DebitCreditMemo.Credit);
            else if (kind is CategoryKind.Expense) query = query.Where(t => t.DebitCreditMemo != DebitCreditMemo.Credit);
            return query;
        }

        query = query.Where(t => t.Category == category);
        if (subCategory == Categories.NoSubCategory) return query.Where(t => t.SubCategory == null || t.SubCategory == "");
        return string.IsNullOrEmpty(subCategory) ? query : query.Where(t => t.SubCategory == subCategory);
    }

    /// <summary>One account's transactions for the FDX surface; null when the account doesn't exist.</summary>
    public async Task<(IReadOnlyList<Transaction> Transactions, int Total)?> GetAccountTransactionsPageAsync(
        string accountId, DateOnly? from, DateOnly? to, int skip, int take, CancellationToken ct = default)
    {
        if (!await db.Accounts.AnyAsync(a => a.AccountId == accountId, ct)) return null;

        var query = db.Transactions.AsNoTracking().Where(t => t.AccountId == accountId).Between(from, to);
        var total = await query.CountAsync(ct);
        return (await query.NewestFirst().Skip(skip).Take(take).ToListAsync(ct), total);
    }

    // ---------------------------------------------------------------- reports

    /// <summary>How many of the newest transactions the dashboard gets: more than a tall window's panel shows.</summary>
    public const int RecentTransactionCount = 30;

    /// <param name="category">Only this category's transactions (e.g. its spending by month); null for everything.</param>
    /// <param name="subCategory">With <paramref name="category"/>: one of its details, or <see cref="Categories.NoSubCategory"/>.</param>
    /// <param name="accountIds">Only these accounts' transactions and balances; null or none for every account.</param>
    /// <param name="kind">With <paramref name="category"/>: only what counts as income or spending (see <see cref="TransactionFilter.Kind"/>).</param>
    /// <param name="tagIds">Only transactions with any of these tags; null or none for every transaction. Balances (net worth) aren't affected.</param>
    public async Task<DashboardSummary> GetDashboardAsync(DateOnly from, DateOnly to, string? category = null, string? subCategory = null, IReadOnlyCollection<string>? accountIds = null, CategoryKind? kind = null, IReadOnlyCollection<string>? tagIds = null, CancellationToken ct = default)
    {
        var query = WhereAccounts(WhereCategory(db.Transactions.Between(from, to), category, subCategory, kind), accountIds).WithAnyTag(tagIds);
        var transactions = await query.NewestFirst().ToLedgerTransactionsAsync(ct);
        var accounts = await db.Accounts.AsNoTracking().ToListAsync(ct);
        // Totals in Canadian dollars, each transaction at its month's rate; the list keeps its account's currency.
        var fdx = await InCadAsync(transactions.Select(t => t.Transaction).ToList(), accounts, ct);
        if (accountIds is { Count: > 0 }) accounts = accounts.Where(a => accountIds.Contains(a.AccountId)).ToList();
        var (income, expenses) = CashFlowCalculator.Totals(fdx);

        return new DashboardSummary(
            from,
            to,
            income,
            expenses,
            CashFlowCalculator.Monthly(fdx, from, to),
            CashFlowCalculator.SpendingByCategory(fdx),
            CashFlowCalculator.IncomeByCategory(fdx),
            CashFlowCalculator.NetWorth(accounts, new CurrencyRates(await GetExchangeRatesAsync(ct)), await RealEstateValueAsync(accountIds, ct)),
            transactions.Take(RecentTransactionCount).ToList(),
            CashFlowCalculator.SpendingDetails(fdx),
            CashFlowCalculator.IncomeDetails(fdx),
            CashFlowCalculator.SpendingByAccount(fdx),
            CashFlowCalculator.IncomeByAccount(fdx));
    }

    /// <summary>
    /// Net worth at the end of each month from <paramref name="from"/> to <paramref name="to"/>, worked back
    /// from today's balances (see <see cref="CashFlowCalculator.MonthlyNetWorth"/>). Like the dashboard's
    /// net worth it follows the account filter but not tags.
    /// </summary>
    public async Task<IReadOnlyList<MonthlyNetWorth>> GetNetWorthByMonthAsync(DateOnly from, DateOnly to, IReadOnlyCollection<string>? accountIds = null, CancellationToken ct = default)
    {
        var accounts = await db.Accounts.AsNoTracking().ToListAsync(ct);
        if (accountIds is { Count: > 0 }) accounts = accounts.Where(a => accountIds.Contains(a.AccountId)).ToList();
        // Everything since the first month began: what's walked back from today's balances.
        var transactions = await WhereAccounts(db.Transactions.AsNoTracking().Between(new DateOnly(from.Year, from.Month, 1), null), accountIds).ToListAsync(ct);
        // Each month's real estate at its area's index for that month, so the line follows the market.
        var (properties, readings) = await RealEstateInViewAsync(accountIds, ct);
        Func<DateOnly, decimal>? realEstateOn = properties.Count == 0 ? null : end => PropertyValuation.TotalValueOn(properties, readings, end);
        var rates = new CurrencyRates(await GetExchangeRatesAsync(ct));
        // And each investment account's holdings at their listings' month-end closes, so it follows the market too.
        var holdings = await GetHoldingsAsync(ct: ct);
        var marketMoves = holdings.Count == 0 ? null : HoldingPriceMoves.ByAccount(holdings, await GetPriceHistoryAsync(ct), rates);
        return CashFlowCalculator.MonthlyNetWorth(accounts, transactions, from, to, rates, realEstateOn: realEstateOn, marketMoves: marketMoves);
    }

    /// <summary>
    /// One account's balance at the end of each month from <paramref name="from"/> to <paramref name="to"/>,
    /// in its own currency, worked back from today's balance like <see cref="GetNetWorthByMonthAsync"/>
    /// (an investment account following its holdings' month-end prices). Empty for an account that
    /// isn't there or has no balance set.
    /// </summary>
    public async Task<IReadOnlyList<MonthlyAccountValue>> GetAccountValueByMonthAsync(string accountId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.AccountId == accountId, ct);
        if (account is null) return [];
        var transactions = await db.Transactions.AsNoTracking().Where(t => t.AccountId == accountId).Between(new DateOnly(from.Year, from.Month, 1), null).ToListAsync(ct);
        var rates = new CurrencyRates(await GetExchangeRatesAsync(ct));
        Func<DateOnly, decimal?>? marketMove = null;
        if (!account.IsLiability && await GetHoldingsAsync(accountId, ct) is { Count: > 0 } holdings)
        {
            marketMove = HoldingPriceMoves.ByAccount(holdings, await GetPriceHistoryAsync(ct), rates).GetValueOrDefault(accountId);
        }

        return CashFlowCalculator.MonthlyAccountValue(account, transactions, from, to, rates, marketMove);
    }

    /// <summary>
    /// The properties' values, carried to today by their areas' house price index readings. With an
    /// account filter, only the properties secured by one of those accounts count, so a property's
    /// value shows with its mortgage.
    /// </summary>
    private async Task<decimal> RealEstateValueAsync(IReadOnlyCollection<string>? accountIds, CancellationToken ct)
    {
        var (properties, readings) = await RealEstateInViewAsync(accountIds, ct);
        return properties.Count == 0 ? 0 : PropertyValuation.TotalValue(properties, readings);
    }

    private async Task<(List<RealEstateProperty> Properties, IReadOnlyList<HpiReading> Readings)> RealEstateInViewAsync(IReadOnlyCollection<string>? accountIds, CancellationToken ct)
    {
        IEnumerable<RealEstateProperty> properties = await GetPropertiesAsync(ct);
        if (accountIds is { Count: > 0 }) properties = properties.Where(p => p.AccountIds.Any(accountIds.Contains));
        var list = properties.ToList();
        return (list, list.Count == 0 ? [] : await GetHpiReadingsAsync(ct));
    }

    /// <param name="person">Whose claims: null for everyone, a household member id, or <see cref="TaxReportBuilder.Unassigned"/>.</param>
    public async Task<TaxReport> GetTaxReportAsync(int year, string? person = null, CancellationToken ct = default)
    {
        // A year's claims can include amounts paid the year before (a medical window) or early the
        // next year (RRSP contributions), so read the neighbouring periods too.
        var (from, to) = TaxReportBuilder.ReportingRange(year);
        var transactions = await db.Transactions
            .Where(t => EF.Property<string?>(t, LedgerDbContext.TaxCode) != null)
            .Between(from, to)
            .ToLedgerTransactionsAsync(ct);
        var report = TaxReportBuilder.Build(year, transactions, person);

        var shown = report.Categories.SelectMany(c => c.Transactions)
            .Concat(report.RrspFromNextYear)
            .Select(t => (t.Transaction.AccountId, t.Transaction.TransactionId))
            .ToHashSet();
        var ids = shown.Select(k => k.TransactionId).Distinct().ToList();
        var receipts = await db.Receipts.AsNoTracking()
            .Where(r => ids.Contains(r.TransactionId))
            .OrderBy(r => r.UploadedAt)
            .Select(ReceiptInfoProjection)
            .ToListAsync(ct);

        return report with { Receipts = receipts.Where(r => shown.Contains((r.AccountId, r.TransactionId))).ToList() };
    }

    /// <summary>Tagged transactions in the year, plus untagged spending in categories where claims are common.</summary>
    public async Task<TaxReview> GetTaxReviewAsync(int year, CancellationToken ct = default)
    {
        var categories = TaxReviewBuilder.CategoriesToCheck.ToList();
        var transactions = await db.Transactions
            .Where(t => EF.Property<string?>(t, LedgerDbContext.TaxCode) != null
                || (t.DebitCreditMemo == DebitCreditMemo.Debit && t.Category != null && categories.Contains(t.Category)))
            .Between(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31))
            .ToLedgerTransactionsAsync(ct);
        return TaxReviewBuilder.Build(year, transactions);
    }

    public async Task<MedicalEstimate> GetMedicalEstimateAsync(int year, decimal? netIncome, string? person = null, CancellationToken ct = default) =>
        MedicalExpenses.Estimate(year, await GetMedicalCandidatesAsync(year, person, ct), netIncome);

    /// <summary>
    /// Makes <paramref name="year"/> claim the medical expenses of the 12-month window ending in
    /// <paramref name="endMonth"/>, moving expenses outside it to their neighbouring years.
    /// Returns how many transactions changed.
    /// </summary>
    public async Task<int> ApplyMedicalWindowAsync(int year, int endMonth, string? person = null, CancellationToken ct = default)
    {
        if (endMonth is < 1 or > 12) throw LedgerValidationException.For("endMonth", "Choose a month from 1 to 12.");

        var changes = MedicalExpenses.ClaimYearsFor(year, endMonth, await GetMedicalCandidatesAsync(year, person, ct));
        foreach (var (item, claimYear) in changes)
        {
            await SetTaxTagAsync(item.Transaction.AccountId, item.Transaction.TransactionId, item.Tax! with { ClaimYear = claimYear }, ct);
        }

        return changes.Count;
    }

    private async Task<List<LedgerTransaction>> GetMedicalCandidatesAsync(int year, string? person, CancellationToken ct)
    {
        var (from, to) = MedicalExpenses.Range(year);
        var transactions = await db.Transactions
            .Where(t => EF.Property<string?>(t, LedgerDbContext.TaxCode) == TaxCategories.Medical)
            .Between(from, to)
            .ToLedgerTransactionsAsync(ct);
        return MedicalExpenses.Candidates(year, TaxReportBuilder.ForPerson(transactions, person)).ToList();
    }

    public async Task<string> GetTaxReportCsvAsync(int year, string? person = null, CancellationToken ct = default)
    {
        var report = await GetTaxReportAsync(year, person, ct);
        var accountNames = (await db.Accounts.AsNoTracking().ToListAsync(ct)).ToDictionary(a => a.AccountId, a => a.DisplayName);
        var memberNames = (await GetHouseholdAsync(ct)).ToDictionary(m => m.Id, m => m.Name);
        return TaxReportBuilder.ToCsv(report, accountNames, memberNames);
    }

    /// <summary>
    /// Sets the business-use share on every claim of <see cref="SetBusinessUseRequest.TaxCode"/>
    /// for <paramref name="year"/>, or only those in one spending category. Returns how many changed.
    /// </summary>
    public async Task<int> SetBusinessUseAsync(int year, SetBusinessUseRequest request, string? person = null, CancellationToken ct = default)
    {
        if (TaxCategories.Find(request.TaxCode) is not { } category)
        {
            throw LedgerValidationException.For("taxCode", $"Unknown tax category '{request.TaxCode}'.");
        }

        ValidateBusinessUse(category.Code, request.Percent);
        var percent = NormalizePercent(request.Percent);

        var (from, to) = TaxReportBuilder.ReportingRange(year);
        var transactions = await db.Transactions
            .Where(t => EF.Property<string?>(t, LedgerDbContext.TaxCode) == category.Code)
            .Between(from, to)
            .ToLedgerTransactionsAsync(ct);

        var changed = 0;
        foreach (var item in TaxReportBuilder.ForPerson(transactions, person))
        {
            if (TaxReportBuilder.ClaimYear(item) != year || item.Tax!.Status == TaxTagStatus.Rejected) continue;
            if (request.Category is { Length: > 0 } spending && item.Transaction.Category != spending) continue;
            if (item.Tax.BusinessUsePercent == percent) continue;

            await SetTaxTagAsync(item.Transaction.AccountId, item.Transaction.TransactionId, item.Tax with { BusinessUsePercent = percent }, ct);
            changed++;
        }

        return changed;
    }

    // ---------------------------------------------------------------- holdings

    /// <summary>Each investment account's holdings snapshot (one account's, with <paramref name="accountId"/>).</summary>
    public async Task<IReadOnlyList<HoldingsSnapshot>> GetHoldingsAsync(string? accountId = null, CancellationToken ct = default)
    {
        var query = db.Holdings.AsNoTracking();
        if (accountId is not null) query = query.Where(h => h.AccountId == accountId);
        var rows = await query.ToListAsync(ct);

        // Sorted here: SQLite can't order DateTimeOffset, and it's a few hundred rows at most.
        return rows
            .GroupBy(h => h.AccountId)
            .Select(g => new HoldingsSnapshot(
                g.Key,
                DateOnly.FromDateTime(g.Max(h => h.AsOf).UtcDateTime),
                g.OrderBy(h => h.Position).Select(ToHolding).ToList()))
            .OrderBy(s => s.AccountId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Replaces an investment account's holdings with a new snapshot and, when asked, sets the
    /// account balance to its total (unless the balance is from a later date). Null when the
    /// account doesn't exist.
    /// </summary>
    public async Task<ImportHoldingsResult?> ImportHoldingsAsync(string accountId, ImportHoldingsRequest request, CancellationToken ct = default)
    {
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.AccountId == accountId, ct);
        if (account is null) return null;
        if (account.AccountCategory != AccountCategory.InvestmentAccount)
        {
            throw LedgerValidationException.For("account", "Holdings can only be imported into an investment account.");
        }

        // Holdings in another currency are valued at the ledger's rate for it, when it has one.
        var rates = (await GetExchangeRatesAsync(ct)).ToDictionary(r => r.Currency, r => r.Rate);
        var holdings = ValidateHoldings(request)
            .Select(h => h.Currency != Holding.Cad && rates.TryGetValue(h.Currency, out var rate) ? h with { ExchangeRate = rate } : h)
            .ToList();
        var asOf = new DateTimeOffset(request.AsOf.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        // Removed and added in one SaveChanges, so a failed import leaves the old snapshot in place.
        db.Holdings.RemoveRange(await db.Holdings.Where(h => h.AccountId == accountId).ToListAsync(ct));
        db.Holdings.AddRange(holdings.Select((h, i) => new HoldingRecord
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            AsOf = asOf,
            Position = i,
            Symbol = h.Symbol,
            Description = h.Description,
            Quantity = h.Quantity,
            Price = h.Price,
            MarketValue = h.MarketValue,
            BookValue = h.BookValue,
            Currency = h.Currency,
            ExchangeRate = h.ExchangeRate,
            PriceAsOf = h.PriceAsOf,
            PriceSource = h.PriceSource,
        }));

        // The balance is in the account's currency: holdings in another go through Canadian dollars.
        var snapshot = new HoldingsSnapshot(accountId, request.AsOf, holdings);
        var currency = CurrencyRates.CurrencyOf(account);
        var total = Holding.TotalIn(holdings, currency, currency == Holding.Cad ? 1m : rates.TryGetValue(currency, out var accountRate) ? accountRate : null);
        var balanceUpdated = request.SetBalance && total is not null && (account.BalanceAsOf is not { } current || DateOnly.FromDateTime(current.UtcDateTime) <= request.AsOf);
        if (balanceUpdated)
        {
            db.Entry(account).CurrentValues.SetValues(account with { CurrentBalance = total, BalanceAsOf = asOf });
        }

        await db.SaveChangesAsync(ct);
        return new ImportHoldingsResult(snapshot, balanceUpdated);
    }

    /// <summary>Removes an account's holdings snapshot; false when it had none.</summary>
    public async Task<bool> DeleteHoldingsAsync(string accountId, CancellationToken ct = default) =>
        await db.Holdings.Where(h => h.AccountId == accountId).ExecuteDeleteAsync(ct) > 0;

    // ---------------------------------------------------------------- price history

    /// <summary>Enough for a few hundred holdings across every month the feed carries.</summary>
    public const int MaxPriceReadings = 50_000;

    /// <summary>The month-end closes saved for the holdings' listings, from the monthly price history feed.</summary>
    public async Task<IReadOnlyList<PriceReading>> GetPriceHistoryAsync(CancellationToken ct = default) =>
        (await db.PriceHistory.AsNoTracking().ToListAsync(ct))
            .Select(r => new PriceReading(r.Symbol, r.Currency, DateOnly.FromDateTime(r.Month.UtcDateTime), r.Close))
            .ToList();

    /// <summary>
    /// Saves month-end closes from the monthly price history feed: a symbol, currency and month not
    /// yet saved is added, and one saved with another close (the feed adjusts for splits) is changed.
    /// Saved at once, and only when something changed.
    /// </summary>
    public async Task<ApplyPriceHistoryResult> ApplyPriceHistoryAsync(ApplyPriceHistoryRequest request, CancellationToken ct = default)
    {
        var readings = ValidatePriceHistory(request);
        if (readings.Count == 0) return new ApplyPriceHistoryResult(0);

        var records = await db.PriceHistory.ToListAsync(ct);
        var existing = records
            .GroupBy(r => (r.Symbol.ToUpperInvariant(), r.Currency, DateOnly.FromDateTime(r.Month.UtcDateTime)))
            .ToDictionary(g => g.Key, g => g.First());
        int changes = 0, count = records.Count;
        foreach (var ((symbol, currency, month), close) in readings)
        {
            if (existing.TryGetValue((symbol.ToUpperInvariant(), currency, month), out var record))
            {
                if (record.Close == close) continue;
                record.Close = close;
            }
            else
            {
                db.PriceHistory.Add(new PriceHistoryRecord { Id = Guid.NewGuid(), Symbol = symbol, Currency = currency, Month = new DateTimeOffset(month.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), Close = close });
                count++;
            }

            changes++;
        }

        if (count > MaxPriceReadings)
        {
            db.ChangeTracker.Clear();
            throw LedgerValidationException.For("readings", $"A ledger can keep at most {MaxPriceReadings} month-end prices.");
        }

        if (changes > 0) await db.SaveChangesAsync(ct);
        return new ApplyPriceHistoryResult(changes);
    }

    /// <summary>Each reading's symbol, currency and month with its close; the last one wins for a repeat.</summary>
    private static Dictionary<(string Symbol, string Currency, DateOnly Month), decimal> ValidatePriceHistory(ApplyPriceHistoryRequest request)
    {
        if (request.Readings is not { } list) return [];
        if (list.Count > ApplyPriceHistoryRequest.MaxReadings)
        {
            throw LedgerValidationException.For("readings", $"Send at most {ApplyPriceHistoryRequest.MaxReadings} month-end prices at once.");
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var result = new Dictionary<(string, string, DateOnly), decimal>();
        foreach (var r in list)
        {
            var symbol = (r.Symbol ?? "").Trim();
            var currency = (r.Currency ?? "").Trim().ToUpperInvariant();
            if (symbol.Length is 0 or > Holding.MaxSymbolLength || currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
            {
                throw LedgerValidationException.For("readings", "Every month-end price needs a symbol and a currency code.");
            }

            var month = HoldingPriceMoves.FirstOfMonth(r.Month);
            if (r.Close is <= 0 or >= PriceReading.MaxClose || month.Year < 1970 || month > today)
            {
                throw LedgerValidationException.For("readings", $"The price for {symbol} in {month:yyyy-MM} can't be right.");
            }

            result[(symbol, currency, month)] = Math.Round(r.Close, 6);
        }

        return result;
    }

    // ---------------------------------------------------------------- exchange rates

    /// <summary>The ledger's rate for each currency it has one for.</summary>
    public async Task<IReadOnlyList<ExchangeRate>> GetExchangeRatesAsync(CancellationToken ct = default) =>
        (await db.ExchangeRates.AsNoTracking().ToListAsync(ct))
            .OrderBy(r => r.Currency, StringComparer.Ordinal)
            .Select(r => new ExchangeRate(r.Currency, r.Rate, DateOnly.FromDateTime(r.AsOf.UtcDateTime), r.Source))
            .ToList();

    /// <summary>
    /// Sets the ledger's rate for a currency and revalues every holding in it. An account whose
    /// balance was its holdings' total (within a dollar) gets the new total, so net worth follows.
    /// </summary>
    public async Task<ExchangeRate> SetExchangeRateAsync(ExchangeRate rate, CancellationToken ct = default)
    {
        var currency = (rate.Currency ?? "").Trim().ToUpperInvariant();
        if (!IsCurrencyCode(currency) || currency == Holding.Cad)
        {
            throw LedgerValidationException.For("currency", "Enter a currency code other than CAD, such as USD.");
        }

        if (rate.Rate is <= 0 or > ExchangeRate.MaxRate)
        {
            throw LedgerValidationException.For("rate", $"Enter how many Canadian dollars 1 {currency} is worth.");
        }

        if (rate.AsOf.Year < 1990 || rate.AsOf > DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1))
        {
            throw LedgerValidationException.For("asOf", "The rate's date can't be in the future.");
        }

        var value = Math.Round(rate.Rate, 8);
        var stored = await GetExchangeRatesAsync(ct);
        var before = new CurrencyRates(stored);
        var after = new CurrencyRates(stored.Where(r => r.Currency != currency).Append(new ExchangeRate(currency, value, rate.AsOf)));
        var record = await db.ExchangeRates.SingleOrDefaultAsync(r => r.Currency == currency, ct);
        if (record is null)
        {
            record = new ExchangeRateRecord { Id = Guid.NewGuid(), Currency = currency };
            db.ExchangeRates.Add(record);
        }

        record.Rate = value;
        record.AsOf = new DateTimeOffset(rate.AsOf.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        record.Source = rate.Source;

        // Revalue the holdings in this currency, and the balances that were their accounts' totals.
        // A balance is in its account's currency, so one in this currency moves with the rate when
        // the account's holdings are in another.
        var holdings = await db.Holdings.ToListAsync(ct);
        var accountIds = holdings.Select(h => h.AccountId).Distinct().ToList();
        var accounts = await db.Accounts.Where(a => accountIds.Contains(a.AccountId)).ToListAsync(ct);
        foreach (var account in accounts)
        {
            var rows = holdings.Where(h => h.AccountId == account.AccountId).ToList();
            var accountCurrency = CurrencyRates.CurrencyOf(account);
            if (accountCurrency != currency && rows.All(h => h.Currency != currency || h.ExchangeRate == value)) continue;

            var was = Holding.TotalIn(rows.Select(ToHolding), accountCurrency, before.RateOf(accountCurrency));
            foreach (var h in rows.Where(h => h.Currency == currency)) h.ExchangeRate = value;
            var now = Holding.TotalIn(rows.Select(ToHolding), accountCurrency, after.RateOf(accountCurrency));

            if (account.CurrentBalance is { } balance && was is { } total && Math.Abs(balance - total) < 1 && now is { } revalued && revalued != balance)
            {
                db.Entry(account).CurrentValues.SetValues(account with { CurrentBalance = revalued });
            }
        }

        await db.SaveChangesAsync(ct);
        return new ExchangeRate(currency, value, rate.AsOf, rate.Source);
    }

    // ---------------------------------------------------------------- monthly exchange rates

    /// <summary>Enough for every currency the Bank of Canada publishes over a couple of decades.</summary>
    public const int MaxMonthlyExchangeRates = 10_000;

    /// <summary>The Bank of Canada's monthly average rates saved for the ledger's currencies, by currency then month.</summary>
    public async Task<IReadOnlyList<MonthlyExchangeRate>> GetMonthlyExchangeRatesAsync(CancellationToken ct = default) =>
        (await db.MonthlyExchangeRates.AsNoTracking().ToListAsync(ct))
            .Select(r => new MonthlyExchangeRate(r.Currency, DateOnly.FromDateTime(r.Month.UtcDateTime), r.Rate))
            .OrderBy(r => r.Currency, StringComparer.Ordinal)
            .ThenBy(r => r.Month)
            .ToList();

    /// <summary>
    /// Saves monthly average rates: a currency and month not yet saved is added, and one saved with
    /// another rate is changed. Saved at once, and only when something changed.
    /// </summary>
    public async Task<ApplyMonthlyExchangeRatesResult> ApplyMonthlyExchangeRatesAsync(ApplyMonthlyExchangeRatesRequest request, CancellationToken ct = default)
    {
        var list = request.Rates ?? [];
        if (list.Count > ApplyMonthlyExchangeRatesRequest.MaxRates)
        {
            throw LedgerValidationException.For("rates", $"Send at most {ApplyMonthlyExchangeRatesRequest.MaxRates} monthly rates at once.");
        }

        var thisMonth = HoldingPriceMoves.FirstOfMonth(DateOnly.FromDateTime(DateTime.UtcNow));
        var rates = new Dictionary<(string Currency, DateOnly Month), decimal>();
        foreach (var r in list)
        {
            var currency = (r.Currency ?? "").Trim().ToUpperInvariant();
            var month = HoldingPriceMoves.FirstOfMonth(r.Month);
            if (!IsCurrencyCode(currency) || currency == Holding.Cad || r.Rate is <= 0 or > ExchangeRate.MaxRate || month.Year < 1950 || month > thisMonth)
            {
                throw LedgerValidationException.For("rates", $"The {currency} rate for {month:yyyy-MM} can't be right.");
            }

            rates[(currency, month)] = Math.Round(r.Rate, 8);
        }

        if (rates.Count == 0) return new ApplyMonthlyExchangeRatesResult(0);
        var records = await db.MonthlyExchangeRates.ToListAsync(ct);
        var existing = records
            .GroupBy(r => (r.Currency, DateOnly.FromDateTime(r.Month.UtcDateTime)))
            .ToDictionary(g => g.Key, g => g.First());
        int changes = 0, count = records.Count;
        foreach (var ((currency, month), rate) in rates)
        {
            if (existing.TryGetValue((currency, month), out var record))
            {
                if (record.Rate == rate) continue;
                record.Rate = rate;
            }
            else
            {
                db.MonthlyExchangeRates.Add(new MonthlyExchangeRateRecord { Id = Guid.NewGuid(), Currency = currency, Month = new DateTimeOffset(month.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), Rate = rate });
                count++;
            }

            changes++;
        }

        if (count > MaxMonthlyExchangeRates)
        {
            db.ChangeTracker.Clear();
            throw LedgerValidationException.For("rates", $"A ledger can keep at most {MaxMonthlyExchangeRates} monthly rates.");
        }

        if (changes > 0) await db.SaveChangesAsync(ct);
        return new ApplyMonthlyExchangeRatesResult(changes);
    }

    /// <summary>
    /// The transactions with their amounts in Canadian dollars, each at its month's average rate (see
    /// <see cref="MonthlyCurrencyRates"/>), by its account's currency. As they are when every account is in Canadian dollars.
    /// </summary>
    private async Task<IReadOnlyList<Transaction>> InCadAsync(IReadOnlyList<Transaction> transactions, IReadOnlyList<Account>? accounts, CancellationToken ct)
    {
        accounts ??= await db.Accounts.AsNoTracking().ToListAsync(ct);
        var currencyOf = accounts.ToDictionary(a => a.AccountId, CurrencyRates.CurrencyOf);
        if (currencyOf.Values.All(c => c == Holding.Cad)) return transactions;
        var rates = new MonthlyCurrencyRates(new CurrencyRates(await GetExchangeRatesAsync(ct)), await GetMonthlyExchangeRatesAsync(ct));
        return rates.InCad(transactions, currencyOf);
    }

    // ---------------------------------------------------------------- quotes

    /// <summary>
    /// Prices holdings from the quote feed. Each quote applies to every holding with its symbol (as
    /// the report wrote it) and currency that has a quantity: the price becomes the quote, the
    /// market value quantity × price (× 100 for an option contract). A quote older than the holding's snapshot is skipped, since
    /// the statement is newer. An account whose balance was its holdings' total (within a dollar)
    /// gets the new total, so net worth follows, as with exchange rates; the balance's date stays
    /// the statement's, so a later import still compares against it. Cash named by its currency
    /// ("USD", from a report imported before such lines were read as cash) becomes cash in that
    /// currency here, so its value and the balance come right without importing again.
    /// </summary>
    public async Task<ApplyQuotesResult> ApplyQuotesAsync(ApplyQuotesRequest request, CancellationToken ct = default)
    {
        var quotes = ValidateQuotes(request);

        var holdings = await db.Holdings.ToListAsync(ct);
        var accountIds = holdings.Select(h => h.AccountId).Distinct().ToList();
        var accounts = await db.Accounts.Where(a => accountIds.Contains(a.AccountId)).ToListAsync(ct);
        var rates = new CurrencyRates(await GetExchangeRatesAsync(ct));

        int repriced = 0, balances = 0;
        foreach (var account in accounts)
        {
            var rows = holdings.Where(h => h.AccountId == account.AccountId).ToList();
            var accountCurrency = CurrencyRates.CurrencyOf(account);
            var was = Holding.TotalIn(rows.Select(ToHolding), accountCurrency, rates.RateOf(accountCurrency));

            var changed = false;
            foreach (var row in rows)
            {
                if (ToHolding(row) is { NamedCashCurrency: { } cashCurrency } named)
                {
                    var cash = named.AsNamedCash(rates.RateOf(cashCurrency));
                    (row.Symbol, row.Description, row.Quantity, row.Price, row.MarketValue, row.BookValue) = (cash.Symbol, cash.Description, cash.Quantity, cash.Price, cash.MarketValue, cash.BookValue);
                    (row.Currency, row.ExchangeRate, row.PriceAsOf, row.PriceSource) = (cash.Currency, cash.ExchangeRate, null, HoldingPriceSource.Statement);
                    repriced++;
                    changed = true;
                    continue;
                }

                // An option imported at quantity × price (before contracts were valued at 100 shares).
                if (ToHolding(row) is { MissesContractMultiplier: true } option)
                {
                    row.MarketValue = option.WithContractValue().MarketValue;
                    repriced++;
                    changed = true;
                }

                // The symbol and quantity as read (a statement may name them only in the description).
                var listed = ToHolding(row);
                if (listed.Symbol is not { } symbol || listed.Quantity is not { } quantity || quantity == 0) continue;
                if (!quotes.TryGetValue((symbol, row.Currency), out var quote)) continue;
                if (DateOnly.FromDateTime(quote.AsOf.UtcDateTime) < DateOnly.FromDateTime(row.AsOf.UtcDateTime)) continue;

                var value = listed.ValueAt(quantity, quote.Price);
                if (row.PriceSource == HoldingPriceSource.Quote && row.Price == quote.Price && row.MarketValue == value && row.PriceAsOf == quote.AsOf) continue;

                // Kept, so the quantity stays the statement's once the price moves.
                (row.Symbol, row.Quantity) = (symbol, quantity);
                row.Price = quote.Price;
                row.MarketValue = value;
                row.PriceAsOf = quote.AsOf;
                row.PriceSource = HoldingPriceSource.Quote;
                repriced++;
                changed = true;
            }

            if (!changed) continue;
            var now = Holding.TotalIn(rows.Select(ToHolding), accountCurrency, rates.RateOf(accountCurrency));
            if (account.CurrentBalance is { } balance && was is { } total && Math.Abs(balance - total) < 1 && now is { } revalued && revalued != balance)
            {
                db.Entry(account).CurrentValues.SetValues(account with { CurrentBalance = revalued });
                balances++;
            }
        }

        if (repriced > 0) await db.SaveChangesAsync(ct);
        return new ApplyQuotesResult(repriced, balances);
    }

    private static Dictionary<(string Symbol, string Currency), HoldingQuote> ValidateQuotes(ApplyQuotesRequest request)
    {
        if (request.Quotes is not { } quotes) return [];
        if (quotes.Count > ApplyQuotesRequest.MaxQuotes)
        {
            throw LedgerValidationException.For("quotes", $"Send at most {ApplyQuotesRequest.MaxQuotes} quotes at once.");
        }

        var latest = DateTimeOffset.UtcNow.AddDays(1);
        var clean = new Dictionary<(string, string), HoldingQuote>();
        foreach (var q in quotes)
        {
            var symbol = (q.Symbol ?? "").Trim();
            var currency = (q.Currency ?? "").Trim().ToUpperInvariant();
            if (symbol.Length is 0 or > Holding.MaxSymbolLength || currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
            {
                throw LedgerValidationException.For("quotes", "Every quote needs a symbol and a currency code.");
            }

            if (q.Price is <= 0 or >= 1_000_000_000m || q.AsOf.Year < 1990 || q.AsOf > latest)
            {
                throw LedgerValidationException.For("quotes", $"The quote for {symbol} has a price or date that can't be right.");
            }

            clean[(symbol, currency)] = q with { Symbol = symbol, Currency = currency, Price = Math.Round(q.Price, 8) };
        }

        return clean;
    }

    // ---------------------------------------------------------------- real estate

    public const int MaxProperties = 50;
    /// <summary>Enough for every property's area to carry the feed's index back to 2005 and on for decades.</summary>
    public const int MaxHpiReadings = 30_000;

    /// <summary>The household's properties, by name, each with the accounts secured by it.</summary>
    public async Task<IReadOnlyList<RealEstateProperty>> GetPropertiesAsync(CancellationToken ct = default)
    {
        var records = await db.Properties.AsNoTracking().ToListAsync(ct);
        if (records.Count == 0) return [];
        var links = await db.Accounts.AsNoTracking()
            .Select(a => new { a.AccountId, SecuredBy = EF.Property<string?>(a, LedgerDbContext.SecuredBy) })
            .Where(a => a.SecuredBy != null)
            .ToListAsync(ct);
        return records
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => new RealEstateProperty(
                p.Id, p.Name, p.City, p.Value, p.ValueAsOf is { } asOf ? DateOnly.FromDateTime(asOf.UtcDateTime) : null,
                links.Where(l => l.SecuredBy == p.Id).Select(l => l.AccountId).Order(StringComparer.Ordinal).ToList()))
            .ToList();
    }

    public async Task<RealEstateProperty> CreatePropertyAsync(PropertyRequest request, CancellationToken ct = default)
    {
        if (await db.Properties.CountAsync(ct) >= MaxProperties)
        {
            throw LedgerValidationException.For("name", $"A ledger can have at most {MaxProperties} properties.");
        }

        var record = new PropertyRecord { Id = $"pr-{Guid.NewGuid():N}"[..15], Name = "" };
        db.Properties.Add(record);
        await SavePropertyAsync(record, request, ct);
        return (await GetPropertiesAsync(ct)).Single(p => p.Id == record.Id);
    }

    /// <summary>Changes a property, and which accounts are secured by it. Null when there's no such property.</summary>
    public async Task<RealEstateProperty?> UpdatePropertyAsync(string id, PropertyRequest request, CancellationToken ct = default)
    {
        var record = await db.Properties.SingleOrDefaultAsync(p => p.Id == id, ct);
        if (record is null) return null;
        await SavePropertyAsync(record, request, ct);
        return (await GetPropertiesAsync(ct)).Single(p => p.Id == record.Id);
    }

    /// <summary>Deletes a property; the loans secured by it stay, no longer linked to anything.</summary>
    public async Task<bool> DeletePropertyAsync(string id, CancellationToken ct = default)
    {
        var record = await db.Properties.SingleOrDefaultAsync(p => p.Id == id, ct);
        if (record is null) return false;

        await db.Accounts
            .Where(a => EF.Property<string?>(a, LedgerDbContext.SecuredBy) == id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => EF.Property<string?>(a, LedgerDbContext.SecuredBy), (string?)null), ct);
        db.Properties.Remove(record);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task SavePropertyAsync(PropertyRecord record, PropertyRequest request, CancellationToken ct)
    {
        var name = PropertyValuation.CleanText(request.Name);
        if (name.Length is 0 or > RealEstateProperty.MaxNameLength)
        {
            throw LedgerValidationException.For("name", $"Give the property a name of at most {RealEstateProperty.MaxNameLength} characters, such as its street address.");
        }

        var city = PropertyValuation.CleanText(request.City);
        if (city.Length > RealEstateProperty.MaxCityLength)
        {
            throw LedgerValidationException.For("city", $"Keep the city or area to {RealEstateProperty.MaxCityLength} characters.");
        }

        if (request.Value is <= 0 or > RealEstateProperty.MaxValue)
        {
            throw LedgerValidationException.For("value", "Enter what the property is worth, more than zero.");
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var asOf = request.Value is null ? (DateOnly?)null : request.ValueAsOf ?? today;
        if (asOf is { } date && (date.Year < 1900 || date > today.AddDays(1)))
        {
            throw LedgerValidationException.For("valueAsOf", "The value's date can't be in the future.");
        }

        var accountIds = (request.AccountIds ?? []).Distinct().ToList();
        var accounts = await db.Accounts.AsNoTracking().Where(a => accountIds.Contains(a.AccountId)).ToListAsync(ct);
        if (accounts.Count != accountIds.Count)
        {
            throw LedgerValidationException.For("accountIds", "One of the chosen accounts doesn't exist.");
        }

        if (accounts.Any(a => !a.IsLiability))
        {
            throw LedgerValidationException.For("accountIds", "Only loans and lines of credit can be secured by a property.");
        }

        record.Name = name;
        record.City = city.Length == 0 ? null : city;
        record.Value = request.Value is { } value ? Math.Round(value, 2, MidpointRounding.AwayFromZero) : null;
        record.ValueAsOf = asOf is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null;
        await db.SaveChangesAsync(ct);

        // The chosen accounts are secured by this property (and no other); the rest no longer are.
        var id = record.Id;
        await db.Accounts
            .Where(a => EF.Property<string?>(a, LedgerDbContext.SecuredBy) == id && !accountIds.Contains(a.AccountId))
            .ExecuteUpdateAsync(s => s.SetProperty(a => EF.Property<string?>(a, LedgerDbContext.SecuredBy), (string?)null), ct);
        if (accountIds.Count > 0)
        {
            await db.Accounts
                .Where(a => accountIds.Contains(a.AccountId))
                .ExecuteUpdateAsync(s => s.SetProperty(a => EF.Property<string?>(a, LedgerDbContext.SecuredBy), id), ct);
        }
    }

    /// <summary>Every house price index reading, by area then month.</summary>
    public async Task<IReadOnlyList<HpiReading>> GetHpiReadingsAsync(CancellationToken ct = default) =>
        (await db.HousePriceIndex.AsNoTracking().ToListAsync(ct))
            .Select(r => new HpiReading(r.Area, DateOnly.FromDateTime(r.Month.UtcDateTime), r.Index))
            .OrderBy(r => r.Area, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Month)
            .ToList();

    /// <summary>Records an area's index for a month, replacing the one already there for that month.</summary>
    public async Task<HpiReading> SetHpiReadingAsync(HpiReading reading, CancellationToken ct = default)
    {
        var area = PropertyValuation.CleanText(reading.Area);
        if (area.Length is 0 or > HpiReading.MaxAreaLength)
        {
            throw LedgerValidationException.For("area", $"Enter the area the index is for, at most {HpiReading.MaxAreaLength} characters.");
        }

        if (reading.Index is <= 0 or > HpiReading.MaxIndex)
        {
            throw LedgerValidationException.For("index", "Enter the index (or benchmark price), more than zero.");
        }

        var month = new DateOnly(reading.Month.Year, reading.Month.Month, 1);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (month.Year < 1900 || month > today)
        {
            throw LedgerValidationException.For("month", "The reading's month can't be in the future.");
        }

        var at = new DateTimeOffset(month.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var existing = (await db.HousePriceIndex.Where(r => r.Month == at).ToListAsync(ct)).FirstOrDefault(r => HpiReading.SameArea(r.Area, area));
        if (existing is null)
        {
            if (await db.HousePriceIndex.CountAsync(ct) >= MaxHpiReadings)
            {
                throw LedgerValidationException.For("month", $"A ledger can keep at most {MaxHpiReadings} index readings. Delete some old ones first.");
            }

            // An area already in use keeps its spelling, so "oakville" lands with "Oakville".
            var known = (await db.HousePriceIndex.Select(r => r.Area).Distinct().ToListAsync(ct)).FirstOrDefault(a => HpiReading.SameArea(a, area));
            existing = new HpiRecord { Id = Guid.NewGuid(), Area = known ?? area, Month = at };
            db.HousePriceIndex.Add(existing);
        }

        existing.Index = Math.Round(reading.Index, 4);
        await db.SaveChangesAsync(ct);
        return new HpiReading(existing.Area, month, existing.Index);
    }

    /// <summary>
    /// Makes each area's readings the series given, from the house price index feed: months added,
    /// changed, or removed when the feed no longer has them, so an area is never a mix of the feed's
    /// index and one typed in. Areas not named keep their readings. Saved at once, and only when
    /// something changed.
    /// </summary>
    public async Task<ApplyHpiResult> ApplyHpiAsync(ApplyHpiRequest request, CancellationToken ct = default)
    {
        var series = ValidateHpi(request);
        if (series.Count == 0) return new ApplyHpiResult(0, 0);

        var records = await db.HousePriceIndex.ToListAsync(ct);
        int areas = 0, changes = 0, count = records.Count;
        foreach (var (area, months) in series)
        {
            var existing = records.Where(r => HpiReading.SameArea(r.Area, area)).ToList();
            // An area already in use keeps its spelling, so "oakville" lands with "Oakville".
            var name = existing.FirstOrDefault()?.Area ?? area;
            var byMonth = existing.GroupBy(r => DateOnly.FromDateTime(r.Month.UtcDateTime)).ToDictionary(g => g.Key, g => g.ToList());
            var changed = 0;

            foreach (var (month, group) in byMonth)
            {
                // A month the feed doesn't have, and any second reading for one month, go.
                var keep = months.ContainsKey(month) ? group[0] : null;
                foreach (var extra in group.Where(r => r != keep))
                {
                    db.HousePriceIndex.Remove(extra);
                    changed++;
                    count--;
                }

                if (keep is not null && keep.Index != months[month])
                {
                    keep.Index = months[month];
                    changed++;
                }
            }

            foreach (var (month, index) in months.Where(m => !byMonth.ContainsKey(m.Key)))
            {
                db.HousePriceIndex.Add(new HpiRecord { Id = Guid.NewGuid(), Area = name, Month = new DateTimeOffset(month.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), Index = index });
                changed++;
                count++;
            }

            if (changed > 0) areas++;
            changes += changed;
        }

        if (count > MaxHpiReadings)
        {
            db.ChangeTracker.Clear();
            throw LedgerValidationException.For("series", $"A ledger can keep at most {MaxHpiReadings} index readings.");
        }

        if (changes > 0) await db.SaveChangesAsync(ct);
        return new ApplyHpiResult(areas, changes);
    }

    /// <summary>Each series' area, cleaned, with its months that have an index; one series per area.</summary>
    private static List<(string Area, Dictionary<DateOnly, decimal> Months)> ValidateHpi(ApplyHpiRequest request)
    {
        if (request.Series is not { } list) return [];
        if (list.Count > ApplyHpiRequest.MaxSeries)
        {
            throw LedgerValidationException.For("series", $"Send at most {ApplyHpiRequest.MaxSeries} areas at a time.");
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var result = new List<(string Area, Dictionary<DateOnly, decimal> Months)>();
        foreach (var s in list)
        {
            var area = PropertyValuation.CleanText(s.Area);
            if (area.Length is 0 or > HpiReading.MaxAreaLength)
            {
                throw LedgerValidationException.For("series", $"Each area needs a name of at most {HpiReading.MaxAreaLength} characters.");
            }

            if (result.Any(r => HpiReading.SameArea(r.Area, area)))
            {
                throw LedgerValidationException.For("series", $"{area} is in the request twice.");
            }

            var from = new DateOnly(s.From.Year, s.From.Month, 1);
            if (s.Index is not { Count: > 0 and <= OwnquityHpi.MaxMonths } index || from.Year < 1900 || from.AddMonths(index.Count - 1) > today)
            {
                throw LedgerValidationException.For("series", $"The index for {area} has months that can't be right.");
            }

            var months = new Dictionary<DateOnly, decimal>();
            for (var i = 0; i < index.Count; i++)
            {
                if (index[i] is not { } value) continue;
                if (value is <= 0 or > HpiReading.MaxIndex)
                {
                    throw LedgerValidationException.For("series", $"The index for {area} has a value that can't be right.");
                }

                months[from.AddMonths(i)] = Math.Round(value, 4);
            }

            if (months.Count > 0) result.Add((area, months));
        }

        return result;
    }

    /// <summary>Deletes an area's reading for a month. False when there's none.</summary>
    public async Task<bool> DeleteHpiReadingAsync(string area, DateOnly month, CancellationToken ct = default)
    {
        var at = new DateTimeOffset(new DateOnly(month.Year, month.Month, 1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var record = (await db.HousePriceIndex.Where(r => r.Month == at).ToListAsync(ct)).FirstOrDefault(r => HpiReading.SameArea(r.Area, area));
        if (record is null) return false;
        db.HousePriceIndex.Remove(record);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static List<Holding> ValidateHoldings(ImportHoldingsRequest request)
    {
        if (request.Holdings is not { Count: > 0 } holdings)
        {
            throw LedgerValidationException.For("holdings", "There are no holdings to import.");
        }

        if (holdings.Count > Holding.MaxPerAccount)
        {
            throw LedgerValidationException.For("holdings", $"Import at most {Holding.MaxPerAccount} holdings into one account.");
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (request.AsOf.Year < 1990 || request.AsOf > today.AddDays(1))
        {
            throw LedgerValidationException.For("asOf", "Enter the date of the holdings report, which can't be in the future.");
        }

        const decimal limit = 1_000_000_000_000m;
        var clean = new List<Holding>(holdings.Count);
        foreach (var h in holdings)
        {
            var symbol = string.IsNullOrWhiteSpace(h.Symbol) ? null : h.Symbol.Trim();
            var description = string.IsNullOrWhiteSpace(h.Description) ? null : h.Description.Trim();
            var currency = (h.Currency ?? "").Trim().ToUpperInvariant();
            if (symbol is null && description is null)
            {
                throw LedgerValidationException.For("holdings", "Every holding needs a symbol or a description.");
            }

            if (symbol?.Length > Holding.MaxSymbolLength || description?.Length > Holding.MaxDescriptionLength)
            {
                throw LedgerValidationException.For("holdings", $"Symbols can be at most {Holding.MaxSymbolLength} characters and descriptions {Holding.MaxDescriptionLength}.");
            }

            if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
            {
                throw LedgerValidationException.For("holdings", $"{symbol ?? description} has no currency code (such as CAD or USD).");
            }

            if (h.ExchangeRate is <= 0 or > 10_000 || (currency == Holding.Cad && h.ExchangeRate != 1))
            {
                throw LedgerValidationException.For("holdings", $"Enter a positive exchange rate to Canadian dollars for {currency}.");
            }

            if (Math.Abs(h.MarketValue) >= limit || Math.Abs(h.BookValue ?? 0) >= limit || Math.Abs(h.Quantity ?? 0) >= limit || Math.Abs(h.Price ?? 0) >= limit)
            {
                throw LedgerValidationException.For("holdings", $"{symbol ?? description} has an amount too large to be right.");
            }

            // A quoted price keeps its source and time (a restored backup); a report's is the statement's.
            var quoted = h.PriceSource == HoldingPriceSource.Quote && h.PriceAsOf is { } at && at <= DateTimeOffset.UtcNow.AddDays(1);
            // An option valued at quantity × price is missing the 100 shares each contract covers.
            clean.Add(new Holding(symbol, description, h.Quantity, h.Price, Math.Round(h.MarketValue, 4), h.BookValue is { } b ? Math.Round(b, 4) : null, currency, h.ExchangeRate,
                quoted ? h.PriceAsOf : null, quoted ? HoldingPriceSource.Quote : HoldingPriceSource.Statement).WithContractValue());
        }

        return clean;
    }

    // ---------------------------------------------------------------- budgets

    /// <summary>The years a budget can be set for or looked at: from 2000 to next year.</summary>
    private static void ValidateBudgetYear(int year)
    {
        if (year < 2000 || year > DateTime.UtcNow.Year + 1)
        {
            throw LedgerValidationException.For("year", $"Pick a year from 2000 to {DateTime.UtcNow.Year + 1}.");
        }
    }

    /// <summary>Every budget line, by year then category.</summary>
    public async Task<IReadOnlyList<BudgetTarget>> GetBudgetTargetsAsync(CancellationToken ct = default) =>
        (await db.Budgets.AsNoTracking().ToListAsync(ct))
            .OrderBy(b => b.Year)
            .ThenBy(b => b.Category, StringComparer.Ordinal)
            .ThenBy(b => b.SubCategory, StringComparer.Ordinal)
            .Select(b => new BudgetTarget(b.Year, b.Category, b.SubCategory, b.Amount, b.Yearly))
            .ToList();

    /// <summary>
    /// The year's budget page: each spending category with this year's and last year's budget, what's
    /// been spent this year, and a suggestion from last year's spending (or the latest complete months).
    /// Amounts are in Canadian dollars, other currencies at each month's average rate.
    /// </summary>
    public async Task<BudgetPlan> GetBudgetPlanAsync(int year, CancellationToken ct = default)
    {
        ValidateBudgetYear(year);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var basis = BudgetCalculator.Basis(year, await FirstTransactionDateAsync(ct), today);
        var yearStart = new DateOnly(year, 1, 1);
        var yearEnd = new DateOnly(year, 12, 31);
        var from = basis is not null && basis.From < yearStart ? basis.From : yearStart;
        var to = basis is not null && basis.To > yearEnd ? basis.To : yearEnd;
        var transactions = await InCadAsync(await db.Transactions.AsNoTracking().Between(from, to).ToListAsync(ct), null, ct);
        var targets = (await GetBudgetTargetsAsync(ct)).Where(t => t.Year == year || t.Year == year - 1);
        return BudgetCalculator.Plan(year, today, basis, await GetCategoryTreeAsync(ct), targets, transactions);
    }

    /// <summary>
    /// Sets the year's budget for the lines given (a year's amount, in Canadian dollars, and whether it's
    /// kept for the year only), or removes a line given no amount. Returns the year's budget lines.
    /// </summary>
    public async Task<IReadOnlyList<BudgetTarget>> SetBudgetAsync(int year, SetBudgetRequest request, CancellationToken ct = default)
    {
        ValidateBudgetYear(year);
        var lines = request.Lines ?? [];
        if (lines.Count > SetBudgetRequest.MaxLines)
        {
            throw LedgerValidationException.For("lines", $"Set at most {SetBudgetRequest.MaxLines} budget lines at once.");
        }

        var tree = await GetCategoryTreeAsync(ct);
        var records = await db.Budgets.Where(b => b.Year == year).ToListAsync(ct);
        var added = 0;
        foreach (var line in lines)
        {
            var category = (line.Category ?? "").Trim();
            var sub = string.IsNullOrWhiteSpace(line.SubCategory) ? null : line.SubCategory.Trim();
            if (!BudgetCalculator.IsBudgetable(category) || !Categories.IsValid(tree, category, sub))
            {
                throw LedgerValidationException.For("category", $"{Categories.Label(category, sub)} isn't a spending category that can have a budget.");
            }

            if (line.Amount is < 0 or > BudgetTarget.MaxAmount)
            {
                throw LedgerValidationException.For("amount", $"Enter a budget for {Categories.Label(category, sub)} of zero or more.");
            }

            var record = records.FirstOrDefault(r => r.Category == category && r.SubCategory == sub);
            var amount = Math.Round(line.Amount ?? 0, 2, MidpointRounding.AwayFromZero);
            if (amount == 0)
            {
                if (record is null) continue;
                db.Budgets.Remove(record);
                records.Remove(record);
            }
            else if (record is null)
            {
                record = new BudgetRecord { Id = Guid.NewGuid(), Year = year, Category = category, SubCategory = sub, Amount = amount, Yearly = line.Yearly ?? false };
                db.Budgets.Add(record);
                records.Add(record);
                added++;
            }
            else
            {
                record.Amount = amount;
                record.Yearly = line.Yearly ?? record.Yearly;
            }
        }

        if (added > 0 && await db.Budgets.CountAsync(ct) + added > BudgetTarget.MaxCount)
        {
            db.ChangeTracker.Clear();
            throw LedgerValidationException.For("lines", $"A ledger can keep at most {BudgetTarget.MaxCount} budget lines over all its years.");
        }

        await db.SaveChangesAsync(ct);
        return (await GetBudgetTargetsAsync(ct)).Where(t => t.Year == year).ToList();
    }

    /// <summary>
    /// Each month of the years from <paramref name="fromYear"/> to <paramref name="toYear"/> up to this
    /// one, with every budget line's twelfth of its year and the month's spending in Canadian dollars.
    /// Covers the whole ledger: budgets are the household's, whatever accounts or tags the dashboard shows.
    /// </summary>
    public async Task<IReadOnlyList<BudgetMonth>> GetBudgetMonthsAsync(int fromYear, int toYear, CancellationToken ct = default)
    {
        ValidateBudgetYear(fromYear);
        ValidateBudgetYear(toYear);
        if (toYear < fromYear || toYear - fromYear >= 10)
        {
            throw LedgerValidationException.For("year", "Pick up to ten years, the first no later than the last.");
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var transactions = await InCadAsync(await db.Transactions.AsNoTracking().Between(new DateOnly(fromYear, 1, 1), new DateOnly(toYear, 12, 31)).ToListAsync(ct), null, ct);
        var targets = (await GetBudgetTargetsAsync(ct)).Where(t => t.Year >= fromYear && t.Year <= toYear);
        return BudgetCalculator.Months(fromYear, toYear, today, targets, transactions);
    }

    /// <summary>The day of the ledger's earliest transaction; null when it has none.</summary>
    private async Task<DateOnly?> FirstTransactionDateAsync(CancellationToken ct) =>
        await db.Transactions.AsNoTracking()
            .Select(t => t.TransactionTimestamp ?? t.PostedTimestamp)
            .Where(d => d != null)
            .OrderBy(d => d)
            .FirstOrDefaultAsync(ct) is { } first
            ? DateOnly.FromDateTime(first.Date)
            : null;

    /// <summary>Budget lines this ledger has none of for that year and category; ones this version would refuse are skipped.</summary>
    private async Task RestoreBudgetsAsync(LedgerBackup backup, CancellationToken ct)
    {
        var have = (await GetBudgetTargetsAsync(ct)).Select(t => (t.Year, t.Category, t.SubCategory)).ToHashSet();
        foreach (var year in (backup.Budgets ?? []).Where(t => !have.Contains((t.Year, t.Category, t.SubCategory))).GroupBy(t => t.Year))
        {
            foreach (var target in year)
            {
                try
                {
                    await SetBudgetAsync(year.Key, new SetBudgetRequest([new BudgetLineRequest(target.Category, target.SubCategory, target.Amount, target.Yearly)]), ct);
                }
                catch (LedgerValidationException)
                {
                }

                db.ChangeTracker.Clear();
            }
        }
    }

    // ---------------------------------------------------------------- household

    public const int MaxHouseholdMembers = 10;

    public async Task<IReadOnlyList<HouseholdMember>> GetHouseholdAsync(CancellationToken ct = default) =>
        await db.HouseholdMembers.AsNoTracking().OrderBy(m => m.Name).ThenBy(m => m.Id).ToListAsync(ct);

    public async Task<HouseholdMember> AddHouseholdMemberAsync(string name, CancellationToken ct = default)
    {
        var existing = await GetHouseholdAsync(ct);
        if (existing.Count >= MaxHouseholdMembers)
        {
            throw LedgerValidationException.For("name", $"A household can have at most {MaxHouseholdMembers} people.");
        }

        var member = new HouseholdMember($"hm-{Guid.NewGuid():N}"[..15], ValidateMemberName(name, existing, exceptId: null));
        db.HouseholdMembers.Add(member);
        await db.SaveChangesAsync(ct);
        return member;
    }

    public async Task<HouseholdMember?> RenameHouseholdMemberAsync(string id, string name, CancellationToken ct = default)
    {
        var clean = ValidateMemberName(name, await GetHouseholdAsync(ct), exceptId: id);
        var updated = await db.HouseholdMembers.Where(m => m.Id == id).ExecuteUpdateAsync(s => s.SetProperty(m => m.Name, clean), ct);
        return updated > 0 ? new HouseholdMember(id, clean) : null;
    }

    /// <summary>Removes a person; claims assigned to them become unassigned rather than disappearing.</summary>
    public async Task<bool> DeleteHouseholdMemberAsync(string id, CancellationToken ct = default)
    {
        if (!await db.HouseholdMembers.AnyAsync(m => m.Id == id, ct)) return false;

        await db.Transactions
            .Where(t => EF.Property<string?>(t, LedgerDbContext.TaxClaimedBy) == id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => EF.Property<string?>(t, LedgerDbContext.TaxClaimedBy), (string?)null), ct);
        await db.Transactions
            .Where(t => EF.Property<string?>(t, LedgerDbContext.EarnedBy) == id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => EF.Property<string?>(t, LedgerDbContext.EarnedBy), (string?)null), ct);
        await db.Accounts
            .Where(a => EF.Property<string?>(a, LedgerDbContext.HeldBy) == id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => EF.Property<string?>(a, LedgerDbContext.HeldBy), (string?)null), ct);
        await db.TaxSituations.Where(s => s.PersonId == id).ExecuteDeleteAsync(ct);
        await db.HouseholdMembers.Where(m => m.Id == id).ExecuteDeleteAsync(ct);
        return true;
    }

    private static string ValidateMemberName(string? name, IReadOnlyList<HouseholdMember> existing, string? exceptId)
    {
        var clean = (name ?? "").Trim();
        if (clean.Length == 0) throw LedgerValidationException.For("name", "Enter a name.");
        if (clean.Length > HouseholdMember.MaxNameLength) throw LedgerValidationException.For("name", $"Keep names under {HouseholdMember.MaxNameLength} characters.");
        if (existing.Any(m => m.Id != exceptId && string.Equals(m.Name, clean, StringComparison.OrdinalIgnoreCase)))
        {
            throw LedgerValidationException.For("name", $"{clean} is already in the household.");
        }

        return clean;
    }

    // ---------------------------------------------------------------- categories

    public async Task<IReadOnlyList<CustomCategory>> GetCustomCategoriesAsync(CancellationToken ct = default) =>
        await db.CustomCategories.AsNoTracking().OrderBy(c => c.Category).ThenBy(c => c.SubCategory).ThenBy(c => c.Id).ToListAsync(ct);

    /// <summary>The category tree with this ledger's own categories added.</summary>
    public async Task<IReadOnlyList<CategoryNode>> GetCategoryTreeAsync(CancellationToken ct = default) =>
        Categories.WithCustom(await GetCustomCategoriesAsync(ct));

    /// <summary>
    /// Adds a top-level category (counted as spending) or, with a sub-category, a detail under an
    /// existing category. Names must be new at their level, ignoring case.
    /// </summary>
    public async Task<CustomCategory> AddCustomCategoryAsync(CustomCategoryRequest request, CancellationToken ct = default)
    {
        var existing = await GetCustomCategoriesAsync(ct);
        if (existing.Count >= CustomCategory.MaxCount)
        {
            throw LedgerValidationException.For("category", $"A ledger can have at most {CustomCategory.MaxCount} categories of its own.");
        }

        var tree = Categories.WithCustom(existing);
        CustomCategory added;
        if (request.SubCategory is null)
        {
            var name = ValidateCategoryName(request.Category, "category");
            if (tree.Any(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw LedgerValidationException.For("category", $"There's already a category called {name}.");
            }

            added = new CustomCategory(NewCategoryId(), name, null);
        }
        else
        {
            if (Categories.Find(tree, request.Category) is not { } parent || parent.Name == Categories.Uncategorized)
            {
                throw LedgerValidationException.For("category", $"Unknown category '{request.Category}'.");
            }

            var name = ValidateCategoryName(request.SubCategory, "subCategory");
            if (parent.SubCategories.Any(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw LedgerValidationException.For("subCategory", $"{parent.Name} already has {name}.");
            }

            added = new CustomCategory(NewCategoryId(), parent.Name, name);
        }

        db.CustomCategories.Add(added);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return added;
    }

    /// <summary>
    /// Removes a category the user added. Transactions filed under a removed detail stay in its
    /// category; those in a removed top-level category (and its details) become Uncategorized.
    /// </summary>
    public async Task<bool> DeleteCustomCategoryAsync(string id, CancellationToken ct = default)
    {
        var category = await db.CustomCategories.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
        if (category is null) return false;

        if (category.SubCategory is { } sub)
        {
            await db.Transactions
                .Where(t => t.Category == category.Category && t.SubCategory == sub)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.SubCategory, (string?)null), ct);
            await db.CustomCategories.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
        }
        else
        {
            await db.Transactions
                .Where(t => t.Category == category.Category)
                .ExecuteUpdateAsync(s =>
                {
                    s.SetProperty(t => t.Category, Categories.Uncategorized);
                    s.SetProperty(t => t.SubCategory, (string?)null);
                }, ct);
            await db.CustomCategories.Where(c => c.Category == category.Category).ExecuteDeleteAsync(ct);
        }

        return true;
    }

    private static string NewCategoryId() => $"cc-{Guid.NewGuid():N}";

    private static string ValidateCategoryName(string? name, string field)
    {
        var clean = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length == 0) throw LedgerValidationException.For(field, "Enter a name.");
        if (clean.Length > CustomCategory.MaxNameLength) throw LedgerValidationException.For(field, $"Keep names under {CustomCategory.MaxNameLength} characters.");
        if (clean.Contains('|') || clean.Contains('›') || clean == Categories.NoSubCategory)
        {
            throw LedgerValidationException.For(field, "Names can't contain | or ›.");
        }

        return clean;
    }

    // ---------------------------------------------------------------- tags

    /// <summary>The ledger's tags: the built-in ones (<see cref="DefaultTags"/>) and its own, by name.</summary>
    public async Task<IReadOnlyList<LedgerTag>> GetTagsAsync(CancellationToken ct = default) =>
        DefaultTags.WithCustom(await GetOwnTagsAsync(ct));

    /// <summary>Only the tags this ledger added.</summary>
    private async Task<List<LedgerTag>> GetOwnTagsAsync(CancellationToken ct) =>
        await db.LedgerTags.AsNoTracking().ToListAsync(ct);

    /// <summary>Every tag with how many transactions have it.</summary>
    public async Task<IReadOnlyList<TagUsage>> GetTagUsageAsync(CancellationToken ct = default)
    {
        var tags = await GetTagsAsync(ct);
        var lists = await db.Transactions
            .Select(t => EF.Property<string?>(t, LedgerDbContext.Tags))
            .Where(t => t != null)
            .ToListAsync(ct);
        var counts = lists.SelectMany(TagList.Parse).CountBy(id => id).ToDictionary();
        return tags.Select(t => new TagUsage(t, counts.GetValueOrDefault(t.Id))).ToList();
    }

    public async Task<LedgerTag> AddTagAsync(string name, CancellationToken ct = default)
    {
        if ((await GetOwnTagsAsync(ct)).Count >= LedgerTag.MaxCount)
        {
            throw LedgerValidationException.For("name", $"A ledger can have at most {LedgerTag.MaxCount} tags of its own.");
        }

        var tag = new LedgerTag(TagList.NewId(), ValidateTagName(name, await GetTagsAsync(ct), null));
        db.LedgerTags.Add(tag);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return tag;
    }

    /// <summary>Renames one of the ledger's own tags; its transactions keep it. Returns null when it doesn't exist.</summary>
    public async Task<LedgerTag?> RenameTagAsync(string id, string name, CancellationToken ct = default)
    {
        if (DefaultTags.IsDefault(id)) throw LedgerValidationException.For("name", "Built-in tags can't be renamed.");
        if ((await GetOwnTagsAsync(ct)).All(t => t.Id != id)) return null;

        var renamed = new LedgerTag(id, ValidateTagName(name, await GetTagsAsync(ct), id));
        await db.LedgerTags.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Name, renamed.Name), ct);
        return renamed;
    }

    /// <summary>Deletes one of the ledger's own tags and takes it off every transaction (which otherwise stay as they are).</summary>
    public async Task<bool> DeleteTagAsync(string id, CancellationToken ct = default)
    {
        if (DefaultTags.IsDefault(id)) throw LedgerValidationException.For("id", "Built-in tags can't be deleted.");
        if (!await db.LedgerTags.AnyAsync(t => t.Id == id, ct)) return false;

        await RemoveTagFromAsync(db.Transactions, id, ct);
        await db.LedgerTags.Where(t => t.Id == id).ExecuteDeleteAsync(ct);
        return true;
    }

    /// <summary>Replaces one transaction's tags. Returns false when the transaction doesn't exist.</summary>
    public async Task<bool> SetTagsAsync(string accountId, string transactionId, IReadOnlyList<string>? tagIds, CancellationToken ct = default)
    {
        var ids = (tagIds ?? []).Distinct().ToList();
        if (ids.Count > LedgerTag.MaxPerTransaction)
        {
            throw LedgerValidationException.For("tagIds", $"A transaction can have at most {LedgerTag.MaxPerTransaction} tags.");
        }

        await EnsureTagsExistAsync(ids, ct);
        var stored = TagList.Format(ids);
        var updated = await db.Transactions
            .Where(t => t.AccountId == accountId && t.TransactionId == transactionId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => EF.Property<string?>(t, LedgerDbContext.Tags), stored), ct);
        return updated > 0;
    }

    /// <summary>
    /// Adds a tag to several transactions (skipping those that already have it, or already have
    /// <see cref="LedgerTag.MaxPerTransaction"/> tags), or takes it off them. Returns how many changed.
    /// </summary>
    public async Task<int> TagManyAsync(TagManyRequest request, CancellationToken ct = default)
    {
        if (request.Transactions.Count > MaxImportSize)
        {
            throw LedgerValidationException.For("transactions", $"Change at most {MaxImportSize} transactions at a time.");
        }

        await EnsureTagsExistAsync([request.TagId], ct);
        var marker = TagList.Marker(request.TagId);
        var tail = request.TagId + TagList.Separator;

        // Ids all have the same length, so the stored length says how many tags there are.
        var maxBefore = 1 + (LedgerTag.MaxPerTransaction - 1) * (TagList.MaxIdLength + 1);

        var changed = 0;
        foreach (var account in request.Transactions.GroupBy(t => t.AccountId))
        {
            foreach (var chunk in account.Select(t => t.TransactionId).Distinct().Chunk(500))
            {
                var rows = db.Transactions.Where(t => t.AccountId == account.Key && chunk.Contains(t.TransactionId));
                if (request.Remove)
                {
                    changed += await RemoveTagFromAsync(rows, request.TagId, ct);
                    continue;
                }

                changed += await rows
                    .Where(t => EF.Property<string?>(t, LedgerDbContext.Tags) == null
                        || (!EF.Property<string>(t, LedgerDbContext.Tags).Contains(marker) && EF.Property<string>(t, LedgerDbContext.Tags).Length <= maxBefore))
                    .ExecuteUpdateAsync(s => s.SetProperty(
                        t => EF.Property<string?>(t, LedgerDbContext.Tags),
                        t => EF.Property<string?>(t, LedgerDbContext.Tags) == null ? marker : EF.Property<string?>(t, LedgerDbContext.Tags) + tail), ct);
            }
        }

        return changed;
    }

    /// <summary>Takes a tag off these transactions; a list left empty becomes null. Returns how many changed.</summary>
    private static async Task<int> RemoveTagFromAsync(IQueryable<Transaction> rows, string id, CancellationToken ct)
    {
        var marker = TagList.Marker(id);
        var separator = TagList.Separator.ToString();
        var changed = await rows
            .Where(t => EF.Property<string?>(t, LedgerDbContext.Tags) != null && EF.Property<string>(t, LedgerDbContext.Tags).Contains(marker))
            .ExecuteUpdateAsync(s => s.SetProperty(
                t => EF.Property<string?>(t, LedgerDbContext.Tags),
                t => EF.Property<string>(t, LedgerDbContext.Tags).Replace(marker, separator)), ct);
        await rows
            .Where(t => EF.Property<string?>(t, LedgerDbContext.Tags) == separator)
            .ExecuteUpdateAsync(s => s.SetProperty(t => EF.Property<string?>(t, LedgerDbContext.Tags), (string?)null), ct);
        return changed;
    }

    private async Task EnsureTagsExistAsync(IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return;
        var known = (await GetTagsAsync(ct)).Select(t => t.Id).ToHashSet();
        if (!ids.All(known.Contains))
        {
            throw LedgerValidationException.For("tagIds", "That tag no longer exists.");
        }
    }

    private static string CleanTagName(string? name) =>
        string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string ValidateTagName(string? name, IReadOnlyList<LedgerTag> existing, string? exceptId)
    {
        var clean = CleanTagName(name);
        if (clean.Length == 0) throw LedgerValidationException.For("name", "Enter a name.");
        if (clean.Length > LedgerTag.MaxNameLength) throw LedgerValidationException.For("name", $"Keep tag names under {LedgerTag.MaxNameLength} characters.");
        if (existing.Any(t => t.Id != exceptId && string.Equals(t.Name, clean, StringComparison.OrdinalIgnoreCase)))
        {
            throw LedgerValidationException.For("name", $"There's already a tag called {clean}.");
        }

        return clean;
    }

    // ---------------------------------------------------------------- receipts

    public const int MaxReceiptBytes = 5 * 1024 * 1024;
    public const int MaxReceiptsPerTransaction = 10;

    public static IReadOnlyList<string> ReceiptContentTypes { get; } =
        ["application/pdf", "image/jpeg", "image/png", "image/webp", "image/heic", "image/heif"];

    private static readonly System.Linq.Expressions.Expression<Func<Receipt, ReceiptInfo>> ReceiptInfoProjection =
        r => new ReceiptInfo(r.Id, r.AccountId, r.TransactionId, r.FileName, r.ContentType, r.Size, r.UploadedAt);

    /// <summary>Attaches a receipt to a transaction; null when the transaction doesn't exist.</summary>
    public async Task<ReceiptInfo?> AddReceiptAsync(string accountId, string transactionId, UploadReceiptRequest request, CancellationToken ct = default)
    {
        var contentType = ValidateReceipt(request.ContentType, request.Content);
        if (!await db.Transactions.AnyAsync(t => t.AccountId == accountId && t.TransactionId == transactionId, ct)) return null;

        if (await db.Receipts.CountAsync(r => r.AccountId == accountId && r.TransactionId == transactionId, ct) >= MaxReceiptsPerTransaction)
        {
            throw LedgerValidationException.For("receipt", $"A transaction can have at most {MaxReceiptsPerTransaction} receipts.");
        }

        var receipt = new Receipt
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TransactionId = transactionId,
            FileName = CleanFileName(request.FileName),
            ContentType = contentType,
            Size = request.Content.Length,
            // Whole milliseconds, which is what the browser database keeps.
            UploadedAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            Content = request.Content,
        };
        db.Receipts.Add(receipt);
        await db.SaveChangesAsync(ct);
        return new ReceiptInfo(receipt.Id, accountId, transactionId, receipt.FileName, contentType, receipt.Size, receipt.UploadedAt);
    }

    public async Task<IReadOnlyList<ReceiptInfo>> GetReceiptsAsync(string accountId, string transactionId, CancellationToken ct = default) =>
        await db.Receipts.AsNoTracking()
            .Where(r => r.AccountId == accountId && r.TransactionId == transactionId)
            .OrderBy(r => r.UploadedAt)
            .Select(ReceiptInfoProjection)
            .ToListAsync(ct);

    public Task<ReceiptFile?> GetReceiptAsync(Guid id, CancellationToken ct = default) =>
        db.Receipts.AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => new ReceiptFile(new ReceiptInfo(r.Id, r.AccountId, r.TransactionId, r.FileName, r.ContentType, r.Size, r.UploadedAt), r.Content))
            .SingleOrDefaultAsync(ct);

    public async Task<bool> DeleteReceiptAsync(Guid id, CancellationToken ct = default) =>
        await db.Receipts.Where(r => r.Id == id).ExecuteDeleteAsync(ct) > 0;

    /// <summary>
    /// Spending that a receipt may belong to, best first: around the receipt's date when it is
    /// known, otherwise in the last four months.
    /// </summary>
    public async Task<IReadOnlyList<ReceiptMatch>> FindReceiptMatchesAsync(ReceiptFields receipt, DateOnly? today = null, CancellationToken ct = default)
    {
        var dates = new[] { receipt.Date, receipt.OtherDate }.OfType<DateOnly>().ToList();
        var (from, to) = dates.Count > 0
            ? (dates.Min().AddDays(-1), dates.Max().AddDays(Core.Receipts.ReceiptMatcher.MaxDaysLater))
            : ((today ?? DateOnly.FromDateTime(DateTime.UtcNow)).AddMonths(-4), today ?? DateOnly.FromDateTime(DateTime.UtcNow));

        var candidates = await db.Transactions
            .Where(t => t.DebitCreditMemo == DebitCreditMemo.Debit)
            .Between(from, to)
            .NewestFirst()
            .Take(MaxTransactionsPerQuery)
            .ToLedgerTransactionsAsync(ct);
        return Core.Receipts.ReceiptMatcher.Rank(receipt, candidates);
    }

    /// <summary>
    /// Checks a receipt file (type, size, and that its first bytes match its type) and returns its
    /// normalized content type; throws <see cref="LedgerValidationException"/> otherwise.
    /// </summary>
    public static string CheckReceiptFile(string? contentType, byte[]? content) => ValidateReceipt(contentType, content);

    /// <summary>Accepts PDFs and common photo formats, checking the file starts the way its type says it does.</summary>
    private static string ValidateReceipt(string? contentType, byte[]? content)
    {
        var type = contentType?.Trim().ToLowerInvariant() switch
        {
            "image/jpg" or "image/pjpeg" => "image/jpeg",
            var t => t ?? "",
        };

        if (!ReceiptContentTypes.Contains(type))
        {
            throw LedgerValidationException.For("receipt", "Attach a PDF or a photo (JPEG, PNG, WebP or HEIC).");
        }

        if (content is not { Length: > 0 })
        {
            throw LedgerValidationException.For("receipt", "That file is empty.");
        }

        if (content.Length > MaxReceiptBytes)
        {
            throw LedgerValidationException.For("receipt", $"Receipts can be at most {MaxReceiptBytes / (1024 * 1024)} MB.");
        }

        var bytes = content.AsSpan();
        var matches = type switch
        {
            "application/pdf" => bytes.StartsWith("%PDF-"u8),
            "image/jpeg" => bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]),
            "image/png" => bytes.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
            "image/webp" => bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8),
            _ => bytes.Length >= 12 && bytes.Slice(4, 4).SequenceEqual("ftyp"u8), // HEIC/HEIF
        };

        if (!matches)
        {
            throw LedgerValidationException.For("receipt", "That file isn't a valid " + (type == "application/pdf" ? "PDF." : "image."));
        }

        return type;
    }

    /// <summary>Just the file's own name: no folders, no control characters, and not too long.</summary>
    private static string CleanFileName(string? fileName)
    {
        var name = (fileName ?? "").Replace('\\', '/');
        name = new string(name[(name.LastIndexOf('/') + 1)..].Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length == 0) name = "receipt";
        if (name.Length <= Receipt.MaxFileNameLength) return name;

        var extension = Path.GetExtension(name);
        if (extension.Length > 16) extension = "";
        return name[..(Receipt.MaxFileNameLength - extension.Length)] + extension;
    }

    // ---------------------------------------------------------------- tax situations and estimates

    private static readonly JsonSerializerOptions SituationJson = new(JsonSerializerDefaults.General)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Reads a stored situation; older, detailed ones keep only their province.</summary>
    private static TaxSituation ReadSituation(string data)
    {
        try
        {
            return JsonSerializer.Deserialize<TaxSituation>(data, SituationJson) ?? new TaxSituation();
        }
        catch (JsonException)
        {
            return new TaxSituation();
        }
    }

    /// <summary>
    /// The people a year's estimate is for: each household member, or <see cref="TaxSituations.Self"/>
    /// (named "You") when there's no household.
    /// </summary>
    private async Task<IReadOnlyList<(string Id, string Name)>> TaxPeopleAsync(CancellationToken ct)
    {
        var household = await GetHouseholdAsync(ct);
        return household.Count == 0 ? [(TaxSituations.Self, "You")] : household.Select(m => (m.Id, m.Name)).ToList();
    }

    /// <summary>
    /// A person's situation for <paramref name="year"/>: what they saved for it, else the latest
    /// earlier one, else the defaults.
    /// </summary>
    private async Task<(TaxSituation Situation, bool Saved)> GetTaxSituationAsync(int year, string personId, CancellationToken ct)
    {
        var records = await db.TaxSituations.AsNoTracking()
            .Where(s => s.PersonId == personId && s.Year <= year)
            .OrderByDescending(s => s.Year)
            .Take(1)
            .ToListAsync(ct);
        if (records.FirstOrDefault() is not { } record) return (new TaxSituation(), false);
        var situation = ReadSituation(record.Data);
        // A notice of assessment's deduction limit is for one year only.
        return record.Year == year ? (situation, true) : (situation with { RrspDeductionLimit = null }, false);
    }

    /// <summary>Saves one person's situation for a year, replacing what was there.</summary>
    public async Task<TaxSituationEntry> SaveTaxSituationAsync(int year, string personId, TaxSituation situation, CancellationToken ct = default)
    {
        if (!(await TaxPeopleAsync(ct)).Any(p => p.Id == personId))
        {
            throw LedgerValidationException.For("personId", "That person isn't in the household.");
        }

        var clean = ValidateSituation(year, situation);
        var data = JsonSerializer.Serialize(clean, SituationJson);
        var record = await db.TaxSituations.SingleOrDefaultAsync(s => s.Year == year && s.PersonId == personId, ct);
        if (record is null)
        {
            db.TaxSituations.Add(new TaxSituationRecord { Id = NewSituationId(), Year = year, PersonId = personId, Data = data });
        }
        else
        {
            record.Data = data;
        }

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return new TaxSituationEntry(year, personId, clean);
    }

    private static string NewSituationId() => $"ts-{Guid.NewGuid():N}"[..15];

    private static TaxSituation ValidateSituation(int year, TaxSituation situation)
    {
        if (year is < 2000 or > 2100) throw LedgerValidationException.For("year", "Choose a tax year from 2000 to 2100.");

        var province = string.IsNullOrWhiteSpace(situation.Province) ? null : situation.Province.Trim().ToUpperInvariant();
        if (province is not null && !TaxRates.IsProvince(province))
        {
            throw LedgerValidationException.For("province", province == "QC"
                ? "Quebec residents file a separate provincial return, which LoonieBooks doesn't estimate."
                : "Choose a province or territory.");
        }

        static decimal? Percent(decimal? value, decimal max, string field, string label)
        {
            if (value is null or 0) return null;
            if (value < 0 || value > max) throw LedgerValidationException.For(field, $"{label} must be from 0 to {max}%.");
            return Math.Round(value.Value, 2, MidpointRounding.AwayFromZero);
        }

        var espp = Percent(situation.EsppPercent, 50, "esppPercent", "The ESPP contribution");
        var rrsp = Percent(situation.RrspPercent, 50, "rrspPercent", "The group RRSP contribution");
        var pension = Percent(situation.PensionPercent, 50, "pensionPercent", "The pension contribution");
        if ((espp ?? 0) + (rrsp ?? 0) + (pension ?? 0) > 50)
        {
            throw LedgerValidationException.For("pensionPercent", "ESPP, group RRSP and pension contributions together can be at most 50% of salary.");
        }

        var limit = situation.RrspDeductionLimit;
        if (limit is < 0 or > 10_000_000m) throw LedgerValidationException.For("rrspDeductionLimit", "Enter an RRSP deduction limit from $0 to $10,000,000.");
        var adjustment = situation.PensionAdjustment;
        if (adjustment is < 0 or > 1_000_000m) throw LedgerValidationException.For("pensionAdjustment", "Enter a pension adjustment from $0 to $1,000,000.");
        // Unlike the payroll plans, a 0% share is a real answer: someone who owns none of the property.
        var rentalShare = situation.RentalSharePercent;
        if (rentalShare is < 0 or > 100) throw LedgerValidationException.For("rentalSharePercent", "The share of rental property must be from 0 to 100%.");

        return situation with
        {
            Province = province,
            EsppPercent = espp,
            EsppMatchPercent = Percent(situation.EsppMatchPercent, 100, "esppMatchPercent", "The employer's ESPP match"),
            RrspPercent = rrsp,
            RrspMatchPercent = Percent(situation.RrspMatchPercent, 50, "rrspMatchPercent", "The employer's RRSP match"),
            RrspDeductionLimit = limit is { } l ? Math.Round(l, 2, MidpointRounding.AwayFromZero) : null,
            PensionPercent = pension,
            PensionMatchPercent = Percent(situation.PensionMatchPercent, 50, "pensionMatchPercent", "The employer's pension contribution"),
            PensionAdjustment = adjustment is { } a ? Math.Round(a, 2, MidpointRounding.AwayFromZero) : null,
            RentalSharePercent = rentalShare is { } r ? Math.Round(r, 2, MidpointRounding.AwayFromZero) : null,
        };
    }

    /// <summary>Whose each account is: account id to household member id. Shared accounts aren't listed.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetAccountHoldersAsync(CancellationToken ct = default) =>
        (await db.Accounts.AsNoTracking()
            .Select(a => new { a.AccountId, HeldBy = EF.Property<string?>(a, LedgerDbContext.HeldBy) })
            .Where(a => a.HeldBy != null)
            .ToListAsync(ct))
        .ToDictionary(a => a.AccountId, a => a.HeldBy!);

    /// <summary>Sets whose an account is (a household member id), or makes it shared (null). False when there's no such account.</summary>
    public async Task<bool> SetAccountHolderAsync(string accountId, string? personId, CancellationToken ct = default)
    {
        personId = await ValidateMemberIdAsync(personId, ct);
        return await db.Accounts
            .Where(a => a.AccountId == accountId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => EF.Property<string?>(a, LedgerDbContext.HeldBy), personId), ct) > 0;
    }

    /// <summary>
    /// Sets whose income a deposit is (a household member id, or <see cref="TaxSituations.Split"/>
    /// for rent split between co-owners), or clears it (null). False when there's no such transaction.
    /// </summary>
    public async Task<bool> SetEarnedByAsync(string accountId, string transactionId, string? personId, CancellationToken ct = default)
    {
        if (personId == TaxSituations.Split)
        {
            var deposit = await db.Transactions
                .Where(t => t.AccountId == accountId && t.TransactionId == transactionId)
                .Select(t => new { t.SubCategory })
                .SingleOrDefaultAsync(ct);
            if (deposit is null) return false;
            if (!await db.HouseholdMembers.AnyAsync(ct))
            {
                throw LedgerValidationException.For("personId", "Add the people in your household before splitting rent between them.");
            }

            if (deposit.SubCategory != Categories.RentalIncome)
            {
                throw LedgerValidationException.For("personId", "Only Rental Income deposits can be split between co-owners.");
            }
        }
        else
        {
            personId = await ValidateMemberIdAsync(personId, ct);
        }

        return await db.Transactions
            .Where(t => t.AccountId == accountId && t.TransactionId == transactionId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => EF.Property<string?>(t, LedgerDbContext.EarnedBy), personId), ct) > 0;
    }

    private async Task<string?> ValidateMemberIdAsync(string? personId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(personId)) return null;
        if (!await db.HouseholdMembers.AnyAsync(m => m.Id == personId, ct))
        {
            throw LedgerValidationException.For("personId", "That person isn't in the household.");
        }

        return personId;
    }

    /// <summary>The income sub-categories the estimate counts.</summary>
    private static readonly string[] CountedIncome = [Categories.Salary, Categories.BusinessIncome, Categories.RentalIncome];

    /// <summary>
    /// Everyone's tax estimate for <paramref name="year"/>, from the income deposits in their
    /// accounts (or assigned to them) and the claims assigned to them. Without a household, the
    /// ledger's keeper gets all of it. <paramref name="today"/> projects the year in progress.
    /// </summary>
    public async Task<TaxEstimateSet> GetTaxEstimatesAsync(int year, DateOnly? today = null, CancellationToken ct = default)
    {
        var (from, to) = TaxReportBuilder.ReportingRange(year);
        var claimed = await db.Transactions
            .Where(t => EF.Property<string?>(t, LedgerDbContext.TaxCode) != null)
            .Between(from, to)
            .ToLedgerTransactionsAsync(ct);

        var yearEnd = new DateOnly(year, 12, 31);
        var asOf = today ?? DateOnly.FromDateTime(DateTime.Now);
        var receivedTo = asOf < yearEnd ? asOf : yearEnd;
        var income = asOf.Year < year
            ? []
            : await db.Transactions
                .Where(t => t.Category == Categories.Income)
                .Between(new DateOnly(year, 1, 1), receivedTo)
                .ToLedgerTransactionsAsync(ct);
        var factor = asOf.Year == year && asOf < yearEnd
            ? Math.Round((decimal)(DateTime.IsLeapYear(year) ? 366 : 365) / asOf.DayOfYear, 4)
            : 1m;

        var people = await TaxPeopleAsync(ct);
        var household = people[0].Id != TaxSituations.Self;
        var holders = household ? await GetAccountHoldersAsync(ct) : new Dictionary<string, string>();
        string? EarnerOf(LedgerTransaction t) => household ? t.EarnedBy ?? holders.GetValueOrDefault(t.Transaction.AccountId) : TaxSituations.Self;

        var situations = new List<(string Id, string Name, TaxSituation Situation, bool Saved)>();
        foreach (var (id, name) in people)
        {
            var (situation, saved) = await GetTaxSituationAsync(year, id, ct);
            situations.Add((id, name, situation, saved));
        }

        IReadOnlyList<TaxCategorySummary> unassigned = household
            ? TaxReportBuilder.Build(year, claimed, TaxReportBuilder.Unassigned).Categories
            : [];

        // Rent split between co-owners goes to each by their share, and so do the rental expenses no one claimed.
        var splitting = household && income.Any(t => t.EarnedBy == TaxSituations.Split);
        var splitRental = splitting
            ? income.Where(t => t.EarnedBy == TaxSituations.Split && t.Transaction.SubCategory == Categories.RentalIncome).Sum(t => t.Transaction.SignedAmount)
            : 0;
        var shares = splitting
            ? TaxSituations.RentalShares(situations.Select(p => (p.Id, p.Situation.RentalSharePercent)))
            : new Dictionary<string, decimal>();
        var splitExpenses = splitting
            ? unassigned.Where(c => c.Category.Code == TaxCategories.Rental).Sum(c => c.Confirmed)
            : 0;
        static decimal Part(decimal amount, decimal share) => Math.Round(amount * share, 2, MidpointRounding.AwayFromZero);

        IncomeReceived IncomeOf(string personId)
        {
            var mine = income.Where(t => EarnerOf(t) == personId).ToList();
            decimal Sum(params string?[] subCategories) => mine.Where(t => subCategories.Contains(t.Transaction.SubCategory)).Sum(t => t.Transaction.SignedAmount);
            var other = mine.Where(t => !CountedIncome.Contains(t.Transaction.SubCategory)).Sum(t => t.Transaction.SignedAmount);
            decimal? share = splitting ? shares.GetValueOrDefault(personId) : null;
            return new IncomeReceived(
                Sum(Categories.Salary),
                Sum(Categories.BusinessIncome),
                Sum(Categories.RentalIncome) + Part(splitRental, share ?? 0),
                other,
                receivedTo,
                factor,
                splitRental,
                share);
        }

        var estimates = new List<PersonTaxEstimate>();
        foreach (var (id, name, situation, saved) in situations)
        {
            var claims = TaxClaims.From(TaxReportBuilder.Build(year, claimed, household ? id : null));
            if (splitExpenses != 0)
            {
                var confirmed = new Dictionary<string, decimal>(claims.Confirmed, StringComparer.OrdinalIgnoreCase);
                confirmed[TaxCategories.Rental] = claims[TaxCategories.Rental] + Part(splitExpenses, shares.GetValueOrDefault(id));
                claims = claims with { Confirmed = confirmed };
            }

            var received = IncomeOf(id);
            estimates.Add(new PersonTaxEstimate(id, name, situation, saved, TaxEstimator.EstimateFromIncome(year, situation, received, claims), received, claims.Confirmed));
        }

        if (splitting) unassigned = unassigned.Where(c => c.Category.Code != TaxCategories.Rental).ToList();
        var shared = household
            ? income
                .Where(t => CountedIncome.Contains(t.Transaction.SubCategory) && !holders.ContainsKey(t.Transaction.AccountId))
                .OrderByDescending(t => t.Transaction.EffectiveDate)
                .Take(TaxEstimateSet.MaxSharedIncome)
                .ToList()
            : [];
        return new TaxEstimateSet(
            year,
            TaxRates.For(year) is not null,
            estimates,
            unassigned.Sum(c => c.Transactions.Count(t => t.Tax?.Status == TaxTagStatus.Confirmed)),
            unassigned.Sum(c => c.Confirmed),
            shared);
    }

    // ---------------------------------------------------------------- backup

    public async Task<LedgerBackup> ExportAsync(CancellationToken ct = default) => new(
        LedgerBackup.CurrentVersion,
        DateTimeOffset.UtcNow,
        await db.Accounts.AsNoTracking().OrderBy(a => a.AccountId).ToListAsync(ct),
        await db.Transactions.OrderBy(t => t.AccountId).ThenBy(t => t.TransactionId).ToLedgerTransactionsAsync(ct),
        await db.Receipts.AsNoTracking()
            .OrderBy(r => r.AccountId).ThenBy(r => r.TransactionId).ThenBy(r => r.UploadedAt)
            .Select(r => new ReceiptFile(new ReceiptInfo(r.Id, r.AccountId, r.TransactionId, r.FileName, r.ContentType, r.Size, r.UploadedAt), r.Content))
            .ToListAsync(ct),
        await GetHouseholdAsync(ct),
        await GetCustomCategoriesAsync(ct),
        await GetTagsAsync(ct),
        (await db.TaxSituations.AsNoTracking().OrderBy(s => s.Year).ThenBy(s => s.PersonId).ToListAsync(ct))
            .Select(s => new TaxSituationEntry(s.Year, s.PersonId, ReadSituation(s.Data)))
            .ToList(),
        await GetAccountHoldersAsync(ct),
        await GetHoldingsAsync(ct: ct),
        await GetExchangeRatesAsync(ct),
        await GetPropertiesAsync(ct),
        await GetHpiReadingsAsync(ct),
        await GetBudgetTargetsAsync(ct));

    /// <summary>
    /// Merges a backup into this ledger. Accounts already present are kept as they are, and
    /// transactions go through the normal duplicate-skipping import, so restoring the same backup
    /// twice (or uploading it in several chunks) adds nothing new.
    /// </summary>
    public async Task<RestoreResult> RestoreAsync(LedgerBackup backup, CancellationToken ct = default)
    {
        if (backup.Version is < 1 or > LedgerBackup.CurrentVersion)
        {
            throw LedgerValidationException.For("version", $"This backup is version {backup.Version}; this version of LoonieBooks reads up to {LedgerBackup.CurrentVersion}.");
        }

        var accountIds = new Dictionary<string, string>();
        var accountsAdded = 0;
        foreach (var account in backup.Accounts.DistinctBy(a => a.AccountId))
        {
            var id = await ResolveRestoredAccountIdAsync(account.AccountId, ct);
            accountIds[account.AccountId] = id;
            if (await db.Accounts.AnyAsync(a => a.AccountId == id, ct)) continue;

            var restored = account with { AccountId = id };
            db.Accounts.Add(restored);
            db.Entry(restored).Property(LedgerDbContext.CreatedAt).CurrentValue = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            accountsAdded++;
        }

        // People first, so claims assigned to them keep their assignment.
        var memberIds = new Dictionary<string, string>();
        foreach (var member in backup.Household?.DistinctBy(m => m.Id) ?? [])
        {
            var id = await ResolveRestoredMemberIdAsync(member.Id, ct);
            memberIds[member.Id] = id;
            if (await db.HouseholdMembers.AnyAsync(m => m.Id == id, ct)) continue;

            var name = (member.Name ?? "").Trim();
            db.HouseholdMembers.Add(new HouseholdMember(id, name.Length is > 0 and <= HouseholdMember.MaxNameLength ? name : "Household member"));
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        // Whose each account is, for accounts this restore added (accounts already here keep theirs).
        foreach (var (backupAccountId, backupMemberId) in backup.AccountHolders ?? new Dictionary<string, string>())
        {
            if (!accountIds.TryGetValue(backupAccountId, out var accountId) || !memberIds.TryGetValue(backupMemberId, out var memberId)) continue;
            await db.Accounts
                .Where(a => a.AccountId == accountId && EF.Property<string?>(a, LedgerDbContext.HeldBy) == null)
                .ExecuteUpdateAsync(s => s.SetProperty(a => EF.Property<string?>(a, LedgerDbContext.HeldBy), memberId), ct);
        }

        // Each person's situation, unless this ledger already has that person's year. Invalid ones are skipped.
        foreach (var entry in backup.TaxSituations ?? [])
        {
            var personId = entry.PersonId == TaxSituations.Self ? TaxSituations.Self : memberIds.GetValueOrDefault(entry.PersonId ?? "");
            if (personId is null || entry.Situation is null) continue;
            if (await db.TaxSituations.AnyAsync(s => s.Year == entry.Year && s.PersonId == personId, ct)) continue;

            TaxSituation clean;
            try
            {
                clean = ValidateSituation(entry.Year, entry.Situation);
            }
            catch (LedgerValidationException)
            {
                continue;
            }

            db.TaxSituations.Add(new TaxSituationRecord { Id = NewSituationId(), Year = entry.Year, PersonId = personId, Data = JsonSerializer.Serialize(clean, SituationJson) });
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        // Categories before transactions, so transactions filed under them keep their category.
        // New ids: a category is identified by its names, and ids only need to be unique.
        var tree = await GetCategoryTreeAsync(ct);
        var categoryCount = (await GetCustomCategoriesAsync(ct)).Count;
        foreach (var custom in (backup.Categories ?? []).OrderBy(c => c.SubCategory is null ? 0 : 1))
        {
            if (categoryCount >= CustomCategory.MaxCount) break;
            var name = custom.SubCategory ?? custom.Category;
            if (string.IsNullOrWhiteSpace(name) || name.Length > CustomCategory.MaxNameLength) continue;
            var skip = custom.SubCategory is null
                ? Categories.Find(tree, custom.Category) is not null
                : Categories.Find(tree, custom.Category) is not { } parent || parent.SubCategories.Contains(custom.SubCategory);
            if (skip) continue;

            db.CustomCategories.Add(new CustomCategory(NewCategoryId(), custom.Category, custom.SubCategory));
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            tree = await GetCategoryTreeAsync(ct);
            categoryCount++;
        }

        // Tags too, matched by name (built-in ones included): restoring into a ledger that already
        // has "Rental" puts the backup's Rental transactions under it. Transactions refer to tags
        // by id, so map them.
        var tagIds = new Dictionary<string, string>();
        var ledgerTags = (await GetTagsAsync(ct)).ToList();
        var ownTagCount = (await GetOwnTagsAsync(ct)).Count;
        foreach (var tag in backup.Tags?.DistinctBy(t => t.Id) ?? [])
        {
            var name = CleanTagName(tag.Name);
            if (name.Length is 0 or > LedgerTag.MaxNameLength) continue;
            if (ledgerTags.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } match)
            {
                if (ownTagCount >= LedgerTag.MaxCount) continue;
                match = new LedgerTag(TagList.NewId(), name);
                db.LedgerTags.Add(match);
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                ledgerTags.Add(match);
                ownTagCount++;
            }

            tagIds[tag.Id] = match.Id;
        }

        int added = 0, duplicates = 0;
        foreach (var group in backup.Transactions.GroupBy(t => t.Transaction.AccountId))
        {
            if (!accountIds.TryGetValue(group.Key, out var accountId))
            {
                throw LedgerValidationException.For("transactions", $"The backup has transactions for account '{group.Key}' but not the account itself.");
            }

            foreach (var original in group.Chunk(MaxImportSize))
            {
                var chunk = original
                    .Select(t => t.Tax?.ClaimedBy is { } member ? t with { Tax = t.Tax with { ClaimedBy = memberIds.GetValueOrDefault(member) } } : t)
                    .Select(t => t.EarnedBy is { } earner and not TaxSituations.Split ? t with { EarnedBy = memberIds.GetValueOrDefault(earner) } : t)
                    .Select(t => t.Tags is { Count: > 0 } tags ? t with { Tags = [.. tags.Where(tagIds.ContainsKey).Select(id => tagIds[id])] } : t)
                    .ToList();
                var result = await ImportAsync(accountId, new ImportTransactionsRequest(chunk, Source: "backup"), ct);
                added += result!.Added;
                duplicates += result.Duplicates;
                db.ChangeTracker.Clear();
            }
        }

        var receiptsAdded = 0;
        foreach (var file in backup.Receipts ?? [])
        {
            if (!accountIds.TryGetValue(file.Info.AccountId, out var accountId))
            {
                throw LedgerValidationException.For("receipts", $"The backup has receipts for account '{file.Info.AccountId}' but not the account itself.");
            }

            var id = await ResolveRestoredReceiptIdAsync(file.Info.Id, ct);
            if (await db.Receipts.AnyAsync(r => r.Id == id, ct)) continue;

            if (!await db.Transactions.AnyAsync(t => t.AccountId == accountId && t.TransactionId == file.Info.TransactionId, ct))
            {
                throw LedgerValidationException.For("receipts", "The backup has a receipt for a transaction it doesn't include.");
            }

            db.Receipts.Add(new Receipt
            {
                Id = id,
                AccountId = accountId,
                TransactionId = file.Info.TransactionId,
                FileName = CleanFileName(file.Info.FileName),
                ContentType = ValidateReceipt(file.Info.ContentType, file.Content),
                Size = file.Content.Length,
                UploadedAt = file.Info.UploadedAt,
                Content = file.Content,
            });
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            receiptsAdded++;
        }

        // Exchange rates for currencies this ledger has none for, before the holdings valued at them.
        var haveRates = (await GetExchangeRatesAsync(ct)).Select(r => r.Currency).ToHashSet();
        foreach (var rate in (backup.ExchangeRates ?? []).Where(r => !haveRates.Contains(r.Currency)).DistinctBy(r => r.Currency))
        {
            try
            {
                await SetExchangeRateAsync(rate, ct);
            }
            catch (LedgerValidationException)
            {
                // A rate this version would refuse.
            }

            db.ChangeTracker.Clear();
        }

        // Holdings snapshots, for accounts that have none yet. Ones this version would refuse are skipped.
        foreach (var snapshot in backup.Holdings ?? [])
        {
            if (!accountIds.TryGetValue(snapshot.AccountId, out var accountId)) continue;
            if (await db.Holdings.AnyAsync(h => h.AccountId == accountId, ct)) continue;

            try
            {
                await ImportHoldingsAsync(accountId, new ImportHoldingsRequest(snapshot.AsOf, snapshot.Holdings, SetBalance: false), ct);
            }
            catch (LedgerValidationException)
            {
                // Not an investment account any more, or a snapshot from a newer version.
            }

            db.ChangeTracker.Clear();
        }

        await RestoreRealEstateAsync(backup, accountIds, ct);
        await RestoreBudgetsAsync(backup, ct);
        return new RestoreResult(accountsAdded, added, duplicates, receiptsAdded);
    }

    /// <summary>
    /// Properties this ledger has none of by that name, secured by the restored accounts that aren't
    /// secured by anything yet; then index readings for months it has none for. Ones this version
    /// would refuse are skipped.
    /// </summary>
    private async Task RestoreRealEstateAsync(LedgerBackup backup, Dictionary<string, string> accountIds, CancellationToken ct)
    {
        var have = await GetPropertiesAsync(ct);
        var secured = have.SelectMany(p => p.AccountIds).ToHashSet();
        foreach (var property in backup.Properties ?? [])
        {
            if (have.Any(p => string.Equals(p.Name, PropertyValuation.CleanText(property.Name), StringComparison.OrdinalIgnoreCase))) continue;
            var loans = (property.AccountIds ?? []).Select(id => accountIds.GetValueOrDefault(id)).OfType<string>().Where(id => !secured.Contains(id)).ToList();
            try
            {
                await CreatePropertyAsync(new PropertyRequest(property.Name, property.City, property.Value, property.ValueAsOf, loans), ct);
            }
            catch (LedgerValidationException)
            {
            }

            db.ChangeTracker.Clear();
        }

        await RestoreHpiReadingsAsync(backup.HousePriceIndex ?? [], ct);
    }

    /// <summary>
    /// A backup's index readings, the months this ledger hasn't got for their area, added in one go:
    /// with the feed's index a ledger has hundreds per area. Readings that can't be right are skipped.
    /// </summary>
    private async Task RestoreHpiReadingsAsync(IReadOnlyList<HpiReading> readings, CancellationToken ct)
    {
        if (readings.Count == 0) return;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var existing = await GetHpiReadingsAsync(ct);
        var areas = existing.Select(r => r.Area).Distinct().ToList();
        var have = existing.Select(r => (Key: Key(r.Area), r.Month)).ToHashSet();
        var count = existing.Count;

        foreach (var reading in readings)
        {
            var area = PropertyValuation.CleanText(reading.Area);
            var month = new DateOnly(reading.Month.Year, reading.Month.Month, 1);
            if (area.Length is 0 or > HpiReading.MaxAreaLength || reading.Index is <= 0 or > HpiReading.MaxIndex || month.Year < 1900 || month > today) continue;
            if (count >= MaxHpiReadings || !have.Add((Key(area), month))) continue;

            // An area already in use keeps its spelling.
            var name = areas.FirstOrDefault(a => HpiReading.SameArea(a, area));
            if (name is null) areas.Add(name = area);
            db.HousePriceIndex.Add(new HpiRecord { Id = Guid.NewGuid(), Area = name, Month = new DateTimeOffset(month.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), Index = Math.Round(reading.Index, 4) });
            count++;
        }

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        static string Key(string area) => PropertyValuation.CleanText(area).ToUpperInvariant();
    }

    /// <summary>Like account ids: keep a member's id unless another owner already holds it.</summary>
    private async Task<string> ResolveRestoredMemberIdAsync(string id, CancellationToken ct)
    {
        if (await db.HouseholdMembers.AnyAsync(m => m.Id == id, ct)) return id;
        if (id.Length is > 0 and <= 64 && !await db.HouseholdMembers.IgnoreQueryFilters().AnyAsync(m => m.Id == id, ct)) return id;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{db.OwnerId}|member|{id}"));
        return "hm-" + Convert.ToHexStringLower(hash)[..12];
    }

    /// <summary>Like account ids: keep a receipt's id unless another owner already holds it.</summary>
    private async Task<Guid> ResolveRestoredReceiptIdAsync(Guid id, CancellationToken ct)
    {
        if (await db.Receipts.AnyAsync(r => r.Id == id, ct)) return id;
        if (!await db.Receipts.IgnoreQueryFilters().AnyAsync(r => r.Id == id, ct)) return id;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{db.OwnerId}|receipt|{id}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>
    /// Keeps a backup's account id when it is free or already ours. If another owner holds it, a
    /// replacement is derived from the owner and the original id, so repeated uploads map to the
    /// same account instead of creating copies.
    /// </summary>
    private async Task<string> ResolveRestoredAccountIdAsync(string accountId, CancellationToken ct)
    {
        if (await db.Accounts.AnyAsync(a => a.AccountId == accountId, ct)) return accountId;
        if (accountId.Length <= 64 && !await db.Accounts.IgnoreQueryFilters().AnyAsync(a => a.AccountId == accountId, ct)) return accountId;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{db.OwnerId}|{accountId}"));
        return "acct-" + Convert.ToHexStringLower(hash)[..12];
    }

    // ---------------------------------------------------------------- helpers

    private static string NewAccountId() => $"acct-{Guid.NewGuid():N}"[..17];

    private static void Validate(AccountRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Nickname)) errors["nickname"] = ["Give the account a name."];
        if (request.AccountNumberLast4 is { Length: > 0 } last4 && (last4.Length > 4 || !last4.All(char.IsAsciiDigit)))
        {
            errors["accountNumberLast4"] = ["Enter at most the last 4 digits. Never store full account numbers."];
        }

        if (errors.Count > 0) throw new LedgerValidationException(errors);
    }

    private static bool IsCurrencyCode(string code) => code.Length == 3 && code.All(char.IsAsciiLetterUpper);

    private static string CurrencyCode(string code)
    {
        var clean = (code ?? "").Trim().ToUpperInvariant();
        return IsCurrencyCode(clean) ? clean : throw LedgerValidationException.For("currencyCode", "Enter a currency code such as CAD or USD.");
    }

    private static Account ToAccount(string accountId, AccountRequest r) => new()
    {
        AccountId = accountId,
        AccountCategory = r.AccountCategory,
        AccountType = r.AccountType,
        Nickname = r.Nickname.Trim(),
        InstitutionName = r.InstitutionName?.Trim(),
        AccountNumberDisplay = string.IsNullOrEmpty(r.AccountNumberLast4) ? null : "****" + r.AccountNumberLast4,
        CurrentBalance = r.CurrentBalance,
        BalanceAsOf = r.CurrentBalance is null ? null : r.BalanceAsOf ?? DateTimeOffset.UtcNow,
        CreditLine = r.CreditLine,
        AvailableCredit = r.CreditLine is { } line && r.CurrentBalance is { } owed ? line - owed : null,
    };
}
