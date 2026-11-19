using MapleLedger.Core.RealEstate;

namespace MapleLedger.UnitTests.RealEstate;

public sealed class HousePriceIndexFeedTests
{
    // The shape ownquity.com serves (houseManager.Web/Services/HpiFeed.cs), cut down.
    private const string Feed = """
        {
          "v": 1, "source": "ownquity.com", "index": "CREA MLS® Home Price Index, composite",
          "through": "2025-12", "updatedUtc": "2026-04-20T13:13:47Z", "future": {"ignored": true},
          "regions": [
            {"id":"AGGREGATE","name":"Canada","kind":"country","places":[],"from":"2025-10","hpi":[270.1,271,271.6],"benchmark":[465000,468000,469300]},
            {"id":"QUEBEC","name":"Quebec","kind":"province","province":"QC","places":[],"from":"2025-10","hpi":[300,301,302]},
            {"id":"OAKVILLE_MILTON","name":"Oakville Milton","kind":"area","province":"ON","places":["Milton","Oakville"],"from":"2025-10","hpi":[350.1,null,351]},
            {"id":"MONTREAL_CMA","name":"Montréal CMA","kind":"area","province":"QC","places":["Montreal"],"from":"2025-10","hpi":[320,321,322]},
            {"id":"QUEBEC_CMA","name":"Québec CMA","kind":"area","province":"QC","places":[],"from":"2025-10","hpi":[330,331,332]},
            {"id":"ST_JOHNS_NL","name":"St. John's (NL)","kind":"area","province":"NL","places":["St. John's"],"from":"2025-10","hpi":[250,251,252]},
            {"id":"GREATER_VANCOUVER","name":"Greater Vancouver","kind":"area","province":"BC","places":["Vancouver"],"from":"2025-10","hpi":[330,331,332]},
            {"id":"VANCOUVER_ISLAND","name":"Vancouver Island","kind":"area","province":"BC","places":["Vancouver"],"from":"2025-10","hpi":[400,401,402]},
            {"id":"NO_INDEX","name":"Nowhere","from":"2025-10","hpi":[null,0,-1]},
            {"id":"NO_FROM","name":"Nowhen","hpi":[1,2]}
          ]
        }
        """;

    private static readonly HpiBook Book = OwnquityHpi.Parse(Feed);

    [Fact]
    public void ParseReadsEachRegionsSeriesAndSkipsOnesWithoutAnIndex()
    {
        Assert.Equal(8, Book.Count);
        Assert.Equal("ownquity.com", Book.Source);
        Assert.Equal("CREA MLS® Home Price Index, composite", Book.IndexName);
        Assert.Equal(new DateOnly(2025, 12, 1), Book.Through);

        var oakville = Book.Regions.Single(r => r.Id == "OAKVILLE_MILTON");
        Assert.Equal(("Oakville Milton", "area", "ON", "Oakville Milton, ON"), (oakville.Name, oakville.Kind, oakville.Province, oakville.Label));
        Assert.Equal(["Milton", "Oakville"], oakville.Places);
        Assert.Equal(new DateOnly(2025, 10, 1), oakville.From);
        Assert.Equal([350.1m, null, 351m], oakville.Index);
        Assert.Equal(
            [new HpiReading("Oakville", new DateOnly(2025, 10, 1), 350.1m), new HpiReading("Oakville", new DateOnly(2025, 12, 1), 351m)],
            oakville.Readings("Oakville"));
        Assert.Equal("Canada", Book.Regions.Single(r => r.Id == "AGGREGATE").Label);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"v":2,"regions":[]}""")]
    [InlineData("""{"v":1}""")]
    public void AnythingButTheFeedGivesAnEmptyBook(string json)
    {
        var book = OwnquityHpi.Parse(json);
        Assert.Equal(0, book.Count);
        Assert.Null(book.Through);
        Assert.Null(book.Find("Oakville"));
    }

    [Theory]
    [InlineData("Oakville Milton", "OAKVILLE_MILTON")]
    [InlineData("oakville", "OAKVILLE_MILTON")]
    [InlineData("  Milton ", "OAKVILLE_MILTON")]
    [InlineData("Oakville, ON", "OAKVILLE_MILTON")]
    [InlineData("Oakville Milton, ON", "OAKVILLE_MILTON")]
    [InlineData("Montreal", "MONTREAL_CMA")]
    [InlineData("Montréal", "MONTREAL_CMA")]
    [InlineData("MONTREAL CMA", "MONTREAL_CMA")]
    [InlineData("Québec CMA", "QUEBEC_CMA")]
    [InlineData("Quebec", "QUEBEC")] // the province's own name comes first
    [InlineData("St. John's", "ST_JOHNS_NL")]
    [InlineData("St Johns NL", "ST_JOHNS_NL")]
    [InlineData("Greater Vancouver", "GREATER_VANCOUVER")]
    [InlineData("Canada", "AGGREGATE")]
    public void ACityOrAreaFindsItsRegion(string city, string id) => Assert.Equal(id, Book.Find(city)?.Id);

    [Theory]
    [InlineData("Vancouver")] // two regions list it
    [InlineData("Hamilton")]
    [InlineData("Oak")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ANameThatIsntOneRegionsFindsNone(string? city) => Assert.Null(Book.Find(city));
}
