using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace PSR.Service.Api.Logging;

public static class SerilogExtensions
{
    /// <summary>Structured logging: human console (docker logs) + daily-rolling compact-JSON file
    /// behind an async sink so request threads never block on disk I/O. Framework/EF noise capped
    /// at Warning; app logs at Information. appsettings "Serilog" section can override.</summary>
    public static void AddSerilogLogging(this WebApplicationBuilder builder)
    {
        builder.Host.UseSerilog((ctx, services, cfg) =>
        {
            cfg.MinimumLevel.Information()
               .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
               .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
               .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
               .ReadFrom.Configuration(ctx.Configuration)
               .ReadFrom.Services(services)
               .Enrich.FromLogContext()
               .WriteTo.Console()
               .WriteTo.Async(a => a.File(
                   path: "logs/api-.json",
                   formatter: new CompactJsonFormatter(),
                   rollingInterval: RollingInterval.Day,
                   retainedFileCountLimit: 30,
                   fileSizeLimitBytes: 50 * 1024 * 1024,
                   rollOnFileSizeLimit: true,
                   shared: false,
                   buffered: false));
        });
    }

    /// <summary>Request logging with actor context: every request line carries UserId, Username,
    /// and client IP; health-check chatter demoted to Debug so the file stays signal.</summary>
    public static IApplicationBuilder UseSerilogRequestLoggingWithContext(this WebApplication app)
    {
        return app.UseSerilogRequestLogging(opts =>
        {
            opts.MessageTemplate =
                "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0} ms";
            opts.GetLevel = (http, _, ex) =>
                ex is not null || http.Response.StatusCode >= 500 ? LogEventLevel.Error
                : http.Request.Path.StartsWithSegments("/health") ? LogEventLevel.Debug
                : LogEventLevel.Information;
            opts.EnrichDiagnosticContext = (diag, http) =>
            {
                diag.Set("ClientIp", http.Connection.RemoteIpAddress?.ToString());
                var user = http.User;
                if (user?.Identity?.IsAuthenticated == true)
                {
                    diag.Set("UserId", user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                        ?? user.FindFirst("sub")?.Value);
                    diag.Set("Username", user.Identity.Name
                        ?? user.FindFirst("unique_name")?.Value);
                }
            };
        });
    }
}
