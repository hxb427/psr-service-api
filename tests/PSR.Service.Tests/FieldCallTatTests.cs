using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Common;
using PSR.Service.Api.Data;
using PSR.Service.Api.Stock;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>Pins the turnaround figures the office will quote, and makes the MySQL provider translate
/// the three reads behind them.
///
/// Two halves, for two different kinds of failure. The arithmetic is where a wrong answer is
/// plausible rather than obvious: an average that silently counts a row it could not measure, or a
/// day count that rounds up and puts a six-day call under a seven-day alert, reads as a real figure
/// and is simply wrong. The queries are where nothing fails until production — a null check on a
/// nullable column, a projection into a record constructor and an enum compared against a string
/// conversion are each translated at run time, and <c>ToQueryString</c> forces that without a
/// database the way <see cref="FieldCallQueryTests"/> does.</summary>
public class FieldCallTatTests
{
    private static AppDbContext NewContext() => new DesignTimeDbContextFactory().CreateDbContext([]);

    public FieldCallTatTests() => ShopClock.Configure(5.5);

    /// <summary>Just the WHERE clause. The SELECT list names the columns of the projection, so a bare
    /// "contains created_at" would be true of a query with no filter on it at all.</summary>
    private static string Where(string sql)
    {
        var start = sql.IndexOf("WHERE", StringComparison.Ordinal);
        if (start < 0) return "";
        var end = sql.IndexOf("ORDER BY", start, StringComparison.Ordinal);
        return end < 0 ? sql[start..] : sql[start..end];
    }

    private static string OrderBy(string sql)
    {
        var start = sql.IndexOf("ORDER BY", StringComparison.Ordinal);
        return start < 0 ? "" : sql[start..];
    }

    private static readonly DateTime Logged = new(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);

    private static FieldCallClosedRow Closed(string callNo, double hours, long engineerId = 1) =>
        new(engineerId, callNo, Logged, Logged.AddHours(hours));

    // ---------------------------------------------------------------- the threshold

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(366)]
    public void The_threshold_falls_back_to_a_week_when_nothing_usable_is_asked_for(int? requested)
    {
        // Clamped rather than refused: this arrives off a dropdown, so a value outside the range can
        // only be a stale client or a hand-made URL, and neither is worth a 400 on a read. Zero in
        // particular would make every call critical the moment it was logged, which is a label that
        // would stop meaning anything.
        FieldCallTat.CriticalDays(requested).Should().Be(FieldCallTat.DefaultCriticalDays);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(30)]
    [InlineData(365)]
    public void A_threshold_inside_the_range_is_honoured(int requested)
    {
        FieldCallTat.CriticalDays(requested).Should().Be(requested);
    }

    [Fact]
    public void The_cutoff_is_elapsed_time_and_not_a_calendar_day()
    {
        // Logged at eleven last night; it is one in the morning now. Two hours old, under a one-day
        // alert. A count of calendar days would call this "yesterday", make it a day old and flag it
        // at breakfast — which is the whole reason created_at is compared as an instant.
        var now = new DateTime(2026, 11, 7, 1, 0, 0, DateTimeKind.Utc);
        var loggedLastNight = new DateTime(2026, 11, 6, 23, 0, 0, DateTimeKind.Utc);

        var cutoff = FieldCallTat.Cutoff(now, 1);

        cutoff.Should().Be(new DateTime(2026, 11, 6, 1, 0, 0, DateTimeKind.Utc));
        loggedLastNight.Should().BeOnOrAfter(cutoff, "a two-hour-old call is not a day overdue");
    }

    // ---------------------------------------------------------------- one call

    [Fact]
    public void A_turnaround_is_hours_to_a_tenth()
    {
        // Six minutes is the finest reading the two stamps behind this can defend; anything more
        // precise is a decimal place somebody would try to interpret.
        FieldCallTat.Hours(Logged, Logged.AddMinutes(150)).Should().Be(2.5);
        FieldCallTat.Hours(Logged, Logged.AddMinutes(151)).Should().Be(2.5);
        FieldCallTat.Hours(Logged, Logged.AddDays(3)).Should().Be(72);
    }

    [Fact]
    public void A_call_logged_complete_has_a_nil_turnaround_rather_than_none()
    {
        // The create path stamps the completion from the created stamp, so the two are the same value
        // and this is the pair that reaches the figure. Zero is the right answer: the call WAS
        // finished before it was written down.
        FieldCallTat.Hours(Logged, Logged).Should().Be(0);
    }

    [Fact]
    public void A_completion_that_predates_the_logging_is_not_a_negative_turnaround()
    {
        // Should not happen - all three closing paths stamp off the clock, after the row exists - but
        // a backfilled row carries whatever the old data said, and one bad pair must not be able to
        // drag an average below zero where nobody could tell whether the report or the data was wrong.
        FieldCallTat.Hours(Logged, Logged.AddHours(-1)).Should().BeNull();
    }

    [Fact]
    public void Days_open_is_floored_so_it_never_overstates_the_wait()
    {
        // Five days and twenty hours. Rounded up this would print as six, and a six-day call sitting
        // in a five-day alert list reads as a bug in the threshold the reader just chose.
        var now = Logged.AddDays(5).AddHours(20);

        FieldCallTat.DaysOpen(Logged, now).Should().Be(5);
    }

    // ---------------------------------------------------------------- a set of calls

    [Fact]
    public void Nothing_measurable_reads_as_zero_rather_than_as_missing()
    {
        var stat = FieldCallTat.Stat([]);

        // The section still has tiles to draw, and a filter that matched nothing has a true answer.
        stat.CompletedCount.Should().Be(0);
        stat.AvgHours.Should().Be(0);
        stat.FastestCallNo.Should().BeNull();
        stat.SlowestCallNo.Should().BeNull();
    }

    [Fact]
    public void The_average_and_the_two_extremes_describe_the_same_calls()
    {
        var stat = FieldCallTat.Stat([
            Closed("CAL00001", 2.5),
            Closed("CAL00002", 24),
            Closed("CAL00003", 6),
        ]);

        stat.CompletedCount.Should().Be(3);
        stat.AvgHours.Should().Be(10.8);          // 32.5 / 3, to a tenth
        stat.MinHours.Should().Be(2.5);
        stat.MaxHours.Should().Be(24);
        // The extremes come back as call numbers so the office can open the record rather than go
        // looking for whichever call took a day.
        stat.FastestCallNo.Should().Be("CAL00001");
        stat.SlowestCallNo.Should().Be("CAL00002");
    }

    [Fact]
    public void An_unmeasurable_row_is_left_out_of_the_count_the_average_is_over()
    {
        var stat = FieldCallTat.Stat([
            Closed("CAL00001", 2),
            Closed("CAL00002", 4),
            new(1, "CAL00003", Logged, Logged.AddHours(-5)),   // unreadable as a duration
        ]);

        // Counting it would make the average 2 over three calls instead of 3 over two, which is the
        // quiet kind of wrong: the figure stays plausible and nothing says a row was dropped.
        stat.CompletedCount.Should().Be(2);
        stat.AvgHours.Should().Be(3);
        stat.SlowestCallNo.Should().Be("CAL00002");
    }

    // ---------------------------------------------------------------- the three reads

    [Fact]
    public void The_closed_read_selects_on_the_completion_stamp_and_not_on_the_status()
    {
        using var db = NewContext();

        var sql = FieldCallsEndpoints.ClosedQuery(db, null, null, null, 100).ToQueryString();

        sql.Should().Contain("completed_at");
        Where(sql).Should().Contain("IS NOT NULL");
        // A Complete call carrying no completion moment has no turnaround, so it belongs out of the
        // average rather than in it at zero - which is what filtering on the status would do.
        Where(sql).Should().NotContain("call_status");
        sql.Should().Contain("LIMIT");
    }

    [Fact]
    public void The_closed_read_keeps_the_recent_calls_when_its_window_bites()
    {
        using var db = NewContext();

        var sql = FieldCallsEndpoints.ClosedQuery(db, null, null, null, 100).ToQueryString();

        // A turnaround figure is about how the team is working now, so a window that does bite should
        // drop the founding calls and not this month's.
        OrderBy(sql).Should().Contain("DESC");
    }

    [Fact]
    public void The_open_read_translates_to_the_stored_status_name()
    {
        using var db = NewContext();

        var sql = FieldCallsEndpoints.OpenQuery(db, null, null, null, 100).ToQueryString();

        // call_status is a string conversion, so a translation that sent an ordinal would match
        // nothing and report an empty backlog - the one answer this section must never invent.
        Where(sql).Should().Contain("call_status");
        Where(sql).Should().Contain("Incomplete");
        // Oldest first, the opposite of the closed read: if this window bites, the rows worth keeping
        // are the ones that have been waiting longest.
        OrderBy(sql).Should().Contain("created_at");
        OrderBy(sql).Should().NotContain("DESC");
    }

    [Fact]
    public void The_alert_read_filters_on_how_old_the_call_is()
    {
        using var db = NewContext();

        var sql = FieldCallsEndpoints.CriticalQuery(
            db, null, null, null, new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc), 500)
            .ToQueryString();

        var where = Where(sql);
        where.Should().Contain("call_status");
        where.Should().Contain("Incomplete");
        // Age is measured from the day the call was SAVED, which is created_at. The received date is
        // what the office typed and can be any day at all; the save stamp is when the clock started.
        where.Should().Contain("created_at");
        OrderBy(sql).Should().Contain("created_at");
        OrderBy(sql).Should().NotContain("DESC");
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("open")]
    [InlineData("critical")]
    public void Every_read_shares_the_registers_own_received_date_bounds(string read)
    {
        using var db = NewContext();
        var from = new DateTime(2026, 10, 1);
        var to = new DateTime(2026, 10, 31);

        var sql = read switch
        {
            "closed" => FieldCallsEndpoints.ClosedQuery(db, 7, from, to, 100).ToQueryString(),
            "open" => FieldCallsEndpoints.OpenQuery(db, 7, from, to, 100).ToQueryString(),
            _ => FieldCallsEndpoints.CriticalQuery(db, 7, from, to, DateTime.UtcNow, 500).ToQueryString(),
        };

        var where = Where(sql);
        // The same column the register itself filters on, so this section and the Calls tab beside it
        // answer for the same set of calls. Filtering on created_at here would make the two disagree
        // over every call that was keyed in late.
        where.Should().Contain("call_received_date");
        where.Should().Contain(">=");
        where.Should().Contain("<=");
        where.Should().Contain("engineer_id");
    }

    [Fact]
    public void A_zoned_bound_is_taken_onto_the_shop_day_before_it_reaches_the_sql()
    {
        using var db = NewContext();

        // Midnight IST on the 21st, as a .NET client serialises DateTime.Today and a UTC server reads
        // it back. Filtered raw, the window would start on the evening of the 20th - the same
        // off-by-a-day the register's own bounds go through ShopClock to avoid.
        var sql = FieldCallsEndpoints.ClosedQuery(
            db, null, new DateTime(2026, 10, 20, 18, 30, 0, DateTimeKind.Utc), null, 100).ToQueryString();

        sql.Should().Contain("2026-10-21");
        sql.Should().NotContain("2026-10-20");
    }
}
