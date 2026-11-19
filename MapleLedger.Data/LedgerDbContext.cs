using MapleLedger.Core;
using MapleLedger.Core.Tax;
using MapleLedger.Fdx;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MapleLedger.Data;

/// <summary>
/// Stores the FDX <see cref="Account"/> and <see cref="Transaction"/> records directly, so data
/// that later arrives from an FDX API needs no translation. LoonieBooks' own annotations (owner,
/// tax tags, manual categorization) are shadow properties rather than fields on the FDX types.
///
/// The same model runs on SQL Server (premium users, on the server) and on SQLite in the browser
/// (everyone else). Every ledger row belongs to <see cref="OwnerId"/>: queries only ever see that
/// owner's rows, and new rows are stamped with it on save.
/// </summary>
public sealed class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
{
    public const string Owner = "OwnerId";
    public const string TaxCode = nameof(TaxCode);
    public const string TaxStatus = nameof(TaxStatus);
    public const string TaxNote = nameof(TaxNote);
    public const string TaxClaimYear = nameof(TaxClaimYear);
    public const string TaxClaimedBy = nameof(TaxClaimedBy);
    public const string TaxBusinessUsePercent = nameof(TaxBusinessUsePercent);
    public const string CategoryIsManual = nameof(CategoryIsManual);
    public const string ImportBatchId = nameof(ImportBatchId);

    /// <summary>The user said this isn't a duplicate of a transaction it looked like (see <c>DuplicateFinder</c>).</summary>
    public const string KeptAsDistinct = nameof(KeptAsDistinct);
    public const string CreatedAt = nameof(CreatedAt);

    /// <summary>The transaction's tag ids in <see cref="TagList"/> form ("|tg-1|tg-2|"), or null for none.</summary>
    public const string Tags = nameof(Tags);

    /// <summary>On an account: the household member it belongs to, or null for shared (or no household).</summary>
    public const string HeldBy = nameof(HeldBy);

    /// <summary>On a loan or line-of-credit account: the property (<see cref="PropertyRecord"/>) it's secured by, or null.</summary>
    public const string SecuredBy = nameof(SecuredBy);

    /// <summary>On an income transaction: the household member whose income it is, overriding the account's owner.</summary>
    public const string EarnedBy = nameof(EarnedBy);

    /// <summary>The owner used by the browser database, which only ever holds one person's data.</summary>
    public const string LocalOwner = "local";

    private const string SqliteProvider = "Microsoft.EntityFrameworkCore.Sqlite";

    /// <summary>
    /// Whose data this context reads and writes. Null (the default) matches no rows and refuses to
    /// save, so a context nobody scoped can't leak or mix data.
    /// </summary>
    public string? OwnerId { get; set; }

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<Receipt> Receipts => Set<Receipt>();
    public DbSet<HouseholdMember> HouseholdMembers => Set<HouseholdMember>();
    public DbSet<CustomCategory> CustomCategories => Set<CustomCategory>();
    public DbSet<LedgerTag> LedgerTags => Set<LedgerTag>();
    public DbSet<TaxSituationRecord> TaxSituations => Set<TaxSituationRecord>();
    public DbSet<LedgerUser> Users => Set<LedgerUser>();
    public DbSet<HoldingRecord> Holdings => Set<HoldingRecord>();
    public DbSet<ExchangeRateRecord> ExchangeRates => Set<ExchangeRateRecord>();
    public DbSet<PropertyRecord> Properties => Set<PropertyRecord>();
    public DbSet<HpiRecord> HousePriceIndex => Set<HpiRecord>();
    public DbSet<PriceHistoryRecord> PriceHistory => Set<PriceHistoryRecord>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<decimal>().HavePrecision(19, 4);
        configurationBuilder.Properties<AccountCategory>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<AccountType>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<AccountStatus>().HaveConversion<string>().HaveMaxLength(16);
        configurationBuilder.Properties<DebitCreditMemo>().HaveConversion<string>().HaveMaxLength(8);
        configurationBuilder.Properties<TransactionStatus>().HaveConversion<string>().HaveMaxLength(16);
        configurationBuilder.Properties<TransactionType>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<TaxTagStatus>().HaveConversion<string>().HaveMaxLength(16);
        configurationBuilder.Properties<UserTier>().HaveConversion<string>().HaveMaxLength(16);
        configurationBuilder.Properties<string>().HaveMaxLength(256);

        // SQLite can't compare or sort DateTimeOffset in SQL; store a number so date filters and
        // "newest first" still run in the database.
        if (Database.ProviderName == SqliteProvider)
        {
            configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UnixMillisecondsConverter>();

            // Receipt files cross the browser's SQLite bridge as base64 text, which it carries
            // reliably; binary parameters aren't documented for it.
            configurationBuilder.Properties<byte[]>().HaveConversion<BytesToStringConverter, ArrayStructuralComparer<byte>>();
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(account =>
        {
            account.ToTable("Accounts");
            account.HasKey(a => a.AccountId);
            account.Property(a => a.AccountId).HasMaxLength(64);
            account.Property(a => a.InterestRate).HasPrecision(9, 6);
            account.ComplexProperty(a => a.Currency, currency =>
            {
                currency.Property(c => c.CurrencyCode).HasMaxLength(3).HasColumnName("CurrencyCode");
                currency.Property(c => c.OriginalCurrencyCode).HasMaxLength(3).HasColumnName("OriginalCurrencyCode");
                currency.Property(c => c.CurrencyRate).HasPrecision(19, 8).HasColumnName("CurrencyRate");
            });
            account.Property<DateTimeOffset>(CreatedAt);
            account.Property<string?>(HeldBy).HasMaxLength(64);
            account.Property<string?>(SecuredBy).HasMaxLength(64);
            account.Property<string>(Owner).HasMaxLength(64).IsRequired();
            account.HasIndex(Owner);
            account.HasQueryFilter(a => EF.Property<string>(a, Owner) == OwnerId);
        });

        modelBuilder.Entity<Transaction>(transaction =>
        {
            transaction.ToTable("Transactions");
            transaction.HasKey(t => new { t.AccountId, t.TransactionId });
            transaction.Property(t => t.AccountId).HasMaxLength(64);
            transaction.Property(t => t.TransactionId).HasMaxLength(64);
            transaction.Property(t => t.Description).HasMaxLength(1024);
            transaction.Property(t => t.Memo).HasMaxLength(1024);
            transaction.Property(t => t.ForeignCurrency).HasMaxLength(3);
            transaction.HasOne<Account>().WithMany().HasForeignKey(t => t.AccountId).OnDelete(DeleteBehavior.Cascade);
            transaction.HasIndex(t => t.TransactionTimestamp);

            transaction.Property<string?>(TaxCode).HasMaxLength(32);
            transaction.Property<TaxTagStatus?>(TaxStatus);
            transaction.Property<string?>(TaxNote).HasMaxLength(1024);
            transaction.Property<int?>(TaxClaimYear);
            transaction.Property<string?>(TaxClaimedBy).HasMaxLength(64);
            transaction.Property<decimal?>(TaxBusinessUsePercent).HasPrecision(5, 2);
            transaction.Property<bool>(CategoryIsManual);
            transaction.Property<Guid?>(ImportBatchId);
            transaction.Property<bool>(KeptAsDistinct);
            transaction.Property<DateTimeOffset>(CreatedAt);
            transaction.Property<string?>(Tags).HasMaxLength(TagList.MaxLength);
            transaction.Property<string?>(EarnedBy).HasMaxLength(64);
            transaction.HasIndex(TaxCode);

            // Owner is denormalized onto transactions so the filter (and bulk updates, which honour
            // it) never depends on a join.
            transaction.Property<string>(Owner).HasMaxLength(64).IsRequired();
            transaction.HasIndex(Owner);
            transaction.HasQueryFilter(t => EF.Property<string>(t, Owner) == OwnerId);
        });

        modelBuilder.Entity<ImportBatch>(batch =>
        {
            batch.ToTable("ImportBatches");
            batch.Property(b => b.AccountId).HasMaxLength(64);
            batch.Property(b => b.Source).HasMaxLength(32);
            batch.HasOne<Account>().WithMany().HasForeignKey(b => b.AccountId).OnDelete(DeleteBehavior.Cascade);
            batch.Property<string>(Owner).HasMaxLength(64).IsRequired();
            batch.HasQueryFilter(b => EF.Property<string>(b, Owner) == OwnerId);
        });

        modelBuilder.Entity<Receipt>(receipt =>
        {
            receipt.ToTable("Receipts");
            receipt.Property(r => r.AccountId).HasMaxLength(64);
            receipt.Property(r => r.TransactionId).HasMaxLength(64);
            receipt.Property(r => r.FileName).HasMaxLength(Receipt.MaxFileNameLength);
            receipt.Property(r => r.ContentType).HasMaxLength(64);
            receipt.HasOne<Transaction>().WithMany().HasForeignKey(r => new { r.AccountId, r.TransactionId }).OnDelete(DeleteBehavior.Cascade);
            receipt.Property<string>(Owner).HasMaxLength(64).IsRequired();
            receipt.HasIndex(Owner);
            receipt.HasQueryFilter(r => EF.Property<string>(r, Owner) == OwnerId);
        });

        modelBuilder.Entity<HouseholdMember>(member =>
        {
            member.ToTable("HouseholdMembers");
            member.HasKey(m => m.Id);
            member.Property(m => m.Id).HasMaxLength(64);
            member.Property(m => m.Name).HasMaxLength(HouseholdMember.MaxNameLength);
            member.Property<string>(Owner).HasMaxLength(64).IsRequired();
            member.HasIndex(Owner);
            member.HasQueryFilter(m => EF.Property<string>(m, Owner) == OwnerId);
        });

        modelBuilder.Entity<CustomCategory>(category =>
        {
            category.ToTable("CustomCategories");
            category.HasKey(c => c.Id);
            category.Property(c => c.Id).HasMaxLength(64);
            category.Property(c => c.Category).HasMaxLength(64);
            category.Property(c => c.SubCategory).HasMaxLength(64);
            category.Property<string>(Owner).HasMaxLength(64).IsRequired();
            category.HasIndex(Owner);
            category.HasQueryFilter(c => EF.Property<string>(c, Owner) == OwnerId);
        });

        modelBuilder.Entity<LedgerTag>(tag =>
        {
            tag.ToTable("Tags");
            tag.HasKey(t => t.Id);
            tag.Property(t => t.Id).HasMaxLength(TagList.MaxIdLength);
            tag.Property(t => t.Name).HasMaxLength(64);
            tag.Property<string>(Owner).HasMaxLength(64).IsRequired();
            tag.HasIndex(Owner);
            tag.HasQueryFilter(t => EF.Property<string>(t, Owner) == OwnerId);
        });

        modelBuilder.Entity<TaxSituationRecord>(situation =>
        {
            situation.ToTable("TaxSituations");
            situation.HasKey(s => s.Id);
            situation.Property(s => s.Id).HasMaxLength(64);
            situation.Property(s => s.PersonId).HasMaxLength(64);
            situation.Property(s => s.Data).HasMaxLength(TaxSituationRecord.MaxDataLength);
            situation.Property<string>(Owner).HasMaxLength(64).IsRequired();
            situation.HasIndex(Owner, nameof(TaxSituationRecord.Year), nameof(TaxSituationRecord.PersonId)).IsUnique();
            situation.HasQueryFilter(s => EF.Property<string>(s, Owner) == OwnerId);
        });

        modelBuilder.Entity<HoldingRecord>(holding =>
        {
            holding.ToTable("Holdings");
            holding.HasKey(h => h.Id);
            holding.Property(h => h.AccountId).HasMaxLength(64);
            holding.Property(h => h.Symbol).HasMaxLength(MapleLedger.Core.Investments.Holding.MaxSymbolLength);
            holding.Property(h => h.Description).HasMaxLength(MapleLedger.Core.Investments.Holding.MaxDescriptionLength);
            holding.Property(h => h.Currency).HasMaxLength(3);
            holding.Property(h => h.Quantity).HasPrecision(24, 8);
            holding.Property(h => h.Price).HasPrecision(24, 8);
            holding.Property(h => h.ExchangeRate).HasPrecision(19, 8);
            holding.Property(h => h.PriceSource).HasConversion<string>().HasMaxLength(16);
            holding.HasOne<Account>().WithMany().HasForeignKey(h => h.AccountId).OnDelete(DeleteBehavior.Cascade);
            holding.Property<string>(Owner).HasMaxLength(64).IsRequired();
            holding.HasIndex(Owner);
            holding.HasQueryFilter(h => EF.Property<string>(h, Owner) == OwnerId);
        });

        modelBuilder.Entity<ExchangeRateRecord>(rate =>
        {
            rate.ToTable("ExchangeRates");
            rate.HasKey(r => r.Id);
            rate.Property(r => r.Currency).HasMaxLength(3);
            rate.Property(r => r.Rate).HasPrecision(19, 8);
            rate.Property(r => r.Source).HasConversion<string>().HasMaxLength(16);
            rate.Property<string>(Owner).HasMaxLength(64).IsRequired();
            rate.HasIndex(Owner, nameof(ExchangeRateRecord.Currency)).IsUnique();
            rate.HasQueryFilter(r => EF.Property<string>(r, Owner) == OwnerId);
        });

        modelBuilder.Entity<PropertyRecord>(property =>
        {
            property.ToTable("Properties");
            property.HasKey(p => p.Id);
            property.Property(p => p.Id).HasMaxLength(64);
            property.Property(p => p.Name).HasMaxLength(MapleLedger.Core.RealEstate.RealEstateProperty.MaxNameLength);
            property.Property(p => p.City).HasMaxLength(MapleLedger.Core.RealEstate.RealEstateProperty.MaxCityLength);
            property.Property<string>(Owner).HasMaxLength(64).IsRequired();
            property.HasIndex(Owner);
            property.HasQueryFilter(p => EF.Property<string>(p, Owner) == OwnerId);
        });

        modelBuilder.Entity<HpiRecord>(reading =>
        {
            reading.ToTable("HousePriceIndex");
            reading.HasKey(r => r.Id);
            reading.Property(r => r.Area).HasMaxLength(MapleLedger.Core.RealEstate.HpiReading.MaxAreaLength);
            reading.Property(r => r.Index).HasPrecision(19, 4);
            reading.Property<string>(Owner).HasMaxLength(64).IsRequired();
            reading.HasIndex(Owner);
            reading.HasQueryFilter(r => EF.Property<string>(r, Owner) == OwnerId);
        });

        modelBuilder.Entity<PriceHistoryRecord>(reading =>
        {
            reading.ToTable("PriceHistory");
            reading.HasKey(r => r.Id);
            reading.Property(r => r.Symbol).HasMaxLength(MapleLedger.Core.Investments.Holding.MaxSymbolLength);
            reading.Property(r => r.Currency).HasMaxLength(3);
            reading.Property(r => r.Close).HasPrecision(19, 6);
            reading.Property<string>(Owner).HasMaxLength(64).IsRequired();
            reading.HasIndex(Owner);
            reading.HasQueryFilter(r => EF.Property<string>(r, Owner) == OwnerId);
        });

        modelBuilder.Entity<LedgerUser>(user =>
        {
            user.ToTable("Users");
            user.Property(u => u.Id).HasMaxLength(64);
            user.Property(u => u.Email).HasMaxLength(320);
            user.HasIndex(u => u.Email);
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampOwner();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampOwner();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>New ledger rows belong to the current owner; rows can never change owner.</summary>
    private void StampOwner()
    {
        foreach (var entry in ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified))
        {
            if (entry.Metadata.FindProperty(Owner) is null) continue;

            var owner = entry.Property(Owner);
            if (OwnerId is null)
            {
                throw new InvalidOperationException("Set LedgerDbContext.OwnerId before saving ledger data.");
            }

            if (entry.State == EntityState.Added)
            {
                owner.CurrentValue = OwnerId;
            }
            else if (!Equals(owner.OriginalValue, OwnerId) || owner.IsModified)
            {
                throw new InvalidOperationException("Ledger rows can't be moved to another owner.");
            }
        }
    }

    /// <summary>
    /// Milliseconds rather than ticks: in the browser, parameters cross into SQLite through
    /// JavaScript, where integers above 2^53 aren't exact and are sent as text instead. Ticks
    /// (~6e17) would then compare as text against INTEGER expressions and match nothing;
    /// milliseconds (~2e12) stay ordinary numbers.
    /// </summary>
    private sealed class UnixMillisecondsConverter() : ValueConverter<DateTimeOffset, long>(
        value => value.ToUnixTimeMilliseconds(),
        milliseconds => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds));
}

/// <summary>An audit record of one import (a paste, a restored backup, and later an FDX sync).</summary>
public sealed class ImportBatch
{
    public Guid Id { get; set; }
    public required string AccountId { get; set; }
    public required string Source { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
    public int Received { get; set; }
    public int Added { get; set; }
    public int Duplicates { get; set; }
}

/// <summary>
/// A receipt or statement attached to a transaction (an official donation receipt, a pharmacy
/// receipt). Stored with the rest of the ledger: in the browser, or on the server for premium users.
/// </summary>
public sealed class Receipt
{
    public const int MaxFileNameLength = 128;

    public Guid Id { get; set; }
    public required string AccountId { get; set; }
    public required string TransactionId { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long Size { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
    public required byte[] Content { get; set; }
}

/// <summary>
/// One row of an investment account's holdings snapshot (<see cref="MapleLedger.Core.Investments.HoldingsSnapshot"/>).
/// Every row of an account has the same <see cref="AsOf"/>; <see cref="Position"/> keeps the report's order.
/// </summary>
public sealed class HoldingRecord
{
    public Guid Id { get; set; }
    public required string AccountId { get; set; }
    public DateTimeOffset AsOf { get; set; }
    public int Position { get; set; }
    public string? Symbol { get; set; }
    public string? Description { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? Price { get; set; }
    public decimal MarketValue { get; set; }
    public decimal? BookValue { get; set; }
    public required string Currency { get; set; }
    public decimal ExchangeRate { get; set; }

    /// <summary>When <see cref="Price"/> was quoted, when it came from the quote feed; null for the statement's.</summary>
    public DateTimeOffset? PriceAsOf { get; set; }

    public MapleLedger.Core.Investments.HoldingPriceSource PriceSource { get; set; }
}

/// <summary>The ledger's rate for one currency (<see cref="MapleLedger.Core.Investments.ExchangeRate"/>).</summary>
public sealed class ExchangeRateRecord
{
    public Guid Id { get; set; }
    public required string Currency { get; set; }
    public decimal Rate { get; set; }
    public DateTimeOffset AsOf { get; set; }
    public MapleLedger.Core.Investments.ExchangeRateSource Source { get; set; }
}

/// <summary>A property the household owns (<see cref="MapleLedger.Core.RealEstate.RealEstateProperty"/>). Loans point at it through <see cref="LedgerDbContext.SecuredBy"/>.</summary>
public sealed class PropertyRecord
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public string? City { get; set; }
    public decimal? Value { get; set; }
    public DateTimeOffset? ValueAsOf { get; set; }
}

/// <summary>One house price index reading (<see cref="MapleLedger.Core.RealEstate.HpiReading"/>); <see cref="Month"/> is the month's first day, UTC.</summary>
public sealed class HpiRecord
{
    public Guid Id { get; set; }
    public required string Area { get; set; }
    public DateTimeOffset Month { get; set; }
    public decimal Index { get; set; }
}

/// <summary>
/// A listing's close at a month's end (<see cref="MapleLedger.Core.Investments.PriceReading"/>), from the
/// monthly price history feed; <see cref="Month"/> is the month's first day, UTC.
/// </summary>
public sealed class PriceHistoryRecord
{
    public Guid Id { get; set; }
    public required string Symbol { get; set; }
    public required string Currency { get; set; }
    public DateTimeOffset Month { get; set; }
    public decimal Close { get; set; }
}

/// <summary>
/// One person's <see cref="MapleLedger.Core.Tax.TaxSituation"/> for a tax year, kept as JSON so new
/// slip fields need no migration.
/// </summary>
public sealed class TaxSituationRecord
{
    public const int MaxDataLength = 4000;

    public required string Id { get; set; }
    public int Year { get; set; }

    /// <summary>A household member id, or <see cref="MapleLedger.Core.Tax.TaxSituations.Self"/>.</summary>
    public required string PersonId { get; set; }

    public required string Data { get; set; }
}

public enum UserTier
{
    Free,

    /// <summary>May store data on the server (it has an operating cost).</summary>
    Premium,
}

/// <summary>A registered (signed-in) user. Only exists on the server.</summary>
public sealed class LedgerUser
{
    /// <summary>The identity provider's stable subject id (Google "sub").</summary>
    public required string Id { get; set; }
    public required string Email { get; set; }
    public string? DisplayName { get; set; }
    public UserTier Tier { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSignInAt { get; set; }
}
