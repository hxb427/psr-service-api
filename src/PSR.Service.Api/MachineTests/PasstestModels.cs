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
    string? WarrantyMonthsSource,   // machine | dealer — which term produced the verdict
    List<MachineComponentDto> Components);

public record MachineComponentDto(string Field, string Label, string Serial);

/// <summary>Every factory record carrying a serial, newest first. Inward autofill asks for this rather
/// than for a single record: a re-tested unit has more than one row, and a component serial belongs to
/// every machine it was fitted to, so which row is the right one is the operator's call, not the
/// database's.</summary>
public record MachineTestMatchesDto(List<MachineTestDto> Matches, bool Truncated);

public record MachineCustomersDto(List<string> Customers);

/// <summary>One row of the legacy <c>dealer_warranty</c> master (the curated dealer list the old app
/// used for warranty months). Read-only source for the dealer import.</summary>
public record LegacyDealerRow(string Name, int? WarrantyMonths, string? Remarks);

/// <summary>A distinct <c>passtestdata.Customer</c> value with how many machines carry it, plus the
/// address and raw warranty text off its most recent invoice. The count separates real dealers
/// (many units) from one-off direct customers (usually 1); the warranty text is the machine term
/// this buyer was last sold on, which beats the house default as an import suggestion.</summary>
public record LegacyCustomerRow(string Name, int MachineCount, string? Address, string? WarrantyText);

/// <summary>One passtestdata row exactly as stored — every column, in column order, stringified.
/// Backs the SN Info page, which shows the whole row rather than the curated warranty subset.</summary>
public record MachineRawRow(List<KeyValuePair<string, string>> Cells)
{
    public string? Get(string column)
    {
        foreach (var c in Cells)
            if (string.Equals(c.Key, column, StringComparison.OrdinalIgnoreCase))
                return c.Value.Trim() is { Length: > 0 } v ? v : null;
        return null;
    }
}

/// <summary>One column of a passtestdata row, ready to display.</summary>
public record MachineFieldDto(string Column, string Label, string? Value);

/// <summary>A passtestdata row for the SN Info page: a few fields lifted out so a result list can be
/// rendered, the computed warranty, and then the complete row in column order.</summary>
public record MachineRecordDto(
    string? MachineSerial,
    string? Model,
    string? Customer,
    DateTime? InvoiceDate,
    string? MatchedField,
    string? MatchedLabel,
    string? MatchedValue,
    int? WarrantyMonths,
    DateTime? WarrantyExpiry,
    string WarrantyStatus,          // IN | OUT | UNKNOWN
    List<MachineFieldDto> Fields);

public record MachineSearchDto(List<MachineRecordDto> Records, int Limit, bool Truncated);
