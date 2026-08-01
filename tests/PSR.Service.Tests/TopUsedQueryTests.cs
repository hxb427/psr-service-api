using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data;
using PSR.Service.Api.Reference;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>The top-used ranking groups in SQL, so an untranslatable GroupBy would only blow up when a
/// technician opened the add-line dialog against the real database. ToQueryString forces the provider to
/// translate without needing a live server (the design-time factory pins the MySQL version), which is
/// exactly the failure these tests exist to catch early.</summary>
public class TopUsedQueryTests
{
    private static AppDbContext NewContext() => new DesignTimeDbContextFactory().CreateDbContext([]);

    [Fact]
    public void Top_parts_query_translates_to_sql()
    {
        using var db = NewContext();

        var sql = TopUsedEndpoints.TopPartsQuery(db, technicianId: 42).Take(10).ToQueryString();

        sql.Should().Contain("GROUP BY");
        sql.Should().Contain("COUNT(");
        // The ranking must be applied by the database, not after Take truncates an arbitrary 10 rows.
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("DESC");
        sql.Should().Contain("LIMIT");
    }

    [Fact]
    public void Top_charges_query_translates_to_sql()
    {
        using var db = NewContext();

        var sql = TopUsedEndpoints.TopChargesQuery(db, technicianId: 42).Take(10).ToQueryString();

        sql.Should().Contain("GROUP BY");
        sql.Should().Contain("COUNT(");
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("DESC");
        sql.Should().Contain("LIMIT");
    }

    /// <summary>Deleted jobs and other technicians' jobs must never feed someone's personal ranking,
    /// and the reference-row join must exclude deactivated parts/charges from taking a slot.</summary>
    [Fact]
    public void Ranking_is_scoped_to_the_caller_and_excludes_deleted_and_inactive_rows()
    {
        using var db = NewContext();

        var partsSql = TopUsedEndpoints.TopPartsQuery(db, technicianId: 42).ToQueryString();
        var chargesSql = TopUsedEndpoints.TopChargesQuery(db, technicianId: 42).ToQueryString();

        foreach (var sql in new[] { partsSql, chargesSql })
        {
            sql.Should().Contain("technician_id");
            sql.Should().Contain("is_deleted");
            sql.Should().Contain("is_active");
        }
    }
}
