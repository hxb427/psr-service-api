using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Data;
using PSR.Service.Api.Stock;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>Same reason as DocumentEndpointRoutingTests: minimal-API binding is resolved when the route
/// table is built, so an unbindable handler is a production startup crash rather than a build error.
/// It earns its keep here because direct-issue takes a list of lines in its body — exactly the shape a
/// binder can decide to treat as something else. No server listens and no database is touched.</summary>
public class StockRequestEndpointRoutingTests
{
    private static IReadOnlyList<Endpoint> BuildStockRequestEndpoints()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddMemoryCache();

        // Every service a handler takes must be registered, or binding infers it as a body parameter
        // and the check passes vacuously.
        builder.Services.AddDbContext<AppDbContext>(opts =>
            opts.UseMySql("Server=localhost;Database=none", new MySqlServerVersion(new Version(8, 0, 36))));
        builder.Services.AddScoped<IAuditService, AuditService>();
        builder.Services.AddScoped<NumberSequenceService>();
        builder.Services.AddScoped<StockLedgerService>();
        builder.Services.AddScoped<SerialService>();

        var app = builder.Build();

        app.MapStockRequestEndpoints();

        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).ToList();
    }

    [Fact]
    public void Every_stock_request_route_handler_can_be_bound()
    {
        var act = BuildStockRequestEndpoints;

        act.Should().NotThrow("an unbindable minimal-API handler takes the whole API down at startup");
    }

    [Theory]
    [InlineData("/stock-requests/direct-issue")]
    [InlineData("/stock-requests/{id:long}/issue")]
    [InlineData("/stock-requests/technicians")]
    public void Route_is_registered(string route)
    {
        var routes = BuildStockRequestEndpoints().OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText).ToList();

        routes.Should().Contain(route);
    }
}

/// <summary>Direct issue takes a list of lines now, but a desktop build in the field still sends the
/// one-part shape it was released with. The store cannot be asked to update before its next issue of
/// stock, so both are read the same way — and that is worth pinning, because the compatibility is
/// invisible in the handler (it reads EffectiveLines and never sees which shape arrived).</summary>
public class DirectIssueShapeTests
{
    [Fact]
    public void Lines_are_used_when_the_client_sends_them()
    {
        var req = new DirectIssueRequest(4, new List<DirectIssueLine> { new(1, 2), new(3, 5) });

        req.EffectiveLines().Should().HaveCount(2)
            .And.Contain(l => l.PartId == 3 && l.Qty == 5);
    }

    [Fact]
    public void The_old_single_part_body_is_read_as_one_line()
    {
        var req = new DirectIssueRequest(4, PartId: 7, Qty: 3, Serials: new[] { "A", "B", "C" });

        req.EffectiveLines().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new DirectIssueLine(7, 3, new[] { "A", "B", "C" }));
    }

    /// <summary>A current client's body wins outright — its one-part fields are never populated, and
    /// reading them as an extra line would invent stock movement nobody asked for.</summary>
    [Fact]
    public void Lines_win_over_the_old_fields()
    {
        var req = new DirectIssueRequest(4, new List<DirectIssueLine> { new(1, 2) }, PartId: 99, Qty: 50);

        req.EffectiveLines().Should().ContainSingle().Which.PartId.Should().Be(1);
    }

    [Fact]
    public void An_empty_body_yields_nothing_to_issue()
    {
        new DirectIssueRequest(4).EffectiveLines().Should().BeEmpty();
    }
}
