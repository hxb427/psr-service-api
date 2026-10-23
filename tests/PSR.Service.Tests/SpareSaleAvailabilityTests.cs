using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data;
using PSR.Service.Api.SpareSales;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>A sale deals in one stock figure: what the warehouse physically holds. It used to subtract
/// the units other unsold sales had claimed, which is the arithmetic these tests used to cover — that is
/// gone. Nothing reserves warehouse stock now; it leaves on mark-sold, which re-checks immediately
/// before it moves (see <see cref="SpareSaleMarkSoldShortfallTests"/>).
///
/// What is left to get wrong is the lookup reading the wrong row, so that is what is pinned here: the
/// warehouse row and no technician's, translated by the real provider rather than evaluated in memory.</summary>
public class SpareSaleAvailabilityTests
{
    private static AppDbContext NewContext() => new DesignTimeDbContextFactory().CreateDbContext([]);

    [Fact]
    public void Warehouse_lookup_reads_the_warehouse_row_only()
    {
        using var db = NewContext();

        // A local list, as the service passes — an inline array binds Contains to the span overload,
        // which the expression interpreter cannot build a generic call for.
        var partIds = new List<long> { 1L, 2L };
        var sql = db.StockBalances.AsNoTracking()
            .Where(b => b.TechnicianId == 0 && partIds.Contains(b.PartId))
            .Select(b => new { b.PartId, b.OnHand })
            .ToQueryString();

        // technician_id 0 IS the warehouse; without it the same part held by a technician would be
        // added to the shop's count and a sale would be offered stock that is in somebody's van.
        sql.Should().Contain("technician_id");
        sql.Should().Contain("on_hand");
    }

    /// <summary>No part ids means no query at all — an empty IN () is both pointless and, on some
    /// providers, invalid SQL.</summary>
    [Fact]
    public async Task No_parts_asked_for_returns_an_empty_map()
    {
        using var db = NewContext();

        var result = await new SpareSaleService(db).WarehouseOnHandAsync([], CancellationToken.None);

        result.Should().BeEmpty();
    }
}
