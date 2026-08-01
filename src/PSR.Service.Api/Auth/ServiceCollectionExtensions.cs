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
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy("Admin", p => p.RequireRole(RoleNames.Admin));
            options.AddPolicy("StockView", p => p.RequireRole(
                RoleNames.Admin, RoleNames.Manager, RoleNames.Supervisor, RoleNames.Viewer, RoleNames.StoreManager));
            options.AddPolicy("StockManage", p => p.RequireRole(
                RoleNames.Admin, RoleNames.Manager, RoleNames.Supervisor, RoleNames.StoreManager));
            options.AddPolicy("ReturnAck", p => p.RequireRole(
                RoleNames.Admin, RoleNames.Manager, RoleNames.Supervisor));

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
            options.AddPolicy("DispatchManage", p => p.RequireRole(
                RoleNames.Admin, RoleNames.Manager, RoleNames.Supervisor));
            options.AddPolicy("PaymentManage", p => p.RequireRole(
                RoleNames.Admin, RoleNames.Accounts));

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
