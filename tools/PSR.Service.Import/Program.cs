using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;

// PSR.Service.Import — one-time, idempotent importer for reference data.
// Reads the OLD MySQL directly (clean column names), maps old -> new schema, and UPSERTs by
// natural key into the target DB. Safe by default (dry run); pass --apply to write.
//
// Usage:
//   dotnet run --project tools/PSR.Service.Import                 (dry run, uses appsettings)
//   dotnet run --project tools/PSR.Service.Import -- --apply      (writes)
//   ... -- --source "<old-conn>" --target "<new-conn>" --apply
//   or set IMPORT_SOURCE / IMPORT_TARGET env vars.

bool apply = args.Contains("--apply");
string? source = GetArg("--source") ?? Environment.GetEnvironmentVariable("IMPORT_SOURCE");
string? target = GetArg("--target") ?? Environment.GetEnvironmentVariable("IMPORT_TARGET");

var cfgPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
if ((source is null || target is null) && File.Exists(cfgPath))
{
    using var doc = JsonDocument.Parse(File.ReadAllText(cfgPath));
    var root = doc.RootElement;
    source ??= root.GetProperty("Source").GetProperty("ConnectionString").GetString();
    target ??= root.GetProperty("Target").GetProperty("ConnectionString").GetString();
}

if (string.IsNullOrWhiteSpace(source) || source.Contains("CHANGE_ME"))
{
    Console.Error.WriteLine("Source connection not configured (appsettings Source / IMPORT_SOURCE / --source).");
    return 1;
}
if (string.IsNullOrWhiteSpace(target) || target.Contains("CHANGE_ME"))
{
    Console.Error.WriteLine("Target connection not configured (appsettings Target / IMPORT_TARGET / --target).");
    return 1;
}

Console.WriteLine(apply
    ? "MODE: APPLY — changes WILL be written to the target."
    : "MODE: DRY RUN — nothing is written. Pass --apply to commit.");
Console.WriteLine($"Source : {Token(source, "Server")} / {Token(source, "Database")}");
Console.WriteLine($"Target : {Token(target, "Server")} / {Token(target, "Database")}");
Console.WriteLine(new string('-', 64));

// Connect to source first + list its tables, so this diagnostic prints even if the
// target is unreachable (e.g. RDS with public access disabled).
using var src = new MySqlConnection(source);
try { src.Open(); }
catch (Exception ex) { Console.Error.WriteLine($"Source connect failed: {ex.Message}"); return 1; }

var tables = new List<string>();
using (var tc = src.CreateCommand())
{
    tc.CommandText = "SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE()";
    using var tr = tc.ExecuteReader();
    while (tr.Read()) tables.Add(tr.GetString(0));
}
Console.WriteLine($"Source has {tables.Count} table(s): {string.Join(", ", tables.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))}");
Console.WriteLine(new string('-', 64));

var options = new DbContextOptionsBuilder<AppDbContext>()
    .UseMySql(target, new MySqlServerVersion(new Version(8, 0, 32)))
    .Options;
using var db = new AppDbContext(options);

try { db.Database.Migrate(); }
catch (Exception ex)
{
    Console.Error.WriteLine($"Target connect/migrate failed: {ex.Message}");
    Console.Error.WriteLine("If the target is the live RDS: it has no public access — connect via an SSH tunnel through EC2 (see tool README), or run this on the EC2 box.");
    return 1;
}

var results = new List<ImportResult>
{
    RunOne("parts", new[] { "price_master", "pricemaster", "price_list", "pricelist" }, t => Importers.ImportParts(src, db, t)),
    RunOne("service_charges", new[] { "servicecharge", "service_charge", "servicecharges" }, t => Importers.ImportServiceCharges(src, db, t)),
    RunOne("dealers", new[] { "dealer_warranty", "dealerwarranty", "dealer", "dealers" }, t => Importers.ImportDealers(src, db, t)),
};

if (apply)
{
    var saved = await db.SaveChangesAsync();
    Console.WriteLine($"\nSaved {saved} change(s).");
}
else
{
    Console.WriteLine("\nDry run complete — no changes saved.");
}

Console.WriteLine(new string('-', 64));
Console.WriteLine($"{"Table",-16}{"Source",-18}{"Read",6}{"Insert",8}{"Update",8}{"Dup",6}{"Skip",6}");
foreach (var r in results)
{
    var srcName = r.Missing ? "(not found)" : r.SourceTable;
    Console.WriteLine($"{r.Table,-16}{srcName,-18}{r.Read,6}{r.Inserted,8}{r.Updated,8}{r.Duplicates,6}{r.Skipped,6}");
}
return 0;

string? GetArg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

ImportResult RunOne(string label, string[] candidates, Func<string, ImportResult> run)
{
    var table = candidates.FirstOrDefault(c => tables.Contains(c, StringComparer.OrdinalIgnoreCase));
    if (table is null)
    {
        Console.WriteLine($"  {label}: no source table found (tried {string.Join(" / ", candidates)}) — skipped");
        return new ImportResult(label) { Missing = true };
    }
    try
    {
        var r = run(table);
        r.SourceTable = table;
        Console.WriteLine($"  {label}: from '{table}' — read {r.Read}, +{r.Inserted} new, ~{r.Updated} updated");
        return r;
    }
    catch (MySqlException ex)
    {
        Console.WriteLine($"  {label}: source error on '{table}': {ex.Message} — skipped");
        return new ImportResult(label) { Missing = true };
    }
}

static string Token(string conn, string key) =>
    conn.Split(';').FirstOrDefault(p => p.Trim().StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
        ?.Split('=', 2)[1] ?? "?";

internal sealed class ImportResult(string table)
{
    public string Table { get; } = table;
    public string SourceTable { get; set; } = "";
    public bool Missing { get; set; }
    public int Read, Inserted, Updated, Duplicates, Skipped;
}

internal static class Importers
{
    public static ImportResult ImportParts(MySqlConnection src, AppDbContext db, string table)
    {
        var res = new ImportResult("parts");
        var existing = db.Parts.ToList().ToDictionary(p => p.ItemCode, p => p, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var cmd = src.CreateCommand();
        cmd.CommandText = $"SELECT * FROM `{table}`";
        using var r = cmd.ExecuteReader();
        var cols = ColMap(r);

        while (r.Read())
        {
            res.Read++;
            var code = Trim(Str(r, cols, "ItemCode", "ITEM CODE", "Item Code", "item_pscode", "pscode"));
            if (string.IsNullOrWhiteSpace(code)) { res.Skipped++; continue; }
            if (!seen.Add(code)) { res.Duplicates++; continue; }

            var name = Trim(Str(r, cols, "ItemName", "ITEM NAME", "Item Name", "item_name", "name")) ?? code;
            var isNew = !existing.TryGetValue(code, out var part);
            if (isNew) { part = new Part { ItemCode = code }; db.Parts.Add(part); }

            part!.Name = name;
            part.Category = Trim(Str(r, cols, "Group", "Category"));
            part.Unit = Trim(Str(r, cols, "Unit"));
            part.PurchaseRate = Dec(r, cols, "PurchaseRate");
            part.DealerRate = Dec(r, cols, "DealerRate");
            part.CustomerRate = Dec(r, cols, "CustomerRate");
            part.HsnCode = Trim(Str(r, cols, "HSNCode", "HSN", "HsnCode"));
            part.GstPercent = Dec(r, cols, "GST", "GstPercent");
            part.Remarks = Trim(Str(r, cols, "Remarks"));

            if (isNew) res.Inserted++; else res.Updated++;
        }
        return res;
    }

    public static ImportResult ImportServiceCharges(MySqlConnection src, AppDbContext db, string table)
    {
        var res = new ImportResult("service_charges");
        var existing = db.ServiceCharges.ToList()
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var cmd = src.CreateCommand();
        cmd.CommandText = $"SELECT * FROM `{table}`";
        using var r = cmd.ExecuteReader();
        var cols = ColMap(r);

        while (r.Read())
        {
            res.Read++;
            var name = Trim(Str(r, cols, "Item", "name", "ServiceItem"));
            if (string.IsNullOrWhiteSpace(name)) { res.Skipped++; continue; }
            if (!seen.Add(name)) { res.Duplicates++; continue; }

            var isNew = !existing.TryGetValue(name, out var sc);
            if (isNew) { sc = new ServiceCharge { Name = name }; db.ServiceCharges.Add(sc); }

            sc!.Name = name;
            sc.Charge = Dec(r, cols, "ServiceCharge", "charge", "Charge");
            sc.TaxPercent = Dec(r, cols, "Tax", "tax_percent", "TaxPercent", "GST");
            sc.Remarks = Trim(Str(r, cols, "Remarks"));

            if (isNew) res.Inserted++; else res.Updated++;
        }
        return res;
    }

    public static ImportResult ImportDealers(MySqlConnection src, AppDbContext db, string table)
    {
        var res = new ImportResult("dealers");
        var existing = db.Dealers.ToList().ToDictionary(d => d.Name, d => d, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var cmd = src.CreateCommand();
        cmd.CommandText = $"SELECT * FROM `{table}`";
        using var r = cmd.ExecuteReader();
        var cols = ColMap(r);

        while (r.Read())
        {
            res.Read++;
            var name = Trim(Str(r, cols, "Dealer", "DEALER", "dealer", "name"));
            if (string.IsNullOrWhiteSpace(name)) { res.Skipped++; continue; }
            if (!seen.Add(name)) { res.Duplicates++; continue; }

            var isNew = !existing.TryGetValue(name, out var d);
            if (isNew) { d = new Dealer { Name = name }; db.Dealers.Add(d); }

            d!.Name = name;
            d.WarrantyMonths = IntVal(r, cols, "Warranty", "warranty_months", "WarrantyMonths");
            d.Remarks = Trim(Str(r, cols, "Remarks"));

            if (isNew) res.Inserted++; else res.Updated++;
        }
        return res;
    }

    private static Dictionary<string, int> ColMap(MySqlDataReader r)
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < r.FieldCount; i++) d[r.GetName(i)] = i;
        return d;
    }

    private static string? Str(MySqlDataReader r, Dictionary<string, int> cols, params string[] names)
    {
        foreach (var n in names)
            if (cols.TryGetValue(n, out var i))
            {
                var v = r.GetValue(i);
                return v is DBNull ? null : v.ToString();
            }
        return null;
    }

    private static decimal Dec(MySqlDataReader r, Dictionary<string, int> cols, params string[] names)
    {
        var s = Str(r, cols, names);
        return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;
    }

    private static int IntVal(MySqlDataReader r, Dictionary<string, int> cols, params string[] names)
    {
        var s = Str(r, cols, names);
        if (int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) return v;
        return (int)Dec(r, cols, names);
    }

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
