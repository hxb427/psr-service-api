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

    /// <summary>Timeout for the dealer-import scan. Higher than the lookup timeout: it runs a
    /// DISTINCT over the whole table, and the admin is waiting on a button, not on data entry.</summary>
    public int ScanTimeoutSeconds { get; set; } = 30;
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

/// <summary>One row of the legacy <c>dealer_warranty</c> master (the curated dealer list the old app
/// used for warranty months). Read-only source for the dealer import.</summary>
public record LegacyDealerRow(string Name, int? WarrantyMonths, string? Remarks);

/// <summary>A distinct <c>passtestdata.Customer</c> value with how many machines carry it, plus the
/// address off its most recent invoice (Address1 + Address2). The count separates real dealers
/// (many units) from one-off direct customers (usually 1).</summary>
public record LegacyCustomerRow(string Name, int MachineCount, string? Address);
