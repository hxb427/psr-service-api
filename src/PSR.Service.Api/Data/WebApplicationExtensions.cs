using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data.Seed;

namespace PSR.Service.Api.Data;

public static class WebApplicationExtensions
{
    public static async Task ApplyMigrationsAndSeedAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<AppDbContext>>();

        try
        {
            logger.LogInformation("Applying database migrations...");
            await db.Database.MigrateAsync();
            logger.LogInformation("Migrations applied.");

            await AdminSeeder.SeedAsync(db, app.Configuration, logger);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Failed to apply migrations or seed admin user.");
            throw;
        }
    }
}
