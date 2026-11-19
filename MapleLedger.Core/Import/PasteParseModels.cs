using MapleLedger.Fdx;

namespace MapleLedger.Core.Import;

public enum ColumnRole
{
    Ignore,
    Date,
    PostedDate,
    Description,

    /// <summary>A single signed amount column.</summary>
    Amount,

    /// <summary>Money out (withdrawals, charges).</summary>
    Debit,

    /// <summary>Money in (deposits, payments to a card).</summary>
    Credit,
    Balance,
}

/// <summary>How the pasted text was laid out.</summary>
public enum PasteLayout
{
    Empty,

    /// <summary>A table copied from a web page: cells separated by tabs.</summary>
    TabSeparated,

    /// <summary>A CSV export pasted as text.</summary>
    CommaSeparated,

    /// <summary>A table whose cells each landed on their own line; empty cells are lost.</summary>
    OneCellPerLine,

    /// <summary>Statement-style lines: date, description, then amounts, separated by spaces.</summary>
    FreeText,
}

public sealed record PasteParseOptions
{
    /// <summary>Order for numeric dates such as 03/04/2026. <see cref="DateOrder.Auto"/> decides from the data.</summary>
    public DateOrder DateOrder { get; init; } = DateOrder.Auto;

    /// <summary>
    /// True for credit cards and lines of credit, where statements print charges as positive
    /// numbers and a rising balance means more money owed.
    /// </summary>
    public bool IsLiabilityAccount { get; init; }

    /// <summary>Anchors dates printed without a year. Defaults to today.</summary>
    public DateOnly? ReferenceDate { get; init; }

    /// <summary>Overrides column detection for tabular layouts. Ignored when empty.</summary>
    public IReadOnlyList<ColumnRole>? ColumnRoles { get; init; }
}

public sealed record ParsedTransaction(
    int SourceLine,
    DateOnly Date,
    DateOnly? PostedDate,
    string Description,
    decimal Amount,
    DebitCreditMemo Direction,
    decimal? Balance)
{
    public decimal SignedAmount => Direction == DebitCreditMemo.Credit ? Amount : -Amount;
}

public sealed record ParseIssue(int SourceLine, string Message);

public sealed record PasteParseResult(
    PasteLayout Layout,
    IReadOnlyList<string>? Header,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    IReadOnlyList<ColumnRole> ColumnRoles,
    DateOrder DateOrder,
    IReadOnlyList<ParsedTransaction> Transactions,
    IReadOnlyList<ParseIssue> Issues)
{
    public static PasteParseResult Empty { get; } = new(PasteLayout.Empty, null, [], [], DateOrder.Auto, [], []);

    /// <summary>True when the column roles were derived from the layout and cannot be edited per column.</summary>
    public bool RolesAreImplied => Layout is PasteLayout.OneCellPerLine or PasteLayout.FreeText;

    /// <summary>The balance after the most recent transaction, when the paste includes balances.</summary>
    public ParsedTransaction? MostRecentWithBalance =>
        Transactions.Where(t => t.Balance is not null).OrderBy(t => t.Date).ThenBy(t => t.SourceLine).LastOrDefault();

    public decimal TotalCredits => Transactions.Where(t => t.Direction == DebitCreditMemo.Credit).Sum(t => t.Amount);

    public decimal TotalDebits => Transactions.Where(t => t.Direction == DebitCreditMemo.Debit).Sum(t => t.Amount);
}
