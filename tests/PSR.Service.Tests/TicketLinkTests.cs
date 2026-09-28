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
/// The field app closes a Poornasree ticket with a customer OTP, and that close is irreversible from
/// the app's side. The gate in front of it is a lookup on this side: has a field service actually been
/// recorded against the ticket? If that lookup answers wrongly, a ticket closes with parts gone from a
/// technician's balance and nothing in the ledger saying where they went.
///
/// These fix the two ways that could go wrong — the route not existing, and an absent ticket reference
/// being stored as something that matches other absent ones.
/// </summary>
public class TicketLinkTests
{
    private static IReadOnlyList<Endpoint> BuildFieldOpsEndpoints()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddMemoryCache();

        // Every service a handler takes has to be registered, or binding infers it as a body
        // parameter and the route check passes without proving anything.
        builder.Services.AddDbContext<AppDbContext>(opts =>
            opts.UseMySql("Server=localhost;Database=none", new MySqlServerVersion(new Version(8, 0, 36))));
        builder.Services.AddScoped<IAuditService, AuditService>();
        builder.Services.AddScoped<StockLedgerService>();
        builder.Services.AddScoped<SerialService>();
        builder.Services.AddScoped<NumberSequenceService>();

        var app = builder.Build();
        app.MapFieldOpsEndpoints();
        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).ToList();
    }

    [Fact]
    public void Every_field_ops_route_handler_can_be_bound()
    {
        var act = BuildFieldOpsEndpoints;

        act.Should().NotThrow("an unbindable minimal-API handler takes the whole API down at startup");
    }

    /// <summary>The OTP gate in the app has nowhere to ask without this route, and a gate that cannot
    /// ask is a gate that opens.</summary>
    [Fact]
    public void Ticket_service_lookup_route_is_registered()
    {
        var routes = BuildFieldOpsEndpoints().OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText).ToList();

        routes.Should().Contain("/field-services/by-ticket/{ticketId}");
    }

    /// <summary>A ticket reference is either a real one or absent. An empty string is neither: stored
    /// as-is it equals every other empty string, so the by-ticket lookup for one ticketless service
    /// would return every ticketless service ever recorded — and the app would read that as "this
    /// ticket has been serviced" for a ticket nobody had touched.</summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(" 2fbeed47-a2e3-460d-8659-d062b933d7eb ", "2fbeed47-a2e3-460d-8659-d062b933d7eb")]
    public void Absent_ticket_references_are_stored_as_null(string? sent, string? stored)
    {
        FieldOpsEndpoints.NormalizeTicketRef(sent).Should().Be(stored);
    }

    /// <summary>The ticket number travels beside the id because the two systems name a ticket
    /// differently — the API is keyed by UUID, every human quotes TKT-… — and a record that carries
    /// only the UUID cannot be reconciled by anyone reading it.</summary>
    [Fact]
    public void A_service_keeps_both_halves_of_the_ticket_reference()
    {
        var fs = new FieldService
        {
            TicketId = "2fbeed47-a2e3-460d-8659-d062b933d7eb",
            TicketNumber = "TKT-20260604-F0B92422",
        };

        fs.TicketId.Should().NotBeNullOrWhiteSpace();
        fs.TicketNumber.Should().StartWith("TKT-");
    }
}
