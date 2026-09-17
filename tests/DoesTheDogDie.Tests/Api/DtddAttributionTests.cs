using DoesTheDogDie.Api;

namespace DoesTheDogDie.Tests.Api;

public class DtddAttributionTests
{
    [Fact]
    public void Phrase_IsExactRequiredText()
    {
        Assert.Equal("Powered by DoesTheDogDie.com", DtddAttribution.Phrase);
    }

    [Fact]
    public void Url_IsDtddHomepage()
    {
        Assert.Equal("https://www.doesthedogdie.com", DtddAttribution.Url);
    }

    [Fact]
    public void Html_ContainsPhraseAndUrl()
    {
        Assert.Contains(DtddAttribution.Phrase, DtddAttribution.Html);
        Assert.Contains(DtddAttribution.Url, DtddAttribution.Html);
    }
}
