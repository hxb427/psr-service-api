using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Reports;
using PSR.Service.Api.Stock;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>
/// The shelf is a pool of its own, and these cover the two things that makes true: a unit kept off a
/// finished job adds nothing to the warehouse count of its PS code, and an issue filled off the shelf
/// takes nothing off it either.
///
/// Why it matters commercially rather than only in the books: crediting the warehouse made a serviced
/// machine indistinguishable from one off a supplier's invoice. Same number, same code - and that
/// number is what gets reordered against, sold across the counter and quoted to a customer as new
/// stock. The shop keeps serviced units for a narrower purpose, so they are counted separately.
///
/// No database is opened, and that shapes what is checked where. The balance writes are raw SQL, so
/// the ledger can only be CALLED on the paths that write no balance - which, happily, are the shelf
/// paths this is about. Everything else is asserted against
/// <see cref="ReportsEndpoints.WarehouseDelta"/>, the one rule every warehouse figure in the app is
/// derived from, so a row's effect on the warehouse is pinned whether or not a server is reachable.
/// </summary>
public class ShelfPoolTests
{
    private const long UserId = 7;
    private const long PartId = 5;

    /// <summary>Never opened. Pomelo only needs a declared server version to build its SQL
    /// generator, and nothing here executes.</summary>
    private static AppDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql("Server=localhost;Database=none;Uid=none;Pwd=none;",
                      ServerVersion.Parse("8.0.36-mysql"))
            .Options;
        return new AppDbContext(options);
    }

    private static List<StockMovement> Pending(AppDbContext db) =>
        db.ChangeTracker.Entries<StockMovement>().Select(e => e.Entity).ToList();

    // ------------------------------------------------------------------ keeping a unit

    /// <summary>Shelving a unit writes one row, it is a shelf row, and it is the whole of it. The
    /// line this replaced was a Receipt for one unit, which is what put serviced machines into
    /// purchased stock.</summary>
    [Fact]
    public void Shelving_a_unit_writes_a_shelf_row_and_no_receipt()
    {
        using var db = NewContext();
        var ledger = new StockLedgerService(db);

        ledger.ShelveUnit(PartId, UserId, "SERVICE", 11, "SN-1", "Kept from service");

        var rows = Pending(db);
        rows.Should().HaveCount(1);
        rows[0].MovementType.Should().Be(MovementType.ShelfIn);
        rows[0].Quantity.Should().Be(1);
        rows[0].ShelfQty.Should().Be(1, "the whole of it went on the shelf");
        rows.Should().NotContain(m => m.MovementType == MovementType.Receipt);
    }

    /// <summary>And the row moves no warehouse figure, which is the requirement stated as the reports
    /// read it: WarehouseDelta is what every warehouse number in the app is derived from.</summary>
    [Fact]
    public void Shelving_a_unit_moves_no_warehouse_quantity()
    {
        ReportsEndpoints.WarehouseDelta(MovementType.ShelfIn, 1, shelfQty: 1).Should().Be(0);
    }

    /// <summary>The serial is on the row. A shelf row with no serial on it describes a unit nobody
    /// can identify afterwards, which is the same reason stocking a job now requires one.</summary>
    [Fact]
    public void A_shelf_row_names_the_unit_and_the_job()
    {
        using var db = NewContext();
        new StockLedgerService(db).ShelveUnit(PartId, UserId, "SERVICE", 11, "SN-1", "Kept from service");

        var row = Pending(db).Single();
        row.SerialNo.Should().Be("SN-1");
        row.ReferenceType.Should().Be("SERVICE");
        row.ReferenceId.Should().Be(11);
    }

    // ------------------------------------------------------------------ issuing off the shelf

    /// <summary>An issue filled entirely off the shelf never touches the warehouse, so it cannot fail
    /// for want of warehouse stock. That is the case that used to be impossible: an item the shop
    /// only ever services rather than buys has a warehouse count of zero for ever, and every issue of
    /// it failed the guarded decrement.</summary>
    [Fact]
    public async Task An_issue_filled_off_the_shelf_needs_no_warehouse_stock()
    {
        using var db = NewContext();
        var ledger = new StockLedgerService(db);

        // creditTechnician false, so nothing but the movement row is written: a field issue waits for
        // the technician's acknowledgement to credit them, and the balance writes are the raw SQL
        // this test cannot reach.
        var movement = await ledger.IssueAsync(PartId, technicianId: 42, qty: 2, UserId,
            "STOCK_REQUEST", 3, default, creditTechnician: false, shelfQty: 2);

        movement.MovementType.Should().Be(MovementType.Issue);
        movement.Quantity.Should().Be(2, "two units went to the technician");
        movement.ShelfQty.Should().Be(2, "both came off the shelf");
        ReportsEndpoints.WarehouseDelta(movement.MovementType, movement.Quantity, movement.ShelfQty)
            .Should().Be(0);
    }

    /// <summary>A mixed issue - two units off the shelf and the serial off one new box - takes only
    /// the new box off the warehouse. The store produces this one routinely, filling an order for
    /// three from a shelf that holds two.</summary>
    [Fact]
    public void A_mixed_issue_debits_only_its_warehouse_part()
    {
        ReportsEndpoints.WarehouseDelta(MovementType.Issue, 3, shelfQty: 2)
            .Should().Be(-1, "only the one unit that came out of warehouse stock left the warehouse");
    }

    /// <summary>And it stays ONE movement however it is made up: one consignment, so one
    /// acknowledgement, one challan and one set of serial links. Shown on the shelf-only case because
    /// that is the one this test can call - it is the same single IssueAsync either way, with the row
    /// recording the split rather than being split into a part-shipment per pool. Two movements would
    /// hand the technician two shipments to confirm separately for a box that arrived once.</summary>
    [Fact]
    public async Task An_issue_is_one_movement_whichever_pools_fill_it()
    {
        using var db = NewContext();

        await new StockLedgerService(db).IssueAsync(PartId, technicianId: 42, qty: 3,
            UserId, "STOCK_REQUEST", 3, default, creditTechnician: false, shelfQty: 3);

        Pending(db).Should().HaveCount(1);
    }

    /// <summary>A shelf quantity over the issued quantity is clamped rather than trusted. It should
    /// not be reachable - the split is computed from the serials on the issue - but an unclamped
    /// value would make the warehouse part negative, which is a CREDIT to the warehouse on an issue.</summary>
    [Fact]
    public async Task A_shelf_quantity_larger_than_the_issue_cannot_credit_the_warehouse()
    {
        using var db = NewContext();

        var movement = await new StockLedgerService(db).IssueAsync(PartId, technicianId: 42, qty: 2,
            UserId, "STOCK_REQUEST", 3, default, creditTechnician: false, shelfQty: 9);

        movement.ShelfQty.Should().Be(2);
        ReportsEndpoints.WarehouseDelta(movement.MovementType, movement.Quantity, movement.ShelfQty)
            .Should().Be(0);
    }

    /// <summary>An ordinary issue out of warehouse stock is untouched by any of this. The shelf is an
    /// addition, not a replacement.</summary>
    [Fact]
    public void An_issue_out_of_warehouse_stock_still_debits_the_warehouse()
    {
        ReportsEndpoints.WarehouseDelta(MovementType.Issue, 2).Should().Be(-2);
    }

    // ------------------------------------------------------------------ replacements

    /// <summary>A replacement handed over off the shelf is the case the shelf is for: the customer
    /// gets a working machine today and the shop keeps theirs to repair. Nothing comes off the
    /// warehouse, because the unit was never in it.</summary>
    [Fact]
    public async Task A_replacement_off_the_shelf_moves_no_warehouse_quantity()
    {
        using var db = NewContext();

        await new StockLedgerService(db).ReplacementOutAsync(PartId, 1, UserId, serviceId: 11, "SN-1",
            "Advance replacement", default, fromShelf: true);

        var row = Pending(db).Single();
        row.MovementType.Should().Be(MovementType.Replacement);
        row.ShelfQty.Should().Be(1);
        ReportsEndpoints.WarehouseDelta(row.MovementType, row.Quantity, row.ShelfQty).Should().Be(0);
    }

    /// <summary>Cancelling puts it back in the pool it came out of, which the swap row remembers.
    /// Reading the unit's status instead would get this wrong every time: SHELFED is where a
    /// cancelled unit lands whichever pool it came from, so a replacement taken out of purchased
    /// stock would be credited to the shelf and quietly become a kept machine.</summary>
    [Fact]
    public async Task A_cancelled_replacement_off_the_shelf_goes_back_to_the_shelf()
    {
        using var db = NewContext();

        await new StockLedgerService(db).ReplacementReturnAsync(PartId, 1, UserId, serviceId: 11,
            "SN-1", "Swap cancelled", default, toShelf: true);

        var row = Pending(db).Single();
        row.MovementType.Should().Be(MovementType.ReplacementReturn);
        row.ShelfQty.Should().Be(1);
        ReportsEndpoints.WarehouseDelta(row.MovementType, row.Quantity, row.ShelfQty).Should().Be(0);
    }

    /// <summary>And one out of purchased stock goes back to the warehouse, which is the half that
    /// would be silently wrong if the unit's status were read instead of the swap row.</summary>
    [Fact]
    public void A_cancelled_replacement_out_of_warehouse_stock_goes_back_to_the_warehouse()
    {
        ReportsEndpoints.WarehouseDelta(MovementType.ReplacementReturn, 1).Should().Be(1);
    }

    // ------------------------------------------------------------------ returns

    /// <summary>Unused stock sent back by a technician comes home to the SHELF, not to the warehouse
    /// it was issued from. It is a named unit in hand from then on, and crediting the warehouse as
    /// well would count that one machine twice: once as a unit on the shelf and once as an anonymous
    /// quantity in the warehouse.</summary>
    [Fact]
    public async Task Unused_stock_booked_back_credits_the_shelf_and_not_the_warehouse()
    {
        using var db = NewContext();

        await new StockLedgerService(db).ReturnToStockAsync(PartId, technicianId: 42, qty: 1, UserId,
            "STOCK_RETURN", 3, default, shelfQty: 1);

        var row = Pending(db).Single();
        row.MovementType.Should().Be(MovementType.Return);
        row.ShelfQty.Should().Be(1);
        ReportsEndpoints.WarehouseDelta(row.MovementType, row.Quantity, row.ShelfQty).Should().Be(0);
    }

    /// <summary>A quantity-only return has no unit to put on the shelf, so it goes back to the
    /// warehouse exactly as it always did. The shelf holds units; there is nothing to hold here.</summary>
    [Fact]
    public void A_quantity_only_return_still_credits_the_warehouse()
    {
        ReportsEndpoints.WarehouseDelta(MovementType.Return, 3).Should().Be(3);
    }

    // ------------------------------------------------------------------ the pick list

    /// <summary>The queries behind the shelf pick list and the shelf column compile. Both filter on
    /// <c>status</c>, which is persisted through a value converter, and both are grouped or counted -
    /// the combination that fails while EF is COMPILING the query, so the endpoint answers 500 with
    /// nothing wrong in the data to point at.</summary>
    [Fact]
    public void The_shelf_queries_translate_to_sql()
    {
        using var db = NewContext();

        var units = db.ComponentSerials.AsNoTracking()
            .Where(c => c.PartId == PartId && c.Status == SerialStatus.Shelfed
                        && c.OwnerType == SerialOwnerType.ServiceCenter)
            .OrderBy(c => c.LastUpdatedAt ?? c.CreatedAt);
        units.ToQueryString().Should().Contain("component_serials");

        var counts = db.ComponentSerials.AsNoTracking()
            .Where(c => c.Status == SerialStatus.Shelfed && c.OwnerType == SerialOwnerType.ServiceCenter)
            .GroupBy(c => c.PartId)
            .Select(g => new { PartId = g.Key, Count = g.Count() });
        counts.ToQueryString().Should().Contain("component_serials");
    }

    /// <summary>The consumption report's database predicate compiles too, now that it carries the
    /// shelf clause. It is an expression tree applied in SQL, so a shape EF cannot translate is a
    /// 500 on the report rather than a build error.</summary>
    [Fact]
    public void The_consumption_predicate_translates_to_sql()
    {
        using var db = NewContext();

        var sql = db.StockMovements.AsNoTracking().Where(ConsumptionRules.IsConsuming).ToQueryString();
        sql.Should().Contain("stock_movements");
        sql.Should().Contain("shelf_qty", "the predicate has to skip rows made up entirely of shelf units");
    }
}
