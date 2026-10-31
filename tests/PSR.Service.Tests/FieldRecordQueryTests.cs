using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Common;
using PSR.Service.Api.Data;
using PSR.Service.Api.Services;
using PSR.Service.Api.Stock;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>Makes the MySQL provider translate the visit and sale list queries, and pins down the
/// one thing about them that no compiler can see: the date window.
///
/// The call register's bounds compare against a `date` column, so passing the shop day straight in
/// is correct there (<see cref="FieldCallQueryTests"/>). These two filter created_at, which is an
/// instant stored in UTC, and a shop day's midnight is five and a half hours away from the
/// boundary it looks like. Getting that wrong does not fail a build or throw — it quietly returns
/// an evening's work under the wrong date, which is exactly the class of bug that only ever shows
/// up as "the report is missing a job".</summary>
public class FieldRecordQueryTests
{
    private static AppDbContext NewContext() => new DesignTimeDbContextFactory().CreateDbContext([]);

    /// <summary>Just the WHERE clause. The SELECT list names every column on the entity, so a plain
    /// "contains technician_id" is true of a query with no filters at all.
    ///
    /// Structure only. A date bound is sent as a PARAMETER, and <c>ToQueryString</c> prints the
    /// values in a "SET @__fu_0 = TIMESTAMP '...'" preamble above the statement — so an assertion
    /// about which instant was asked for has to be made against the whole string, and only the
    /// columns and the operators can be asserted here.</summary>
    private static string Where(string sql)
    {
        var start = sql.IndexOf("WHERE", StringComparison.Ordinal);
        if (start < 0) return "";
        var end = sql.IndexOf("ORDER BY", start, StringComparison.Ordinal);
        return end < 0 ? sql[start..] : sql[start..end];
    }

    public FieldRecordQueryTests() => ShopClock.Configure(5.5);

    // ---------------------------------------------------------------- visits

    [Fact]
    public void The_unfiltered_visit_list_translates_newest_first_and_limited()
    {
        using var db = NewContext();

        var sql = FieldOpsEndpoints.VisitQuery(db, null, null, null, 100).ToQueryString();

        sql.Should().Contain("field_services");
        sql.Should().Contain("DESC");
        sql.Should().Contain("LIMIT");
        // No filters asked for, so none applied. A window is the absence of a filter, not a filter
        // on everything.
        Where(sql).Should().NotContain("created_at");
    }

    [Fact]
    public void Scoping_a_visit_list_to_one_technician_translates()
    {
        using var db = NewContext();

        var where = Where(FieldOpsEndpoints.VisitQuery(db, 7, null, null, 100).ToQueryString());

        where.Should().Contain("technician_id");
    }

    [Fact]
    public void A_visit_date_window_becomes_a_half_open_range_on_created_at()
    {
        using var db = NewContext();

        var where = Where(FieldOpsEndpoints.VisitQuery(
            db, null, new DateTime(2026, 10, 1), new DateTime(2026, 10, 31), 100).ToQueryString());

        where.Should().Contain("created_at");
        where.Should().Contain(">=");
        // Half open, deliberately: the upper bound is the start of the next day rather than an
        // inclusive instant, because "<= 23:59:59" drops whatever landed in the final second.
        where.Should().Contain("<");
        where.Should().NotContain("<=");
    }

    [Fact]
    public void A_visit_window_is_the_shop_day_in_utc_not_its_midnight()
    {
        using var db = NewContext();

        // The whole of 1 October, as the office means it. In Kochi that day starts at 18:30 UTC on
        // 30 September and ends at 18:30 UTC on 1 October.
        var sql = FieldOpsEndpoints.VisitQuery(
            db, null, new DateTime(2026, 10, 1), new DateTime(2026, 10, 1), 100).ToQueryString();

        sql.Should().Contain("2026-09-30 18:30:00");
        sql.Should().Contain("2026-10-01 18:30:00");
        // Filtered against the day's own midnight instead, a visit filed at seven in the evening on
        // the 1st would be stored as 13:30 UTC, fall outside "< 2026-10-02 00:00" only by luck, and
        // a visit filed at seven on the EVENING OF THE 30TH would be dragged in. Naming the two
        // midnights here is what stops that translation coming back.
        sql.Should().NotContain("2026-10-01 00:00:00");
        sql.Should().NotContain("2026-10-02 00:00:00");
    }

    [Fact]
    public void A_zoned_visit_bound_is_taken_onto_the_shop_day_first()
    {
        using var db = NewContext();

        // Midnight IST on the 21st, as a .NET client serialises DateTime.Today and a UTC server
        // reads it back. It has to mean the 21st, not the evening of the 20th.
        var sql = FieldOpsEndpoints.VisitQuery(
            db, null, new DateTime(2026, 10, 20, 18, 30, 0, DateTimeKind.Utc), null, 100).ToQueryString();

        // Which is the same instant it arrived as: midnight IST on the 21st IS 18:30 UTC on the
        // 20th. Read raw as a shop day it would have been the 20th, and the window would then have
        // started at 18:30 UTC on the 19th — a whole day early.
        sql.Should().Contain("2026-10-20 18:30:00");
        sql.Should().NotContain("2026-10-19");
    }

    [Fact]
    public void One_visit_bound_on_its_own_translates()
    {
        using var db = NewContext();

        var fromOnly = Where(FieldOpsEndpoints.VisitQuery(
            db, null, new DateTime(2026, 10, 1), null, 100).ToQueryString());
        var toOnly = Where(FieldOpsEndpoints.VisitQuery(
            db, null, null, new DateTime(2026, 10, 31), 100).ToQueryString());

        // An open-ended range is a range. Either half missing must not take the other half with it.
        fromOnly.Should().Contain(">=");
        fromOnly.Should().NotContain("<");
        toOnly.Should().Contain("<");
        toOnly.Should().NotContain(">=");
    }

    // ---------------------------------------------------------------- sales

    [Fact]
    public void The_unfiltered_sale_list_translates_newest_first_and_limited()
    {
        using var db = NewContext();

        var sql = FieldOpsEndpoints.SaleQuery(db, null, null, null, 100).ToQueryString();

        sql.Should().Contain("field_sales");
        sql.Should().Contain("DESC");
        sql.Should().Contain("LIMIT");
        Where(sql).Should().NotContain("created_at");
    }

    [Fact]
    public void A_sale_date_window_is_the_same_shop_day_window_as_a_visit()
    {
        using var db = NewContext();

        var sql = FieldOpsEndpoints.SaleQuery(
            db, 7, new DateTime(2026, 10, 1), new DateTime(2026, 10, 1), 100).ToQueryString();

        Where(sql).Should().Contain("technician_id");
        sql.Should().Contain("2026-09-30 18:30:00");
        sql.Should().Contain("2026-10-01 18:30:00");
    }

    // ---------------------------------------------------------------- the window itself

    [Fact]
    public void The_shop_day_window_covers_the_whole_of_both_boundary_days()
    {
        ShopClock.Configure(5.5);

        var (from, to) = ShopClock.UtcWindow(new DateTime(2026, 10, 1), new DateTime(2026, 10, 31));

        from.Should().Be(new DateTime(2026, 9, 30, 18, 30, 0));
        to.Should().Be(new DateTime(2026, 10, 31, 18, 30, 0));

        // The first and last instants of the range, stored as this API stores them: 00:00 and 23:59
        // shop time on the boundary days. Both have to fall inside, or a range of "the 1st to the
        // 31st" loses the morning of the first day and the evening of the last.
        var firstInstant = new DateTime(2026, 10, 1).AddHours(-5.5);
        var lastInstant = new DateTime(2026, 10, 31, 23, 59, 59).AddHours(-5.5);
        firstInstant.Should().BeOnOrAfter(from!.Value);
        lastInstant.Should().BeBefore(to!.Value);

        // And the instant just before the range starts must not.
        firstInstant.AddSeconds(-1).Should().BeBefore(from.Value);
    }

    [Fact]
    public void A_window_with_no_bounds_asks_for_nothing()
    {
        var (from, to) = ShopClock.UtcWindow(null, null);

        from.Should().BeNull();
        to.Should().BeNull();
    }
}
