using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data;
using PSR.Service.Api.Reports;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>Same reason as StockEndpointRoutingTests: minimal-API binding is resolved when the route
/// table is built, so a handler the binder cannot work out is a production startup crash rather than
/// a build error — the whole API fails to come up, not just the report.
///
/// It earns its keep on the reports group because these handlers are mostly optional query
/// parameters, and a parameter whose type the binder does not recognise as one is inferred as a body
/// instead. A GET with an inferred body throws at route-build time. No server listens and no database
/// is touched.</summary>
public class ReportEndpointRoutingTests
{
    private static IReadOnlyList<Endpoint> BuildReportEndpoints()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();

        // Every service a handler takes must be registered, or binding infers it as a body parameter
        // and the check passes vacuously.
        builder.Services.AddDbContext<AppDbContext>(opts =>
            opts.UseMySql("Server=localhost;Database=none", new MySqlServerVersion(new Version(8, 0, 36))));
        builder.Services.AddOptions<JwtOptions>();

        var app = builder.Build();

        app.MapReportsEndpoints();

        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).ToList();
    }

    [Fact]
    public void Every_report_route_handler_can_be_bound()
    {
        var act = BuildReportEndpoints;

        act.Should().NotThrow("an unbindable minimal-API handler takes the whole API down at startup");
    }

    /// <summary>The consumption report is listed explicitly because the desktop reads both halves of
    /// it — the report and the per-item drill-down — and a renamed route is a page that loads empty
    /// with an error nobody at the counter can act on.</summary>
    [Theory]
    [InlineData("/reports/consumption")]
    [InlineData("/reports/consumption/{partId:long}")]
    [InlineData("/reports/stock-ledger")]
    [InlineData("/reports/stock-analysis")]
    public void The_stock_report_routes_are_where_the_desktop_looks_for_them(string route)
    {
        var routes = BuildReportEndpoints().OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText)
            .ToList();

        routes.Should().Contain(route);
    }
}
