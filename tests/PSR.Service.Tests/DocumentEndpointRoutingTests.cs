using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Data;
using PSR.Service.Api.Documents;
using PSR.Service.Api.Settings;
using PSR.Service.Api.Stock;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>Same reason as ServiceEndpointRoutingTests: minimal-API binding is resolved when the route
/// table is built, so an unbindable handler is a production startup crash rather than a build error.
/// No server listens and no database is touched.</summary>
public class DocumentEndpointRoutingTests
{
    private static IReadOnlyList<Endpoint> BuildDocumentEndpoints()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddMemoryCache();

        // Every service a handler takes must be registered, or binding infers it as a body parameter
        // and the check passes vacuously. The DbContext is pinned to a server version because the
        // production registration probes the server for it.
        builder.Services.AddDbContext<AppDbContext>(opts =>
            opts.UseMySql("Server=localhost;Database=none", new MySqlServerVersion(new Version(8, 0, 36))));
        builder.Services.AddScoped<IAuditService, AuditService>();
        builder.Services.AddScoped<NumberSequenceService>();
        builder.Services.AddScoped<AppSettingsService>();
        builder.Services.AddSingleton(new CompanyInfo());
        builder.Services.AddScoped<BillingService>();

        var app = builder.Build();

        app.MapDocumentEndpoints();

        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).ToList();
    }

    [Fact]
    public void Every_document_route_handler_can_be_bound()
    {
        var act = BuildDocumentEndpoints;

        act.Should().NotThrow("an unbindable minimal-API handler takes the whole API down at startup");
    }

    [Theory]
    // The quote route is what puts the rates on the generate form before the document exists; without
    // it the form falls back to blank rate boxes, which is the thing it was added to stop.
    [InlineData("/documents/quote")]
    [InlineData("/documents/preview")]
    [InlineData("/documents/sale/preview")]
    public void Route_is_registered(string route)
    {
        var routes = BuildDocumentEndpoints().OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText).ToList();

        routes.Should().Contain(route);
    }
}
