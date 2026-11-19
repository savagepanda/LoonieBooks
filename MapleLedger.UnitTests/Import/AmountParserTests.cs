using MapleLedger.Core.Import;

namespace MapleLedger.UnitTests.Import;

public class AmountParserTests
{
    [Theory]
    [InlineData("$1,234.56", 1234.56)]
    [InlineData("1234.56", 1234.56)]
    [InlineData("-$12.00", -12.00)]
    [InlineData("$-12.00", -12.00)]
    [InlineData("12.00-", -12.00)]
    [InlineData("(45.10)", -45.10)]
    [InlineData("−45.10", -45.10)]
    [InlineData("1 234,56 $", 1234.56)]
    [InlineData("1 234,56 $", 1234.56)]
    [InlineData("12,50", 12.50)]
    [InlineData("1,234", 1234)]
    [InlineData("CAD 99.99", 99.99)]
    [InlineData("+5.00", 5.00)]
    public void ParsesCanadianFormats(string text, double expected)
    {
        Assert.True(AmountParser.TryParse(text, out var amount));
        Assert.Equal((decimal)expected, amount.Value);
    }

    [Theory]
    [InlineData("12.50 CR", AmountMarker.Credit)]
    [InlineData("12.50CR", AmountMarker.Credit)]
    [InlineData("12.50 DR", AmountMarker.Debit)]
    [InlineData("12.50", AmountMarker.None)]
    public void ReadsCreditDebitMarkers(string text, AmountMarker marker)
    {
        Assert.True(AmountParser.TryParse(text, out var amount));
        Assert.Equal(marker, amount.Marker);
        Assert.Equal(12.50m, amount.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("TIM HORTONS")]
    [InlineData("2026-09-28")]
    [InlineData("09/28/2026")]
    [InlineData("416-555-1234")]
    public void RejectsNonAmounts(string text) => Assert.False(AmountParser.TryParse(text, out _));

    [Theory]
    [InlineData("$12", true)]
    [InlineData("12.34", true)]
    [InlineData("1234", false)]
    public void FlagsMoneyShapedValues(string text, bool looksLikeMoney)
    {
        Assert.True(AmountParser.TryParse(text, out var amount));
        Assert.Equal(looksLikeMoney, amount.LooksLikeMoney);
    }
}
