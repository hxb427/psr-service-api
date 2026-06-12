using Serilog;
using Serilog.Formatting.Compact;

namespace PSR.Service.Api.Logging;

public static class SerilogExtensions
{
    public static void AddSerilogLogging(this WebApplicationBuilder builder)
    {
        builder.Host.UseSerilog((ctx, services, cfg) =>
        {
            cfg.ReadFrom.Configuration(ctx.Configuration)
               .ReadFrom.Services(services)
               .Enrich.FromLogContext()
               .WriteTo.Console()
               .WriteTo.File(
                   path: "logs/api-.json",
                   formatter: new CompactJsonFormatter(),
                   rollingInterval: RollingInterval.Day,
                   retainedFileCountLimit: 30,
                   fileSizeLimitBytes: 50 * 1024 * 1024,
                   rollOnFileSizeLimit: true);
        });
    }
}
