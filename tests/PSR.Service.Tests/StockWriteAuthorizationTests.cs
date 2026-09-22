using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Stock;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>
/// Which accounts may WRITE in the stock area.
///
/// Two endpoints were reachable by anyone holding a valid token, a read-only viewer included: raising a
/// stock request, and recording field work. Both groups are mapped with a bare RequireAuthorization(),
/// which asks for a signed-in caller and nothing more, and neither route added a policy of its own — so
/// the gap was invisible at the call site, looking exactly like the gated routes beside it. The desktop
/// hides both from a viewer, which is why nothing complained; the API never did.
///
/// These work off the route table rather than over HTTP, because that is where the omission lived. A
/// policy that is not attached to a route cannot refuse anything, and a route naming a policy that was
/// never registered fails only when someone calls it.
/// </summary>
public class StockWriteAuthorizationTests
{
    // ---------------------------------------------------------------- route table

    private static IReadOnlyList<Endpoint> BuildStockWriteEndpoints()
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
        app.MapFieldOpsEndpoints();

        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).ToList();
    }

    /// <summary>Policy names on a route. A group's bare RequireAuthorization() also lands here, as an
    /// entry with no policy at all, so the empty ones are dropped — those are exactly the case that
    /// admits every signed-in account.</summary>
    private static IEnumerable<string> PoliciesOn(Endpoint endpoint) =>
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Select(a => a.Policy)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!);

    private static string[] MethodsOn(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.ToArray() ?? [];

    private static string Describe(RouteEndpoint endpoint) =>
        $"{string.Join("/", MethodsOn(endpoint))} {endpoint.RoutePattern.RawText}";

    private static readonly string[] WriteMethods = ["POST", "PUT", "PATCH", "DELETE"];

    private static IEnumerable<RouteEndpoint> Writes() =>
        BuildStockWriteEndpoints().OfType<RouteEndpoint>()
            .Where(e => MethodsOn(e).Any(WriteMethods.Contains));

    /// <summary>The three routes this is all about, named one by one so a failure says which came
    /// loose rather than only that something did.</summary>
    [Theory]
    [InlineData("POST", "/stock-requests/", "StockRequestRaise")]
    [InlineData("POST", "/field-services/", "FieldOpsRecord")]
    [InlineData("POST", "/field-sales/", "FieldOpsRecord")]
    public void The_route_is_gated_by_its_policy(string method, string route, string policy)
    {
        var endpoint = Writes().Single(e =>
            e.RoutePattern.RawText == route && MethodsOn(e).Contains(method));

        PoliciesOn(endpoint).Should().Contain(policy,
            "a bare RequireAuthorization() on the group admits every signed-in account, viewer included");
    }

    /// <summary>Cancelling is the one write here that no policy can express: it turns on whether the
    /// caller raised the row, which is not knowable until the row has been loaded, so the handler
    /// refuses anyone else. Listing it rather than exempting handler-checked writes in general means the
    /// next ungated write has to be argued for in this file instead of arriving unnoticed.</summary>
    private static readonly string[] GatedInsideTheHandler = ["POST /stock-requests/{id:long}/cancel"];

    [Fact]
    public void No_write_is_left_on_a_bare_signed_in_check()
    {
        var ungated = Writes().Where(e => !PoliciesOn(e).Any()).Select(Describe).ToList();

        ungated.Should().BeEquivalentTo(GatedInsideTheHandler,
            "a write carrying no policy is open to every signed-in account — if that is deliberate, the "
            + "handler has to check the caller itself and the route belongs in GatedInsideTheHandler");
    }

    // ---------------------------------------------------------------- policy definitions

    /// <summary>The real policies, wired as Program.cs wires them.
    ///
    /// Jwt:LocalUtcOffsetHours is deliberately left at its default: AddAuth sets the process-wide
    /// ShopClock from it, and 5.5 is what ShopClockBusinessDateTests configures, so the two cannot
    /// disagree while xunit runs the classes side by side.</summary>
    private static IServiceProvider BuildAuthServices()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Signing"] = "TestSigningKey_NotForProductionUse_AtLeast32Chars",
        });
        builder.Services.AddAuth(builder.Configuration);
        return builder.Build().Services;
    }

    private static ClaimsPrincipal SignedInAs(params string[] roles) =>
        new(new ClaimsIdentity(
            roles.Select(r => new Claim(ClaimTypes.Role, r)).Append(new Claim("sub", "1")),
            authenticationType: "Test", nameType: ClaimTypes.Name, roleType: ClaimTypes.Role));

    private static async Task<bool> AdmitsAsync(string policy, params string[] roles)
    {
        var auth = BuildAuthServices().GetRequiredService<IAuthorizationService>();
        return (await auth.AuthorizeAsync(SignedInAs(roles), resource: null, policy)).Succeeded;
    }

    /// <summary>A route may name a policy that was never registered, and nothing says so until someone
    /// calls it. Cross-checking the two lists is the only place a renamed or mistyped policy is caught
    /// before production catches it.</summary>
    [Fact]
    public async Task Every_policy_a_route_names_is_actually_registered()
    {
        var provider = BuildAuthServices().GetRequiredService<IAuthorizationPolicyProvider>();
        var named = BuildStockWriteEndpoints().SelectMany(PoliciesOn).Distinct().ToList();

        named.Should().NotBeEmpty();
        foreach (var name in named)
            (await provider.GetPolicyAsync(name)).Should().NotBeNull(
                "{0} is named by a stock route but registered nowhere", name);
    }

    /// <summary>The account the whole exercise was about. A viewer reads the shop; it does not start
    /// work in it.</summary>
    [Theory]
    [InlineData("StockRequestRaise")]
    [InlineData("FieldOpsRecord")]
    public async Task A_viewer_is_refused(string policy) =>
        (await AdmitsAsync(policy, RoleNames.Viewer)).Should().BeFalse();

    [Theory]
    [InlineData("StockRequestRaise")]
    [InlineData("FieldOpsRecord")]
    public async Task A_technician_is_admitted(string policy) =>
        (await AdmitsAsync(policy, RoleNames.Technician)).Should().BeTrue();

    /// <summary>Raising a request asks for stock to be issued to the CALLER: the row is attributed to
    /// them, and issuing it credits their balance. The store's own roles are refused on purpose — they
    /// issue rather than ask, and direct-issue is their route. It also shuts the back way into a holding
    /// that nobody can see or return: direct-issue refuses a non-technician outright, but issuing
    /// against a request never re-checks, so a request raised by an admin reached the same broken state
    /// the long way round.</summary>
    [Theory]
    [InlineData(RoleNames.Admin)]
    [InlineData(RoleNames.Manager)]
    [InlineData(RoleNames.Supervisor)]
    [InlineData(RoleNames.StoreManager)]
    [InlineData(RoleNames.Accounts)]
    [InlineData(RoleNames.Inward)]
    public async Task Raising_a_request_is_the_technicians_own_action(string role) =>
        (await AdmitsAsync("StockRequestRaise", role)).Should().BeFalse();

    /// <summary>Field work consumes stock off the caller's own balance and moves serials out of their
    /// custody, so there is nothing here for an account that holds neither.</summary>
    [Theory]
    [InlineData(RoleNames.Admin)]
    [InlineData(RoleNames.Manager)]
    [InlineData(RoleNames.Supervisor)]
    [InlineData(RoleNames.StoreManager)]
    public async Task Recording_field_work_is_not_a_supervisory_action(string role) =>
        (await AdmitsAsync("FieldOpsRecord", role)).Should().BeFalse();

    /// <summary>A token carrying no role at all — the barest thing the two endpoints used to take.</summary>
    [Theory]
    [InlineData("StockRequestRaise")]
    [InlineData("FieldOpsRecord")]
    public async Task A_token_carrying_no_role_is_refused(string policy) =>
        (await AdmitsAsync(policy)).Should().BeFalse();

    // ---------------------------------------------------------------- the account flag

    /// <summary>The half of the field-ops gate that cannot be a policy.
    ///
    /// Both kinds of technician hold the same role, so the policy above admits an in-house one — and they
    /// carry a balance and serial custody that this endpoint would consume. What separates them is
    /// is_field_technician, an account column.</summary>
    [Fact]
    public void Only_a_technician_who_carries_stock_off_site_may_record_field_work()
    {
        FieldOpsEndpoints.CarriesStockOffSite(new User { IsFieldTechnician = true }).Should().BeTrue();
        FieldOpsEndpoints.CarriesStockOffSite(new User { IsFieldTechnician = false }).Should().BeFalse();
    }

    /// <summary>Why that check sits in the handler rather than beside the role. An issued token carries
    /// the account's roles and nothing about where it works, so there is no claim to require. If this
    /// ever fails, the flag has become a claim and the handler check can move into the policy.</summary>
    [Fact]
    public void The_field_technician_flag_never_reaches_the_token()
    {
        var jwt = new JwtTokenService(Options.Create(new JwtOptions
        {
            Issuer = "test-iss",
            Audience = "test-aud",
            Signing = "TestSigningKey_NotForProductionUse_AtLeast32Chars",
            ExpiryHours = 1,
        }));

        var (token, _) = jwt.Issue(
            new User { Id = 1, Username = "field-tech", IsFieldTechnician = true },
            [RoleNames.Technician]);

        var claimTypes = new JwtSecurityTokenHandler().ReadJwtToken(token).Claims
            .Select(c => c.Type).ToList();

        claimTypes.Should().NotContain(t => t.Contains("field", StringComparison.OrdinalIgnoreCase));
    }
}
