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

/// <summary>Same reason as StockRequestEndpointRoutingTests: minimal-API binding is resolved when the
/// route table is built, so an unbindable handler is a production startup crash rather than a build
/// error. It earns its keep here because the batch receipt takes a list of lines in its body — exactly
/// the shape a binder can decide to treat as something else. No server listens and no database is
/// touched.</summary>
public class StockEndpointRoutingTests
{
    private static IReadOnlyList<Endpoint> BuildStockEndpoints()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddMemoryCache();

        // Every service a handler takes must be registered, or binding infers it as a body parameter
        // and the check passes vacuously.
        builder.Services.AddDbContext<AppDbContext>(opts =>
            opts.UseMySql("Server=localhost;Database=none", new MySqlServerVersion(new Version(8, 0, 36))));
        builder.Services.AddScoped<IAuditService, AuditService>();
        builder.Services.AddScoped<StockLedgerService>();

        var app = builder.Build();

        app.MapStockEndpoints();

        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).ToList();
    }

    [Fact]
    public void Every_stock_route_handler_can_be_bound()
    {
        var act = BuildStockEndpoints;

        act.Should().NotThrow("an unbindable minimal-API handler takes the whole API down at startup");
    }

    /// <summary>The one-part route is listed alongside the batch on purpose: a desktop build in the
    /// field still books stock in through it, and the store cannot be asked to update before its next
    /// delivery arrives.</summary>
    [Theory]
    [InlineData("/stock/receipts")]
    [InlineData("/stock/receipts/batch")]
    [InlineData("/stock/adjustments")]
    [InlineData("/stock/movements")]
    public void Route_is_registered(string route)
    {
        var routes = BuildStockEndpoints().OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText).ToList();

        routes.Should().Contain(route);
    }
}
