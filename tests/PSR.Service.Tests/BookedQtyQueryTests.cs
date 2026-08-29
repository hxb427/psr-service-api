using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data;
using PSR.Service.Api.Services;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>Adding a component checks what the technician still has spare, which means a join and a
/// SUM. An untranslatable one would only blow up when a technician added a part on the real database,
/// so ToQueryString forces the provider to translate it here instead (the design-time factory pins the
/// MySQL version).</summary>
public class BookedQtyQueryTests
{
    private static AppDbContext NewContext() => new DesignTimeDbContextFactory().CreateDbContext([]);

    [Fact]
    public void Booked_quantity_query_translates_to_sql()
    {
        using var db = NewContext();

        var sql = ServicesEndpoints.BookedQtyQuery(db, partId: 7, technicianId: 42).ToQueryString();

        sql.Should().Contain("service_lines");
        sql.Should().Contain("services");
        // One narrow column out, so the caller's SUM runs in SQL rather than over materialised rows.
        sql.Should().Contain("qty");
    }

    [Fact]
    public void Booked_quantity_counts_only_unconsumed_lines()
    {
        using var db = NewContext();

        var sql = ServicesEndpoints.BookedQtyQuery(db, partId: 7, technicianId: 42).ToQueryString();

        // Completion is what consumes stock, so only jobs still in service hold uncounted quantity.
        sql.Should().Contain("InService");
        // Deleted jobs hold nothing, and a service-charge line has no part behind it.
        sql.Should().Contain("is_deleted");
        sql.Should().Contain("Component");
        sql.Should().Contain("Replacement");
    }
}
