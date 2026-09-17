namespace DoesTheDogDie.Tests;

public class ThrottleOptionsTests
{
    public static TheoryData<DateTimeOffset, DateTimeOffset> RollOverCases => new()
    {
        { new DateTimeOffset(2026, 9, 16, 13, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero) },
        { new DateTimeOffset(2026, 12, 31, 23, 59, 0, TimeSpan.Zero), new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero) },
        { new DateTimeOffset(2026, 9, 30, 23, 30, 0, TimeSpan.FromHours(-5)), new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero) },
    };

    [Theory]
    [MemberData(nameof(RollOverCases))]
    public void NextUtcMonthStart_RollsOver(DateTimeOffset now, DateTimeOffset expected)
    {
        var result = ThrottleOptions.NextUtcMonthStart(now);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Defaults_AreDocumentedValues()
    {
        var options = new ThrottleOptions();

        Assert.Equal(0, options.MonthlyReserve);
        Assert.Equal(1000, options.MaxQueueLength);
        Assert.Equal(30, options.DefaultMinuteLimit);
    }
}
