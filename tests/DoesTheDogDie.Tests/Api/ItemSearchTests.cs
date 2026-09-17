using DoesTheDogDie.Api;

namespace DoesTheDogDie.Tests.Api;

public class ItemSearchTests
{
    [Fact]
    public void ByQuery_Encodes()
    {
        var search = ItemSearch.ByQuery("old yeller");

        Assert.Equal(ItemSearchKind.Query, search.Kind);
        Assert.Equal("old yeller", search.Query);
        Assert.Equal("?q=old%20yeller", search.ToQueryString());
    }

    [Fact]
    public void ByImdbId()
    {
        var search = ItemSearch.ByImdbId("tt0050798");

        Assert.Equal(ItemSearchKind.Imdb, search.Kind);
        Assert.Equal("tt0050798", search.ImdbId);
        Assert.Equal("?imdb=tt0050798", search.ToQueryString());
    }

    [Fact]
    public void ByTmdbId()
    {
        var search = ItemSearch.ByTmdbId(22660);

        Assert.Equal(ItemSearchKind.Tmdb, search.Kind);
        Assert.Equal(22660, search.TmdbId);
        Assert.Equal("?tmdb=22660", search.ToQueryString());
    }

    [Fact]
    public void ByName_WithoutYear()
    {
        var search = ItemSearch.ByName("Halloween");

        Assert.Equal(ItemSearchKind.Name, search.Kind);
        Assert.Equal("Halloween", search.Name);
        Assert.Null(search.ReleaseYear);
        Assert.Equal("?name=Halloween", search.ToQueryString());
    }

    [Fact]
    public void ByName_WithYear()
    {
        var search = ItemSearch.ByName("Halloween", 1978);

        Assert.Equal(1978, search.ReleaseYear);
        Assert.Equal("?name=Halloween&releaseYear=1978", search.ToQueryString());
    }

    [Fact]
    public void ByQuery_Empty_Throws()
    {
        Assert.Throws<ArgumentException>(() => ItemSearch.ByQuery(""));
        Assert.Throws<ArgumentException>(() => ItemSearch.ByQuery("   "));
    }
}
