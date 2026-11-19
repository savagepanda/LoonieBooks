using MapleLedger.Core.Import;

namespace MapleLedger.UnitTests.Import;

public class DateParserTests
{
    private static readonly DateOnly Today = new(2026, 9, 30);

    [Theory]
    [InlineData("2026-09-28", 2026, 9, 28)]
    [InlineData("2026/09/28", 2026, 9, 28)]
    [InlineData("2026-09-28T00:00:00", 2026, 9, 28)]
    [InlineData("Sep 28, 2026", 2026, 9, 28)]
    [InlineData("Sept. 28, 2026", 2026, 9, 28)]
    [InlineData("September 28 2026", 2026, 9, 28)]
    [InlineData("28 Sep 2026", 2026, 9, 28)]
    [InlineData("28-SEP-2026", 2026, 9, 28)]
    [InlineData("28 sept. 2026", 2026, 9, 28)]
    [InlineData("1er janvier 2026", 2026, 1, 1)]
    [InlineData("15 août 2026", 2026, 8, 15)]
    [InlineData("Mar 3, 2026", 2026, 3, 3)]
    [InlineData("Mon, Sep 28, 2026", 2026, 9, 28)]
    [InlineData("09/28/2026", 2026, 9, 28)]
    [InlineData("28/09/2026", 2026, 9, 28)]
    public void ParsesBankDateFormats(string text, int y, int m, int d)
    {
        Assert.True(DateParser.TryParse(text, DateOrder.Auto, Today, out var date));
        Assert.Equal(new DateOnly(y, m, d), date);
    }

    [Fact]
    public void AmbiguousNumericDatesFollowTheRequestedOrder()
    {
        Assert.True(DateParser.TryParse("03/04/2026", DateOrder.MonthDayYear, Today, out var mdy));
        Assert.True(DateParser.TryParse("03/04/2026", DateOrder.DayMonthYear, Today, out var dmy));
        Assert.Equal(new DateOnly(2026, 3, 4), mdy);
        Assert.Equal(new DateOnly(2026, 4, 3), dmy);
    }

    [Fact]
    public void DetectsDayFirstFromAnyUnambiguousValue()
    {
        Assert.Equal(DateOrder.DayMonthYear, DateParser.DetectOrder(["03/04/2026", "28/04/2026"]));
        Assert.Equal(DateOrder.MonthDayYear, DateParser.DetectOrder(["03/04/2026", "04/28/2026"]));
        Assert.Equal(DateOrder.Auto, DateParser.DetectOrder(["03/04/2026"]));
    }

    [Fact]
    public void YearlessDatesResolveToTheMostRecentOccurrence()
    {
        Assert.True(DateParser.TryParse("Sep 28", DateOrder.Auto, Today, out var recent));
        Assert.Equal(new DateOnly(2026, 9, 28), recent);

        // A December charge seen in January belongs to the previous year.
        Assert.True(DateParser.TryParse("Dec 28", DateOrder.Auto, new DateOnly(2027, 1, 5), out var lastYear));
        Assert.Equal(new DateOnly(2026, 12, 28), lastYear);
    }

    [Theory]
    [InlineData("TIM HORTONS")]
    [InlineData("12.50")]
    [InlineData("2026-02-30")]
    [InlineData("")]
    public void RejectsNonDates(string text) => Assert.False(DateParser.TryParse(text, DateOrder.Auto, Today, out _));
}
