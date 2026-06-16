using Serilog;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data;
using PSR.Service.Api.Health;
using PSR.Service.Api.Logging;
using PSR.Service.Api.Reference;
using PSR.Service.Api.Users;

var builder = WebApplication.CreateBuilder(args);

builder.AddSerilogLogging();
builder.Services.AddPersistence(builder.Configuration);
builder.Services.AddAuth(builder.Configuration);
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddCors(opts => opts.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseCors();
app.UseAuthentication();
app.UseTokenVersionValidation();
app.UseAuthorization();

app.MapHealthEndpoints();
app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapRoleEndpoints();
app.MapAuditEndpoints();
app.MapPartEndpoints();
app.MapServiceChargeEndpoints();
app.MapDealerEndpoints();

await app.ApplyMigrationsAndSeedAsync();
app.Run();

public partial class Program;
