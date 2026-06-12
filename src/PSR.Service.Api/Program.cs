using Serilog;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data;
using PSR.Service.Api.Health;
using PSR.Service.Api.Logging;

var builder = WebApplication.CreateBuilder(args);

builder.AddSerilogLogging();
builder.Services.AddPersistence(builder.Configuration);
builder.Services.AddAuth(builder.Configuration);
builder.Services.AddMemoryCache();
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

await app.ApplyMigrationsAndSeedAsync();
app.Run();

public partial class Program;
