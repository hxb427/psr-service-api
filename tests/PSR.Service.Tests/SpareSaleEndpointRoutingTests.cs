using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Data;
using PSR.Service.Api.SpareSales;
using PSR.Service.Api.Stock;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>Same reason as DocumentEndpointRoutingTests: minimal-API binding is resolved when the route
/// table is built, so an unbindable handler is a production startup crash rather than a build error.
/// No server listens and no database is touched.</summary>
public class SpareSaleEndpointRoutingTests
{
    private static IReadOnlyList<Endpoint> BuildSpareSaleEndpoints()
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
        builder.Services.AddScoped<SpareSaleService>();
        builder.Services.AddScoped<StockLedgerService>();

        var app = builder.Build();

        app.MapSpareSaleEndpoints();

        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).ToList();
    }

    [Fact]
    public void Every_spare_sale_route_handler_can_be_bound()
    {
        var act = BuildSpareSaleEndpoints;

        act.Should().NotThrow("an unbindable minimal-API handler takes the whole API down at startup");
    }

    [Theory]
    // The courier route is what keeps the generate form's courier boxes filled between attempts; a PI
    // previewed and then abandoned used to take the typed courier details with it.
    [InlineData("/spare-sales/{id:long}/courier")]
    [InlineData("/spare-sales/{id:long}/clear-pi")]
    [InlineData("/spare-sales/{id:long}/mark-sold")]
    public void Route_is_registered(string route)
    {
        var routes = BuildSpareSaleEndpoints().OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText).ToList();

        routes.Should().Contain(route);
    }
}
