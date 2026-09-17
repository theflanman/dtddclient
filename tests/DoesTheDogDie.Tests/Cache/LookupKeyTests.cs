using DoesTheDogDie.Api;
using DoesTheDogDie.Cache;

namespace DoesTheDogDie.Tests.Cache;

public class LookupKeyTests
{
    [Fact]
    public void FromSearch_Query_IsNull()
    {
        var search = ItemSearch.ByQuery("halloween");

        var key = LookupKey.FromSearch(search);

        Assert.Null(key);
    }

    [Fact]
    public void FromSearch_Imdb()
    {
        var search = ItemSearch.ByImdbId("tt0050798");

        var key = LookupKey.FromSearch(search);

        Assert.Equal(new LookupKey(LookupKind.Imdb, "tt0050798"), key);
    }

    [Fact]
    public void FromSearch_Tmdb()
    {
        var search = ItemSearch.ByTmdbId(948);

        var key = LookupKey.FromSearch(search);

        Assert.Equal(new LookupKey(LookupKind.Tmdb, "948"), key);
    }

    [Fact]
    public void FromSearch_NameNormalizesCaseAndYear()
    {
        var padded = ItemSearch.ByName("  Halloween ", 1978);
        var lower = ItemSearch.ByName("halloween", 1978);
        var differentYear = ItemSearch.ByName("halloween", 2018);

        var paddedKey = LookupKey.FromSearch(padded);
        var lowerKey = LookupKey.FromSearch(lower);
        var differentYearKey = LookupKey.FromSearch(differentYear);

        Assert.Equal(paddedKey, lowerKey);
        Assert.NotEqual(paddedKey, differentYearKey);
    }

    [Fact]
    public void Equality()
    {
        var a = new LookupKey(LookupKind.Imdb, "tt0050798");
        var b = new LookupKey(LookupKind.Imdb, "tt0050798");
        var c = new LookupKey(LookupKind.Tmdb, "tt0050798");

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.NotEqual(a, c);
    }
}
