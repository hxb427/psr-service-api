using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace PSR.Service.Api.MachineTests;

/// <summary>Reads the legacy <c>passtestdata</c> table straight from the Hostinger MySQL over a
/// read-only login (EC2 reaches it directly — no PHP bridge). The ONLY thing that holds that
/// connection string. Results are cached (the table changes slowly); failures degrade to null so
/// inward/service entry still work when the source is down.</summary>
public class PasstestRepository(
    IOptions<PasstestOptions> options, IMemoryCache cache, ILogger<PasstestRepository> log)
{
    private readonly PasstestOptions _opt = options.Value;

    // Every serial-bearing column (matches the old app's lookup set).
    private static readonly string[] SerialColumns =
    {
        "m_ser_no", "m_mb_no", "m_sb_no", "m_pump_no", "m_232_no", "m_dpst_no",
        "bp_no", "stirrer_no", "printer_no", "rfid_no", "gsm_no", "solarch_no",
        "adapter_no", "battery_no", "display_no", "keypad_no", "solarpanel_no",
    };

    // Component column → human label.
    private static readonly (string Field, string Label)[] ComponentMap =
    {
        ("m_mb_no", "Mainboard"), ("m_sb_no", "Sensor board"), ("m_pump_no", "Pump"),
        ("m_232_no", "RS232"), ("m_dpst_no", "DPST"), ("bp_no", "BP"), ("stirrer_no", "Stirrer"),
        ("printer_no", "Printer"), ("rfid_no", "RFID"), ("gsm_no", "GSM"), ("solarch_no", "Solar charger"),
        ("adapter_no", "Adapter"), ("battery_no", "Battery"), ("display_no", "Display"),
        ("keypad_no", "Keypad"), ("solarpanel_no", "Solar panel"),
    };

    public bool Configured => !string.IsNullOrWhiteSpace(_opt.ConnectionString);

    public async Task<MachineTestDto?> FindBySerialAsync(string serial, int? warrantyMonths, CancellationToken ct)
    {
        var sn = serial.Trim();
        if (sn.Length == 0 || !Configured) return null;

        var cacheKey = $"passtest:sn:{sn.ToLowerInvariant()}";
        if (!cache.TryGetValue(cacheKey, out Dictionary<string, string>? row) || row is null)
        {
            row = await FetchRowAsync(sn, ct);
            if (row is not null) cache.Set(cacheKey, row, TimeSpan.FromMinutes(_opt.SerialCacheMinutes));
        }
        return row is null ? null : Map(row, warrantyMonths);
    }

    public async Task<List<string>> CustomersAsync(CancellationToken ct)
    {
        if (!Configured) return [];
        if (cache.TryGetValue("passtest:customers", out List<string>? cached) && cached is not null)
            return cached;

        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _opt.CommandTimeoutSeconds;
            cmd.CommandText =
                "SELECT DISTINCT Customer FROM passtestdata " +
                "WHERE Customer IS NOT NULL AND TRIM(Customer) <> '' ORDER BY Customer";
            var list = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                if (!reader.IsDBNull(0)) list.Add(reader.GetString(0));

            cache.Set("passtest:customers", list, TimeSpan.FromMinutes(_opt.CustomersCacheMinutes));
            return list;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Passtest customers query failed");
            return [];
        }
    }

    private async Task<Dictionary<string, string>?> FetchRowAsync(string sn, CancellationToken ct)
    {
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _opt.CommandTimeoutSeconds;
            // One query: OR across every serial column (case-insensitive exact — MySQL collation is CI).
            var where = string.Join(" OR ", SerialColumns.Select(c => $"TRIM(`{c}`) = @sn"));
            cmd.CommandText = $"SELECT * FROM passtestdata WHERE {where} LIMIT 1";
            cmd.Parameters.AddWithValue("@sn", sn);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;

            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? "" : reader.GetValue(i)?.ToString() ?? "";

            row["__matchedField"] = SerialColumns.FirstOrDefault(c =>
                row.TryGetValue(c, out var v) && string.Equals(v.Trim(), sn, StringComparison.OrdinalIgnoreCase)) ?? "";
            return row;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Passtest by-serial query failed for {Serial}", sn);
            return null;
        }
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new MySqlConnection(_opt.ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static MachineTestDto Map(Dictionary<string, string> row, int? warrantyMonths)
    {
        string? Get(string k) => row.TryGetValue(k, out var v) && v.Trim().Length > 0 ? v.Trim() : null;

        var invDate = DateTime.TryParse(Get("InvDate"), out var d) ? d : (DateTime?)null;
        var matched = Get("__matchedField");

        var components = ComponentMap
            .Select(c => (c.Field, c.Label, Serial: Get(c.Field)))
            .Where(x => x.Serial is not null)
            .Select(x => new MachineComponentDto(x.Field, x.Label, x.Serial!))
            .ToList();

        var addr = string.Join(", ", new[] { Get("Address1"), Get("Address2") }.Where(s => s is not null));

        string status = "UNKNOWN";
        DateTime? expiry = null;
        if (invDate is { } inv && warrantyMonths is { } months && months > 0)
        {
            expiry = inv.AddMonths(months);
            status = DateTime.UtcNow.Date <= expiry.Value.Date ? "IN" : "OUT";
        }

        return new MachineTestDto(
            Get("m_model"), Get("m_ser_no"),
            matched, matched is null ? null : ComponentMap.FirstOrDefault(c => c.Field == matched).Label ?? "Machine",
            invDate, Get("Customer"), addr.Length > 0 ? addr : null,
            status, expiry, warrantyMonths, components);
    }
}
