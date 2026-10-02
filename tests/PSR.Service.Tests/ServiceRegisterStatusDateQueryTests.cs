using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data;
using PSR.Service.Api.Reports;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>The register's completed / dispatched / stocked / replaced / written-off dates are not
/// columns on a job — a job stores only the status it is in now — so they are read from the status
/// history and joined back to the very query that chose the rows, paging and all.
///
/// Two ways that shape can break, both only on the real database: an <c>ids.Contains()</c> trips the
/// funcletizer bug this codebase avoids everywhere, and MySQL rejects a LIMITed subquery under IN
/// ("This version of MySQL doesn't yet support 'LIMIT &amp; IN/ALL/ANY/SOME subquery'") — which is
/// exactly what the right-hand side of this join is once a page or a 10,000-row export cap is applied.
/// ToQueryString makes the provider translate it here instead of in front of someone exporting.</summary>
public class ServiceRegisterStatusDateQueryTests
{
    private static AppDbContext NewContext() => new DesignTimeDbContextFactory().CreateDbContext([]);

    /// <summary>The ids query the endpoint hands over: the register's own filtered, ordered, paged
    /// selection — not a list read back from the first round trip.</summary>
    private static IQueryable<long> PagedIds(AppDbContext db, int page = 2, int size = 50) =>
        db.Services.AsNoTracking()
            .Where(s => !s.IsDeleted)
            .OrderByDescending(s => s.Id)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(s => s.Id);

    [Fact]
    public void Status_date_query_translates_to_sql()
    {
        using var db = NewContext();

        var sql = ReportsEndpoints.StatusDateQuery(db, PagedIds(db)).ToQueryString();

        sql.Should().Contain("service_status_history");
        sql.Should().Contain("services");
        // Three narrow columns out: the grouping to one date per (job, status) runs over those, not
        // over whole history rows.
        sql.Should().Contain("service_id");
        sql.Should().Contain("to_status");
        sql.Should().Contain("changed_at");
    }

    [Fact]
    public void Status_date_query_reaches_the_paged_rows_through_a_join_not_an_in_subquery()
    {
        using var db = NewContext();

        var sql = ReportsEndpoints.StatusDateQuery(db, PagedIds(db)).ToQueryString();

        // The paging has to survive — otherwise every job's history comes back for one page of rows.
        sql.Should().Contain("LIMIT");
        sql.Should().Contain("OFFSET");
        // ...and it has to arrive by JOIN. `IN (SELECT ... LIMIT ...)` parses here and fails on MySQL.
        sql.Should().Contain("JOIN");
        sql.Should().NotContain("IN (SELECT");
    }

    [Fact]
    public void Status_date_query_asks_for_every_status_the_register_dates()
    {
        using var db = NewContext();

        var sql = ReportsEndpoints.StatusDateQuery(db, PagedIds(db)).ToQueryString();

        // The day the work finished, plus each of the four ways a job closes. A status missing here
        // is a column that silently exports blank.
        sql.Should().Contain("Completed");
        sql.Should().Contain("Dispatched");
        sql.Should().Contain("Stocked");
        sql.Should().Contain("Replaced");
        sql.Should().Contain("TotalLoss");
        // Older records stopped at the retired spelling of Completed and still have a date to show.
        sql.Should().Contain("PendingDispatch");
    }
}
