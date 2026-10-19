using System.Globalization;
using FinancialTransactions.Consolidation;

namespace FinancialTransactions.UnitTests;

/// <summary>Dia de negócio em America/Sao_Paulo (H15). Os instantes esperados são literais conferidos à mão, não derivados do código testado.</summary>
public sealed class ConsolidationBusinessDayTests
{
    private static DateTimeOffset Utc(string iso) => DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture);

    [Theory(DisplayName = "BusinessDay_Of_RespectsTheLocalMidnightBoundary")]
    [InlineData("2026-09-02T02:59:59.999999Z", "2026-09-01")]
    [InlineData("2026-09-02T03:00:00Z", "2026-09-02")]
    [InlineData("2026-09-01T23:59:59.999999-03:00", "2026-09-01")]
    [InlineData("2026-09-02T00:00:00-03:00", "2026-09-02")]
    [InlineData("2026-09-02T00:00:00+00:00", "2026-09-01")] // = 21:00 local do dia 1
    [InlineData("2026-09-02T05:30:00+02:30", "2026-09-02")] // = 03:00Z
    public void OfRespectsTheLocalMidnightBoundary(string instant, string expectedDay)
    {
        Assert.Equal(DateOnly.Parse(expectedDay, CultureInfo.InvariantCulture), BusinessDay.Of(Utc(instant)));
    }

    [Theory(DisplayName = "BusinessDay_Of_ValidForHistoricalDaylightSavingDates")]
    [InlineData("2018-01-15T01:59:59Z", "2018-01-14")] // verão (-02): 23:59:59 do dia 14
    [InlineData("2018-01-15T02:00:00Z", "2018-01-15")] // 00:00 local do dia 15
    [InlineData("2018-11-04T02:59:59Z", "2018-11-03")] // antes da lacuna das 00:00 que não existiram em 04/11
    [InlineData("2018-11-04T03:00:00Z", "2018-11-04")] // fim da lacuna: 01:00 local (-02)
    [InlineData("2018-07-01T03:00:00Z", "2018-07-01")] // inverno (-03)
    public void OfIsValidForHistoricalDaylightSavingDates(string instant, string expectedDay)
    {
        Assert.Equal(DateOnly.Parse(expectedDay, CultureInfo.InvariantCulture), BusinessDay.Of(Utc(instant)));
    }

    [Fact(DisplayName = "BusinessDay_StartAndEnd_AreLocalMidnightsInUtc")]
    public void StartAndEndAreLocalMidnightsInUtc()
    {
        var day = new DateOnly(2026, 9, 2);

        Assert.Equal(Utc("2026-09-02T03:00:00Z"), BusinessDay.StartUtc(day));
        Assert.Equal(Utc("2026-09-03T03:00:00Z"), BusinessDay.EndExclusiveUtc(day));
    }

    [Fact(DisplayName = "BusinessDay_StartUtc_WhenLocalMidnightDoesNotExist_IsTheEndOfTheGap")]
    public void StartUtcWhenLocalMidnightDoesNotExistIsTheEndOfTheGap()
    {
        // 04/11/2018: às 00:00 local o relógio saltou para 01:00 (-02); o dia começa em 03:00Z, e o anterior termina aí.
        Assert.Equal(Utc("2018-11-04T03:00:00Z"), BusinessDay.StartUtc(new DateOnly(2018, 11, 4)));
        Assert.Equal(Utc("2018-11-04T03:00:00Z"), BusinessDay.EndExclusiveUtc(new DateOnly(2018, 11, 3)));
    }

    [Fact(DisplayName = "BusinessDay_EveryInstantBelongsToExactlyTheDayWhoseRangeContainsIt")]
    public void EveryInstantBelongsToExactlyTheDayWhoseRangeContainsIt()
    {
        var start = Utc("2026-09-02T03:00:00Z");
        var end = Utc("2026-09-03T03:00:00Z");
        var day = new DateOnly(2026, 9, 2);

        Assert.Equal(day, BusinessDay.Of(start));
        Assert.Equal(day, BusinessDay.Of(end.AddTicks(-10)));
        Assert.NotEqual(day, BusinessDay.Of(end));
        Assert.NotEqual(day, BusinessDay.Of(start.AddTicks(-10)));
    }

    [Fact(DisplayName = "BusinessDay_TimeZone_IsNamedAndAvailable")]
    public void TimeZoneIsNamedAndAvailable()
    {
        Assert.True(BusinessDay.IsTimeZoneAvailable(out var error), error);
        Assert.Equal("America/Sao_Paulo", BusinessDay.TimeZoneId);
    }
}
