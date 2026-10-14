using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Stock;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>/serials/available is built from an array of statuses the shipment may contain, and
/// <c>status</c> is persisted through a value converter. That combination is the one shape EF can
/// fail to translate, and when it does it throws while *compiling* the query — so the endpoint
/// returns 500 with no bad row involved and nothing in the data to point at.
///
/// These force translation without a database: ToQueryString compiles the query and renders SQL,
/// so a translation failure surfaces here rather than in production.</summary>
public class AvailableSerialsQueryTests
{
    private static AppDbContext NewContext()
    {
        // Never opened. Pomelo only needs a declared server version to build its SQL generator,
        // which is all query compilation touches.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql("Server=localhost;Database=none;Uid=none;Pwd=none;",
                      ServerVersion.Parse("8.0.36-mysql"))
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>Mirrors SerialService.AvailableForTechnicianAsync exactly.</summary>
    private static IQueryable<ComponentSerial> AvailableQuery(
        AppDbContext db, long technicianId, long? partId, StockReturnKind? returnKind)
    {
        // Uses the production constants throughout — an array literal here would reintroduce the
        // very span-overload binding the fix removes, and the test would stop guarding anything.
        var allowed = SerialService.ShippableFor(returnKind ?? StockReturnKind.GoodStock);

        var q = db.ComponentSerials.AsNoTracking()
            .Where(c => c.OwnerType == SerialOwnerType.Technician && c.TechnicianId == technicianId
                     && allowed.Contains(c.Status));
        if (partId is { } pid) q = q.Where(c => c.PartId == pid);
        return q.OrderBy(c => c.SerialNumber);
    }

    [Fact]
    public void Faulty_list_translates_to_sql()
    {
        // Two allowed statuses — the list the Return-shipments screen asks for.
        using var db = NewContext();
        var sql = AvailableQuery(db, 42, null, StockReturnKind.Faulty).ToQueryString();
        sql.Should().Contain("component_serials");
    }

    [Fact]
    public void Good_stock_list_translates_to_sql()
    {
        // One allowed status. Worth asserting separately: a single-element collection can take a
        // different path through EF than a multi-element one, so one working says nothing about
        // the other.
        using var db = NewContext();
        var sql = AvailableQuery(db, 42, null, StockReturnKind.GoodStock).ToQueryString();
        sql.Should().Contain("component_serials");
    }

    [Fact]
    public void Faulty_list_filtered_by_part_translates_to_sql()
    {
        using var db = NewContext();
        var sql = AvailableQuery(db, 42, 7, StockReturnKind.Faulty).ToQueryString();
        sql.Should().Contain("component_serials");
    }

    [Fact]
    public void Default_list_translates_to_sql()
    {
        using var db = NewContext();
        var sql = AvailableQuery(db, 42, null, null).ToQueryString();
        sql.Should().Contain("component_serials");
    }
}
