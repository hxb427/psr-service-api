namespace PSR.Service.Api.MachineTests;

/// <summary>Config for the read-only passtest MySQL (Hostinger). The connection string is a secret —
/// set via env/user-secrets (Passtest__ConnectionString), not appsettings. Empty ⇒ feature off.</summary>
public class PasstestOptions
{
    public const string SectionName = "Passtest";
    public string ConnectionString { get; set; } = string.Empty;
    public int CommandTimeoutSeconds { get; set; } = 5;
    public int SerialCacheMinutes { get; set; } = 15;
    public int CustomersCacheMinutes { get; set; } = 60;
}

/// <summary>One serviced-unit factory-test record resolved from passtestdata, with warranty computed
/// server-side. Shape the clients bind to — decoupled from the legacy column names.</summary>
public record MachineTestDto(
    string? Model,
    string? MachineSerial,
    string? MatchedField,
    string? MatchedLabel,
    DateTime? InvoiceDate,
    string? Customer,
    string? Address,
    string WarrantyStatus,          // IN | OUT | UNKNOWN
    DateTime? WarrantyExpiry,
    int? WarrantyMonths,
    List<MachineComponentDto> Components);

public record MachineComponentDto(string Field, string Label, string Serial);

public record MachineCustomersDto(List<string> Customers);
