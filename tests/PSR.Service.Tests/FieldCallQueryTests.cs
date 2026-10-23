using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Common;
using PSR.Service.Api.Data;
using PSR.Service.Api.Services;
using PSR.Service.Api.Stock;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>Makes the MySQL provider translate the two queries behind the call register, here rather
/// than in front of whoever opens the page.
///
/// Neither of these can fail at build time and neither is covered by a rule test. The register's
/// filters go through a string enum conversion and compare against a `date` column; the address
/// suggestions are a DISTINCT over a projection that is then ordered and limited, which is exactly
/// the shape EF is free to reorder. <c>ToQueryString</c> forces the whole translation without a
/// database, the way <see cref="ServiceRegisterStatusDateQueryTests"/> does.</summary>
public class FieldCallQueryTests
{
    private static AppDbContext NewContext() => new DesignTimeDbContextFactory().CreateDbContext([]);

    /// <summary>Just the WHERE clause, which is the only part of the statement a filter assertion
    /// can be made against. The SELECT list names every column on the entity, so a plain
    /// "contains call_type" is true of a query with no filters at all, and a plain "does not
    /// contain" can never pass.</summary>
    private static string Where(string sql)
    {
        var start = sql.IndexOf("WHERE", StringComparison.Ordinal);
        if (start < 0) return "";
        var end = sql.IndexOf("ORDER BY", start, StringComparison.Ordinal);
        return end < 0 ? sql[start..] : sql[start..end];
    }

    public FieldCallQueryTests() => ShopClock.Configure(5.5);

    // ---------------------------------------------------------------- the register

    [Fact]
    public void The_unfiltered_register_translates_and_reads_as_a_calendar()
    {
        using var db = NewContext();

        var sql = FieldCallsEndpoints
            .RegisterQuery(db, null, null, null, null, null, 100).ToQueryString();

        sql.Should().Contain("field_calls");
        // Newest day first, with the id breaking ties inside a day. Without the second key MySQL
        // may hand back the same date's calls in a different order on each read, and a list that
        // reshuffles under the reader is worse than one sorted wrongly.
        sql.Should().Contain("call_received_date` DESC");
        sql.Should().Contain("LIMIT");
    }

    [Fact]
    public void Scoping_to_one_engineer_translates()
    {
        using var db = NewContext();

        var sql = FieldCallsEndpoints
            .RegisterQuery(db, 7, null, null, null, null, 100).ToQueryString();

        sql.Should().Contain("engineer_id");
    }

    [Fact]
    public void The_enum_filters_translate_to_the_stored_names()
    {
        using var db = NewContext();

        var where = Where(FieldCallsEndpoints
            .RegisterQuery(db, null, "DealerVisit", "Complete", null, null, 100).ToQueryString());

        // The columns are string conversions, so the filter has to arrive as the enum NAME. A
        // translation that sent an ordinal would match nothing and report an empty register.
        where.Should().Contain("call_type");
        where.Should().Contain("call_status");
        where.Should().Contain("DealerVisit");
        where.Should().Contain("Complete");
    }

    [Fact]
    public void An_unparseable_filter_does_not_reach_the_sql()
    {
        using var db = NewContext();

        var where = Where(FieldCallsEndpoints
            .RegisterQuery(db, null, "WarrantyClaim", "nonsense", null, null, 100).ToQueryString());

        // Dropped rather than refused, and dropped means absent from the WHERE clause — not turned
        // into a filter on the default, which would quietly show the wrong slice of the register.
        where.Should().NotContain("call_type");
        where.Should().NotContain("call_status");
    }

    [Fact]
    public void Both_date_bounds_translate_and_are_inclusive()
    {
        using var db = NewContext();

        var where = Where(FieldCallsEndpoints.RegisterQuery(
            db, null, null, null,
            new DateTime(2026, 10, 1), new DateTime(2026, 10, 31), 100).ToQueryString());

        where.Should().Contain("call_received_date");
        where.Should().Contain(">=");
        where.Should().Contain("<=");
        // The day the office means, not created_at — a call keyed in three days late belongs to the
        // day it arrived, so created_at must not be what the bounds are applied to.
        where.Should().NotContain("created_at");
    }

    [Fact]
    public void A_zoned_bound_is_taken_onto_the_shop_day_before_it_reaches_the_sql()
    {
        using var db = NewContext();

        // Midnight IST on the 21st, as a .NET client serialises DateTime.Today and a UTC server
        // reads it back. Filtered raw this would cut the register at the evening of the 20th and
        // quietly include a day nobody asked for.
        var sql = FieldCallsEndpoints.RegisterQuery(
            db, null, null, null,
            new DateTime(2026, 10, 20, 18, 30, 0, DateTimeKind.Utc), null, 100).ToQueryString();

        sql.Should().Contain("2026-10-21");
        sql.Should().NotContain("2026-10-20");
    }

    // ---------------------------------------------------------------- address suggestions

    [Fact]
    public void The_address_suggestions_translate_to_a_distinct_ordered_limited_select()
    {
        using var db = NewContext();

        var sql = CustomersEndpoints.AddressQuery(db, "Pattom", 20).ToQueryString();

        sql.Should().Contain("customers");
        sql.Should().Contain("address");
        // All three have to survive together. DISTINCT is what stops one street appearing four
        // times because four accounts sit on it; the ORDER BY is what makes the list stable; the
        // LIMIT is what keeps it a suggestion list.
        sql.Should().Contain("DISTINCT");
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("LIMIT");
    }

    [Fact]
    public void The_address_suggestions_skip_inactive_accounts_and_empty_addresses()
    {
        using var db = NewContext();

        var sql = CustomersEndpoints.AddressQuery(db, "Pattom", 20).ToQueryString();

        sql.Should().Contain("is_active");
        // An empty address would otherwise match every search and sit at the top of every list.
        sql.Should().Contain("address` <> ''");
    }
}
