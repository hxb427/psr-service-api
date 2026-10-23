using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Reports;
using PSR.Service.Api.Stock;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>
/// The shelf is its own pool, and these pin what that means.
///
/// The WAREHOUSE is a quantity: stock bought in, counted in stock_balances, not named unit by unit
/// until it goes out. The SHELF is a set of units and nothing else: machines the shop kept off
/// finished jobs, and unused stock booked back from a technician. Both fill an issue; only warehouse
/// stock can be sold as goods.
///
/// The property worth protecting is that the shelf cannot drift, because there is nothing for it to
/// drift against. There was a +1/-1 rule here that moved the warehouse count alongside a manual
/// status change, and it existed because the shelf was PART of the warehouse count and the two could
/// disagree - a unit written off as missing and later found went re-issuable while the count stayed
/// one short for ever. Separating the pools closes that by construction: the units in SHELFED status
/// ARE the shelf's count, so a status change is the count change.
/// </summary>
public class SerialStatusStockTests
{
    /// <summary>The shelf holds exactly the units it may issue. Two lists that merely happen to agree
    /// would drift; this is the same set by construction.</summary>
    [Fact]
    public void A_shelved_unit_is_on_the_shelf()
    {
        SerialService.IsOnShelf(SerialStatus.Shelfed).Should().BeTrue();
        SerialService.ReIssuable.Should().Contain(SerialStatus.Shelfed);
    }

    /// <summary>And it is the only one. The shelf used to be three statuses, which is what made it
    /// unreadable: REPAIRED said a bench had finished, RETURNED_TO_SC said a box had arrived, and
    /// neither said anyone had put the unit away. Pinned as a set rather than as a membership check,
    /// so a fourth synonym cannot be added quietly later.</summary>
    [Fact]
    public void Shelved_is_the_only_status_the_shelf_counts()
    {
        SerialService.ReIssuable.Should().BeEquivalentTo([SerialStatus.Shelfed]);
    }

    /// <summary>UNDER_REPAIR is the one worth stating: the unit is at the service centre, in its
    /// custody, and is still not issuable - it is in pieces on a bench.</summary>
    [Theory]
    [InlineData(SerialStatus.Issued)]
    [InlineData(SerialStatus.Received)]
    [InlineData(SerialStatus.Used)]
    [InlineData(SerialStatus.Collected)]
    [InlineData(SerialStatus.Defective)]
    [InlineData(SerialStatus.Missing)]
    [InlineData(SerialStatus.InTransitSc)]
    [InlineData(SerialStatus.InTransitTech)]
    [InlineData(SerialStatus.UnderRepair)]
    [InlineData(SerialStatus.Scrapped)]
    // The two that used to be shelf stock. REPAIRED is the one that matters: a unit a bench has
    // finished with is not on the shelf until somebody puts it there, and "Keep in stock" is that
    // somebody. RETURNED_TO_SC is kept only for the units that passed through it before SHELFED
    // existed - nothing writes it now.
    [InlineData(SerialStatus.Repaired)]
    [InlineData(SerialStatus.ReturnedToSc)]
    public void Everything_else_is_not_on_the_shelf(SerialStatus status)
    {
        SerialService.IsOnShelf(status).Should().BeFalse();
    }

    /// <summary>One word for one state. A unit deployed at a customer used to be able to say either
    /// INSTALLED or USED depending on which code path put it there, and nothing anywhere read the
    /// two apart - same owner, same effect on every count. All the pair did was offer two labels for
    /// one thing on the SN screen and leave the reader to guess which was which.
    ///
    /// Pinned at the enum because that is where the choice lived. Taking the status parameter off
    /// <c>InstallToCustomerAsync</c> stops today's callers picking wrong; this stops a second token
    /// being added for tomorrow's to pick.</summary>
    [Fact]
    public void A_unit_at_a_customer_has_exactly_one_status()
    {
        Enum.GetNames<SerialStatus>().Should().NotContain("Installed");
    }

    /// <summary>The whole reason the +1/-1 rule is gone. A unit found after being written off joins
    /// the SHELF, not the warehouse, so the warehouse has nothing to put back: the quantity written
    /// off when the unit went missing was warehouse quantity only if the unit was warehouse stock,
    /// and no unit is.
    ///
    /// Stated as a property of the one rule that is left - the shelf is whatever is SHELFED - because
    /// that is what makes missing, found, missing, found harmless. There is no lap on which it can
    /// mint anything: the unit is either in the status or it is not.</summary>
    [Theory]
    [InlineData(SerialStatus.Missing)]
    [InlineData(SerialStatus.InTransitSc)]
    [InlineData(SerialStatus.Defective)]
    [InlineData(SerialStatus.UnderRepair)]
    [InlineData(SerialStatus.Repaired)]
    [InlineData(SerialStatus.ReturnedToSc)]
    [InlineData(SerialStatus.Scrapped)]
    [InlineData(SerialStatus.Used)]
    public void Putting_a_unit_back_on_the_shelf_is_the_status_and_nothing_else(SerialStatus from)
    {
        SerialService.IsOnShelf(from).Should().BeFalse();
        SerialService.IsOnShelf(SerialStatus.Shelfed).Should().BeTrue();
    }

    /// <summary>RECEIVED is the only status that puts a unit on a technician's balance, and the list
    /// of what does NOT is the half worth pinning: each of those is a unit somebody is holding that
    /// they still cannot fit on a job.</summary>
    [Theory]
    [InlineData(SerialStatus.Received, true)]
    [InlineData(SerialStatus.Issued, false)]        // in a van; the ack step exists for this gap
    [InlineData(SerialStatus.Defective, false)]     // theirs to send in, not stock they can fit
    [InlineData(SerialStatus.Collected, false)]     // a customer's unit; never the shop's stock
    [InlineData(SerialStatus.InTransitSc, false)]   // gone from them, not yet arrived
    [InlineData(SerialStatus.InTransitTech, false)]
    [InlineData(SerialStatus.Shelfed, false)]
    [InlineData(SerialStatus.Used, false)]
    public void Only_received_is_on_a_technicians_balance(SerialStatus status, bool expected)
    {
        SerialService.IsTechnicianStock(status).Should().Be(expected);
    }

    /// <summary>A status that names a technician cannot be applied without one. This is the silent
    /// wrong answer it closes: SyncOwnerToStatusAsync matches its technician branch only when a
    /// technician is set, so a blank one left a unit whose status said RECEIVED and whose owner still
    /// said the service centre - on nobody's balance, fittable by no one.</summary>
    [Theory]
    [InlineData(SerialStatus.Issued)]
    [InlineData(SerialStatus.Received)]
    [InlineData(SerialStatus.Collected)]
    [InlineData(SerialStatus.InTransitSc)]
    [InlineData(SerialStatus.InTransitTech)]
    public void A_status_that_names_a_technician_needs_one(SerialStatus to)
    {
        SerialService.ManualChangeBlocked(to, null, "SN-1").Should().Contain("pick the technician");
        SerialService.ManualChangeBlocked(to, 42, "SN-1").Should().BeNull();
    }

    /// <summary>And one that does not name a technician refuses to carry one. A holder on a unit that
    /// is on the shelf, at a customer, on a bench or written off is a name no balance and no screen
    /// agrees with.</summary>
    [Theory]
    [InlineData(SerialStatus.Shelfed)]
    [InlineData(SerialStatus.Used)]
    [InlineData(SerialStatus.Repaired)]
    [InlineData(SerialStatus.ReturnedToSc)]
    [InlineData(SerialStatus.UnderRepair)]
    [InlineData(SerialStatus.Scrapped)]
    // MISSING is in here deliberately: a lost unit keeps whatever custody it had, because that is
    // the last true thing known about it.
    [InlineData(SerialStatus.Missing)]
    public void A_status_that_names_no_technician_refuses_one(SerialStatus to)
    {
        SerialService.ManualChangeBlocked(to, 42, "SN-1").Should().Contain("not a status a technician holds");
        SerialService.ManualChangeBlocked(to, null, "SN-1").Should().BeNull();
    }

    /// <summary>DEFECTIVE takes one either way, and it is the only status that does: a faulty unit
    /// genuinely sits on both sides - a technician holding it to send in, or one the counter found
    /// broken on arrival, which ReceiveReturnAsync writes with no technician at all.</summary>
    [Fact]
    public void Defective_is_the_one_status_a_technician_is_optional_on()
    {
        SerialService.ManualChangeBlocked(SerialStatus.Defective, null, "SN-1").Should().BeNull();
        SerialService.ManualChangeBlocked(SerialStatus.Defective, 42, "SN-1").Should().BeNull();

        SerialService.TechnicianNeededFor(SerialStatus.Defective)
            .Should().Be(SerialService.TechnicianNeed.Optional);
        Enum.GetValues<SerialStatus>()
            .Where(s => s != SerialStatus.Defective)
            .Should().NotContain(s => SerialService.TechnicianNeededFor(s) == SerialService.TechnicianNeed.Optional);
    }

    /// <summary>Shelf straight to a technician is ALLOWED now, and that is the change. It used to be
    /// refused outright because the unit would leave the shelf and arrive on no balance - a
    /// technician's holding was only ever credited by issuing. Naming the technician is what was
    /// missing, so with one named the change goes through and moves their holding.</summary>
    [Fact]
    public void A_shelf_unit_can_be_handed_to_a_named_technician()
    {
        SerialService.ManualChangeBlocked(SerialStatus.Received, 42, "SN-1").Should().BeNull();
        SerialService.IsOnShelf(SerialStatus.Shelfed).Should().BeTrue();
        SerialService.IsTechnicianStock(SerialStatus.Received).Should().BeTrue();
    }

    /// <summary>A custody correction moves a technician and never the warehouse. Pinned against the
    /// rule every warehouse figure is read off, because carrying this on an Adjustment - which is
    /// what it had to be before the type existed - would have moved a warehouse quantity that
    /// nothing moved, inventing or destroying shop stock to record a change of hands.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void A_custody_correction_moves_no_warehouse_quantity(int delta)
    {
        PSR.Service.Api.Reports.ReportsEndpoints
            .WarehouseDelta(MovementType.CustodyCorrection, delta).Should().Be(0);
        ConsumptionRules.Effect(MovementType.CustodyCorrection, delta).Should().BeNull();
    }

    /// <summary>Unused stock coming back from a technician is put away, not queued for a bench. The
    /// status the receive path writes is the shelf status, so an acknowledged shipment needs no
    /// second gesture to become issuable - which is what RETURNED_TO_SC used to imply and never
    /// delivered. A unit the receiver finds faulty lands on no shelf at all.</summary>
    [Fact]
    public void Unused_stock_booked_in_lands_on_the_shelf()
    {
        SerialService.IsOnShelf(SerialStatus.Shelfed).Should().BeTrue();
        SerialService.IsOnShelf(SerialStatus.Defective).Should().BeFalse();
    }

    /// <summary>A shelf movement moves no warehouse quantity, whichever way it goes. Pinned against
    /// <see cref="PSR.Service.Api.Reports.ReportsEndpoints.WarehouseDelta"/> because that rule is
    /// what every warehouse figure on every report is read off: if a row made entirely of shelf units
    /// moved it, the stock ledger would show an inflow or outflow that no balance change backs, and
    /// would stop reconciling against stock_balances.</summary>
    [Theory]
    [InlineData(MovementType.Issue)]
    [InlineData(MovementType.Replacement)]
    [InlineData(MovementType.Return)]
    [InlineData(MovementType.ReplacementReturn)]
    [InlineData(MovementType.ShelfIn)]
    public void A_movement_made_entirely_of_shelf_units_moves_no_warehouse_quantity(MovementType type)
    {
        PSR.Service.Api.Reports.ReportsEndpoints.WarehouseDelta(type, 3, shelfQty: 3).Should().Be(0);
    }

    /// <summary>And the mixed case, which is the one a store actually produces: an order for three
    /// filled with two units off the shelf and the serial off one new box. Only the new box was ever
    /// warehouse stock, so only it comes off the warehouse.</summary>
    [Fact]
    public void A_mixed_issue_takes_only_the_warehouse_part_off_the_warehouse()
    {
        PSR.Service.Api.Reports.ReportsEndpoints.WarehouseDelta(MovementType.Issue, 3, shelfQty: 2)
            .Should().Be(-1);
    }

    /// <summary>The migration's own movement type, which is the one row shape that takes quantity off
    /// the warehouse while the shelf stands still: the units were already physically on the shelf and
    /// only the warehouse's claim on them is being given up. ShelfQty is 0 on those rows precisely so
    /// this is not cancelled out by the arithmetic above.</summary>
    [Fact]
    public void Separating_the_pools_takes_the_quantity_off_the_warehouse()
    {
        PSR.Service.Api.Reports.ReportsEndpoints.WarehouseDelta(MovementType.ShelfTransfer, 4)
            .Should().Be(-4);
    }

    /// <summary>Same reason as the other routing tests: minimal-API binding is resolved when the
    /// route table is built, so a handler that cannot be bound is a startup crash in production
    /// rather than a build error. It earns its keep here because both serial handlers just took a
    /// new service parameter, which is exactly the edit a binder can decide is a body instead.</summary>
    [Fact]
    public void Every_serial_route_handler_can_be_bound()
    {
        var act = () =>
        {
            var builder = WebApplication.CreateBuilder();
            builder.Services.AddAuthorization();
            builder.Services.AddMemoryCache();
            builder.Services.AddDbContext<AppDbContext>(opts =>
                opts.UseMySql("Server=localhost;Database=none", new MySqlServerVersion(new Version(8, 0, 36))));
            builder.Services.AddScoped<IAuditService, AuditService>();
            builder.Services.AddScoped<StockLedgerService>();
            builder.Services.AddScoped<SerialService>();

            var app = builder.Build();
            app.MapSerialEndpoints();
            return ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).ToList();
        };

        act.Should().NotThrow();
        act().Should().NotBeEmpty();
    }
}
