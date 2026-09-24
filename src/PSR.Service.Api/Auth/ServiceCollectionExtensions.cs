using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Auth;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAuth(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<JwtOptions>(config.GetSection(JwtOptions.SectionName));
        services.AddSingleton<JwtTokenService>();
        services.AddSingleton<UserTokenVersionCache>();

        var jwt = config.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

        // Fix the shop's wall clock before the first request. Business dates -- received, document,
        // sale -- default from this; timestamps stay UTC and do not touch it.
        PSR.Service.Api.Common.ShopClock.Configure(jwt.LocalUtcOffsetHours);
        if (string.IsNullOrWhiteSpace(jwt.Signing) || jwt.Signing.Length < 32)
            throw new InvalidOperationException(
                "Jwt:Signing is missing or too short (must be at least 32 characters).");

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(opts =>
            {
                opts.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Signing)),
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "unique_name",
                    RoleClaimType = ClaimTypes.Role,
                };

                // An expired token never reaches TokenVersionValidationMiddleware — it fails here and
                // leaves the client with a bare 401 that is indistinguishable from a token-version
                // kick. Both show the user "your session has expired", so without this line there is
                // no way to tell an ordinary 24-hour timeout from a session being killed elsewhere.
                opts.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = ctx =>
                    {
                        var reason = ctx.Exception switch
                        {
                            SecurityTokenExpiredException e => $"token expired at {e.Expires:u}",
                            SecurityTokenInvalidSignatureException => "invalid signature (signing key mismatch)",
                            _ => ctx.Exception.GetType().Name,
                        };
                        ctx.HttpContext.RequestServices
                            .GetRequiredService<ILoggerFactory>()
                            .CreateLogger("PSR.Service.Api.Auth.JwtBearer")
                            .LogInformation("Rejecting request on {Path}: {Reason}",
                                ctx.HttpContext.Request.Path, reason);
                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy("Admin", p => p.RequireRole(RoleNames.Admin));
            // Reaches the user-management pages. Which accounts each of these may actually see or
            // change is decided per target by UserHierarchy — this policy is only the front door.
            options.AddPolicy("UserManage", p => p.RequireRole(
                RoleNames.Admin, RoleNames.Manager, RoleNames.Supervisor));
            // Reaches the settings console. Managers get the invoice switches; the version floor and
            // the warranty default stay admin-only and are checked inside the handler.
            options.AddPolicy("SettingsManage", p => p.RequireRole(RoleNames.Admin, RoleNames.Manager));
            // The priced catalogue — parts and service charges. Adding an item or changing a rate is a
            // commercial decision, so it stops at manager; supervisors and below read it only.
            options.AddPolicy("CatalogueManage", p => p.RequireRole(RoleNames.Admin, RoleNames.Manager));
            // The dealer master: who the shop bills, and on what warranty terms. Kept apart from the
            // priced catalogue because it is not a pricing decision — a dealer's warranty months and
            // address are a record of an arrangement somebody made, and the people who take jobs in
            // over the counter are the ones who find them wrong.
            //
            // The bulk import from the legacy database is NOT under this. It scans passtestdata and
            // creates dealers wholesale, which is the one action here that cannot be undone by
            // correcting a field, so it stays with the Admin policy on its own route.
            options.AddPolicy("DealerManage", p => p.RequireRole(
                RoleNames.Admin, RoleNames.Manager, RoleNames.Supervisor));
            // Flipping serial tracking on a part changes what the shop floor is asked to record, not
            // what anything costs, so it reaches one role further down than the rest of the catalogue.
            options.AddPolicy("SerialTrackingManage", p => p.RequireRole(
                RoleNames.Admin, RoleNames.Manager, RoleNames.Supervisor));
            options.AddPolicy("StockView", p => p.RequireRole(
                RoleNames.Admin, RoleNames.Manager, RoleNames.Supervisor, RoleNames.Viewer, RoleNames.StoreManager));
            options.AddPolicy("StockManage", p => p.RequireRole(
                RoleNames.Admin, RoleNames.Manager, RoleNames.Supervisor, RoleNames.StoreManager));
            options.AddPolicy("ReturnAck", p => p.RequireRole(
                RoleNames.Admin, RoleNames.Manager, RoleNames.Supervisor));
            // Raising a request is asking for stock to be issued to YOURSELF: the row is attributed to
            // the caller and issuing it credits the caller's balance. So it is the technician's own
            // action, and the store roles that fill the rest of this area are deliberately absent --
            // they issue, they do not ask, and direct-issue is the route they use to hand stock over
            // with no request behind it. It also closes the other way into a holding that nobody can
            // see or return: direct-issue already refuses a non-technician outright ("is not a
            // technician and cannot hold stock"), but issuing against a request never re-checked, so a
            // request raised by a non-technician was a way to reach the same broken state.
            options.AddPolicy("StockRequestRaise", p => p.RequireRole(RoleNames.Technician));
            // On-site work -- a service performed or a part sold at the customer's premises, consuming
            // stock the caller is carrying. This is the ROLE half only. Whether the account actually
            // carries stock off-site is the is_field_technician flag, which is not in the token and so
            // cannot be expressed here; the handler checks it against the user record.
            options.AddPolicy("FieldOpsRecord", p => p.RequireRole(RoleNames.Technician));

            // Phase 4 — service workflow (inward_manager / dispatch_manager roles removed → folded into manager/supervisor)
            // The receiving-desk role is admitted HERE and nowhere else — booking inward is the whole
            // of what it can do. Every other policy below deliberately omits it.
            options.AddPolicy("InwardManage", p => p.RequireRole(
                RoleNames.Admin, RoleNames.Manager, RoleNames.Supervisor, RoleNames.Inward));
            options.AddPolicy("ServiceAssign", p => p.RequireRole(
                RoleNames.Admin, RoleNames.Manager, RoleNames.Supervisor));
            options.AddPolicy("ServiceManage", p => p.RequireRole(
                RoleNames.Admin, RoleNames.Manager, RoleNames.Supervisor));
            options.AddPolicy("ServiceDelete", p => p.RequireRole(
                RoleNames.Admin, RoleNames.Manager, RoleNames.Supervisor));
            // Correcting a booked job's descriptive fields from Global Search. Same roles as
            // ServiceManage, but the endpoint ALSO checks the ServiceRecordEditEnabled switch — the
            // role decides who may, the switch decides whether anyone may.
            options.AddPolicy("ServiceRecordEdit", p => p.RequireRole(
                RoleNames.Admin, RoleNames.Manager, RoleNames.Supervisor));
            options.AddPolicy("DispatchManage", p => p.RequireRole(
                RoleNames.Admin, RoleNames.Manager, RoleNames.Supervisor));
            options.AddPolicy("PaymentManage", p => p.RequireRole(
                RoleNames.Admin, RoleNames.Accounts));

            // Spare sales — counter sales of warehouse stock. Entering one is a commercial decision
            // (legacy restricted the page to admin/manager/supervisor); viewing adds the roles that
            // read the register: accounts bills it, store_manager picks it, viewer is read-only.
            options.AddPolicy("SaleManage", p => p.RequireRole(
                RoleNames.Admin, RoleNames.Manager, RoleNames.Supervisor));
            options.AddPolicy("SaleView", p => p.RequireRole(
                RoleNames.Admin, RoleNames.Manager, RoleNames.Supervisor, RoleNames.Viewer,
                RoleNames.Accounts, RoleNames.StoreManager));

            // Phase 5 — documents (PI / Invoice / DC). Generating is a billing action; viewing is wider.
            options.AddPolicy("DocumentManage", p => p.RequireRole(
                RoleNames.Admin, RoleNames.Manager, RoleNames.Supervisor, RoleNames.Accounts));
            options.AddPolicy("DocumentView", p => p.RequireRole(
                RoleNames.Admin, RoleNames.Manager, RoleNames.Supervisor, RoleNames.Viewer, RoleNames.Accounts));

            // Phase 6 — reports. Full reports for every non-technician role (legacy hub rule);
            // technicians still get their own performance / parts-used via the unrestricted endpoints.
            options.AddPolicy("ReportsFull", p => p.RequireRole(
                RoleNames.Admin, RoleNames.Manager, RoleNames.Supervisor, RoleNames.Viewer,
                RoleNames.StoreManager, RoleNames.Accounts));
        });
        return services;
    }
}
