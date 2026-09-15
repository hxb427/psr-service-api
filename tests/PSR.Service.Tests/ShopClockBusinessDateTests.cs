using FluentAssertions;
using PSR.Service.Api.Common;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>Every inward entry in production was booked one day early, and nothing failed — the number
/// was simply wrong in the database, on screen, and on the printed documents. These pin the conversion
/// so it cannot come back that quietly.
///
/// The cause: a .NET client's DateTime.Today is Kind=Local, so System.Text.Json serialises midnight IST
/// as "…T00:00:00+05:30"; the deserialiser then converts that into the RECEIVING process's zone, and the
/// API runs in a UTC container, so it arrived as 18:30 on the previous day.</summary>
public class ShopClockBusinessDateTests
{
    private const double Ist = 5.5;

    public ShopClockBusinessDateTests() => ShopClock.Configure(Ist);

    /// <summary>The exact shape the old client sent. The deserialiser hands the handler a Kind=Local
    /// value, and what "Local" means depends on the machine — so the case is written as the INSTANT the
    /// client meant (midnight IST = 18:30 UTC the day before) and then expressed in the host's zone,
    /// exactly as the deserialiser would. That makes the test say the same thing on a UTC container and
    /// on a developer's IST laptop; hard-coding 18:30 only described the container.</summary>
    [Theory]
    [InlineData(2026, 9, 14, 18, 30, 2026, 9, 15)]   // midnight IST on the 15th
    [InlineData(2026, 12, 31, 18, 30, 2027, 1, 1)]   // and across a year boundary
    [InlineData(2026, 2, 28, 18, 30, 2026, 3, 1)]    // and a month end
    public void A_zone_bearing_date_is_taken_back_to_the_shop_day(
        int y, int mo, int d, int h, int mi, int ey, int emo, int ed)
    {
        var arrived = new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Utc).ToLocalTime();
        arrived.Kind.Should().Be(DateTimeKind.Local, "this is the shape the deserialiser produces");

        var shopDay = ShopClock.BusinessDate(arrived);

        shopDay.Should().Be(new DateTime(ey, emo, ed));
        shopDay.TimeOfDay.Should().Be(TimeSpan.Zero, "a business date is a day, not an instant");
    }

    /// <summary>What a corrected client sends. Must pass through untouched — if this shifted, fixing
    /// the client would introduce the same off-by-one in the other direction.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(9, 15)]
    [InlineData(23, 59)]
    public void A_bare_wall_clock_date_only_loses_its_time(int hour, int minute)
        => ShopClock.BusinessDate(new DateTime(2026, 9, 15, hour, minute, 0, DateTimeKind.Unspecified))
            .Should().Be(new DateTime(2026, 9, 15));

    /// <summary>A client that stamps "Z" is naming an instant, so it gets moved onto the shop's clock
    /// before the day is taken — 19:00Z is already tomorrow here.</summary>
    [Theory]
    [InlineData(2026, 9, 14, 19, 0, 2026, 9, 15)]    // 00:30 IST on the 15th
    [InlineData(2026, 9, 14, 18, 0, 2026, 9, 14)]    // 23:30 IST, still the 14th
    [InlineData(2026, 9, 14, 0, 0, 2026, 9, 14)]     // 05:30 IST
    public void A_utc_instant_is_dated_on_the_shop_clock(
        int y, int mo, int d, int h, int mi, int ey, int emo, int ed)
        => ShopClock.BusinessDate(new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Utc))
            .Should().Be(new DateTime(ey, emo, ed));

    [Fact]
    public void Null_stays_null_so_callers_can_fall_back_to_today()
        => ShopClock.BusinessDate((DateTime?)null).Should().BeNull();

    /// <summary>The whole point of the repair: whatever shape the same moment arrives in, it dates to
    /// the same shop day. This is the invariant the old code broke.</summary>
    [Fact]
    public void The_same_moment_dates_the_same_however_it_arrives()
    {
        // 2026-09-15 00:00 IST, expressed three ways.
        var asUtc = new DateTime(2026, 9, 14, 18, 30, 0, DateTimeKind.Utc);
        var asLocal = asUtc.ToLocalTime();          // same instant, in whatever zone the host runs
        var asWallClock = new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Unspecified);

        var expected = new DateTime(2026, 9, 15);
        ShopClock.BusinessDate(asLocal).Should().Be(expected);
        ShopClock.BusinessDate(asUtc).Should().Be(expected);
        ShopClock.BusinessDate(asWallClock).Should().Be(expected);
    }

    /// <summary>Applying it twice must not move the date again — the server coerces on every write,
    /// and an edit re-sends a date this method already produced.</summary>
    [Fact]
    public void Coercion_is_idempotent()
    {
        var once = ShopClock.BusinessDate(
            DateTime.SpecifyKind(new DateTime(2026, 9, 14, 18, 30, 0), DateTimeKind.Local));

        ShopClock.BusinessDate(once).Should().Be(once);
    }

    /// <summary>Today is read off the shop's clock, not the server's. A UTC server in the evening here
    /// is already on tomorrow's date.</summary>
    [Fact]
    public void Today_is_the_shop_day_not_the_servers()
        => ShopClock.Today.Should().Be(DateTime.UtcNow.AddHours(Ist).Date);
}
