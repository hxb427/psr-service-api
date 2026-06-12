using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Data.Seed;

public static class AdminSeeder
{
    public const string DefaultAdminPassword = "ChangeMe!2026";

    public static async Task SeedAsync(AppDbContext db, IConfiguration config, ILogger logger)
    {
        if (await db.Users.AnyAsync())
        {
            logger.LogInformation("Users already exist; skipping admin seed.");
            return;
        }

        var username = config["Seed:AdminUsername"] ?? "admin";
        var password = Environment.GetEnvironmentVariable("SEED_ADMIN_PASSWORD")
                       ?? config["Seed:AdminPassword"]
                       ?? DefaultAdminPassword;
        var usingDefault = string.Equals(password, DefaultAdminPassword, StringComparison.Ordinal);

        var adminRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == RoleNames.Admin)
            ?? throw new InvalidOperationException(
                $"Role '{RoleNames.Admin}' not found. Migrations may not have seeded roles correctly.");

        var admin = new User
        {
            Username = username,
            PasswordHash = PasswordHasher.Hash(password),
            FullName = "System Administrator",
            IsActive = true,
            MustChangePassword = true,
        };
        admin.UserRoles.Add(new UserRole { Role = adminRole });

        db.Users.Add(admin);
        await db.SaveChangesAsync();

        logger.LogWarning("""

============================================================
 ADMIN USER SEEDED
   Username : {Username}
   Password : {PwdMessage}
   This account MUST change its password on first login.
============================================================
""",
            username,
            usingDefault
                ? $"{DefaultAdminPassword}  <-- DEFAULT, CHANGE IMMEDIATELY"
                : "(from SEED_ADMIN_PASSWORD env var or appsettings)");
    }
}
