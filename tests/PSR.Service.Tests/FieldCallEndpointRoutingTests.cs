using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Data;
using PSR.Service.Api.Services;
using PSR.Service.Api.Stock;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>Same reason as the other routing tests: minimal-API binding is resolved when the route
/// table is built, so an unbindable handler is a production startup crash rather than a build error.
///
/// It earns its keep on the call register because the list handler takes eight loose query
/// parameters, two of them <c>DateTime?</c>. A binder that cannot make a query parameter of one of
/// those decides it must be the request body instead, and a GET with a body is a route that is
/// registered and then never matches anything. No server listens and no database is touched.</summary>
public class FieldCallEndpointRoutingTests
{
    private static IReadOnlyList<Endpoint> BuildEndpoints()
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

        var app = builder.Build();

        app.MapFieldCallEndpoints();
        // The address suggestions the call form's address box reads live on the customers group.
        app.MapCustomerEndpoints();

        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).ToList();
    }

    [Fact]
    public void Every_field_call_route_handler_can_be_bound()
    {
        var act = BuildEndpoints;

        act.Should().NotThrow("an unbindable minimal-API handler takes the whole API down at startup");
    }

    [Theory]
    [InlineData("/field-calls/")]
    [InlineData("/customers/addresses")]
    public void Route_is_registered(string route)
    {
        var routes = BuildEndpoints().OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText).ToList();

        routes.Should().Contain(route);
    }

    /// <summary>The suggestion route must not be swallowed by the customer-by-id route.
    ///
    /// It is safe because <c>{id:long}</c> cannot match "addresses", which is the whole reason the
    /// two can be declared in either order — but that safety is a property of the constraint, and a
    /// constraint is exactly the kind of thing somebody relaxes later.</summary>
    [Fact]
    public void The_address_route_is_not_shadowed_by_the_customer_by_id_route()
    {
        var routes = BuildEndpoints().OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText).ToList();

        routes.Should().Contain("/customers/{id:long}");
        routes.Should().Contain("/customers/addresses");
    }
}
