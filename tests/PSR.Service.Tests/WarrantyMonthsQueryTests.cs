using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data;
using PSR.Service.Api.MachineTests;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>The warranty verdict is InvDate + warranty months, so the months figure decides whether a
/// machine is reported in or out of warranty. When the caller cannot name a dealer — the dashboard's
/// quick check, the global-search serial filter, a direct-customer inward — the months come from the
/// dealer on the serial's most recent job, and "most recent" has to survive translation to SQL. An
/// ordering that the provider drops would silently read an arbitrary older job's dealer and quote the
/// wrong term. ToQueryString makes the provider translate without a live server.</summary>
public class WarrantyMonthsQueryTests
{
    private static AppDbContext NewContext() => new DesignTimeDbContextFactory().CreateDbContext([]);

    [Fact]
    public void Dealer_months_for_serial_query_translates_to_sql()
    {
        using var db = NewContext();

        var sql = MachineTestsEndpoints.DealerMonthsForSerialQuery(db, "SN-123").Take(1).ToQueryString();

        sql.Should().Contain("JOIN");
        // The newest job must be chosen by the database, not by whichever row it happens to return.
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("DESC");
        sql.Should().Contain("LIMIT");
    }

    [Fact]
    public void Dealer_months_for_serial_query_excludes_deleted_jobs()
    {
        using var db = NewContext();

        var sql = MachineTestsEndpoints.DealerMonthsForSerialQuery(db, "SN-123").ToQueryString();

        // A job deleted as a mis-booking must not go on answering for the serial's warranty.
        sql.Should().Contain("is_deleted");
    }
}
