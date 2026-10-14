using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data;
using PSR.Service.Api.Reports;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>The consumption report's queries, made to translate.
///
/// Same reason as ServiceRegisterStatusDateQueryTests: these are the only part of the report that can
/// be wrong in a way the rule tests cannot see, because they fail in the PROVIDER rather than in the
/// arithmetic. A shape EF or MySQL will not take compiles perfectly and then throws in front of
/// whoever clicked the row. ToQueryString makes the provider translate it here instead.
///
/// Two shapes are at risk. The movement filter is an Expression shared by both halves of the report
/// and has to reach the database rather than be evaluated client-side — a filter that falls back to
/// the client fetches the whole ledger and still returns the right answer, so nothing fails and the
/// page merely gets slower every month. The five document lookups each join a NULLABLE reference
/// column to a non-nullable key with two left-joined party tables behind it, which is exactly the
/// kind of shape that translates on one provider and not another.
///
/// No server listens and no database is touched: ToQueryString asks for the SQL, not the rows.</summary>
public class ConsumptionQueryTranslationTests
{
    private static AppDbContext NewContext() => new DesignTimeDbContextFactory().CreateDbContext([]);

    private static readonly DateTime From = new(2025, 10, 31, 18, 30, 0);
    private static readonly DateTime To = new(2026, 10, 31, 18, 30, 0);

    [Fact]
    public void The_movement_filter_reaches_the_database()
    {
        using var db = NewContext();

        var sql = ReportsEndpoints.ConsumingMovements(db, From, To).ToQueryString();

        sql.Should().Contain("stock_movements");
        // The window has to be in the WHERE, not applied after the rows arrive.
        sql.Should().Contain("created_at");
        // ...and so does the movement-type filter, or a year of issues and receipts comes back to be
        // thrown away one row at a time.
        sql.Should().Contain("movement_type");
    }

    /// <summary>Every document lookup, including the sale-return one that hops through the sale to
    /// reach the party. Driven off the production list so a sixth reference type added later is
    /// covered by this test the day it is written rather than the day somebody notices.</summary>
    [Fact]
    public void Every_document_lookup_translates_to_sql()
    {
        using var db = NewContext();

        var queries = ReportsEndpoints.DocumentQueries(db, partId: 42, From, To);

        queries.Should().HaveCount(5, "one per reference type a consuming movement can carry");
        foreach (var (type, query) in queries)
        {
            var translate = () => query.ToQueryString();

            translate.Should().NotThrow($"the {type} drill-down fails in front of the user otherwise");
            // A LEFT JOIN is the point of the party lookups: a job with no customer and no dealer on it
            // must still return its number, not drop off the list.
            translate().Should().Contain("JOIN");
        }
    }

    /// <summary>The reference type has to be part of the join, not just the key. Job 412 and field sale
    /// 412 are different documents, and a lookup that matched on the id alone would label a field
    /// sale's movement with a service job's number — a wrong answer that reads as a right one.</summary>
    [Fact]
    public void A_document_lookup_matches_on_the_reference_type_as_well_as_the_id()
    {
        using var db = NewContext();

        foreach (var (type, query) in ReportsEndpoints.DocumentQueries(db, partId: 42, From, To))
        {
            var sql = query.ToQueryString();

            sql.Should().Contain("reference_type", $"{type} would otherwise match any document with that id");
            sql.Should().Contain("reference_id");
            sql.Should().Contain("part_id", $"{type} would otherwise read other parts' movements");
        }
    }
}
