using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data;

namespace PSR.Service.Api.Health;

public static class HealthEndpoints
{
    private static readonly DateTime StartedAt = DateTime.UtcNow;

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", [AllowAnonymous] async (AppDbContext db, CancellationToken ct) =>
        {
            bool dbOk;
            try
            {
                dbOk = await db.Database.CanConnectAsync(ct);
            }
            catch
            {
                dbOk = false;
            }

            var response = new
            {
                status = dbOk ? "ok" : "degraded",
                version = typeof(HealthEndpoints).Assembly.GetName().Version?.ToString() ?? "unknown",
                uptimeSeconds = (long)(DateTime.UtcNow - StartedAt).TotalSeconds,
                dbConnected = dbOk,
                serverTimeUtc = DateTime.UtcNow,
            };

            return Results.Json(response, statusCode: dbOk ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        }).WithTags("health");

        return app;
    }
}
