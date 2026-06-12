using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PSR.Service.Api.Data;

/// <summary>
/// Used by `dotnet ef` tooling to instantiate <see cref="AppDbContext"/> at design time
/// (for migrations, scaffolding, etc.) without needing a live MySQL server to detect the
/// server version. Runtime DbContext registration in <see cref="ServiceCollectionExtensions"/>
/// uses <c>ServerVersion.AutoDetect</c> against the real DB.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql(
                "Server=localhost;Database=psr_service_design;User=design;Password=design",
                new MySqlServerVersion(new Version(8, 0, 32)),
                mysql => mysql.MigrationsAssembly(typeof(AppDbContext).Assembly.GetName().Name))
            .Options;
        return new AppDbContext(options);
    }
}
