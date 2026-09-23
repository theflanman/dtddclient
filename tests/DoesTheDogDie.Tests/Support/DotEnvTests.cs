namespace DoesTheDogDie.Tests.Support;

public class DotEnvTests
{
    [Fact]
    public void Parse_ReadsKeyValue()
    {
        var result = DotEnv.Parse("FOO=bar\n");

        Assert.Equal("bar", result["FOO"]);
    }

    [Fact]
    public void Parse_StripsDoubleQuotes()
    {
        var result = DotEnv.Parse("FOO=\"bar\"\n");

        Assert.Equal("bar", result["FOO"]);
    }

    [Fact]
    public void Parse_SkipsCommentsAndBlankLines()
    {
        var result = DotEnv.Parse("# a comment\n\nFOO=bar\n   \n");

        Assert.Single(result);
        Assert.Equal("bar", result["FOO"]);
    }

    [Fact]
    public void Parse_IgnoresLinesWithoutEquals()
    {
        var result = DotEnv.Parse("not a valid line\nFOO=bar\n");

        Assert.Single(result);
        Assert.Equal("bar", result["FOO"]);
    }

    [Fact]
    public void Parse_HandlesCrlfLineEndings()
    {
        // A .env written on Windows, or checked out with core.autocrlf=true, arrives CRLF.
        var result = DotEnv.Parse("# a comment\r\n\r\nFOO=bar\r\nBAZ=\"qux\"\r\n");

        Assert.Equal(2, result.Count);
        Assert.Equal("bar", result["FOO"]);
        Assert.Equal("qux", result["BAZ"]);
    }

    [Fact]
    public void Parse_LastWins()
    {
        var result = DotEnv.Parse("FOO=first\nFOO=second\n");

        Assert.Equal("second", result["FOO"]);
    }
}
