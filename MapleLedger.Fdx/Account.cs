using System.Text.Json.Serialization;

namespace MapleLedger.Fdx;

/// <summary>ISO 4217 currency information attached to accounts and transactions.</summary>
public sealed record Currency(string CurrencyCode, decimal? CurrencyRate = null, string? OriginalCurrencyCode = null)
{
    public static Currency Cad { get; } = new("CAD");
}

/// <summary>
/// An FDX account. FDX models deposit, loan, line-of-credit and investment accounts as separate
/// schemas that share a common descriptor; this type flattens them, leaving category-specific
/// balance fields null when they don't apply to <see cref="AccountCategory"/>.
/// </summary>
public sealed record Account
{
    public required string AccountId { get; init; }
    public required AccountCategory AccountCategory { get; init; }
    public required AccountType AccountType { get; init; }
    public AccountStatus Status { get; init; } = AccountStatus.Open;

    /// <summary>Masked account number suitable for display, e.g. "****1234".</summary>
    public string? AccountNumberDisplay { get; init; }
    public string? ProductName { get; init; }
    public string? Nickname { get; init; }
    public string? Description { get; init; }
    public Currency Currency { get; init; } = Currency.Cad;

    /// <summary>Institution name. Not part of the FDX account schema (the FI is implied by the endpoint); kept for manual/pasted accounts.</summary>
    public string? InstitutionName { get; init; }

    public DateTimeOffset? BalanceAsOf { get; init; }

    /// <summary>
    /// Deposit/investment: the account value. Loan/LOC: the amount owed, as a positive number.
    /// </summary>
    public decimal? CurrentBalance { get; init; }

    // Deposit accounts
    public decimal? AvailableBalance { get; init; }

    // Loan and line-of-credit accounts
    public decimal? PrincipalBalance { get; init; }
    public decimal? CreditLine { get; init; }
    public decimal? AvailableCredit { get; init; }
    public decimal? InterestRate { get; init; }

    [JsonIgnore]
    public bool IsLiability => AccountCategory is AccountCategory.LoanAccount or AccountCategory.LocAccount;

    [JsonIgnore]
    public string DisplayName => Nickname ?? ProductName ?? AccountNumberDisplay ?? AccountId;
}
