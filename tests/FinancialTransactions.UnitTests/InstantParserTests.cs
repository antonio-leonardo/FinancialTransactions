using FinancialTransactions.Domain;

namespace FinancialTransactions.UnitTests;

public class InstantParserTests
{
    [Theory]
    [InlineData("2026-09-01T12:00:00Z", "2026-09-01T12:00:00Z")]
    [InlineData("2026-09-01T09:00:00-03:00", "2026-09-01T12:00:00Z")]
    [InlineData("2026-09-01T17:30:00+05:30", "2026-09-01T12:00:00Z")]
    [InlineData("2026-09-01T12:00:00.5Z", "2026-09-01T12:00:00.5Z")]
    [InlineData("2026-09-01T12:00:00.123Z", "2026-09-01T12:00:00.123Z")]
    [InlineData("2026-09-01T12:00:00.1234567Z", "2026-09-01T12:00:00.1234567Z")]
    [InlineData("2026-09-01T09:00:00.25-03:00", "2026-09-01T12:00:00.25Z")]
    [InlineData("2026-09-01T09:00:00.1234567-03:00", "2026-09-01T12:00:00.1234567Z")]
    public void WithOffsetAcceptsZAndNumericOffsetsWithOptionalFraction(string raw, string expectedUtc)
    {
        var parsed = InstantParser.TryParseWithOffset(raw, out var value);

        Assert.True(parsed);
        Assert.Equal(DateTimeOffset.ParseExact(expectedUtc, "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal), value);
    }

    [Fact]
    public void WithOffsetPreservesTheOffsetGiven()
    {
        var parsed = InstantParser.TryParseWithOffset("2026-09-01T09:00:00-03:00", out var value);

        Assert.True(parsed);
        Assert.Equal(TimeSpan.FromHours(-3), value.Offset);
    }

    [Fact]
    public void WithOffsetGivesOffsetZeroForZ()
    {
        var parsed = InstantParser.TryParseWithOffset("2026-09-01T12:00:00Z", out var value);

        Assert.True(parsed);
        Assert.Equal(TimeSpan.Zero, value.Offset);
    }

    [Theory]
    [InlineData("2026-09-01T09:00:00")]
    [InlineData("2026-09-01T09:00:00.123")]
    [InlineData("2026-09-01")]
    [InlineData("00:30")]
    [InlineData("09:00:00")]
    [InlineData("09/01/2026")]
    [InlineData("Sep 1 2026")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" 2026-09-01T12:00:00Z")]
    [InlineData("2026-09-01T12:00:00Z ")]
    [InlineData("2026-13-01T00:00:00Z")]
    [InlineData("2026-09-01T24:00:00Z")]
    [InlineData("2026-09-01 12:00:00Z")]
    [InlineData("20260901")]
    public void WithOffsetRejectsMissingOffsetPartialRegionalTextAndInvalid(string raw)
    {
        var parsed = InstantParser.TryParseWithOffset(raw, out var value);

        Assert.False(parsed);
        Assert.Equal(default, value);
    }

    [Fact]
    public void WithOffsetRejectsNull()
    {
        Assert.False(InstantParser.TryParseWithOffset(null, out _));
        Assert.False(InstantParser.TryParseUtcOrOffset(null, out _));
    }

    [Theory]
    [InlineData("2026-09-01T12:00:00", "2026-09-01T12:00:00Z")]
    [InlineData("2026-09-01T12:00:00.25", "2026-09-01T12:00:00.25Z")]
    [InlineData("2026-09-01T09:00:00-03:00", "2026-09-01T12:00:00Z")]
    [InlineData("2026-09-01T12:00:00Z", "2026-09-01T12:00:00Z")]
    public void UtcOrOffsetAcceptsCompleteWithoutOffsetAsUtc(string raw, string expectedUtc)
    {
        var parsed = InstantParser.TryParseUtcOrOffset(raw, out var value);

        Assert.True(parsed);
        Assert.Equal(DateTimeOffset.ParseExact(expectedUtc, "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal), value);
        Assert.Equal(TimeSpan.Zero, value.ToUniversalTime().Offset);
    }

    [Fact]
    public void UtcOrOffsetCompleteWithoutOffsetHasOffsetZeroNotLocal()
    {
        var parsed = InstantParser.TryParseUtcOrOffset("2026-09-01T12:00:00", out var value);

        Assert.True(parsed);
        Assert.Equal(TimeSpan.Zero, value.Offset);
    }

    [Theory]
    [InlineData("2026-09-01")]
    [InlineData("00:30")]
    [InlineData("09/01/2026")]
    [InlineData("Sep 1 2026")]
    [InlineData("2026-09-01T12:00")]
    [InlineData("")]
    public void UtcOrOffsetRejectsDateOnlyHourOnlyRegionalAndText(string raw)
    {
        Assert.False(InstantParser.TryParseUtcOrOffset(raw, out _));
    }
}
