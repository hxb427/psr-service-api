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

    /// <summary>The most recent record for a serial, or null. Callers that only want one answer — the
    /// dashboard's warranty check, the global-search serial panel — use this; inward autofill asks for
    /// every match instead, because picking the wrong one there fills a form with another unit's
    /// details.
    ///
    /// <paramref name="fallbackMonths"/> is only used when the machine's own row carries no warranty
    /// term. passtestdata.Warranty is per-machine — what was actually sold — so it outranks the
    /// dealer's blanket term and the house default.</summary>
    public async Task<MachineTestDto?> FindBySerialAsync(string serial, int? fallbackMonths, CancellationToken ct)
        => (await FindMatchesBySerialAsync(serial, fallbackMonths, 1, ct)).FirstOrDefault();

    /// <summary>Every passtestdata row carrying this serial in any of its serial columns, newest
    /// invoice first.
    ///
    /// More than one is normal rather than exceptional: a unit that came back through the factory is
    /// tested again and gets a second row, and a component serial is reused across the machines it
    /// was fitted to. The old lookup took whichever row the server happened to return first, so an
    /// inward form could be autofilled from a five-year-old test of the same unit.</summary>
    public async Task<List<MachineTestDto>> FindMatchesBySerialAsync(
        string serial, int? fallbackMonths, int limit, CancellationToken ct)
    {
        var sn = serial.Trim();
        if (sn.Length == 0 || !Configured) return [];

        // The cache is keyed by serial AND row cap: a one-row hit must not answer a request for all
        // of them. Both keys hold the same rows for the same serial, so the extra entry is cheap.
        var take = Math.Clamp(limit, 1, 50);
        var cacheKey = $"passtest:sn:{take}:{sn.ToLowerInvariant()}";
        if (!cache.TryGetValue(cacheKey, out List<Dictionary<string, string>>? rows) || rows is null)
        {
            rows = await FetchRowsAsync(sn, take, ct);
            if (rows is not null) cache.Set(cacheKey, rows, TimeSpan.FromMinutes(_opt.SerialCacheMinutes));
        }
        return rows is null ? [] : rows.Select(r => Map(r, fallbackMonths)).ToList();
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

    /// <summary>The legacy <c>dealer_warranty</c> master — the curated dealer list, and the only
    /// legacy source that carries warranty months. Uncached (the import button must see live data).
    /// Returns null when the table is unreachable (e.g. the read-only login was never granted
    /// SELECT on it) so the caller can say so instead of reporting "no dealers found".</summary>
    public async Task<List<LegacyDealerRow>?> ScanLegacyDealersAsync(CancellationToken ct)
    {
        if (!Configured) return null;
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _opt.ScanTimeoutSeconds;
            cmd.CommandText =
                "SELECT Dealer, Warranty, Remarks FROM dealer_warranty " +
                "WHERE Dealer IS NOT NULL AND TRIM(Dealer) <> '' ORDER BY Dealer";

            var rows = new List<LegacyDealerRow>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(0).Trim();
                if (name.Length == 0) continue;
                int? months = reader.IsDBNull(1) ? null
                    : int.TryParse(reader.GetValue(1)?.ToString(), out var m) ? m : null;
                var remarks = reader.IsDBNull(2) ? null : reader.GetString(2).Trim();
                rows.Add(new LegacyDealerRow(name, months, remarks?.Length > 0 ? remarks : null));
            }
            return rows;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Legacy dealer_warranty scan failed");
            return null;
        }
    }

    /// <summary>Distinct customer names off passtestdata with their machine counts, bypassing the
    /// 60-minute cache used by <see cref="CustomersAsync"/> — the import button must not show
    /// hour-stale names. Grouping costs the same as DISTINCT and the count tells the admin which
    /// names are dealers and which are one-off direct customers. Null on failure.</summary>
    public async Task<List<LegacyCustomerRow>?> ScanCustomersAsync(CancellationToken ct)
    {
        if (!Configured) return null;
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _opt.ScanTimeoutSeconds;
            // Address1 + Address2 off the customer's most recent invoice — rows with a blank address
            // sort last so an old row with an address beats a new row without one. GROUP_CONCAT is
            // capped at 1024 bytes but only the first element is read, so the cap can't corrupt it.
            cmd.CommandText =
                "SELECT TRIM(Customer) AS name, COUNT(*) AS machines, " +
                "SUBSTRING_INDEX(GROUP_CONCAT(" +
                "  CONCAT_WS(', ', NULLIF(TRIM(Address1), ''), NULLIF(TRIM(Address2), '')) " +
                "  ORDER BY (CONCAT_WS('', TRIM(Address1), TRIM(Address2)) <> '') DESC, InvDate DESC " +
                "  SEPARATOR '~|~'), '~|~', 1) AS addr, " +
                "SUBSTRING_INDEX(GROUP_CONCAT(" +
                "  IFNULL(TRIM(Warranty), '') " +
                "  ORDER BY (TRIM(IFNULL(Warranty, '')) <> '') DESC, InvDate DESC " +
                "  SEPARATOR '~|~'), '~|~', 1) AS warr " +
                "FROM passtestdata " +
                "WHERE Customer IS NOT NULL AND TRIM(Customer) <> '' " +
                "GROUP BY TRIM(Customer) ORDER BY machines DESC";

            var list = new List<LegacyCustomerRow>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reader.IsDBNull(0)) continue;
                var addr = reader.IsDBNull(2) ? null : reader.GetString(2).Trim();
                var warr = reader.IsDBNull(3) ? null : reader.GetString(3).Trim();
                list.Add(new LegacyCustomerRow(
                    reader.GetString(0), reader.GetInt32(1),
                    addr?.Length > 0 ? addr : null, warr?.Length > 0 ? warr : null));
            }
            return list;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Passtest customer scan failed");
            return null;
        }
    }

    /// <summary>Rows whose serial, customer or model contains <paramref name="term"/>, newest invoice
    /// first. Backs the SN Info page, so it returns whole rows in column order — a partial match can
    /// land on any of the eighteen serial columns and the point of the page is to show which.
    /// Uncached: a search is expected to reflect the server as it is now. Null on failure.</summary>
    public async Task<List<MachineRawRow>?> SearchAsync(string term, int limit, CancellationToken ct)
    {
        var q = term.Trim();
        if (q.Length == 0 || !Configured) return null;

        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _opt.ScanTimeoutSeconds;

            // Column list is a fixed allowlist; only the term is user input, and it is parameterised.
            var cols = SerialColumns.Concat(["Customer", "m_model"]);
            var where = string.Join(" OR ", cols.Select(c => $"`{c}` LIKE @q"));
            cmd.CommandText = $"SELECT * FROM passtestdata WHERE {where} ORDER BY InvDate DESC LIMIT {limit}";
            cmd.Parameters.AddWithValue("@q", $"%{Escape(q)}%");

            var rows = new List<MachineRawRow>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) rows.Add(new MachineRawRow(ReadCells(reader)));
            return rows;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Passtest search failed for {Term}", q);
            return null;
        }
    }

    /// <summary>LIKE wildcards typed into the search box are literal characters, not operators — a
    /// bare "%" would otherwise match every row in the table.</summary>
    private static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>Every column of the current row, in column order. Dates go in ISO rather than the
    /// host's culture: a DATE stringified as "01/02/2024" is unreadable — 1 Feb or 2 Jan depending
    /// on where the container happens to run.</summary>
    private static List<KeyValuePair<string, string>> ReadCells(System.Data.Common.DbDataReader reader)
    {
        var cells = new List<KeyValuePair<string, string>>(reader.FieldCount);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var value = reader.IsDBNull(i) ? "" : reader.GetValue(i) switch
            {
                DateTime dt => dt.ToString("yyyy-MM-dd"),
                var v => v?.ToString() ?? "",
            };
            cells.Add(new KeyValuePair<string, string>(reader.GetName(i), value));
        }
        return cells;
    }

    /// <summary>Rows whose serial columns hold <paramref name="sn"/> exactly, newest invoice first.
    /// Null (not an empty list) when the query itself failed, so the caller can tell "source down"
    /// apart from "no such serial".</summary>
    private async Task<List<Dictionary<string, string>>?> FetchRowsAsync(string sn, int limit, CancellationToken ct)
    {
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _opt.CommandTimeoutSeconds;
            // One query: OR across every serial column (case-insensitive exact — MySQL collation is CI).
            // Ordered so the newest test of a re-worked unit is the one a single-row caller sees.
            var where = string.Join(" OR ", SerialColumns.Select(c => $"TRIM(`{c}`) = @sn"));
            cmd.CommandText = $"SELECT * FROM passtestdata WHERE {where} ORDER BY InvDate DESC LIMIT {limit}";
            cmd.Parameters.AddWithValue("@sn", sn);

            var rows = new List<Dictionary<string, string>>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var cell in ReadCells(reader)) row[cell.Key] = cell.Value;

                row["__matchedField"] = SerialColumns.FirstOrDefault(c =>
                    row.TryGetValue(c, out var v) && string.Equals(v.Trim(), sn, StringComparison.OrdinalIgnoreCase)) ?? "";
                rows.Add(row);
            }
            return rows;
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

    private static MachineTestDto Map(Dictionary<string, string> row, int? fallbackMonths)
    {
        string? Get(string k) => row.TryGetValue(k, out var v) && v.Trim().Length > 0 ? v.Trim() : null;

        var invDate = ParseLegacyDate(Get("InvDate"));
        var matched = Get("__matchedField");

        var components = ComponentMap
            .Select(c => (c.Field, c.Label, Serial: Get(c.Field)))
            .Where(x => x.Serial is not null)
            .Select(x => new MachineComponentDto(x.Field, x.Label, x.Serial!))
            .ToList();

        var addr = string.Join(", ", new[] { Get("Address1"), Get("Address2") }.Where(s => s is not null));

        // The machine's own term wins: it is what was sold with this unit. The dealer term (or the
        // house default) only answers for units whose row has no warranty figure.
        var machineMonths = ParseWarrantyMonths(Get("Warranty"));
        var months = machineMonths ?? fallbackMonths;
        var source = machineMonths is not null ? "machine" : months is not null ? "dealer" : null;

        string status = "UNKNOWN";
        DateTime? expiry = null;
        if (invDate is { } inv && months is { } m && m > 0)
        {
            expiry = inv.AddMonths(m);
            // Whole months elapsed, matching the legacy dialog: the anniversary of the invoice is the
            // first day OUT, not the last day IN.
            var today = DateTime.UtcNow.Date;
            var elapsed = ((today.Year - inv.Year) * 12) + (today.Month - inv.Month);
            if (today.Day < inv.Day) elapsed--;
            status = elapsed < m ? "IN" : "OUT";
        }

        return new MachineTestDto(
            Get("m_model"), Get("m_ser_no"),
            matched, matched is null ? null : ComponentMap.FirstOrDefault(c => c.Field == matched).Label ?? "Machine",
            invDate, Get("Customer"), addr.Length > 0 ? addr : null,
            status, expiry, months, source, components);
    }

    /// <summary>Warranty months out of the legacy free-text <c>Warranty</c> column. It is a VARCHAR
    /// filled in by hand, so it holds "24", "12 months" and "15/r" alike — the "/r" suffix marks a
    /// replacement unit and the months are the part before the slash. Mirrors the old app's parse so
    /// the same row yields the same verdict in both.</summary>
    public static int? ParseWarrantyMonths(string? raw)
    {
        var s = raw?.Trim();
        if (string.IsNullOrEmpty(s)) return null;

        if (s.Contains('/'))
            return int.TryParse(s.Split('/')[0].Trim(), out var slashed) && slashed > 0 ? slashed : null;

        var digits = System.Text.RegularExpressions.Regex.Match(s, @"\d+");
        return digits.Success && int.TryParse(digits.Value, out var n) && n > 0 ? n : null;
    }

    /// <summary>Invoice (purchase) date. A real DATE column arrives ISO from FetchRowAsync, but the
    /// field has held free text too, so read it the way the old app did: four leading digits mean
    /// y-m-d, anything else is the Indian d-m-y it was typed in. Deliberately not DateTime.TryParse —
    /// that reads "01/02/2024" as 2 January under the invariant culture, and this data means
    /// 1 February. Guessing the wrong way round moves a warranty verdict by months.</summary>
    public static DateTime? ParseLegacyDate(string? raw)
    {
        var s = raw?.Trim();
        if (string.IsNullOrEmpty(s)) return null;

        var parts = s.Split(' ')[0].Split('/', '-');   // drop any time component
        if (parts.Length != 3) return null;
        if (!int.TryParse(parts[0], out var p0) || !int.TryParse(parts[1], out var p1)
            || !int.TryParse(parts[2], out var p2)) return null;

        var (year, month, day) = parts[0].Length == 4 ? (p0, p1, p2) : (p2, p1, p0);
        try { return new DateTime(year, month, day); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
