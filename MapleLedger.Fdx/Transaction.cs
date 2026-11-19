using System.Text.Json.Serialization;

namespace MapleLedger.Fdx;

/// <summary>
/// An FDX transaction. As with <see cref="Account"/>, the per-category FDX transaction schemas
/// (deposit, loan, LOC, investment) are flattened into one type.
/// </summary>
public sealed record Transaction
{
    public required string TransactionId { get; init; }
    public required string AccountId { get; init; }

    /// <summary>When the transaction posted to the account.</summary>
    public DateTimeOffset? PostedTimestamp { get; init; }

    /// <summary>When the transaction occurred (the date shown on most bank statements).</summary>
    public DateTimeOffset? TransactionTimestamp { get; init; }

    public string? Description { get; init; }
    public string? Memo { get; init; }
    public string? Payee { get; init; }
    public string? Reference { get; init; }
    public string? CheckNumber { get; init; }

    /// <summary>Direction of the money movement; <see cref="Amount"/> is always non-negative.</summary>
    public required DebitCreditMemo DebitCreditMemo { get; init; }
    public required decimal Amount { get; init; }
    public TransactionStatus Status { get; init; } = TransactionStatus.Posted;
    public TransactionType? TransactionType { get; init; }

    public string? Category { get; init; }
    public string? SubCategory { get; init; }

    public decimal? ForeignAmount { get; init; }
    public string? ForeignCurrency { get; init; }

    /// <summary>Running account balance after this transaction, when the source provides it.</summary>
    public decimal? RunningBalance { get; init; }

    /// <summary>Amount with sign applied: credits positive, debits negative, memos zero.</summary>
    [JsonIgnore]
    public decimal SignedAmount => DebitCreditMemo switch
    {
        DebitCreditMemo.Credit => Amount,
        DebitCreditMemo.Debit => -Amount,
        _ => 0m,
    };

    [JsonIgnore]
    public DateOnly EffectiveDate => DateOnly.FromDateTime((TransactionTimestamp ?? PostedTimestamp ?? DateTimeOffset.MinValue).Date);
}
