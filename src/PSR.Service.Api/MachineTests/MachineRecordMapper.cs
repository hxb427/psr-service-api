namespace PSR.Service.Api.MachineTests;

/// <summary>Turns a raw passtestdata row into the shape the SN Info page binds to: the whole row in
/// column order with readable labels, plus the handful of fields a result list needs and the warranty
/// worked out from the row's own figures.</summary>
public static class MachineRecordMapper
{
    /// <summary>Legacy column names are terse and inconsistent ("m_232_no", "bp_no"). Anything not
    /// listed falls back to a prettified column name, so a column added to passtestdata later still
    /// shows up readably instead of being dropped.</summary>
    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["m_model"] = "Model",
        ["m_ser_no"] = "Machine serial",
        ["m_mb_no"] = "Mainboard",
        ["m_sb_no"] = "Sensor board",
        ["m_pump_no"] = "Pump",
        ["m_232_no"] = "RS232",
        ["m_dpst_no"] = "DPST",
        ["bp_no"] = "BP",
        ["stirrer_no"] = "Stirrer",
        ["printer_no"] = "Printer",
        ["rfid_no"] = "RFID",
        ["gsm_no"] = "GSM",
        ["solarch_no"] = "Solar charger",
        ["adapter_no"] = "Adapter",
        ["battery_no"] = "Battery",
        ["display_no"] = "Display",
        ["keypad_no"] = "Keypad",
        ["solarpanel_no"] = "Solar panel",
        ["InvDate"] = "Purchase date",
        ["Customer"] = "Customer",
        ["Address1"] = "Address line 1",
        ["Address2"] = "Address line 2",
        ["Warranty"] = "Warranty",
    };

    /// <summary>The columns a search term can land on, in the order they should be reported. Machine
    /// serial first so a hit on the unit itself is never described as a hit on one of its parts.</summary>
    private static readonly string[] MatchableColumns =
    [
        "m_ser_no", "m_mb_no", "m_sb_no", "m_pump_no", "m_232_no", "m_dpst_no", "bp_no",
        "stirrer_no", "printer_no", "rfid_no", "gsm_no", "solarch_no", "adapter_no",
        "battery_no", "display_no", "keypad_no", "solarpanel_no", "Customer", "m_model",
    ];

    public static string Label(string column) =>
        Labels.TryGetValue(column, out var l) ? l : Prettify(column);

    public static MachineRecordDto Map(MachineRawRow row, string? term = null)
    {
        var invDate = PasstestRepository.ParseLegacyDate(row.Get("InvDate"));
        var months = PasstestRepository.ParseWarrantyMonths(row.Get("Warranty"));

        string status = "UNKNOWN";
        DateTime? expiry = null;
        if (invDate is { } inv && months is { } m && m > 0)
        {
            expiry = inv.AddMonths(m);
            // Whole months elapsed, matching the legacy dialog: the invoice anniversary is the first
            // day OUT, not the last day IN.
            var today = DateTime.UtcNow.Date;
            var elapsed = ((today.Year - inv.Year) * 12) + (today.Month - inv.Month);
            if (today.Day < inv.Day) elapsed--;
            status = elapsed < m ? "IN" : "OUT";
        }

        var (matchedField, matchedValue) = FindMatch(row, term);

        var fields = row.Cells
            .Select(c => new MachineFieldDto(c.Key, Label(c.Key), c.Value.Trim() is { Length: > 0 } v ? v : null))
            .ToList();

        return new MachineRecordDto(
            row.Get("m_ser_no"), row.Get("m_model"), row.Get("Customer"), invDate,
            matchedField, matchedField is null ? null : Label(matchedField), matchedValue,
            months, expiry, status, fields);
    }

    /// <summary>Which column the search term hit. Without it a result list is a wall of identical
    /// rows — the user needs to see that this row surfaced because of its pump serial.</summary>
    private static (string? Field, string? Value) FindMatch(MachineRawRow row, string? term)
    {
        var t = term?.Trim();
        if (string.IsNullOrEmpty(t)) return (null, null);

        foreach (var column in MatchableColumns)
        {
            var value = row.Get(column);
            if (value is not null && value.Contains(t, StringComparison.OrdinalIgnoreCase))
                return (column, value);
        }
        return (null, null);
    }

    /// <summary>"solarch_no" -> "Solarch", "some_field" -> "Some field". The trailing "_no" is the
    /// legacy suffix for a serial column and reads as noise once the column is labelled.</summary>
    private static string Prettify(string column)
    {
        var s = column.Replace('_', ' ').Trim();
        if (s.EndsWith(" no", StringComparison.OrdinalIgnoreCase)) s = s[..^3].Trim();
        if (s.Length == 0) return column;
        return char.ToUpperInvariant(s[0]) + s[1..];
    }
}
