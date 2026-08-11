# Passtest lookup — setup (direct MySQL, no bridge)

The API reads Hostinger's `passtestdata` directly over a read-only MySQL login (EC2 reaches the DB).
No PHP, no API key, no file to upload. Clients call `/machine-tests/*` (JWT); only the API holds the
connection string.

## 1. Create a read-only, remote-capable MySQL user (Hostinger phpMyAdmin)

```sql
CREATE USER 'passtest_ro'@'%' IDENTIFIED BY 'a-strong-password';
GRANT SELECT ON harisree_db.passtestdata TO 'passtest_ro'@'%';
GRANT SELECT ON harisree_db.dealer_warranty TO 'passtest_ro'@'%';
FLUSH PRIVILEGES;
```

The `dealer_warranty` grant is what the **Load dealers** button on the Dealers page reads — it is the
legacy dealer master and the only legacy source carrying warranty months. Without it the button still
works off `passtestdata.Customer` alone, but every candidate arrives with no warranty term and the
review dialog says so.

(Prefer `'passtest_ro'@'<EC2-public-IP>'` over `'%'` if you can pin it.)

## 2. Allow the EC2 IP through Hostinger "Remote MySQL"

hPanel → Databases → **Remote MySQL** → add the EC2 public IP (or the DB's allowlist).
Note the DB host Hostinger shows for remote connections (often `mysql.<domain>` or a server IP),
and the port (3306).

## 3. Set the connection string on the API host (env var — NOT appsettings.json)

```
Passtest__ConnectionString=Server=THE_DB_HOST;Port=3306;Database=harisree_db;Uid=passtest_ro;Pwd=a-strong-password;SslMode=Preferred;Default Command Timeout=5
```

(docker-compose: put it under the api service `environment:`. Restarting the API also applies the
two pending EF migrations.)

## 4. Verify (with a normal admin JWT)

```
GET /machine-tests/customers            -> { "customers": [ ... ] }
GET /machine-tests/by-serial/{knownSN}?dealerId=1  -> record + warranty IN/OUT
GET /dealers/import-candidates          -> { "candidates": [ ... ], "warnings": [ ... ] }
```

A `warnings` entry naming `dealer_warranty` means step 1's second GRANT is missing.

Empty string / unreachable → endpoints return 503 / 404 and the apps fall back to manual entry.

## Notes
- Results cached (serial 15 min, customers 60 min) so Hostinger is queried rarely. The dealer-import
  scan deliberately bypasses that cache — the button must not show hour-stale names — and runs on a
  longer timeout (`Passtest__ScanTimeoutSeconds`, default 30) because it aggregates the whole table.
- The scan never transfers rows: MySQL collapses passtestdata to distinct customer names, and the API
  drops the ones already in `dealers`, so only genuinely new names reach the client.
- Read-only login can only SELECT that one table — safe if the string ever leaks (still rotate).
- When passtestdata later moves to RDS as `machine_tests`, only `PasstestRepository` changes; the
  `/machine-tests/*` contract and every client stay the same.
