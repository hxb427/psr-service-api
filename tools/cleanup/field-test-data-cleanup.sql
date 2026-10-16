-- ===========================================================================
-- Remove field-portal (Android) test data, and the WPF-side rows it spawned,
-- from the PRODUCTION psr_service database.
--
-- The scope is defined ONCE, in section 1. Every later section reads only the
-- zz_* scratch tables that section 1 fills, so nothing after section 1 can
-- widen the blast radius by accident.
--
-- Run order: 0 -> 1 -> 2 (READ THE OUTPUT) -> 3 -> 4 -> 5.
-- Section 2 is a dry run. Do not run section 3 until section 2 looks right.
--
-- BEFORE YOU START (not optional, this is production):
--   aws rds create-db-snapshot \
--     --db-instance-identifier <instance> \
--     --db-snapshot-identifier psr-pre-field-test-cleanup-$(date +%Y%m%d%H%M)
--   aws rds wait db-snapshot-available \
--     --db-snapshot-identifier psr-pre-field-test-cleanup-...
--
-- Also stop the API while sections 3-5 run, so nothing writes underneath you:
--   docker compose stop api      (on the EC2 box)
-- ===========================================================================

-- ###########################################################################
-- 0. SAFETY
-- ###########################################################################
SET SESSION sql_safe_updates = 1;   -- refuses an unkeyed UPDATE/DELETE
SET SESSION autocommit = 0;         -- nothing lands until you COMMIT

-- If a statement in section 3 or 4 is refused with ERROR 1175, that is safe
-- mode saying it could not prove the WHERE clause uses an index. Re-read that
-- WHERE clause; if it is genuinely the scoped one, run it as:
--   SET SESSION sql_safe_updates = 0; <the statement> SET SESSION sql_safe_updates = 1;
-- Never disable it for the whole script.

USE psr_service;


-- ###########################################################################
-- 1. SCOPE  --  the only section you edit
-- ###########################################################################

-- -- 1a. Which users were the test technicians? Find them first:
--        SELECT u.id, u.username, u.full_name
--        FROM users u
--        JOIN user_roles ur ON ur.user_id = u.id
--        JOIN roles r       ON r.id = ur.role_id
--        WHERE r.name IN ('field_technician','technician')
--        ORDER BY u.id;
--
-- -- Or go by what they actually touched during testing:
--        SELECT user_id, MIN(created_at) first_seen, MAX(created_at) last_seen,
--               COUNT(*) n, GROUP_CONCAT(DISTINCT action ORDER BY action) actions
--        FROM audit_log
--        WHERE action LIKE 'field-%' OR action LIKE 'transfer%'
--           OR action LIKE 'stock-return%' OR action LIKE 'stock-request%'
--        GROUP BY user_id ORDER BY n DESC;

DROP TABLE IF EXISTS zz_cleanup_techs;
CREATE TABLE zz_cleanup_techs (user_id BIGINT PRIMARY KEY);

-- >>> EDIT: the test technician user ids.
INSERT INTO zz_cleanup_techs (user_id) VALUES
  (0);   -- <<< replace. e.g. (7),(8),(9)

-- >>> EDIT: the testing window. Half-open: [from, to). UTC — the API stamps
--     created_at with UTC_TIMESTAMP, so do not pass IST here.
SET @from = '2026-01-01 00:00:00';
SET @to   = '2026-10-16 00:00:00';

-- Fail-safe: the placeholder is dropped rather than trusted. If you forgot to
-- edit the ids above, the scope resolves to NOTHING, every count in section 2
-- comes out 0, and section 3 deletes nothing. Check this says what you expect.
DELETE FROM zz_cleanup_techs WHERE user_id = 0;
SELECT COUNT(*) AS technicians_in_scope,
       GROUP_CONCAT(user_id ORDER BY user_id) AS ids
FROM zz_cleanup_techs;

-- ---------------------------------------------------------------------------
-- 1b. Resolve the scope into id sets. Nothing is deleted here.
-- ---------------------------------------------------------------------------

-- Field visits recorded from the Android app.
DROP TABLE IF EXISTS zz_fs;
CREATE TABLE zz_fs (id BIGINT PRIMARY KEY)
SELECT id FROM field_services
WHERE technician_id IN (SELECT user_id FROM zz_cleanup_techs)
  AND created_at >= @from AND created_at < @to;

-- Direct field sales recorded from the Android app.
DROP TABLE IF EXISTS zz_fsale;
CREATE TABLE zz_fsale (id BIGINT PRIMARY KEY)
SELECT id FROM field_sales
WHERE technician_id IN (SELECT user_id FROM zz_cleanup_techs)
  AND created_at >= @from AND created_at < @to;

-- Peer transfers: either side being a test technician puts the transfer in scope.
DROP TABLE IF EXISTS zz_trf;
CREATE TABLE zz_trf (id BIGINT PRIMARY KEY)
SELECT id FROM technician_transfers
WHERE (from_technician_id IN (SELECT user_id FROM zz_cleanup_techs)
    OR to_technician_id   IN (SELECT user_id FROM zz_cleanup_techs))
  AND created_at >= @from AND created_at < @to;

DROP TABLE IF EXISTS zz_trfline;
CREATE TABLE zz_trfline (id BIGINT PRIMARY KEY)
SELECT id FROM technician_transfer_lines WHERE transfer_id IN (SELECT id FROM zz_trf);

-- Return shipments raised from the Android app.
DROP TABLE IF EXISTS zz_ret;
CREATE TABLE zz_ret (id BIGINT PRIMARY KEY)
SELECT id FROM stock_returns
WHERE technician_id IN (SELECT user_id FROM zz_cleanup_techs)
  AND created_at >= @from AND created_at < @to;

-- Stock requests the test technicians raised (WPF issued against these).
DROP TABLE IF EXISTS zz_req;
CREATE TABLE zz_req (id BIGINT PRIMARY KEY)
SELECT id FROM stock_requests
WHERE requested_by_user_id IN (SELECT user_id FROM zz_cleanup_techs)
  AND created_at >= @from AND created_at < @to;

-- Ledger rows belonging to the above. reference_type/reference_id values come
-- from StockLedgerService + the endpoints that call it.
DROP TABLE IF EXISTS zz_mov;
CREATE TABLE zz_mov (id BIGINT PRIMARY KEY)
SELECT id FROM stock_movements
WHERE created_at >= @from AND created_at < @to
  AND ( technician_id IN (SELECT user_id FROM zz_cleanup_techs)
     OR (reference_type = 'FIELD_SERVICE' AND reference_id IN (SELECT id FROM zz_fs))
     OR (reference_type = 'FIELD_SALE'    AND reference_id IN (SELECT id FROM zz_fsale))
     OR (reference_type = 'TRANSFER'      AND reference_id IN (SELECT id FROM zz_trf))
     OR (reference_type = 'STOCK_RETURN'  AND reference_id IN (SELECT id FROM zz_ret))
     OR (reference_type = 'STOCK_REQUEST' AND reference_id IN (SELECT id FROM zz_req)) );

-- Repair jobs the service centre auto-opened when it acknowledged a FAULTY
-- return (StockReturnsEndpoints.cs:435 stamps challan_no = 'RTN-<return_no>').
-- This is the WPF-side data the Android testing created.
DROP TABLE IF EXISTS zz_svc;
CREATE TABLE zz_svc (id BIGINT PRIMARY KEY)
SELECT s.id FROM services s
JOIN stock_returns r ON s.challan_no = CONCAT('RTN-', r.return_no)
WHERE r.id IN (SELECT id FROM zz_ret);

-- Serial-tracked units that only ever existed inside the test flows.
DROP TABLE IF EXISTS zz_ser_candidate;
CREATE TABLE zz_ser_candidate (id BIGINT PRIMARY KEY)
SELECT DISTINCT cs.id FROM component_serials cs
WHERE cs.id IN (SELECT component_serial_id FROM stock_issue_serials
                WHERE stock_movement_id IN (SELECT id FROM zz_mov))
   OR cs.id IN (SELECT component_serial_id FROM stock_return_serials
                WHERE stock_return_id IN (SELECT id FROM zz_ret))
   OR cs.id IN (SELECT component_serial_id FROM technician_transfer_serials
                WHERE transfer_line_id IN (SELECT id FROM zz_trfline))
   OR (cs.created_at >= @from AND cs.created_at < @to
       AND cs.technician_id IN (SELECT user_id FROM zz_cleanup_techs));

-- Keep only the candidates that NOTHING outside the scope still points at.
-- A serial that a real job or a real shipment also touched stays.
DROP TABLE IF EXISTS zz_ser;
CREATE TABLE zz_ser (id BIGINT PRIMARY KEY)
SELECT c.id FROM zz_ser_candidate c
WHERE NOT EXISTS (SELECT 1 FROM stock_issue_serials x
                  WHERE x.component_serial_id = c.id
                    AND x.stock_movement_id NOT IN (SELECT id FROM zz_mov))
  AND NOT EXISTS (SELECT 1 FROM stock_return_serials x
                  WHERE x.component_serial_id = c.id
                    AND x.stock_return_id NOT IN (SELECT id FROM zz_ret))
  AND NOT EXISTS (SELECT 1 FROM technician_transfer_serials x
                  WHERE x.component_serial_id = c.id
                    AND x.transfer_line_id NOT IN (SELECT id FROM zz_trfline))
  AND NOT EXISTS (SELECT 1 FROM services s
                  WHERE s.source_component_serial_id = c.id
                    AND s.id NOT IN (SELECT id FROM zz_svc));

-- The synthetic "Technician Return - <name>" customers the faulty-ack path
-- creates (StockReturnsEndpoints.cs:474). Only ones no surviving row needs.
DROP TABLE IF EXISTS zz_cust;
CREATE TABLE zz_cust (id BIGINT PRIMARY KEY)
SELECT cu.id FROM customers cu
WHERE cu.name LIKE 'Technician Return - %'
  AND cu.organization_name = 'Service center returns'
  AND NOT EXISTS (SELECT 1 FROM services s
                  WHERE s.customer_id = cu.id AND s.id NOT IN (SELECT id FROM zz_svc))
  AND NOT EXISTS (SELECT 1 FROM spare_sales ss  WHERE ss.customer_id = cu.id)
  AND NOT EXISTS (SELECT 1 FROM field_services f
                  WHERE f.customer_id = cu.id AND f.id NOT IN (SELECT id FROM zz_fs))
  AND NOT EXISTS (SELECT 1 FROM field_sales f
                  WHERE f.customer_id = cu.id AND f.id NOT IN (SELECT id FROM zz_fsale))
  AND NOT EXISTS (SELECT 1 FROM component_serials cs
                  WHERE cs.customer_id = cu.id AND cs.id NOT IN (SELECT id FROM zz_ser));

COMMIT;   -- scratch tables only; no business rows touched yet


-- ###########################################################################
-- 2. DRY RUN  --  read this before going further
-- ###########################################################################

SELECT 'field_services'              t, COUNT(*) n FROM zz_fs
UNION ALL SELECT 'field_service_lines', COUNT(*) FROM field_service_lines WHERE field_service_id IN (SELECT id FROM zz_fs)
UNION ALL SELECT 'field_sales',         COUNT(*) FROM zz_fsale
UNION ALL SELECT 'field_sale_lines',    COUNT(*) FROM field_sale_lines WHERE field_sale_id IN (SELECT id FROM zz_fsale)
UNION ALL SELECT 'technician_transfers',        COUNT(*) FROM zz_trf
UNION ALL SELECT 'technician_transfer_lines',   COUNT(*) FROM zz_trfline
UNION ALL SELECT 'technician_transfer_serials', COUNT(*) FROM technician_transfer_serials WHERE transfer_line_id IN (SELECT id FROM zz_trfline)
UNION ALL SELECT 'stock_returns',        COUNT(*) FROM zz_ret
UNION ALL SELECT 'stock_return_serials', COUNT(*) FROM stock_return_serials WHERE stock_return_id IN (SELECT id FROM zz_ret)
UNION ALL SELECT 'stock_requests',       COUNT(*) FROM zz_req
UNION ALL SELECT 'stock_movements',      COUNT(*) FROM zz_mov
UNION ALL SELECT 'stock_issue_serials',  COUNT(*) FROM stock_issue_serials WHERE stock_movement_id IN (SELECT id FROM zz_mov)
UNION ALL SELECT 'stock_issue_acks',     COUNT(*) FROM stock_issue_acks    WHERE stock_movement_id IN (SELECT id FROM zz_mov)
UNION ALL SELECT 'services (RTN- jobs)', COUNT(*) FROM zz_svc
UNION ALL SELECT 'service_lines',          COUNT(*) FROM service_lines          WHERE service_id IN (SELECT id FROM zz_svc)
UNION ALL SELECT 'service_status_history', COUNT(*) FROM service_status_history WHERE service_id IN (SELECT id FROM zz_svc)
UNION ALL SELECT 'component_serials',      COUNT(*) FROM zz_ser
UNION ALL SELECT 'serial_status_history',  COUNT(*) FROM serial_status_history  WHERE component_serial_id IN (SELECT id FROM zz_ser)
UNION ALL SELECT 'customers (synthetic)',  COUNT(*) FROM zz_cust;

-- Eyeball the actual records, not just the counts.
SELECT id, service_no, technician_id, customer_name, ticket_number, total_amount, created_at
FROM field_services WHERE id IN (SELECT id FROM zz_fs) ORDER BY id;

SELECT id, sale_no, technician_id, customer_name, total_amount, created_at
FROM field_sales WHERE id IN (SELECT id FROM zz_fsale) ORDER BY id;

SELECT id, service_no, challan_no, serial_no, service_status, payment_status,
       pi_no, inv_no, outward_dc_no, created_at
FROM services WHERE id IN (SELECT id FROM zz_svc) ORDER BY id;

SELECT id, part_id, serial_number, status, owner_type, owner_ref, created_at
FROM component_serials WHERE id IN (SELECT id FROM zz_ser) ORDER BY id;

-- -------------------------------------------------------------------------
-- 2b. BLOCKERS. Both of these must come back empty. If either returns rows,
--     stop: real paperwork is attached to something in scope.
-- -------------------------------------------------------------------------

-- A generated PI / invoice / DC on an in-scope repair job.
SELECT 'document attached to in-scope job' AS blocker,
       d.id AS document_id, d.doc_type, d.doc_no, l.service_job_id
FROM service_document_lines l
JOIN service_documents d ON d.id = l.document_id
WHERE l.service_job_id IN (SELECT id FROM zz_svc);

-- An in-scope job that already carries a document number or a payment.
SELECT 'job carries billing data' AS blocker, id, service_no, pi_no, inv_no,
       outward_dc_no, payment_status
FROM services
WHERE id IN (SELECT id FROM zz_svc)
  AND (pi_no IS NOT NULL OR inv_no IS NOT NULL OR outward_dc_no IS NOT NULL
       OR payment_status <> 'Pending');

-- Serial candidates that are NOT safe to delete, with the reason. Informational:
-- these were already excluded from zz_ser and will simply be left in place.
SELECT c.id, cs.serial_number, cs.status, 'referenced outside scope' AS note
FROM zz_ser_candidate c
JOIN component_serials cs ON cs.id = c.id
WHERE c.id NOT IN (SELECT id FROM zz_ser);


-- ###########################################################################
-- 3. DELETE  --  only after section 2 is clean
-- ###########################################################################
-- Children first. The cascades that exist would cover some of this, but one of
-- them runs in the surprising direction (stock_movements -> field_service_lines,
-- FieldOpsConfigurations.cs:18), so every child is removed explicitly and the
-- cascades are never relied on.

START TRANSACTION;

-- 3a. field ops
DELETE FROM field_service_lines WHERE field_service_id IN (SELECT id FROM zz_fs);
DELETE FROM field_sale_lines    WHERE field_sale_id    IN (SELECT id FROM zz_fsale);
DELETE FROM field_services      WHERE id IN (SELECT id FROM zz_fs);
DELETE FROM field_sales         WHERE id IN (SELECT id FROM zz_fsale);

-- 3b. peer transfers
DELETE FROM technician_transfer_serials WHERE transfer_line_id IN (SELECT id FROM zz_trfline);
DELETE FROM technician_transfer_lines   WHERE id IN (SELECT id FROM zz_trfline);
DELETE FROM technician_transfers        WHERE id IN (SELECT id FROM zz_trf);

-- 3c. the auto-opened repair jobs (before the serials they reference, and
--     before the synthetic customers they point at — both are Restrict)
DELETE FROM service_lines          WHERE service_id IN (SELECT id FROM zz_svc);
DELETE FROM service_status_history WHERE service_id IN (SELECT id FROM zz_svc);
-- service_replacements keys off two columns, not service_id. A freshly inwarded
-- RTN- job will not have one, but a swap during testing would have.
DELETE FROM service_replacements
WHERE original_service_job_id IN (SELECT id FROM zz_svc)
   OR retained_service_job_id IN (SELECT id FROM zz_svc);
DELETE FROM services               WHERE id IN (SELECT id FROM zz_svc);

-- 3d. returns and issues
DELETE FROM stock_return_serials WHERE stock_return_id IN (SELECT id FROM zz_ret);
DELETE FROM stock_returns        WHERE id IN (SELECT id FROM zz_ret);
DELETE FROM stock_issue_serials  WHERE stock_movement_id IN (SELECT id FROM zz_mov);
DELETE FROM stock_issue_acks     WHERE stock_movement_id IN (SELECT id FROM zz_mov);

-- 3e. ledger rows. Safe now that the serial link rows above are gone.
DELETE FROM stock_movements WHERE id IN (SELECT id FROM zz_mov);

-- 3f. stock_requests. Delete last among the stock rows: the Issue movements
--     above referenced them.
DELETE FROM stock_requests WHERE id IN (SELECT id FROM zz_req);

-- 3g. serials, then their history (no FK between them — order is for reading)
DELETE FROM serial_status_history WHERE component_serial_id IN (SELECT id FROM zz_ser);
DELETE FROM component_serials     WHERE id IN (SELECT id FROM zz_ser);

-- 3h. the synthetic return customers
DELETE FROM customers WHERE id IN (SELECT id FROM zz_cust);

-- Clear any dangling back-reference left on a surviving serial.
UPDATE component_serials SET current_service_job_id = NULL
WHERE current_service_job_id IS NOT NULL
  AND current_service_job_id NOT IN (SELECT id FROM services);

UPDATE component_serials SET customer_id = NULL
WHERE customer_id IS NOT NULL
  AND customer_id NOT IN (SELECT id FROM customers);

-- Verify inside the open transaction: every one of these must now be 0.
-- If any is non-zero, ROLLBACK and work out why before trying again.
SELECT 'field_services left'  k, COUNT(*) v FROM field_services  WHERE id IN (SELECT id FROM zz_fs)
UNION ALL SELECT 'field_sales left',   COUNT(*) FROM field_sales  WHERE id IN (SELECT id FROM zz_fsale)
UNION ALL SELECT 'transfers left',     COUNT(*) FROM technician_transfers WHERE id IN (SELECT id FROM zz_trf)
UNION ALL SELECT 'returns left',       COUNT(*) FROM stock_returns WHERE id IN (SELECT id FROM zz_ret)
UNION ALL SELECT 'requests left',      COUNT(*) FROM stock_requests WHERE id IN (SELECT id FROM zz_req)
UNION ALL SELECT 'movements left',     COUNT(*) FROM stock_movements WHERE id IN (SELECT id FROM zz_mov)
UNION ALL SELECT 'RTN- jobs left',     COUNT(*) FROM services WHERE id IN (SELECT id FROM zz_svc)
UNION ALL SELECT 'serials left',       COUNT(*) FROM component_serials WHERE id IN (SELECT id FROM zz_ser)
UNION ALL SELECT 'synth customers left', COUNT(*) FROM customers WHERE id IN (SELECT id FROM zz_cust);

-- COMMIT;     <<< uncomment and run once the above is all zeros
-- ROLLBACK;   <<< or this, to walk away with nothing changed


-- ###########################################################################
-- 4. BALANCES AND SEQUENCES  --  after COMMIT
-- ###########################################################################

-- 4a. stock_balances is a cache and nothing in the API rebuilds it. With one
--     test part this is easiest to settle by hand. Look first:
SELECT b.id, b.part_id, p.item_code, p.name, b.technician_id, b.on_hand
FROM stock_balances b JOIN parts p ON p.id = b.part_id
ORDER BY b.part_id, b.technician_id;

--     Technician rows for the test users hold stock that no longer has any
--     movement behind it. Zero them (technician_id 0 is the WAREHOUSE — leave
--     that row alone unless you are certain):
-- DELETE FROM stock_balances
-- WHERE technician_id IN (SELECT user_id FROM zz_cleanup_techs);

--     If warehouse on-hand for the test part is now wrong, correct it through
--     the API (POST /stock/adjustments) rather than here, so the correction is
--     a ledger row somebody can read back.

-- 4b. Document numbers do not roll back. Reset the keys whose highest number
--     was consumed by test records. This recomputes each from what survives.
UPDATE number_sequences SET next_value =
  COALESCE((SELECT MAX(CAST(SUBSTRING(service_no, LENGTH(prefix) + 1) AS UNSIGNED)) + 1
            FROM field_services), 1)
WHERE `key` = 'FIELD_SERVICE';

UPDATE number_sequences SET next_value =
  COALESCE((SELECT MAX(CAST(SUBSTRING(sale_no, LENGTH(prefix) + 1) AS UNSIGNED)) + 1
            FROM field_sales), 1)
WHERE `key` = 'FIELD_SALE';

UPDATE number_sequences SET next_value =
  COALESCE((SELECT MAX(CAST(SUBSTRING(transfer_no, LENGTH(prefix) + 1) AS UNSIGNED)) + 1
            FROM technician_transfers), 1)
WHERE `key` = 'TRANSFER';

UPDATE number_sequences SET next_value =
  COALESCE((SELECT MAX(CAST(SUBSTRING(request_no, LENGTH(prefix) + 1) AS UNSIGNED)) + 1
            FROM stock_requests), 1)
WHERE `key` = 'STOCK_REQUEST';

UPDATE number_sequences SET next_value =
  COALESCE((SELECT MAX(CAST(SUBSTRING(return_no, LENGTH(prefix) + 1) AS UNSIGNED)) + 1
            FROM stock_returns), 1)
WHERE `key` = 'STOCK_RETURN';

-- SERVICE is shared with real shop jobs — only reset it if the RTN- jobs held
-- the top numbers, which this query will tell you:
-- (prefix is 'SVC', so the number starts at character 4)
SELECT (SELECT next_value FROM number_sequences WHERE `key` = 'SERVICE') AS seq_next,
       (SELECT MAX(CAST(SUBSTRING(service_no, 4) AS UNSIGNED)) FROM services) AS max_live;
-- UPDATE number_sequences SET next_value =
--   COALESCE((SELECT MAX(CAST(SUBSTRING(service_no, LENGTH(prefix) + 1) AS UNSIGNED)) + 1
--             FROM services), 1)
-- WHERE `key` = 'SERVICE';

-- Do NOT touch PI / INVOICE / DC. Those are year-scoped statutory books and
-- nothing in scope here consumed one (section 2b proved it).

COMMIT;


-- ###########################################################################
-- 5. FINISH
-- ###########################################################################

-- audit_log is the accountability trail and costs nothing to keep. Recommended:
-- leave it. If you do want the test entries gone, scope it tightly:
-- DELETE FROM audit_log
-- WHERE user_id IN (SELECT user_id FROM zz_cleanup_techs)
--   AND created_at >= '2026-01-01' AND created_at < '2026-10-16'
--   AND (action LIKE 'field-%' OR action LIKE 'transfer%'
--        OR action LIKE 'stock-%' OR action LIKE 'serial%');

-- Orphan check — all four must be 0.
SELECT 'orphan field_service_lines' k, COUNT(*) v FROM field_service_lines l
  LEFT JOIN field_services f ON f.id = l.field_service_id WHERE f.id IS NULL
UNION ALL SELECT 'orphan field_sale_lines', COUNT(*) FROM field_sale_lines l
  LEFT JOIN field_sales f ON f.id = l.field_sale_id WHERE f.id IS NULL
UNION ALL SELECT 'orphan transfer_lines', COUNT(*) FROM technician_transfer_lines l
  LEFT JOIN technician_transfers t ON t.id = l.transfer_id WHERE t.id IS NULL
UNION ALL SELECT 'orphan return_serials', COUNT(*) FROM stock_return_serials s
  LEFT JOIN stock_returns r ON r.id = s.stock_return_id WHERE r.id IS NULL;

DROP TABLE IF EXISTS zz_cleanup_techs;
DROP TABLE IF EXISTS zz_fs;
DROP TABLE IF EXISTS zz_fsale;
DROP TABLE IF EXISTS zz_trf;
DROP TABLE IF EXISTS zz_trfline;
DROP TABLE IF EXISTS zz_ret;
DROP TABLE IF EXISTS zz_req;
DROP TABLE IF EXISTS zz_mov;
DROP TABLE IF EXISTS zz_svc;
DROP TABLE IF EXISTS zz_ser_candidate;
DROP TABLE IF EXISTS zz_ser;
DROP TABLE IF EXISTS zz_cust;

COMMIT;

-- Restart the API:  docker compose start api
-- Then confirm in the apps: Android "My visits"/"My sales" empty for the test
-- techs, WPF Service Jobs free of RTN- jobs, WPF Stock page balances sane.
