# PSR Service — legacy analysis & target schema

Purpose: capture exactly what the old Flutter + FastAPI app *does* (the logic), then re-decide the
table structure from scratch for the .NET rebuild. Logic is preserved; schema is redesigned.
We implement one module at a time against this schema.

Source of truth for the legacy behaviour: `service_tracker/lib/services/api_service.dart` (~4,660 lines,
all business logic lives client-side), `service_tracker/server_tables_usage.txt`, and the page set
under `service_tracker/lib/pages/`.

---

## 1. Legacy functional map (entry points → what they do → tables touched)

| Area | Legacy screen(s) | What it does | Legacy table(s) |
|---|---|---|---|
| Auth | login_screen | Fetches ALL `login` rows, compares username + **plaintext** password client-side | `login` |
| Users admin | users_page | CRUD users, single `role` string | `login` |
| Inward (intake) | add_inward_page | Create a service job (challan, customer, serial, model, problem, dealer→warranty lookup, inward DC no) | `service_table`, `dealer_warranty`, `passtestdata` (warranty by serial) |
| Acknowledge | inward_store_page | Mark `ACK_STATUS`; assign technician + priority | `service_table` |
| In-service | in_service_page, technician_in_service_page | Add components/service-charge lines; mark complete; record technician remarks | `service_table` (COMPONENTS, EDITLOGS), `price_master`, `servicecharge` |
| Stock (warehouse) | store_page | View/search stock, adjust quantities, mark serial-trackable | `stock` |
| Stock requests | stock_requests_page | Technician requests parts; store approves (progressive/partial), decrements stock | `stock_requests`, `stock`, `technician_inventory_movements`, `stock_issue_serial_lines` |
| Technician inventory | technician_inventory_page | On-hand per technician = **derived client-side**: issued − consumed − returned | computed from `stock_requests` + `service_table.COMPONENTS` + `stock_input` |
| Returns | return_acknowledgement_page | Technician returns parts; store acknowledges/approves, adds back to stock | `stock_input` (remarks state machine), `technician_return_dispatches(_lines)` |
| Dispatch | pending_dispatch_page | Mark completed jobs dispatched / stocked; outward DC | `service_table` |
| DC generation | dc_generation_page | Generate delivery challan PDF | `service_table` |
| PI generation | pi_generation_page, generate_sale_pi_page | Generate proforma invoice; persist PI no/date | `service_table`, `price_master`, `servicecharge`, `pi`, `sparesales` |
| Invoice | invoice_generation_page | Record invoice no; payment status | `service_table` |
| Spare sales | (generate_sale_pi) | Sell spares directly (no service job) | `sparesales` |
| Price list | price_list_page | Browse parts catalogue + dual pricing | `price_master` |
| Serial tracking | serial_tracking_config_page | Configure tracked components; serial lifecycle | `component_serial_master`, `sn_status_audit`, `passtestdata` |
| Warranty check | warranty_check_dialog | By serial → invoice date + dealer warranty months → in/out of warranty | `passtestdata`, `dealer_warranty` |
| Reports | reports/* | Parts used, missing items, item history, technician performance, master tables | aggregations over `service_table`, `stock_*` |
| Dashboard | dashboard | Counts per status; role-gated navigation | `service_table` (scanned client-side) |
| Daily summary / TAT | daily_summary_page, tat_analysis_page | Daily counts; turnaround = dispatch − received | `service_table` |
| Global search | global_search_page | Search across service records / serials | `service_table` |
| Backup | backup_service | Pull tables to local files | all (generic `/all/{table}`) |

**Roles (9):** admin, manager, viewer, supervisor, inward_manager, technician, dispatch_manager,
store_manager, accounts. A separate `FieldLogin` table exists for field technicians (a sister app,
hsrtech) — out of scope for the WPF rebuild but shares some tables.

---

## 2. Legacy tables (as-is) and their problems

| Table | Purpose | Problems to fix in rebuild |
|---|---|---|
| `login` | users | plaintext passwords; single role string |
| `servicecharge` | labour/service line catalogue | OK; flatten + is_active |
| `price_master` | parts catalogue + pricing | inconsistent column casing; no pagination (client fetches ALL + filters) |
| `dealer_warranty` | dealer → warranty months | OK; becomes `dealers` |
| `passtestdata` | per-machine factory test, 18 serial columns + warranty start | very wide; should normalize → **deferred (serial tracking)** |
| `service_table` | the service job | **COMPONENTS** is an encoded string (`KIND|PSCode|Name|Qty;SN:serial`); **EDITLOGS** is a text blob; statuses are free-text strings; technician/customer/dealer are name strings, not FKs |
| `sparesales` | direct spare sales | `items` is a free-text string + single qty |
| `stock` | warehouse on-hand | `Total_stock` mutated in place (race-prone, no transactions); casing chaos |
| `stock_requests` | part requests + progressive issue | name-based references; no real status enum |
| `stock_input` | returns + receipts | overloaded; `remarks` used as a state machine ("return request"/"approved"/"return_stocked") |
| `technician_inventory_movements`, `stock_issue_serial_lines` | partial movement ledger (added later) | inconsistent with derived balances |
| `component_serial_master`, `sn_status_audit` | serial lifecycle | **deferred** |
| `technician_return_dispatches(_lines)` | field-tech returns w/ ack | **deferred** (field-tech) |
| `pi` | persisted proforma invoices | thin; numbering done ad-hoc |

**Two structural anti-patterns to eliminate everywhere:**
1. **Encoded strings** (`COMPONENTS`, `EDITLOGS`, `sparesales.items`) → normalized child tables.
2. **Derived-by-scanning-everything** (technician balances, dashboard counts) → a proper
   append-only **stock ledger** + indexed status columns the DB can aggregate.

---

## 3. Target schema (redesigned, logic-preserving)

Already built — **Phase 1**: `users`, `roles`, `user_roles`, `audit_log`.

### Phase 2 — reference / master data (admin-only writes)
- **parts** (was `price_master`): `id, item_code (unique), name, category, unit, purchase_rate,
  dealer_rate, customer_rate, hsn_code, gst_percent, is_serial_tracked, remarks, is_active,
  created_at, updated_at`.
- **service_charges** (was `servicecharge`): `id, name, charge, tax_percent, remarks, is_active, ts`.
- **dealers** (was `dealer_warranty`): `id, name (unique), warranty_months, remarks, is_active, ts`.

### Phase 3 — stock & inventory
- **stock_movements** (append-only ledger; replaces `stock_input`, `technician_inventory_movements`,
  `stock_issue_serial_lines`, and the client-side derived balances):
  `id, part_id FK, movement_type (RECEIPT|ISSUE|RETURN|CONSUMPTION|ADJUSTMENT), qty (positive),
  direction (+/− derived from type), location_type (WAREHOUSE|TECHNICIAN), technician_id FK?,
  reference_type (SERVICE|STOCK_REQUEST|RETURN|MANUAL), reference_id?, serial_no?, performed_by FK,
  remarks, created_at`.
- **stock_balances** (fast-read cache, updated in the SAME transaction as each movement):
  `part_id + location_type + technician_id → on_hand_qty, row_version`. On-hand is always reconcilable
  by summing the ledger. Warehouse on-hand and per-technician on-hand both come from here.
- **stock_requests**: `id, request_no, requested_by FK (technician), request_date, part_id FK,
  qty_requested, qty_issued, status (PENDING|PARTIAL|ISSUED|CANCELLED), issued_by FK?, issued_date?,
  remarks, row_version`. Issuing writes ISSUE movements + accumulates qty_issued (progressive approval preserved).
- **stock_returns** (technician → warehouse, with acknowledge): `id, return_no, technician_id FK,
  part_id FK, qty, status (PENDING|ACKNOWLEDGED|STOCKED|MISSING), acknowledged_by FK?, ts`. Approval writes RETURN movements.

### Phase 4 — service workflow (the core)
- **customers** (NEW — replaces free-text customer names): `id, name, organization?, phone?, email?,
  address?, is_active, ts`. (Decision flagged below.)
- **services** (was `service_table`): `id, service_no (challan, unique), customer_id FK,
  serial_no, model_name, description, reported_problem, dealer_id FK?, warranty_status (IN|OUT|UNKNOWN),
  inward_dc_no, outward_dc_no?, dc_date?, date_received, technician_id FK?, priority (LOW|NORMAL|HIGH|URGENT),
  ack_status (PENDING|ACKNOWLEDGED), service_status (state machine below), payment_status (PENDING|PAID|PARTIAL),
  technician_remarks?, created_by FK, created_at, updated_at, row_version`.
- **service_lines** (decodes `COMPONENTS`): `id, service_id FK, line_type (COMPONENT|SERVICE_CHARGE|REPLACEMENT),
  part_id FK?, service_charge_id FK?, description, qty, unit_price, amount, replacement_serial_no?, created_at`.
- **service_status_history** (replaces `EDITLOGS` blob + status churn): `id, service_id FK, from_status,
  to_status, changed_by FK, note, changed_at`. (General field edits go to `audit_log`.)
- **spare_sales** + **spare_sale_lines** (was `sparesales` with a string `items`):
  header `id, sale_no, sale_date, customer_id?, pi_no?, remarks, created_by`; lines `id, spare_sale_id FK,
  part_id FK, qty, unit_price, amount`.

### Phase 5 — documents & numbering
- **service_documents** (PI / Invoice / DC numbering + snapshot; replaces `pi` + the pi_no/inv_no/dc fields):
  `id, service_id FK?, spare_sale_id FK?, doc_type (PI|INVOICE|DC), doc_no (unique per type), doc_date,
  total_amount, tax_amount, created_by, created_at`. PDFs generated on demand (PDFSharp) from lines — not stored.
- **number_sequences** (NEW support table — atomic server-side doc numbering): `key (e.g. SERVICE, DC, PI, INVOICE),
  prefix, year, next_value`. Replaces ad-hoc client-side number generation; guarantees no duplicate challan/PI/DC numbers.

### Deferred (later phase) — serial tracking & field-tech returns
- **machine_tests** + **machine_test_components** (was `passtestdata`): parent `id, model, machine_serial (unique),
  invoice_date (warranty start), customer_id?, address`; child `id, machine_test_id FK, component_type, serial_no`.
- **component_serials** + **serial_status_history** (was `component_serial_master` + `sn_status_audit`).
- Field-technician return dispatches (sister-app `hsrtech` integration).
- **warranty-by-serial** lookup (`GET /warranty/by-serial/{sn}`) depends on machine_tests + dealers.

---

## 4. State machines (enforced server-side; legacy did this client-side with strings)

**Service:** `INWARD → ACKNOWLEDGED → IN_SERVICE → COMPLETED → PENDING_DISPATCH → DISPATCHED`.
Side state `STOCKED` (returned to stock instead of dispatched). `ack_status` and `payment_status`
are independent flags. Each transition is a guarded endpoint that also writes `service_status_history`.

**Stock request:** `PENDING → PARTIAL → ISSUED` (or `CANCELLED`). Each issue writes a stock movement
and decrements warehouse balance in one transaction; technician balance increments.

**Stock return:** `PENDING → ACKNOWLEDGED → STOCKED` (or `MISSING`).

---

## 5. Import / migration strategy (how existing data moves into the new schema)

The old DB (`harisree_db` @ 145.223.18.143) stays readable during transition. The new schema differs,
so we use an **explicit mapping importer**, not a raw copy.

**Mechanism: a one-time .NET console tool `PSR.Service.Import`** (separate project; not shipped in the API image):
1. Reads directly from old MySQL (clean column names — bypasses the casing chaos the old API returned).
2. For each table, maps old columns → new columns with type coercion + dedupe, then **UPSERTs** into
   the new RDS `psr_service` DB by natural key (so it's idempotent / re-runnable).
3. Modes: `--dry-run` (report row counts + sample mappings, write nothing) and `--apply`.
4. Order: **reference data first** (parts, service_charges, dealers — static, safe to run anytime),
   then optionally transactional data at cutover.

**Example column mappings:**
- `price_master` → `parts`: ItemCode→item_code, ItemName→name, Group→category, Unit→unit,
  PurchaseRate→purchase_rate, DealerRate→dealer_rate, CustomerRate→customer_rate, HSNCode→hsn_code,
  GST→gst_percent. New `is_serial_tracked` ← old `stock.sn_trackable` (join by code) else false. Dedupe by item_code.
- `servicecharge` → `service_charges`: Item→name, ServiceCharge→charge, Tax→tax_percent, Remarks→remarks.
- `dealer_warranty` → `dealers`: Dealer→name (dedupe distinct), Warranty→warranty_months, Remarks→remarks.

**Fallback if direct DB access is awkward:** export old tables to CSV/Excel, point the importer at the
files instead of the live DB. Same mapping code, different reader. (This is also how a future admin
"bulk import parts" feature would work — same mapping layer reused.)

**Fields with no legacy source** (e.g. `is_active`, timestamps, new FKs) get sensible defaults
(active=true, created_at=now, FK matched-or-null).

**Transactional data (services, stock):** earlier decision was to start service fresh (no history import).
Revisit at cutover — if needed, services import maps `service_table` → `services` + decodes COMPONENTS →
`service_lines`, and stock seeds an opening-balance `ADJUSTMENT` movement per part from old `stock.Total_stock`.

---

## 6. Proposed build order (one module at a time)

1. **Phase 2 — reference data**: parts, service_charges, dealers (API + WPF), admin-only writes,
   parts list with server-side search/paging. + the `PSR.Service.Import` tool for reference data.
2. **Phase 3 — stock**: stock_movements ledger + balances + stock_requests + returns (API + WPF).
3. **Phase 4 — service workflow**: customers, services + service_lines + status machine (API + WPF). The big one.
4. **Phase 5 — documents**: number_sequences + service_documents + PDF (PI/Invoice/DC) + spare sales.
5. **Phase 6 — reports & dashboard**: aggregations, TAT, daily summary, global search.
6. **Phase 7 (deferred) — serial tracking + warranty-by-serial + machine tests.**

---

## 7. Open structural decisions (need sign-off before Phase 2 build)

1. **Customers table** — introduce a `customers` master (cleaner, dedupes names, supports warranty linkage)
   vs keep `customer_name` as free text on the service (closer to legacy, less friction)? *Recommend: add it.*
2. **Stock model** — ledger + balance-cache hybrid (recommended: auditable + fast + transaction-safe)
   vs a single mutable balance column (simpler, closer to legacy, race-prone)? *Recommend: hybrid ledger.*
3. **Documents** — `service_documents` + `number_sequences` for PI/INV/DC numbering (recommended)
   vs keep numbers as plain columns on the service like legacy? *Recommend: dedicated tables.*
4. **Import depth** — reference data only now, transactional later at cutover (recommended) vs plan full import now?

**All four CONFIRMED as recommended:** add customers; ledger + balance cache; service_documents + number_sequences; reference data only now.

---

## 8. Role → screen / data access (extracted from legacy `dashboard.dart` + `user.dart`)

The legacy app gates screens per role and renders technician/store-specific screens that hide pricing.
This is the authoritative access model to reproduce in WPF.

| Screen / data | admin | manager | supervisor | viewer | inward_mgr | dispatch_mgr | store_mgr | technician | accounts |
|---|:--:|:--:|:--:|:--:|:--:|:--:|:--:|:--:|:--:|
| Add inward entry | ● | ● | ● | – | ● | ● | – | – | – |
| Inward store / In-service | ● | ● | ● | view | ● | ● | – | own (tech screens) | – |
| Pending dispatch | ● | ● | ● | ● | – | ● | ● | – | ● |
| Stock requests (approve) | ● | ● | ● | – | – | – | ● | – | – |
| Return acknowledgement | ● | ● | ● | – | – | – | – | – | – |
| Store (warehouse stock) | ● | ● | ● | ● | – | – | ● | – | – |
| Price list (parts + pricing) | ● | ● | ● | ● | – | – | – | ✗ | – |
| Technician inventory (NO price) | – | – | – | – | – | – | – | ● | – |
| Generate sale PI | ● | ● | ● | ● | – | – | – | – | – |
| Global search | ● | ● | ● | ● | – | – | – | ● | – |
| Export / Daily summary | ● | ● | exp | ● | – | – | – | – | – |
| Reports hub | ● | ● | ● | – | – | – | ● | ● | – |
| TAT analysis | ● | ● | ● | ● | – | – | – | – | – |
| Users | ● | ● (legacy; rebuild = admin-only) | – | – | – | – | – | – | – |
| Serial tracking config | ● | – | – | – | – | – | – | – | – |

Key rules:
- **viewer = read-only everywhere** (`canEdit = false`).
- **Pricing (purchase/dealer/customer rates) visible only to admin, manager, supervisor, viewer.**
  Technician + store_manager (and accounts/inward/dispatch) never see rates — technicians get a
  price-stripped "what parts I hold / use" view.
- `canManageInward` = admin, inward_manager, supervisor. `canManageDispatch` = admin, dispatch_manager,
  supervisor. `canManageTechnicians` = admin, manager. `canProcessServices` = technician.

**Phase 2 application (reference masters):**
- **parts**: write = admin only. Read = any authenticated user, but the API applies a **role-aware
  projection** — pricing fields (purchase_rate, dealer_rate, customer_rate, gst_percent) are serialized
  ONLY for the pricing roles (admin/manager/supervisor/viewer); everyone else gets identity-only
  (item_code, name, unit, category). The WPF "Parts" master screen is shown to pricing roles; the
  price-stripped shape feeds technician/store screens in Phase 3/4.
- **service_charges**, **dealers**: write = admin only; authenticated read (consumed in-service / inward later).
- Auth policies: `Admin` (writes), `Pricing` = RequireRole(admin, manager, supervisor, viewer).
