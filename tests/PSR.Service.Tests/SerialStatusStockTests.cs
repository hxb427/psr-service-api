using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Stock;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>
/// The manual serial screen is where a person asserts where a unit is, and until now it said so to
/// the serial ledger only. A unit written off as missing had its quantity written off with it;
/// flipping it back to RETURNED_TO_SC made it issuable again while the warehouse count stayed one
/// short for ever.
///
/// These pin the arithmetic that closes that: the shelf count follows the status in both directions,
/// and the one transition that would lose a unit altogether is refused instead.
/// </summary>
public class SerialStatusStockTests
{
    /// <summary>The shelf counts exactly the units it may issue. Two lists that merely happen to
    /// agree would drift; this is the same set by construction.</summary>
    [Theory]
    [InlineData(SerialStatus.ReturnedToSc)]
    [InlineData(SerialStatus.Repaired)]
    public void A_unit_back_at_the_service_centre_is_on_the_shelf(SerialStatus status)
    {
        SerialService.IsWarehouseStock(status).Should().BeTrue();
        SerialService.ReIssuable.Should().Contain(status);
    }

    /// <summary>UNDER_REPAIR is the one worth stating: the unit is at the service centre, in its
    /// custody, and is still not stock — it is in pieces on a bench.</summary>
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
    public void Everything_else_is_not_warehouse_stock(SerialStatus status)
    {
        SerialService.IsWarehouseStock(status).Should().BeFalse();
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

    /// <summary>Found after being written off: the unit rejoins the shelf, so the count must too.</summary>
    [Theory]
    [InlineData(SerialStatus.Missing)]
    [InlineData(SerialStatus.InTransitSc)]
    [InlineData(SerialStatus.Defective)]
    [InlineData(SerialStatus.UnderRepair)]
    public void Putting_a_unit_back_on_the_shelf_adds_one(SerialStatus from)
    {
        SerialService.WarehouseDeltaFor(from, SerialStatus.ReturnedToSc).Should().Be(1);
    }

    /// <summary>The half that stops the shelf inventing stock: without it, missing → found → missing
    /// → found adds a unit of stock on every lap.</summary>
    [Theory]
    [InlineData(SerialStatus.Missing)]
    [InlineData(SerialStatus.Scrapped)]
    [InlineData(SerialStatus.UnderRepair)]
    [InlineData(SerialStatus.Defective)]
    [InlineData(SerialStatus.InTransitSc)]
    public void Taking_a_unit_off_the_shelf_removes_one(SerialStatus to)
    {
        SerialService.WarehouseDeltaFor(SerialStatus.ReturnedToSc, to).Should().Be(-1);
    }

    [Fact]
    public void Missing_and_found_cancel_out_exactly()
    {
        var out1 = SerialService.WarehouseDeltaFor(SerialStatus.ReturnedToSc, SerialStatus.Missing);
        var back = SerialService.WarehouseDeltaFor(SerialStatus.Missing, SerialStatus.ReturnedToSc);

        (out1 + back).Should().Be(0);
    }

    /// <summary>A repaired unit was already on the shelf as RETURNED_TO_SC, or arrived there through
    /// a path that credited the count itself. Moving between two shelf statuses must not count it
    /// again.</summary>
    [Theory]
    [InlineData(SerialStatus.ReturnedToSc, SerialStatus.Repaired)]
    [InlineData(SerialStatus.Repaired, SerialStatus.ReturnedToSc)]
    [InlineData(SerialStatus.Missing, SerialStatus.Scrapped)]
    [InlineData(SerialStatus.Collected, SerialStatus.Defective)]
    [InlineData(SerialStatus.Issued, SerialStatus.Missing)]
    public void A_change_that_never_touches_the_shelf_moves_nothing(SerialStatus from, SerialStatus to)
    {
        SerialService.WarehouseDeltaFor(from, to).Should().Be(0);
    }

    /// <summary>The only transition that would take a unit off the shelf and credit nobody: a
    /// technician's usable holding is created by issuing, which a status edit cannot do.</summary>
    [Fact]
    public void Shelf_stock_cannot_be_handed_to_a_technician_by_a_status_change()
    {
        SerialService.ManualChangeBlocked(SerialStatus.ReturnedToSc, SerialStatus.Received, "SN-1")
            .Should().Contain("Issue it to the technician");
    }

    [Theory]
    [InlineData(SerialStatus.Missing, SerialStatus.Received)]      // never was on the shelf
    [InlineData(SerialStatus.ReturnedToSc, SerialStatus.Missing)]
    [InlineData(SerialStatus.ReturnedToSc, SerialStatus.Repaired)]
    public void Every_other_manual_change_is_allowed(SerialStatus from, SerialStatus to)
    {
        SerialService.ManualChangeBlocked(from, to, "SN-1").Should().BeNull();
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
