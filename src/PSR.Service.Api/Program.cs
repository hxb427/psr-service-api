using Serilog;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data;
using PSR.Service.Api.Documents;
using PSR.Service.Api.Health;
using PSR.Service.Api.Logging;
using PSR.Service.Api.Reference;
using PSR.Service.Api.Reports;
using PSR.Service.Api.Services;
using PSR.Service.Api.Settings;
using PSR.Service.Api.Stock;
using PSR.Service.Api.Users;

// QuestPDF Community licence (free for orgs under $1M revenue — applies here).
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);

builder.AddSerilogLogging();
builder.Services.AddPersistence(builder.Configuration);
builder.Services.AddAuth(builder.Configuration);
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<NumberSequenceService>();
builder.Services.AddScoped<StockLedgerService>();
builder.Services.AddScoped<SerialService>();
builder.Services.AddScoped<BillingService>();
builder.Services.AddScoped<AppSettingsService>();
builder.Services.AddSingleton(
    builder.Configuration.GetSection(CompanyInfo.SectionName).Get<CompanyInfo>() ?? new CompanyInfo());
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
app.MapStockEndpoints();
app.MapStockRequestEndpoints();
app.MapStockReturnEndpoints();
app.MapSerialEndpoints();
app.MapStockAckEndpoints();
app.MapTransferEndpoints();
app.MapFieldOpsEndpoints();
app.MapCustomerEndpoints();
app.MapServiceEndpoints();
app.MapDocumentEndpoints();
app.MapSettingsEndpoints();
app.MapReportsEndpoints();

await app.ApplyMigrationsAndSeedAsync();
app.Run();

public partial class Program;
