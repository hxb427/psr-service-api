using Microsoft.EntityFrameworkCore;

namespace PSR.Service.Api.Data;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

        services.AddDbContext<AppDbContext>(opts =>
            opts.UseMySql(
                connectionString,
                ServerVersion.AutoDetect(connectionString),
                mysql =>
                {
                    mysql.MigrationsAssembly(typeof(AppDbContext).Assembly.GetName().Name);
                    // Never let one statement hold a request open longer than the client will wait
                    // (the WPF HttpClient.Timeout is 30s): a stuck query now fails fast and lands in
                    // the log as an error instead of surfacing to the user as a silent 30s hang.
                    //
                    // Deliberately NOT paired with EnableRetryOnFailure. The retrying execution
                    // strategy refuses user-initiated transactions, and ~29 handlers across stock,
                    // billing, sales and services open one with BeginTransactionAsync — every write
                    // endpoint would throw on the first call. Adding it means wrapping all of them in
                    // Database.CreateExecutionStrategy().ExecuteAsync(...) and making each delegate
                    // safely re-runnable, which is a much larger change than the evidence justifies:
                    // the slowest of 20,435 logged requests was 1527ms, so the DB has never been the
                    // stall. Revisit only if RDS actually starts dropping connections.
                    mysql.CommandTimeout(20);
                }
            ));

        return services;
    }
}
