using System.Text;
using MapleLedger.Fdx;

namespace MapleLedger.Core.Import;

/// <summary>
/// Turns text copied from a bank website into transactions. Handles tab-separated tables (the
/// usual result of copying an HTML table), CSV, tables that paste as one cell per line, and
/// statement-style free text. Runs entirely client-side so raw pasted text never leaves the browser.
/// </summary>
public static class PasteParser
{
    private const double ColumnThreshold = 0.8;

    public static PasteParseResult Parse(string? text, PasteParseOptions? options = null)
    {
        options ??= new PasteParseOptions();
        var reference = options.ReferenceDate ?? DateOnly.FromDateTime(DateTime.Today);

        var lines = SplitLines(text);
        if (lines.Count == 0)
        {
            return PasteParseResult.Empty;
        }

        var table = Tabulate(lines, options.DateOrder, reference);
        if (table.Rows.Count == 0)
        {
            return PasteParseResult.Empty with { Layout = table.Layout, Issues = table.Issues };
        }

        var dateOrder = options.DateOrder != DateOrder.Auto
            ? options.DateOrder
            : DateParser.DetectOrder(table.Rows.SelectMany(r => r));

        var columnCount = table.Rows.Max(r => r.Length);
        var roles = table.ImpliedRoles
            ?? (options.ColumnRoles is { Count: > 0 } manual
                ? Fit(manual, columnCount)
                : InferRoles(table, columnCount, dateOrder, reference, options.IsLiabilityAccount));

        var issues = new List<ParseIssue>(table.Issues);
        var transactions = BuildTransactions(table, roles, dateOrder, reference, options, issues);

        return new PasteParseResult(
            table.Layout,
            table.Header,
            table.Rows.Select(r => (IReadOnlyList<string>)Fit(r, columnCount)).ToList(),
            roles,
            dateOrder,
            transactions,
            issues);
    }

    // ---------------------------------------------------------------- layout detection

    private sealed record Table(
        PasteLayout Layout,
        List<string[]> Rows,
        List<int> SourceLines,
        List<string>? Header,
        List<ColumnRole>? ImpliedRoles,
        List<ParseIssue> Issues);

    private static List<(int Line, string Text)> SplitLines(string? text) =>
        (text ?? string.Empty)
            .Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select((l, i) => (Line: i + 1, Text: l.TrimEnd()))
            .Where(l => l.Text.Trim().Length > 0)
            .ToList();

    private static Table Tabulate(List<(int Line, string Text)> lines, DateOrder order, DateOnly reference)
    {
        if (lines.Count(l => l.Text.Contains('\t')) >= lines.Count * 0.5)
        {
            return Positional(PasteLayout.TabSeparated, MergeWrappedTabRows(lines, order, reference).Select(l => (l.Line, l.Text.Split('\t'))).ToList(), order, reference);
        }

        var csv = lines.Select(l => (l.Line, Cells: SplitCsv(l.Text))).ToList();
        var modal = csv.GroupBy(c => c.Cells.Length).MaxBy(g => g.Count())!;
        if (modal.Key >= 3 && modal.Count() >= lines.Count * 0.7)
        {
            return Positional(PasteLayout.CommaSeparated, csv, order, reference);
        }

        var pureDates = lines.Count(l => DateParser.TryParse(l.Text, order, reference, out _));
        var prefixed = lines.Count(l => SplitFreeTextLine(l.Text, order, reference) is not null);
        return pureDates >= 2 && pureDates >= prefixed
            ? OneCellPerLine(lines, order, reference)
            : FreeText(lines, order, reference);
    }

    /// <summary>
    /// A cell containing a line break splits its row across two lines. A copied table keeps its
    /// empty cells, so every complete row is as wide as the widest one. Rejoin a short row that
    /// starts with a date to the lines after it, as long as they don't start a new row themselves.
    /// </summary>
    private static List<(int Line, string Text)> MergeWrappedTabRows(List<(int Line, string Text)> lines, DateOrder order, DateOnly reference)
    {
        bool StartsWithDate(string text) => DateParser.TryParse(text.Split('\t')[0], order, reference, out _);

        var width = lines.Max(l => l.Text.Count(c => c == '\t'));
        var merged = new List<(int Line, string Text)>();
        foreach (var line in lines)
        {
            var tabs = line.Text.Count(c => c == '\t');
            if (merged.Count > 0)
            {
                var last = merged[^1];
                var lastTabs = last.Text.Count(c => c == '\t');
                if (lastTabs < width && lastTabs + tabs <= width && StartsWithDate(last.Text) && !StartsWithDate(line.Text))
                {
                    merged[^1] = (last.Line, last.Text + " " + line.Text);
                    continue;
                }
            }

            merged.Add(line);
        }

        return merged;
    }

    private static Table Positional(PasteLayout layout, List<(int Line, string[] Cells)> rows, DateOrder order, DateOnly reference)
    {
        var trimmed = rows.Select(r => (r.Line, Cells: r.Cells.Select(c => TextNormalizer.CollapseWhitespace(c)).ToArray())).ToList();

        // Anything above the first row containing a date is a title or a header.
        var firstData = trimmed.FindIndex(r => r.Cells.Any(c => DateParser.TryParse(c, order, reference, out _)));
        if (firstData < 0)
        {
            return new Table(layout, [], [], null, null, [new ParseIssue(0, "No dates were found in the pasted text.")]);
        }

        List<string>? header = null;
        for (var i = firstData - 1; i >= 0; i--)
        {
            if (trimmed[i].Cells.Count(c => RoleFromHeader(c) != ColumnRole.Ignore) >= 2)
            {
                header = [.. trimmed[i].Cells];
                break;
            }
        }

        var data = trimmed.Skip(firstData).ToList();
        return new Table(layout, data.Select(r => r.Cells).ToList(), data.Select(r => r.Line).ToList(), header, null, []);
    }

    private static Table OneCellPerLine(List<(int Line, string Text)> lines, DateOrder order, DateOnly reference)
    {
        var records = new List<SemiStructured>();
        SemiStructured? current = null;
        foreach (var (line, raw) in lines)
        {
            var text = TextNormalizer.CollapseWhitespace(raw);
            if (DateParser.TryParse(text, order, reference, out _))
            {
                // A second date straight after the first is the posting date, not a new record.
                if (current is null || current.HasContent || current.Dates.Count >= 2)
                {
                    current = new SemiStructured(line);
                    records.Add(current);
                }

                current.Dates.Add(text);
            }
            else if (current is not null)
            {
                if (AmountParser.TryParse(text, out var amount) && amount.LooksLikeMoney) current.Amounts.Add(text);
                else current.Text.Add(text);
            }
        }

        return FromSemiStructured(PasteLayout.OneCellPerLine, records, []);
    }

    private static Table FreeText(List<(int Line, string Text)> lines, DateOrder order, DateOnly reference)
    {
        var records = new List<SemiStructured>();
        var issues = new List<ParseIssue>();
        foreach (var (line, text) in lines)
        {
            var record = SplitFreeTextLine(text, order, reference);
            if (record is null)
            {
                if (records.Count > 0) issues.Add(new ParseIssue(line, $"Skipped a line with no leading date: \"{Truncate(text)}\""));
                continue;
            }

            records.Add(record with { SourceLine = line });
        }

        return FromSemiStructured(PasteLayout.FreeText, records, issues);
    }

    /// <summary>Splits "Sep 26 Sep 28 TIM HORTONS #123 $4.50 $1,020.00" into dates, description and amounts.</summary>
    private static SemiStructured? SplitFreeTextLine(string text, DateOrder order, DateOnly reference)
    {
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        var record = new SemiStructured(0);

        for (var dateIndex = 0; dateIndex < 2; dateIndex++)
        {
            var taken = TakeDate(tokens, order, reference);
            if (taken is null) break;
            record.Dates.Add(taken);
        }

        if (record.Dates.Count == 0) return null;

        var amounts = new List<string>();
        while (tokens.Count > 0 && amounts.Count < 3)
        {
            var last = tokens[^1];
            // Suffixes printed as their own token ("12.50 CR", "12,50 $") belong to the amount before them.
            if (tokens.Count > 1 && last is "CR" or "DR" or "cr" or "dr" or "$")
            {
                last = tokens[^2] + " " + last;
                if (!AmountParser.TryParse(last, out var suffixed) || !suffixed.LooksLikeMoney) break;
                tokens.RemoveRange(tokens.Count - 2, 2);
                amounts.Insert(0, last);
                continue;
            }

            if (!AmountParser.TryParse(last, out var amount) || !amount.LooksLikeMoney) break;
            tokens.RemoveAt(tokens.Count - 1);
            amounts.Insert(0, last);
        }

        if (amounts.Count == 0 || tokens.Count == 0) return null;

        record.Text.Add(string.Join(' ', tokens));
        record.Amounts.AddRange(amounts);
        return record;
    }

    private static string? TakeDate(List<string> tokens, DateOrder order, DateOnly reference)
    {
        for (var n = Math.Min(3, tokens.Count - 1); n >= 1; n--)
        {
            var candidate = string.Join(' ', tokens.Take(n));
            if (DateParser.TryParse(candidate, order, reference, out _))
            {
                tokens.RemoveRange(0, n);
                return candidate;
            }
        }

        return null;
    }

    private sealed record SemiStructured(int SourceLine)
    {
        public List<string> Dates { get; } = [];
        public List<string> Text { get; } = [];
        public List<string> Amounts { get; } = [];
        public bool HasContent => Text.Count > 0 || Amounts.Count > 0;
    }

    /// <summary>
    /// Without reliable columns, each record becomes [date, posted date?, description, amount,
    /// balance?]. When a record has several amounts the last is taken as the running balance, which
    /// is what bank activity tables put in their final column.
    /// </summary>
    private static Table FromSemiStructured(PasteLayout layout, List<SemiStructured> records, List<ParseIssue> issues)
    {
        var hasPosted = records.Any(r => r.Dates.Count > 1);
        var hasBalance = records.Any(r => r.Amounts.Count > 1);

        var roles = new List<ColumnRole> { ColumnRole.Date };
        if (hasPosted) roles.Add(ColumnRole.PostedDate);
        roles.AddRange([ColumnRole.Description, ColumnRole.Amount]);
        if (hasBalance) roles.Add(ColumnRole.Balance);

        var rows = new List<string[]>();
        foreach (var r in records)
        {
            var cells = new List<string> { r.Dates[0] };
            if (hasPosted) cells.Add(r.Dates.Count > 1 ? r.Dates[1] : string.Empty);
            cells.Add(string.Join(' ', r.Text));

            // With debit, credit and balance all present, the amount is whichever of the first two is non-zero.
            var amount = r.Amounts.Count switch
            {
                0 => string.Empty,
                3 => HasValue(r.Amounts[0]) ? r.Amounts[0] : r.Amounts[1],
                _ => r.Amounts[0],
            };
            cells.Add(amount);
            if (hasBalance) cells.Add(r.Amounts.Count > 1 ? r.Amounts[^1] : string.Empty);
            rows.Add([.. cells]);
        }

        return new Table(layout, rows, records.Select(r => r.SourceLine).ToList(), null, roles, issues);
    }

    // ---------------------------------------------------------------- column roles

    private static List<ColumnRole> InferRoles(Table table, int columnCount, DateOrder order, DateOnly reference, bool isLiability)
    {
        var roles = table.Header is not null
            ? Fit(table.Header.Select(RoleFromHeader).ToList(), columnCount)
            : Enumerable.Repeat(ColumnRole.Ignore, columnCount).ToList();

        // Section titles such as "Pending transactions" have no date and are skipped later; keep
        // them from diluting the column statistics.
        var sample = table.Rows.Where(r => r.Any(c => DateParser.TryParse(c, order, reference, out _))).ToList();
        var stats = Enumerable.Range(0, columnCount).Select(c => ColumnStats.Of(sample, c, order, reference)).ToList();

        if (!roles.Contains(ColumnRole.Date))
        {
            var dateColumns = stats.Where(s => s.IsDate && roles[s.Index] == ColumnRole.Ignore).ToList();
            if (dateColumns.Count > 0) roles[dateColumns[0].Index] = ColumnRole.Date;
            if (dateColumns.Count > 1 && !roles.Contains(ColumnRole.PostedDate)) roles[dateColumns[1].Index] = ColumnRole.PostedDate;
        }

        if (!roles.Contains(ColumnRole.Description))
        {
            var text = stats.Where(s => s.IsText && roles[s.Index] == ColumnRole.Ignore).MaxBy(s => s.AverageLength);
            if (text is not null) roles[text.Index] = ColumnRole.Description;
        }

        var hasAmountRole = roles.Any(r => r is ColumnRole.Amount or ColumnRole.Debit or ColumnRole.Credit);
        if (!hasAmountRole)
        {
            var money = stats.Where(s => s.IsMoney && roles[s.Index] == ColumnRole.Ignore).Select(s => s.Index).ToList();

            // Some exports (TD's CSV) drop ".00" from whole-dollar amounts, so a sparse deposit
            // column of "40", "300", "30.95" doesn't look like money on its own. A numeric column
            // that is filled exactly where a money column is empty is that column's other half.
            var partners = stats
                .Where(s => s.IsNumeric && !s.IsMoney && roles[s.Index] == ColumnRole.Ignore)
                .Where(s => money.Any(m => Exclusive(sample, m, s.Index)))
                .Select(s => s.Index);
            money = [.. money.Concat(partners).Order()];

            AssignMoneyRoles(roles, money, sample);
        }

        if (table.Header is null)
        {
            FixDebitCreditOrder(table, roles, order, reference, isLiability);
        }

        return roles;
    }

    /// <summary>Withdrawal/deposit columns are mutually exclusive: each row fills exactly one of them.</summary>
    private static bool Exclusive(List<string[]> rows, int a, int b) =>
        rows.Count(r => HasValue(Cell(r, a)) ^ HasValue(Cell(r, b))) >= rows.Count * ColumnThreshold;

    private static void AssignMoneyRoles(List<ColumnRole> roles, List<int> money, List<string[]> rows)
    {
        if (money.Count == 0) return;
        if (money.Count == 1)
        {
            roles[money[0]] = ColumnRole.Amount;
            return;
        }

        if (Exclusive(rows, money[0], money[1]))
        {
            roles[money[0]] = ColumnRole.Debit;
            roles[money[1]] = ColumnRole.Credit;
            if (money.Count > 2) roles[money[^1]] = ColumnRole.Balance;
        }
        else
        {
            roles[money[0]] = ColumnRole.Amount;
            roles[money[^1]] = ColumnRole.Balance;
        }
    }

    /// <summary>
    /// Without a header we assume withdrawals come before deposits, as on most Canadian bank
    /// sites. When balances are present, check that assumption against them and swap if the
    /// other order explains more of the balance changes.
    /// </summary>
    private static void FixDebitCreditOrder(Table table, List<ColumnRole> roles, DateOrder order, DateOnly reference, bool isLiability)
    {
        int debit = roles.IndexOf(ColumnRole.Debit), credit = roles.IndexOf(ColumnRole.Credit), balance = roles.IndexOf(ColumnRole.Balance), date = roles.IndexOf(ColumnRole.Date);
        if (debit < 0 || credit < 0 || balance < 0 || date < 0) return;

        int Score(int debitColumn, int creditColumn)
        {
            var rows = Chronological(table.Rows, date, order, reference)
                .Select(r => (Signed: Magnitude(Cell(r, creditColumn)) - Magnitude(Cell(r, debitColumn)), Balance: Value(Cell(r, balance))))
                .ToList();
            var matches = 0;
            for (var i = 1; i < rows.Count; i++)
            {
                if (rows[i].Balance is not { } now || rows[i - 1].Balance is not { } before) continue;
                var expected = isLiability ? -rows[i].Signed : rows[i].Signed;
                if (Math.Abs(now - before - expected) < 0.005m) matches++;
            }

            return matches;
        }

        if (Score(credit, debit) > Score(debit, credit))
        {
            roles[debit] = ColumnRole.Credit;
            roles[credit] = ColumnRole.Debit;
        }
    }

    internal static ColumnRole RoleFromHeader(string header)
    {
        var words = TextNormalizer.ForMatching(header).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        bool Any(params string[] w) => w.Any(words.Contains);

        // A sentence ("You can view your current Rewards point balance") is a note beside the table, not a heading.
        if (words.Count == 0 || words.Count > 5 || Any("USD")) return ColumnRole.Ignore;
        if (Any("POSTED", "POSTING", "INSCRIPTION", "COMPTABILISATION")) return ColumnRole.PostedDate;
        if (Any("DATE")) return ColumnRole.Date;
        if (Any("BALANCE", "SOLDE")) return ColumnRole.Balance;
        if (Any("WITHDRAWAL", "WITHDRAWALS", "DEBIT", "DEBITS", "RETRAIT", "RETRAITS", "CHARGES", "OUT")) return ColumnRole.Debit;
        if (Any("DEPOSIT", "DEPOSITS", "CREDIT", "CREDITS", "DEPOT", "DEPOTS", "PAYMENTS", "IN")) return ColumnRole.Credit;
        if (Any("AMOUNT", "MONTANT", "CAD", "VALUE")) return ColumnRole.Amount;
        if (Any("DESCRIPTION", "DETAILS", "DETAIL", "TRANSACTION", "MERCHANT", "PAYEE", "NAME", "MEMO", "LIBELLE", "NARRATIVE")) return ColumnRole.Description;
        return ColumnRole.Ignore;
    }

    private sealed record ColumnStats(int Index, int NonEmpty, int Dates, int Amounts, int MoneyLike, double AverageLength)
    {
        public bool IsDate => NonEmpty > 0 && Dates >= NonEmpty * ColumnThreshold;
        public bool IsMoney => NonEmpty > 0 && !IsDate && Amounts >= NonEmpty * ColumnThreshold && MoneyLike >= NonEmpty * 0.5;
        public bool IsText => NonEmpty > 0 && !IsDate && !IsMoney;

        /// <summary>Every cell parses as a number, though maybe not one that looks like money.</summary>
        public bool IsNumeric => NonEmpty > 0 && !IsDate && Amounts >= NonEmpty * ColumnThreshold;

        public static ColumnStats Of(List<string[]> rows, int column, DateOrder order, DateOnly reference)
        {
            int nonEmpty = 0, dates = 0, amounts = 0, money = 0;
            long length = 0;
            foreach (var row in rows)
            {
                var cell = Cell(row, column);
                if (cell.Length == 0) continue;
                nonEmpty++;
                length += cell.Length;
                if (DateParser.TryParse(cell, order, reference, out _)) dates++;
                if (AmountParser.TryParse(cell, out var a))
                {
                    amounts++;
                    if (a.LooksLikeMoney) money++;
                }
            }

            return new ColumnStats(column, nonEmpty, dates, amounts, money, nonEmpty == 0 ? 0 : (double)length / nonEmpty);
        }
    }

    // ---------------------------------------------------------------- transactions

    private static List<ParsedTransaction> BuildTransactions(Table table, List<ColumnRole> roles, DateOrder order, DateOnly reference, PasteParseOptions options, List<ParseIssue> issues)
    {
        int dateColumn = roles.IndexOf(ColumnRole.Date), postedColumn = roles.IndexOf(ColumnRole.PostedDate), balanceColumn = roles.IndexOf(ColumnRole.Balance);
        int amountColumn = roles.IndexOf(ColumnRole.Amount), debitColumn = roles.IndexOf(ColumnRole.Debit), creditColumn = roles.IndexOf(ColumnRole.Credit);
        var descriptionColumns = Enumerable.Range(0, roles.Count).Where(i => roles[i] == ColumnRole.Description).ToList();

        if (dateColumn < 0 && postedColumn < 0)
        {
            issues.Add(new ParseIssue(0, "Choose which column holds the transaction date."));
            return [];
        }

        if (amountColumn < 0 && debitColumn < 0 && creditColumn < 0)
        {
            issues.Add(new ParseIssue(0, "Choose which column(s) hold the amount."));
            return [];
        }

        var amountColumnIsUnsigned = amountColumn >= 0 && table.Rows.All(r =>
            !AmountParser.TryParse(Cell(r, amountColumn), out var a) || (a.Value >= 0 && a.Marker == AmountMarker.None));

        var result = new List<ParsedTransaction>();
        for (var i = 0; i < table.Rows.Count; i++)
        {
            var row = table.Rows[i];
            var line = table.SourceLines[i];

            var hasDate = DateParser.TryParse(Cell(row, dateColumn), order, reference, out var date);
            var hasPosted = DateParser.TryParse(Cell(row, postedColumn), order, reference, out var posted);
            if (!hasDate && hasPosted) (date, hasDate) = (posted, true);
            if (!hasDate)
            {
                issues.Add(new ParseIssue(line, $"Skipped: no date in \"{Truncate(string.Join(" ", row))}\""));
                continue;
            }

            decimal amount;
            DebitCreditMemo direction;
            if (amountColumn >= 0)
            {
                if (!AmountParser.TryParse(Cell(row, amountColumn), out var parsed) || parsed.Value == 0)
                {
                    issues.Add(new ParseIssue(line, $"Skipped: no amount in \"{Truncate(string.Join(" ", row))}\""));
                    continue;
                }

                amount = Math.Abs(parsed.Value);
                direction = parsed.Marker switch
                {
                    AmountMarker.Credit => DebitCreditMemo.Credit,
                    AmountMarker.Debit => DebitCreditMemo.Debit,
                    // Bank accounts print money in as positive; card statements print charges as positive.
                    _ => (parsed.Value > 0) ^ options.IsLiabilityAccount ? DebitCreditMemo.Credit : DebitCreditMemo.Debit,
                };
            }
            else
            {
                var debit = Magnitude(Cell(row, debitColumn));
                var credit = Magnitude(Cell(row, creditColumn));
                if (debit == 0 && credit == 0)
                {
                    issues.Add(new ParseIssue(line, $"Skipped: no amount in \"{Truncate(string.Join(" ", row))}\""));
                    continue;
                }

                (amount, direction) = debit != 0 ? (debit, DebitCreditMemo.Debit) : (credit, DebitCreditMemo.Credit);
            }

            var description = TextNormalizer.CollapseWhitespace(string.Join(' ', descriptionColumns.Select(c => Cell(row, c))));
            result.Add(new ParsedTransaction(
                line,
                date,
                hasPosted && postedColumn != dateColumn ? posted : null,
                description.Length > 0 ? description : "(no description)",
                amount,
                direction,
                Value(Cell(row, balanceColumn))));
        }

        if (amountColumnIsUnsigned && balanceColumn >= 0)
        {
            result = InferDirectionFromBalances(result, options.IsLiabilityAccount, issues);
        }

        return result;
    }

    /// <summary>
    /// When amounts carry no sign, the running balance tells us which way each one went: a rise
    /// in an asset account's balance is a credit, a rise in a card's balance owed is a debit.
    /// </summary>
    private static List<ParsedTransaction> InferDirectionFromBalances(List<ParsedTransaction> transactions, bool isLiability, List<ParseIssue> issues)
    {
        var descending = transactions.Count > 1 && transactions[0].Date > transactions[^1].Date;
        var chronological = descending ? Enumerable.Reverse(transactions).ToList() : new List<ParsedTransaction>(transactions);

        var verified = 0;
        for (var i = 0; i < chronological.Count; i++)
        {
            // Unverifiable rows default to money out, by far the most common kind of transaction.
            var direction = DebitCreditMemo.Debit;
            var (before, now) = (i > 0 ? chronological[i - 1].Balance : null, chronological[i].Balance);
            if (before is not null && now is not null)
            {
                var delta = now.Value - before.Value;
                if (Math.Abs(Math.Abs(delta) - chronological[i].Amount) < 0.005m)
                {
                    direction = (delta > 0) ^ isLiability ? DebitCreditMemo.Credit : DebitCreditMemo.Debit;
                    verified++;
                }
            }

            chronological[i] = chronological[i] with { Direction = direction };
        }

        if (chronological.Count > 0 && verified < chronological.Count - 1)
        {
            issues.Add(new ParseIssue(0, $"Amounts have no sign and only {verified} of {chronological.Count} could be confirmed from the balance column. Check the money in/out direction below."));
        }
        else if (chronological.Count > 1)
        {
            issues.Add(new ParseIssue(chronological[0].SourceLine, "The direction of the oldest transaction can't be checked against a previous balance. Verify it."));
        }

        return descending ? Enumerable.Reverse(chronological).ToList() : chronological;
    }

    // ---------------------------------------------------------------- helpers

    private static IEnumerable<string[]> Chronological(List<string[]> rows, int dateColumn, DateOrder order, DateOnly reference)
    {
        DateOnly? first = null, last = null;
        foreach (var row in rows)
        {
            if (!DateParser.TryParse(Cell(row, dateColumn), order, reference, out var d)) continue;
            first ??= d;
            last = d;
        }

        return first > last ? Enumerable.Reverse(rows) : rows;
    }

    private static string Cell(string[] row, int column) => column >= 0 && column < row.Length ? row[column].Trim() : string.Empty;

    private static bool HasValue(string cell) => AmountParser.TryParse(cell, out var a) && a.Value != 0;

    private static decimal Magnitude(string cell) => AmountParser.TryParse(cell, out var a) ? Math.Abs(a.Value) : 0m;

    private static decimal? Value(string cell) => AmountParser.TryParse(cell, out var a) ? a.Value : null;

    private static List<ColumnRole> Fit(IReadOnlyList<ColumnRole> roles, int count) =>
        [.. roles.Take(count), .. Enumerable.Repeat(ColumnRole.Ignore, Math.Max(0, count - roles.Count))];

    private static string[] Fit(IReadOnlyList<string> cells, int count) =>
        [.. cells.Take(count), .. Enumerable.Repeat(string.Empty, Math.Max(0, count - cells.Count))];

    private static string Truncate(string s) => s.Length <= 60 ? s : s[..57] + "...";

    internal static string[] SplitCsv(string line)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { cells.Add(cell.ToString()); cell.Clear(); }
            else cell.Append(c);
        }

        cells.Add(cell.ToString());
        return [.. cells];
    }
}
