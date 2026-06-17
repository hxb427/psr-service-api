# PSR.Service.Import

One-time, **idempotent** importer for reference data: reads the old MySQL (`harisree_db`) directly,
maps the old columns to the new schema, and UPSERTs by natural key into the target DB.

Imports: `price_master → parts`, `servicecharge → service_charges`, `dealer_warranty → dealers`.

- **Natural keys** (re-runnable, no duplicates): parts by `item_code`, service charges by `name`, dealers by `name`.
- Reads `SELECT *` and maps defensively (handles column-name/casing variants).
- Fields with no legacy source default sensibly (`is_active=true`, timestamps now, `is_serial_tracked=false`).
- Runs target migrations first, so the target schema is guaranteed present.

## Configure

Set connection strings via (in priority order) command-line args, env vars, or `appsettings.json`:

| Setting | arg | env | appsettings |
|---|---|---|---|
| Old DB | `--source "<conn>"` | `IMPORT_SOURCE` | `Source.ConnectionString` |
| New DB | `--target "<conn>"` | `IMPORT_TARGET` | `Target.ConnectionString` |

Do **not** commit real passwords — `appsettings.json` ships with `CHANGE_ME` placeholders.

## Run

```powershell
# Dry run (default) — reports what WOULD change, writes nothing
dotnet run --project tools/PSR.Service.Import

# Apply — writes to the target
dotnet run --project tools/PSR.Service.Import -- --apply

# Explicit connections (e.g. live RDS target), via env so secrets stay out of shell history
$env:IMPORT_SOURCE = "Server=145.223.18.143;Database=harisree_db;User=root;Password=...;SslMode=Preferred;AllowPublicKeyRetrieval=True"
$env:IMPORT_TARGET = "Server=<rds-endpoint>;Database=psr_service;User=psr_app;Password=...;SslMode=Required"
dotnet run --project tools/PSR.Service.Import -- --apply
```

Recommended flow: **dry run against local dev first**, eyeball the counts, then `--apply` against the
target you want (local, then live RDS).

### Importing into the live RDS (public access disabled)

The RDS instance isn't reachable from your laptop (private; SG allows only the EC2 box). Open an SSH
tunnel through EC2 and target it via `127.0.0.1`:

```powershell
# Terminal 1 — keep open
ssh -i path\to\psr-deploy.pem -N -L 3307:<rds-endpoint>:3306 ec2-user@<ec2-eip>

# Terminal 2
$env:IMPORT_TARGET = "Server=127.0.0.1;Port=3307;Database=psr_service;User=psr_app;Password=...;SslMode=None;AllowPublicKeyRetrieval=True;TreatTinyAsBoolean=true;AllowUserVariables=true"
dotnet run --project tools/PSR.Service.Import -- --apply
```

`SslMode=None` is fine here because the SSH tunnel already encrypts the laptop→EC2 hop and EC2→RDS
stays inside the VPC.

Output is a per-table summary: Read / Insert / Update / Dup (duplicate keys skipped) / Skip (no key).
