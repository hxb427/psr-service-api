using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Data;
using PSR.Service.Api.Services;
using PSR.Service.Api.Settings;
using PSR.Service.Api.Stock;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>Minimal-API parameter binding is resolved when the route table is BUILT, not when the
/// project compiles — so a handler the framework cannot bind is a startup crash in production, not a
/// build error. Building the endpoints here forces that validation on every test run, with no server
/// listening and no database touched.</summary>
public class ServiceEndpointRoutingTests
{
    private static IReadOnlyList<Endpoint> BuildServiceEndpoints()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddMemoryCache();

        // Binding infers "not a registered service" as a body parameter, so every service a handler
        // takes has to be registered or the inference is wrong and the check is meaningless. The
        // DbContext is pinned to a server version rather than AutoDetect: the production registration
        // opens a connection to probe the server, and this test must not need a database.
        builder.Services.AddDbContext<AppDbContext>(opts =>
            opts.UseMySql("Server=localhost;Database=none", new MySqlServerVersion(new Version(8, 0, 36))));
        builder.Services.AddScoped<IAuditService, AuditService>();
        builder.Services.AddScoped<NumberSequenceService>();
        builder.Services.AddScoped<StockLedgerService>();
        builder.Services.AddScoped<SerialService>();
        builder.Services.AddScoped<AppSettingsService>();

        var app = builder.Build();

        app.MapServiceEndpoints();

        // Enumerating the builder's OWN data sources is what runs RequestDelegateFactory over every
        // handler. Resolving EndpointDataSource from app.Services instead returns the composite the
        // host uses at runtime, which is empty here — and would make these assertions pass vacuously.
        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).ToList();
    }

    [Fact]
    public void Every_service_route_handler_can_be_bound()
    {
        var act = BuildServiceEndpoints;

        act.Should().NotThrow("an unbindable minimal-API handler takes the whole API down at startup");
    }

    [Theory]
    [InlineData("/services/bulk/acknowledge")]
    [InlineData("/services/bulk/start")]
    [InlineData("/services/bulk/assign")]
    [InlineData("/services/bulk/dispatch")]
    [InlineData("/services/bulk/stock")]
    [InlineData("/services/bulk/payment")]
    [InlineData("/services/bulk/outward-reference")]
    [InlineData("/services/bulk/invoice-no")]
    [InlineData("/services/bulk/delete")]
    public void Bulk_route_is_registered(string route)
    {
        var routes = BuildServiceEndpoints().OfType<RouteEndpoint>()
            .Select(e => "/" + e.RoutePattern.RawText!.TrimStart('/'));

        routes.Should().Contain(route);
    }

    [Fact]
    public void Single_job_routes_survive_alongside_the_bulk_ones()
    {
        // The detail pane still acts on one job at a time; the bulk routes are an addition, not a
        // replacement, and both call the same Apply* helpers underneath.
        var routes = BuildServiceEndpoints().OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText!).ToList();

        routes.Should().Contain(r => r.Contains("{id:long}/acknowledge"));
        routes.Should().Contain(r => r.Contains("{id:long}/complete"));
        routes.Should().Contain(r => r.Contains("{id:long}/payment"));
    }
}
