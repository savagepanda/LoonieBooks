using MapleLedger.Core;
using MapleLedger.Web.Client.Services;

namespace MapleLedger.Web.Client.Components;

/// <summary>
/// A budget column, as the budget charts draw it: as tall as the budget or the spending, whichever is
/// more. From the baseline, spending within the budget, then what's left of the budget (faded) or the
/// spending over it (red), with a 2px surface gap where one piece meets the next. A column without a
/// budget is its spending alone, in grey. Only the top piece has rounded corners.
/// </summary>
internal static class BudgetBars
{
    private const double Gap = 2, Radius = 4;

    public sealed record Segment(string Class, decimal From, decimal To, bool Round);

    public static List<Segment> Segments(decimal budget, decimal spent)
    {
        if (budget <= 0) return spent > 0 ? [new("viz-budget-none-bar", 0, spent, true)] : [];
        spent = Math.Max(0, spent);
        var within = Math.Min(spent, budget);
        var list = new List<Segment>();
        if (within > 0) list.Add(new("viz-budget-spent-bar", 0, within, false));
        if (spent < budget) list.Add(new("viz-budget-left-bar", within, budget, true));
        else if (spent > budget) list.Add(new("viz-budget-over-bar", budget, spent, true));
        if (list.Count > 0 && list[^1] is { Round: false } last) list[^1] = last with { Round = true };
        return list;
    }

    /// <summary>The segment's path at <paramref name="x"/>, <paramref name="width"/> wide, by the chart's <paramref name="y"/> scale.</summary>
    public static string Path(Segment segment, double x, double width, Func<decimal, double> y)
    {
        var bottom = y(segment.From) - (segment.From > 0 ? Gap : 0);
        var top = y(segment.To);
        if (bottom - top < 0.5) return string.Empty;
        var r = segment.Round ? Math.Min(Radius, Math.Min(width / 2, bottom - top)) : 0;
        string S(double v) => Display.Svg(v);
        return $"M{S(x)},{S(bottom)} L{S(x)},{S(top + r)} Q{S(x)},{S(top)} {S(x + r)},{S(top)} " +
               $"L{S(x + width - r)},{S(top)} Q{S(x + width)},{S(top)} {S(x + width)},{S(top + r)} L{S(x + width)},{S(bottom)} Z";
    }

    /// <summary>"spent CA$x of CA$y (CA$z left)." or "(… over).", with the pace by today when there is one.</summary>
    public static string Status(decimal budget, decimal spent, decimal? pace)
    {
        var text = $"spent {Money.Format(spent)} of {Money.Format(budget)}"
                   + (spent <= budget ? $" ({Money.Format(budget - spent)} left)." : $" ({Money.Format(spent - budget)} over).");
        return pace is { } p ? $"{text} At an even pace it would be {Money.Format(p)} by today." : text;
    }
}
