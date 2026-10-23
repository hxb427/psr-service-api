using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Stock;

/// <summary>
/// Owns every mutation of <c>component_serials</c> + <c>serial_status_history</c>. Mirrors
/// <see cref="StockLedgerService"/>: call inside the caller's transaction. Methods that create serials
/// persist them (to obtain ids for the audit FK); the audit rows are added to the context and saved by
/// the caller's final SaveChanges — all within the one ambient transaction.
/// </summary>
public class SerialService(AppDbContext db)
{
    // A serial may be (re)issued only when it is brand-new, or SHELFED - put away at the service
    // centre and ready to go out again.
    //
    // One status, not three. REPAIRED and RETURNED_TO_SC used to be in here, and that is what made
    // the shelf unreadable: "repaired" says a bench finished with the unit, "returned to SC" says a
    // box arrived, and neither says anybody put the thing away. A unit is on the shelf because
    // somebody shelved it, so there is now a status that means exactly that and nothing else.
    //
    // The two old statuses are NOT quietly left as shelf stock by another name: migration
    // 20261017_AddShelfedSerialStatus converts every service-centre-owned row in either of them to
    // SHELFED, because every one of them got there through a path that credited the warehouse.
    internal static readonly SerialStatus[] ReIssuable = { SerialStatus.Shelfed };

    // What each kind of return shipment may physically contain. The split matters: good stock going
    // back is stock the technician was issued and still holds, while a faulty return is a customer's
    // unit they collected and never had on their balance. Acknowledging the two does different things
    // to the ledger, so letting one shipment carry both would leave the quantity half-right.
    // Typed as IReadOnlyList, not SerialStatus[], and that is load-bearing rather than style.
    // On .NET 10 `array.Contains(x)` binds to MemoryExtensions.Contains(ReadOnlySpan<T>, T) through
    // the implicit array-to-span conversion, in preference to Enumerable.Contains. Inside an
    // expression tree EF then tries to evaluate a parameter whose type is a ref struct and throws
    // "ReadOnlySpan<SerialStatus> ... violates the constraint of type 'TRet'" while compiling the
    // query — a 500 on /serials/available with nothing wrong in the data. An interface-typed field
    // has no span conversion, so Contains resolves to Enumerable.Contains and translates to IN.
    private static readonly IReadOnlyList<SerialStatus> GoodStockShippable = new[] { SerialStatus.Received };
    private static readonly IReadOnlyList<SerialStatus> FaultyShippable = new[] { SerialStatus.Collected, SerialStatus.Defective };

    internal static IReadOnlyList<SerialStatus> ShippableFor(StockReturnKind kind) =>
        kind == StockReturnKind.Faulty ? FaultyShippable : GoodStockShippable;

    /// <summary>Whether a unit in this status is ON THE SHELF - the service centre's pool of units in
    /// hand, ready to be issued to a technician or handed over as a replacement.
    ///
    /// Defined as "may be issued", not as a second list that happens to match today: the shelf and
    /// the issue pick list have to be the same set of units, or the shelf is offering units an issue
    /// would refuse, or holding units it will not offer.
    ///
    /// THE SHELF IS NOT THE WAREHOUSE, and that is the whole shape of this. The warehouse is a
    /// quantity - stock bought in, counted in <c>stock_balances</c>, not named unit by unit until it
    /// goes out. The shelf is a set of units and nothing else: a machine kept off a finished job, or
    /// unused stock a technician sent back. It has no quantity row anywhere, because the units ARE
    /// the count, and a serviced machine must not be added to the purchased stock of its PS code -
    /// which is exactly what crediting the warehouse for it did.
    ///
    /// So no serial status is warehouse stock any more, and nothing here moves a warehouse balance.
    /// A unit joins the shelf by reaching SHELFED and leaves it by leaving SHELFED; both are facts
    /// about <c>component_serials</c>, so the two can no longer disagree the way a status and a
    /// separate count could.</summary>
    internal static bool IsOnShelf(SerialStatus status) => ReIssuable.Contains(status);

    /// <summary>Whether a unit in this status is counted on a TECHNICIAN's balance - stock they hold
    /// and can fit on a job.
    ///
    /// Exactly one status, and the list of what is NOT on it is the useful half:
    ///
    /// <list type="bullet">
    /// <item><c>Issued</c> - dispatched but not acknowledged. In a courier's van, on nobody's
    /// balance; that gap is the whole reason the acknowledgement step exists.</item>
    /// <item><c>Defective</c> - theirs to hold and send in for service, but not stock they can fit,
    /// so deliberately on no balance.</item>
    /// <item><c>Collected</c> - a customer's faulty unit they picked up. It was never the shop's
    /// stock, so there is nothing to credit.</item>
    /// <item><c>InTransitSc</c> / <c>InTransitTech</c> - gone from the sender, not yet arrived.</item>
    /// </list>
    ///
    /// So a manual change moves a technician's quantity only when it crosses RECEIVED in or out, and
    /// that is what <see cref="StockLedgerService.CustodyCorrectionAsync"/> is called for.</summary>
    internal static bool IsTechnicianStock(SerialStatus status) => status == SerialStatus.Received;

    /// <summary>Whether a status names a technician at all, and so whether the SN screen must ask
    /// for one.
    ///
    /// REQUIRED for the five statuses that cannot be read without a name: who is it with, who is it
    /// going to, who is sending it back. Those are the statuses
    /// <see cref="SyncOwnerToStatusAsync"/> cannot resolve custody for on its own - and when no
    /// technician was supplied it previously just fell through, leaving a unit whose status said
    /// RECEIVED and whose owner still said the service centre. That is the silent wrong answer this
    /// closes.
    ///
    /// OPTIONAL for <c>Defective</c> alone, because a faulty unit genuinely sits on either side: a
    /// technician holding one to send in, or one the counter found broken on arrival
    /// (<see cref="ReceiveReturnAsync"/> sets exactly that, with no technician).
    ///
    /// FORBIDDEN for the rest. A unit on the shelf, at a customer, on a bench or written off is not
    /// in anybody's hands, and naming a technician against one of those would put a holder on a row
    /// that no balance and no screen agrees with. <c>Missing</c> is in here deliberately: a lost
    /// unit keeps whatever custody it had, because that is the last true thing known about it.</summary>
    internal enum TechnicianNeed { Forbidden, Optional, Required }

    internal static TechnicianNeed TechnicianNeededFor(SerialStatus status) => status switch
    {
        SerialStatus.Issued or SerialStatus.Received or SerialStatus.Collected
            or SerialStatus.InTransitSc or SerialStatus.InTransitTech => TechnicianNeed.Required,
        SerialStatus.Defective => TechnicianNeed.Optional,
        _ => TechnicianNeed.Forbidden,
    };

    /// <summary>Why this manual status change must not be applied, or null when it may be.
    ///
    /// Only the technician is checked here now. A shelf unit going straight to RECEIVED used to be
    /// refused outright - the unit would have left the shelf and arrived on no balance, because a
    /// technician's holding was only ever credited by issuing. It is allowed now BECAUSE the screen
    /// names the technician and the change moves their balance, which is what was missing rather than
    /// what was wrong about it. Handing a unit over is still better recorded as an issue, since that
    /// is the thing that produces a challan and an acknowledgement; this is the correction for when
    /// it was not.</summary>
    internal static string? ManualChangeBlocked(SerialStatus to, long? technicianId, string serialNumber) =>
        (TechnicianNeededFor(to), technicianId) switch
        {
            (TechnicianNeed.Required, null) =>
                $"{to} says which technician holds {serialNumber}, so pick the technician.",
            (TechnicianNeed.Forbidden, not null) =>
                $"{to} is not a status a technician holds, so no technician can be set on "
                + $"{serialNumber}. Leave it blank.",
            _ => null,
        };

    /// <summary>Returns serial → reason for any serial that cannot be issued for this part
    /// (duplicated in the request, or currently deployed elsewhere). Empty ⇒ all issuable.</summary>
    public async Task<Dictionary<string, string>> FindIssueConflictsAsync(
        long partId, IReadOnlyCollection<string> serials, CancellationToken ct)
    {
        var conflicts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var trimmed = serials.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

        foreach (var g in trimmed.GroupBy(s => s, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            conflicts[g.Key] = "Entered more than once.";

        var bySn = (await db.ComponentSerials.AsNoTracking().Where(c => c.PartId == partId).ToListAsync(ct))
            .GroupBy(c => c.SerialNumber, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var sn in trimmed)
        {
            if (!bySn.TryGetValue(sn, out var c)) continue;                                   // new serial: fine
            if (ReIssuable.Contains(c.Status) && c.OwnerType == SerialOwnerType.ServiceCenter) continue;
            conflicts[sn] = c.OwnerType switch
            {
                SerialOwnerType.Technician => $"Already with a technician (status {c.Status}).",
                SerialOwnerType.Customer => $"Installed at a customer (status {c.Status}).",
                _ => $"Not available for issue (status {c.Status}).",
            };
        }
        return conflicts;
    }

    /// <summary>How many units each part has on the shelf. The shelf's "balance", derived rather than
    /// stored - see <see cref="IsOnShelf"/> for why there is no row to read instead.
    ///
    /// Every part in one dictionary rather than filtered to a page of them: the set is small by
    /// definition (only units actually in hand), and filtering by a list of ids runs into the
    /// EF Core 9 + .NET 10 funcletizer bug the rest of this codebase works around with OR-chains.</summary>
    public async Task<Dictionary<long, int>> ShelfCountsAsync(CancellationToken ct) =>
        await db.ComponentSerials.AsNoTracking()
            .Where(c => c.Status == SerialStatus.Shelfed && c.OwnerType == SerialOwnerType.ServiceCenter)
            .GroupBy(c => c.PartId)
            .Select(g => new { PartId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.PartId, x => x.Count, ct);

    /// <summary>Whether this one unit is on the shelf, by serial number. For the replacement paths,
    /// which name a single unit and need to know which pool to take it out of.
    ///
    /// READ-ONLY, AND THEREFORE NOT A GUARANTEE. It answers from before the caller's transaction has
    /// written anything, so between this and the write somebody else can take the same unit. Use it to
    /// decide which pool to debit and to say so on a form; use <see cref="ClaimFromShelfAsync"/> to
    /// actually take the unit.</summary>
    public Task<bool> IsOnShelfAsync(long partId, string? serialNo, CancellationToken ct)
    {
        var sn = serialNo?.Trim();
        if (string.IsNullOrEmpty(sn)) return Task.FromResult(false);
        return db.ComponentSerials.AsNoTracking()
            .AnyAsync(c => c.PartId == partId && c.SerialNumber == sn
                           && c.Status == SerialStatus.Shelfed
                           && c.OwnerType == SerialOwnerType.ServiceCenter, ct);
    }

    /// <summary>Take this unit off the shelf, or report that it is not there to take. Returns true
    /// when this caller got it.
    ///
    /// THE SHELF'S MISSING GUARD. Every other check in this file reads before the transaction writes,
    /// which is fine for deciding what to put on a form and useless for deciding who gets the last
    /// one. Two callers naming the same unit both read SHELFED, both passed, and both wrote: the
    /// second overwrote the first, so one job's replacement was recorded against a unit the other job
    /// had also physically handed over — and one of the two units never left the building.
    ///
    /// The warehouse cannot do this, because its quantity is taken by a conditional UPDATE
    /// (<c>StockLedgerService.GuardedDecrementAsync</c>) that fails when the balance would go
    /// negative. The shelf has no quantity to guard: it IS these rows, the ones that are SHELFED in
    /// the service centre's custody. So the guard has to be on the row, and this is the same idiom
    /// pointed at it — the WHERE carries the condition, and nought rows affected is the refusal.
    ///
    /// Two things make it a lock and not merely a re-check, and both come from InnoDB rather than from
    /// anything here. An UPDATE takes an exclusive lock on the row and holds it until the transaction
    /// ends, so the second caller's identical UPDATE blocks rather than racing. And an UPDATE reads
    /// the latest committed row rather than the transaction's snapshot, so when it unblocks it judges
    /// its WHERE against what the winner actually wrote: the status is no longer SHELFED, nought rows
    /// match, and the second caller is refused. If the winner rolls back instead, the lock lifts with
    /// the row still SHELFED and the waiter takes it, which is also right.
    ///
    /// IT WRITES NO STATUS, on purpose. Moving the row here would leave the real transition
    /// (<see cref="InstallToCustomerAsync"/>, <see cref="CaptureOnIssueAsync"/>) writing a history row
    /// whose old status was already the new one — the trail would stop saying the unit came off the
    /// shelf, which is the one thing it is kept for. Touching <c>last_updated_at</c> takes the same
    /// lock and says the same no.
    ///
    /// A serial the shop has never seen returns false and is not an error: that is warehouse stock
    /// being named as it goes out, which is how a serial-tracked part's bought-in stock enters
    /// tracking at all. The caller decides whether false means "warehouse" or "somebody beat me",
    /// because only the caller knows which of the two it was promised.</summary>
    public async Task<bool> ClaimFromShelfAsync(long partId, string? serialNo, CancellationToken ct)
    {
        var sn = serialNo?.Trim();
        if (string.IsNullOrEmpty(sn)) return false;
        var affected = await db.Database.ExecuteSqlInterpolatedAsync(ClaimFromShelfSql(partId, sn), ct);
        return affected == 1;
    }

    /// <summary>The claim's statement, built separately only so it can be read back. A suite with no
    /// database cannot run this SQL, and the two conditions in its WHERE are the whole of what makes
    /// the shelf safe — so they are worth a test that can see them rather than no test at all.
    ///
    /// The status values are literals because that is what the column holds: <c>HasConversion&lt;
    /// string&gt;()</c> stores the enum's own name, and migration
    /// <c>20261017_AddShelfedSerialStatus</c> writes exactly these two words.</summary>
    internal static FormattableString ClaimFromShelfSql(long partId, string serialNo) => $@"
UPDATE `component_serials` SET `last_updated_at` = UTC_TIMESTAMP(6)
WHERE `part_id` = {partId} AND `serial_number` = {serialNo}
  AND `status` = 'Shelfed' AND `owner_type` = 'ServiceCenter'";

    /// <summary>Why a single-unit handover must stop, or null when it may go on. The decision half of
    /// <see cref="ClaimFromShelfAsync"/>, pure so it can be pinned down without a database — the race
    /// itself cannot be, and the rule is the part worth keeping honest.
    ///
    /// Only one of the four combinations is a failure: the form was offered a unit OFF THE SHELF and
    /// the claim could not get it. The other three are all ordinary.
    ///
    /// <list type="bullet">
    /// <item><c>promised, claimed</c> — the shelf unit is now this caller's.</item>
    /// <item><c>not promised, not claimed</c> — a serial the shop has never seen, coming out of
    /// warehouse stock as it always did.</item>
    /// <item><c>not promised, claimed</c> — somebody shelved that very serial in between. The claim is
    /// the more recent fact and the unit is genuinely in hand, so it is taken off the shelf rather
    /// than bought twice.</item>
    /// </list>
    ///
    /// The failure must NOT fall back to the warehouse. That is the quiet wrong answer: it would debit
    /// a quantity for a unit that was never in the warehouse, so the shop would be one unit short on
    /// paper and the customer would have been handed nothing.</summary>
    internal static string? ShelfClaimLostReason(bool promisedShelf, bool claimed, string serialNo)
        => promisedShelf && !claimed
            ? $"Unit {serialNo} is no longer on the shelf — somebody else has issued or used it since "
              + "this form was opened. Nothing has been handed over. Pick another unit, or reopen the "
              + "form to see what is on the shelf now."
            : null;

    /// <summary>Take the shelf units of an issue, and say how the issue splits between the two pools
    /// it can come out of. Call it inside the caller's transaction: it WRITES.
    ///
    /// A serial-tracked part can be issued from either pool, and which one is not a choice the store
    /// makes on a form - it follows from the units picked:
    ///
    /// <list type="bullet">
    /// <item>a serial that is SHELFED in the service centre's custody is a unit ON THE SHELF. It is
    /// picked from the shelf list, it has no warehouse quantity behind it, and issuing it takes it
    /// off the shelf by changing its status.</item>
    /// <item>a serial the shop has never seen is purchased WAREHOUSE stock being named as it goes
    /// out. That is how a serial-tracked part's bought-in stock gets into tracking at all - it is
    /// received as a quantity and named at issue - so its quantity must come off the warehouse.</item>
    /// </list>
    ///
    /// Returned as counts rather than lists because that is what the ledger needs: one debit per pool.
    /// An untracked part never reaches here — it has no units to pick, so its quantity comes off the
    /// warehouse as it always did.
    ///
    /// THIS REPLACED A READ-ONLY SPLIT, and the reason is the whole point of it. That version counted
    /// which of the named serials were SHELFED, from a query taken before the transaction opened. Two
    /// stores filling two issues off the same shelf unit therefore both counted it as theirs: each
    /// debited one fewer from the warehouse than it actually took, and the unit left the building
    /// twice. The read-only version is gone rather than merely left uncalled — it is indistinguishable
    /// from this one at the call site, so keeping it is keeping the bug one autocomplete away.
    ///
    /// The distinction it could not make is the one that matters. A serial that fails to claim is
    /// either a unit the shop has never seen — warehouse stock, perfectly ordinary — or a unit it
    /// knows about that is no longer on the shelf, which is somebody else having taken it since this
    /// request was checked. <see cref="FindIssueConflictsAsync"/> refuses the second case before the
    /// transaction opens, so reaching it here means the unit moved in between. Counting it as
    /// warehouse stock would debit a quantity for a unit that was never in the warehouse, so it is
    /// refused by name instead.</summary>
    public async Task<(int FromShelf, int FromWarehouse)> ClaimIssueSourcesAsync(
        long partId, IReadOnlyCollection<string> serials, CancellationToken ct)
    {
        var trimmed = serials.Select(s => s.Trim()).Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (trimmed.Count == 0) return (0, 0);

        var shelf = 0;
        foreach (var sn in trimmed)
        {
            if (await ClaimFromShelfAsync(partId, sn, ct)) { shelf++; continue; }

            // Known to the shop but not on the shelf. FindIssueConflictsAsync already refused this
            // case before the transaction opened, so reaching it means the unit moved in between —
            // which is exactly the race this exists for.
            if (await db.ComponentSerials.AsNoTracking()
                    .AnyAsync(c => c.PartId == partId && c.SerialNumber == sn, ct))
                throw new StockException(
                    $"Unit {sn} is no longer on the shelf — somebody else has issued or used it since "
                    + "this was checked. Remove it from this issue, or pick another unit.");
        }
        return (shelf, trimmed.Count - shelf);
    }

    /// <summary>Bind serials to a technician on issue, and write the <c>stock_issue_serials</c> link
    /// rows so an acknowledgement knows which serials belong to this movement.
    ///
    /// <paramref name="inTransit"/> is the difference between the two ways stock leaves the store, and
    /// it is not cosmetic. A field issue travels by courier, so the units are ISSUED and still the
    /// service center's until the technician confirms what actually arrived - that confirmation is the
    /// whole reason the acknowledgement step exists. An in-house issue is handed across the counter:
    /// there is no journey to confirm, nobody to confirm it, and no screen on the desktop that could.
    /// Leaving those units ISSUED would strand them, because fitting a unit requires it to be RECEIVED
    /// in the technician's custody - so an in-house technician would be handed a part they could never
    /// book. They are marked received on the spot, which is what physically happened.</summary>
    public async Task CaptureOnIssueAsync(
        long stockMovementId, long partId, string? itemName, long technicianId, string technicianName,
        IReadOnlyCollection<string> serials, long byUser, CancellationToken ct, bool inTransit = true)
    {
        var now = DateTime.UtcNow;
        var issuedStatus = inTransit ? SerialStatus.Issued : SerialStatus.Received;
        var owner = inTransit ? SerialOwnerType.ServiceCenter : SerialOwnerType.Technician;
        var transitRef = inTransit ? $"In transit to {technicianName}" : technicianName;
        var trimmed = serials.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

        var bySn = (await db.ComponentSerials.Where(c => c.PartId == partId).ToListAsync(ct))
            .GroupBy(c => c.SerialNumber, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var touched = new List<(ComponentSerial serial, string? old)>();
        foreach (var sn in trimmed)
        {
            if (bySn.TryGetValue(sn, out var c))
            {
                var old = c.Status.ToString();
                c.Status = issuedStatus;
                c.OwnerType = owner;
                c.OwnerRef = transitRef;
                c.TechnicianId = technicianId;
                c.ItemName ??= itemName;
                // A unit going back out is no longer the previous customer's, and is not being worked on.
                c.CustomerId = null;
                c.CurrentServiceJobId = null;
                c.LastUpdatedAt = now;
                touched.Add((c, old));
            }
            else
            {
                var created = new ComponentSerial
                {
                    PartId = partId, SerialNumber = sn, ItemName = itemName,
                    Status = issuedStatus, OwnerType = owner,
                    OwnerRef = transitRef, TechnicianId = technicianId,
                    LastUpdatedAt = now, CreatedAt = now,
                };
                db.ComponentSerials.Add(created);
                touched.Add((created, null));
            }
        }

        await db.SaveChangesAsync(ct);   // assign ids to newly-created serials

        foreach (var (serial, old) in touched)
        {
            db.StockIssueSerials.Add(new StockIssueSerial
            {
                StockMovementId = stockMovementId, ComponentSerialId = serial.Id,
                // A counter handover is its own acknowledgement, so it is not left waiting for one.
                AckStatus = inTransit ? null : SerialAckStatus.Received,
            });
            AddHistory(serial, old, issuedStatus, byUser,
                inTransit ? $"Issued to {technicianName}" : $"Issued and handed to {technicianName}", now);
        }
    }

    /// <summary>Admin manual status change (mark missing/found/defective/repaired…). Ownership is
    /// synced to the new status so status and owner can't diverge: back-at-SC statuses flip to
    /// SERVICE_CENTER, with-technician statuses flip to the serial's technician, installed/used flip
    /// to CUSTOMER, MISSING keeps the current custody. Returns null if the serial is missing.</summary>
    public async Task<ComponentSerial?> ChangeStatusAsync(
        long serialId, SerialStatus newStatus, long byUser, string remarks, CancellationToken ct,
        long? technicianId = null)
    {
        var c = await db.ComponentSerials.FirstOrDefaultAsync(x => x.Id == serialId, ct);
        if (c is null) return null;
        var old = c.Status.ToString();
        var now = DateTime.UtcNow;
        c.Status = newStatus;

        // The technician goes on BEFORE custody is synced, because that is what the sync reads. With
        // it set afterwards - or not at all - the RECEIVED / COLLECTED / IN_TRANSIT_TECH branch of
        // SyncOwnerToStatusAsync simply does not match, and the unit ends up saying it is with a
        // technician while its owner still says the service centre.
        //
        // Only where the status takes one. A status that forbids a technician keeps whatever custody
        // the sync decides for it, which for MISSING means keeping what it had.
        if (TechnicianNeededFor(newStatus) != TechnicianNeed.Forbidden && technicianId is { } tid)
            c.TechnicianId = tid;

        await SyncOwnerToStatusAsync(c, ct);
        c.LastUpdatedAt = now;
        AddHistory(c, old, newStatus, byUser, remarks, now);
        return c;
    }

    /// <summary>Technician acknowledges one issued serial: RECEIVED / DEFECTIVE flip ownership to the
    /// technician (unit physically arrived); MISSING keeps SERVICE_CENTER custody.</summary>
    public async Task<string?> AckIssueSerialAsync(
        long serialId, SerialAckStatus ack, long technicianId, string technicianName, CancellationToken ct)
    {
        var c = await db.ComponentSerials.FirstOrDefaultAsync(x => x.Id == serialId, ct);
        if (c is null) return $"Serial record {serialId} not found.";
        if (c.TechnicianId != technicianId || c.Status != SerialStatus.Issued)
            return $"{c.SerialNumber} is not awaiting your acknowledgement (status {c.Status}).";

        var old = c.Status.ToString();
        var now = DateTime.UtcNow;
        var newStatus = ack switch
        {
            SerialAckStatus.Received => SerialStatus.Received,
            SerialAckStatus.Defective => SerialStatus.Defective,
            _ => SerialStatus.Missing,
        };
        c.Status = newStatus;
        if (ack is SerialAckStatus.Received or SerialAckStatus.Defective)
        {
            c.OwnerType = SerialOwnerType.Technician;
            c.OwnerRef = technicianName;
        }
        // MISSING: ownership stays SERVICE_CENTER — the unit never reached the technician.
        c.LastUpdatedAt = now;
        AddHistory(c, old, newStatus, technicianId,
            ack == SerialAckStatus.Missing ? "Technician reports unit not received" : "Receipt acknowledged", now);
        return null;
    }

    /// <summary>Service revert: a completed job went back to in-service — the fitted unit returns to
    /// the technician's custody (mirror of <see cref="InstallToCustomerAsync"/>).</summary>
    public async Task UninstallToTechnicianAsync(
        long partId, string serialNumber, long technicianId, string technicianName, long byUser, CancellationToken ct)
    {
        var c = await FindAsync(partId, serialNumber, ct);
        if (c is null) return;
        if (c.Status is not SerialStatus.Used) return;

        var old = c.Status.ToString();
        var now = DateTime.UtcNow;
        c.Status = SerialStatus.Received;
        c.OwnerType = SerialOwnerType.Technician;
        c.OwnerRef = technicianName;
        c.TechnicianId = technicianId;
        c.LastUpdatedAt = now;
        AddHistory(c, old, SerialStatus.Received, byUser, "Service reverted — unit back with technician", now);
    }

    /// <summary>Service flow: a serial-tracked unit is fitted at / handed to a customer. Owner →
    /// CUSTOMER, status USED.
    ///
    /// The status is not a parameter. It used to be, and every caller chose between USED and
    /// INSTALLED for a difference no part of the system acted on - which is how one state came to
    /// be written two ways. Fixing the callers alone would have left the next one free to pick the
    /// other word again, so the choice is gone rather than merely made correctly.</summary>
    public async Task InstallToCustomerAsync(
        long partId, string serialNumber, string? itemName, string customerName,
        long byUser, CancellationToken ct, long? customerId = null)
    {
        const SerialStatus newStatus = SerialStatus.Used;
        var sn = serialNumber.Trim();
        if (sn.Length == 0) return;
        var now = DateTime.UtcNow;
        var c = await db.ComponentSerials.FirstOrDefaultAsync(x => x.PartId == partId && x.SerialNumber == sn, ct);
        if (c is null)
        {
            c = new ComponentSerial
            {
                PartId = partId, SerialNumber = sn, ItemName = itemName,
                Status = newStatus, OwnerType = SerialOwnerType.Customer,
                OwnerRef = customerName, CustomerId = customerId,
                LastUpdatedAt = now, CreatedAt = now,
            };
            db.ComponentSerials.Add(c);
            await db.SaveChangesAsync(ct);
            AddHistory(c, null, newStatus, byUser, $"Deployed to {customerName}", now);
            return;
        }
        var old = c.Status.ToString();
        c.Status = newStatus;
        c.OwnerType = SerialOwnerType.Customer;
        c.OwnerRef = customerName;
        c.CustomerId = customerId;
        c.TechnicianId = null;
        c.LastUpdatedAt = now;
        AddHistory(c, old, newStatus, byUser, $"Deployed to {customerName}", now);
    }

    /// <summary>Field flow: faulty unit collected from a customer — upserts the serial as COLLECTED
    /// held by the technician (legacy updateOwnerOnCollection).</summary>
    public async Task CollectFromCustomerAsync(
        long partId, string serialNumber, string? itemName, long technicianId, string technicianName,
        bool defective, long byUser, CancellationToken ct, long? customerId = null)
    {
        var sn = serialNumber.Trim();
        if (sn.Length == 0) return;
        var now = DateTime.UtcNow;
        var newStatus = defective ? SerialStatus.Defective : SerialStatus.Collected;
        var c = await db.ComponentSerials.FirstOrDefaultAsync(x => x.PartId == partId && x.SerialNumber == sn, ct);
        if (c is null)
        {
            c = new ComponentSerial
            {
                PartId = partId, SerialNumber = sn, ItemName = itemName,
                Status = newStatus, OwnerType = SerialOwnerType.Technician,
                OwnerRef = technicianName, TechnicianId = technicianId, CustomerId = customerId,
                LastUpdatedAt = now, CreatedAt = now,
            };
            db.ComponentSerials.Add(c);
            await db.SaveChangesAsync(ct);
            AddHistory(c, null, newStatus, byUser, "Collected from customer (new record)", now);
            return;
        }
        var old = c.Status.ToString();
        c.Status = newStatus;
        c.OwnerType = SerialOwnerType.Technician;
        c.OwnerRef = technicianName;
        c.TechnicianId = technicianId;
        // CustomerId is deliberately NOT cleared while the unit is only in the technician's hands on
        // its way in: it is what lets the repair job be opened against the customer the unit came
        // from, which is the whole point of not re-keying an inward entry for it. Cleared when the
        // unit is finally stocked or scrapped.
        if (customerId is { } cid) c.CustomerId = cid;
        c.LastUpdatedAt = now;
        AddHistory(c, old, newStatus, byUser, "Collected from customer", now);
    }

    /// <summary>Ship one serial on a technician return: validates custody, sets IN_TRANSIT_SC.
    /// Returns an error message, or null on success.</summary>
    public async Task<string?> ShipReturnSerialAsync(
        long serialId, long technicianId, long byUser, StockReturnKind kind, CancellationToken ct)
    {
        var c = await db.ComponentSerials.FirstOrDefaultAsync(x => x.Id == serialId, ct);
        if (c is null) return $"Serial record {serialId} not found.";
        if (c.OwnerType != SerialOwnerType.Technician || c.TechnicianId != technicianId)
            return $"{c.SerialNumber} is not in your custody.";
        if (!ShippableFor(kind).Contains(c.Status))
            return kind == StockReturnKind.Faulty
                ? $"{c.SerialNumber} is not a unit collected from a customer (status {c.Status}) "
                  + "- send unused stock back as a stock return instead."
                : $"{c.SerialNumber} is not unused stock (status {c.Status}) "
                  + "- send units collected from customers in for service as a faulty return instead.";

        var old = c.Status.ToString();
        var now = DateTime.UtcNow;
        c.Status = SerialStatus.InTransitSc;
        c.LastUpdatedAt = now;
        AddHistory(c, old, SerialStatus.InTransitSc, byUser, "Dispatched to service center", now);
        return null;
    }

    /// <summary>Return flow: receive a unit back at the service centre. Unused stock goes straight
    /// onto the shelf (SHELFED); a unit found faulty goes to DEFECTIVE and no further, because the
    /// bench has to see it before anyone can issue it again. Owner → SERVICE_CENTER either way.
    /// Returns the updated serial, or null if not found.
    ///
    /// Straight to the shelf is the whole point: unused stock coming back from a technician needed
    /// nothing done to it. It used to land on RETURNED_TO_SC, which read as a waypoint and left the
    /// desk looking for a second step that did not exist.</summary>
    public async Task<ComponentSerial?> ReceiveReturnAsync(
        long serialId, bool defective, long byUser, string remarks, CancellationToken ct)
    {
        var c = await db.ComponentSerials.FirstOrDefaultAsync(x => x.Id == serialId, ct);
        if (c is null) return null;
        var old = c.Status.ToString();
        var now = DateTime.UtcNow;
        var newStatus = defective ? SerialStatus.Defective : SerialStatus.Shelfed;
        c.Status = newStatus;
        c.OwnerType = SerialOwnerType.ServiceCenter;
        c.OwnerRef = "Service center";
        c.TechnicianId = null;
        c.LastUpdatedAt = now;
        AddHistory(c, old, newStatus, byUser, remarks, now);
        return c;
    }

    /// <summary>Faulty return arrives at the service center: custody flips to the service center and
    /// the unit goes UNDER_REPAIR, deliberately NOT one of the re-issuable statuses - it is a broken
    /// unit waiting on a repair job, and offering it on the next issue is exactly the mistake the
    /// separate status exists to prevent. The job id is stamped by the caller once the job exists.
    /// Returns the updated serial, or null when the record is gone.</summary>
    public async Task<ComponentSerial?> ReceiveFaultyReturnAsync(
        long serialId, long byUser, string remarks, CancellationToken ct)
    {
        var c = await db.ComponentSerials.FirstOrDefaultAsync(x => x.Id == serialId, ct);
        if (c is null) return null;
        var old = c.Status.ToString();
        var now = DateTime.UtcNow;
        c.Status = SerialStatus.UnderRepair;
        c.OwnerType = SerialOwnerType.ServiceCenter;
        c.OwnerRef = "Service center";
        c.TechnicianId = null;
        c.LastUpdatedAt = now;
        AddHistory(c, old, SerialStatus.UnderRepair, byUser, remarks, now);
        return c;
    }

    /// <summary>A serial-tracked PART has been booked in for service over the counter. The unit goes
    /// UNDER_REPAIR in the service centre's custody with the job stamped on it, and its record is
    /// created if the shop has never seen this one before.
    ///
    /// Custody, not ownership: the machine is still the customer's, and CustomerId keeps saying so.
    /// That is the whole point of registering it — a unit that comes back next year under a different
    /// name is only visible because the last name is still on the record. It is the same convention
    /// the faulty-return path uses, and UNDER_REPAIR is deliberately not re-issuable so a customer's
    /// unit can never be offered on the next issue.
    ///
    /// Returns the unit and who held it before, so the counter can be told when the owner has changed.
    /// Null when the serial is blank.</summary>
    public async Task<(ComponentSerial Unit, long? PreviousCustomerId, string? PreviousOwnerRef, bool WasKnown)?>
        ReceiveForServiceAsync(long partId, string? serialNumber, string? itemName, long? customerId,
            string partyLabel, long byUser, string remarks, CancellationToken ct, long? serviceJobId = null)
    {
        var sn = serialNumber?.Trim();
        if (string.IsNullOrEmpty(sn)) return null;
        var now = DateTime.UtcNow;

        var c = await db.ComponentSerials.FirstOrDefaultAsync(x => x.PartId == partId && x.SerialNumber == sn, ct);
        if (c is null)
        {
            c = new ComponentSerial
            {
                PartId = partId, SerialNumber = sn, ItemName = itemName,
                Status = SerialStatus.UnderRepair, OwnerType = SerialOwnerType.ServiceCenter,
                OwnerRef = $"In for service ({partyLabel})", CustomerId = customerId,
                CurrentServiceJobId = serviceJobId, LastUpdatedAt = now, CreatedAt = now,
            };
            db.ComponentSerials.Add(c);
            await db.SaveChangesAsync(ct);   // id, for the history row's FK
            AddHistory(c, null, SerialStatus.UnderRepair, byUser, remarks, now);
            return (c, null, null, false);
        }

        var previousCustomerId = c.CustomerId;
        var previousOwnerRef = c.OwnerRef;
        var old = c.Status.ToString();
        c.Status = SerialStatus.UnderRepair;
        c.OwnerType = SerialOwnerType.ServiceCenter;
        c.OwnerRef = $"In for service ({partyLabel})";
        c.TechnicianId = null;
        c.CustomerId = customerId;
        c.CurrentServiceJobId = serviceJobId;
        c.LastUpdatedAt = now;
        AddHistory(c, old, SerialStatus.UnderRepair, byUser, remarks, now);
        return (c, previousCustomerId, previousOwnerRef, true);
    }

    /// <summary>Put a unit away on the warehouse shelf: SHELFED, service-centre custody, ready to be
    /// issued or used. This is the point the unit rejoins the pool, and the only thing that puts it
    /// there - the shelf is never inferred from a bench status.
    ///
    /// Reached by the one gesture that means it: "Keep in stock" on a finished job. The unit may have
    /// been UNDER_REPAIR (a customer unit the shop kept), REPAIRED (a bench finished with it) or
    /// nothing the shop had seen before; what it was before does not change what shelving it means.
    ///
    /// The customer link is dropped here - it is service-centre stock now, and its past owners stay
    /// readable in the history rather than lingering as current ownership.</summary>
    public async Task ShelveAsync(long serialId, long byUser, string remarks, CancellationToken ct)
    {
        var c = await db.ComponentSerials.FirstOrDefaultAsync(x => x.Id == serialId, ct);
        if (c is null) return;
        var old = c.Status.ToString();
        var now = DateTime.UtcNow;
        c.Status = SerialStatus.Shelfed;
        c.OwnerType = SerialOwnerType.ServiceCenter;
        c.OwnerRef = "Service center";
        c.TechnicianId = null;
        c.CustomerId = null;
        c.CurrentServiceJobId = null;
        c.LastUpdatedAt = now;
        AddHistory(c, old, SerialStatus.Shelfed, byUser, remarks, now);
    }

    /// <summary>Take a unit into the service centre's own custody, creating its record if the shop has
    /// never seen it before. Owner → SERVICE_CENTER, customer and technician links dropped.
    ///
    /// This is how a machine that arrived as a customer's becomes the shop's. Two moments use it and
    /// they want different statuses, which is why the status is a parameter rather than fixed:
    ///
    /// <list type="bullet">
    /// <item><c>UnderRepair</c> — an advance replacement was issued and the customer's unit stayed
    /// behind. Ownership changes now; the unit is broken, so it is deliberately NOT re-issuable and
    /// no quantity moves yet.</item>
    /// <item><c>Shelfed</c> — a finished job was stocked, or a replacement that never left the
    /// building is going back because the swap was cancelled. Either way the unit is put away and
    /// rejoins the pool in the same breath as the warehouse count.</item>
    /// </list>
    ///
    /// Returns the unit, or null when the serial is blank — an untracked machine with no serial
    /// written on it still has a quantity, and refusing to stock it over a missing record would be
    /// inventing a rule the shop floor cannot satisfy.</summary>
    public async Task<ComponentSerial?> AcquireToServiceCentreAsync(
        long partId, string? serialNumber, string? itemName, SerialStatus status,
        long byUser, string remarks, CancellationToken ct, long? serviceJobId = null)
    {
        var sn = serialNumber?.Trim();
        if (string.IsNullOrEmpty(sn)) return null;
        var now = DateTime.UtcNow;

        var c = await db.ComponentSerials.FirstOrDefaultAsync(x => x.PartId == partId && x.SerialNumber == sn, ct);
        if (c is null)
        {
            c = new ComponentSerial
            {
                PartId = partId, SerialNumber = sn, ItemName = itemName,
                Status = status, OwnerType = SerialOwnerType.ServiceCenter,
                OwnerRef = "Service center", CurrentServiceJobId = serviceJobId,
                LastUpdatedAt = now, CreatedAt = now,
            };
            db.ComponentSerials.Add(c);
            await db.SaveChangesAsync(ct);   // id, for the history row's FK
            AddHistory(c, null, status, byUser, remarks, now);
            return c;
        }

        var old = c.Status.ToString();
        c.Status = status;
        c.OwnerType = SerialOwnerType.ServiceCenter;
        c.OwnerRef = "Service center";
        c.TechnicianId = null;
        // The customer link goes: it is the shop's unit now. Where it has been stays readable in the
        // history rather than lingering as current ownership.
        c.CustomerId = null;
        c.CurrentServiceJobId = serviceJobId;
        c.LastUpdatedAt = now;
        AddHistory(c, old, status, byUser, remarks, now);
        return c;
    }

    /// <summary>The repair job was written off: the unit is scrap. Terminal, and never re-issuable.</summary>
    public async Task MarkScrappedAsync(long serialId, long byUser, string remarks, CancellationToken ct)
    {
        var c = await db.ComponentSerials.FirstOrDefaultAsync(x => x.Id == serialId, ct);
        if (c is null) return;
        var old = c.Status.ToString();
        var now = DateTime.UtcNow;
        c.Status = SerialStatus.Scrapped;
        c.OwnerType = SerialOwnerType.ServiceCenter;
        c.OwnerRef = "Service center";
        c.TechnicianId = null;
        c.CustomerId = null;
        c.CurrentServiceJobId = null;
        c.LastUpdatedAt = now;
        AddHistory(c, old, SerialStatus.Scrapped, byUser, remarks, now);
    }

    /// <summary>How many of a part the technician holds that are NOT yet accounted for unit by unit.
    /// Serial capture on issue is new; the stock already in technicians' hands when it was switched on
    /// has a quantity balance and no serial records. That residue is what this counts.
    ///
    /// Counts RECEIVED units only, to match what the quantity balance counts. A unit that arrived
    /// faulty is in the technician's custody but was never credited as usable stock, so including it
    /// here would understate the residue and refuse a serial that genuinely needs adopting.</summary>
    public async Task<int> UntrackedHoldingAsync(long partId, long technicianId, CancellationToken ct)
    {
        var onHand = await db.StockBalances.AsNoTracking()
            .Where(b => b.PartId == partId && b.TechnicianId == technicianId)
            .Select(b => (int?)b.OnHand).FirstOrDefaultAsync(ct) ?? 0;
        var tracked = await db.ComponentSerials.AsNoTracking()
            .CountAsync(c => c.PartId == partId && c.TechnicianId == technicianId
                          && c.OwnerType == SerialOwnerType.Technician
                          && c.Status == SerialStatus.Received, ct);
        return Math.Max(onHand - tracked, 0);
    }

    /// <summary>Bring a unit the technician already physically holds into serial tracking, at the
    /// moment they name it. Without this, turning serial capture on would strand every unit issued
    /// before the switch: the unit is in their bag, it has a serial written on it, and the server
    /// would refuse it because no record exists.
    ///
    /// Only ever called against <see cref="UntrackedHoldingAsync"/>, so it cannot invent stock - the
    /// technician must have unaccounted-for quantity of that part for the adoption to be allowed.</summary>
    public async Task<ComponentSerial> AdoptForTechnicianAsync(
        long partId, string serialNumber, string? itemName, long technicianId, string technicianName,
        long byUser, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var created = new ComponentSerial
        {
            PartId = partId, SerialNumber = serialNumber.Trim(), ItemName = itemName,
            Status = SerialStatus.Received, OwnerType = SerialOwnerType.Technician,
            OwnerRef = technicianName, TechnicianId = technicianId,
            LastUpdatedAt = now, CreatedAt = now,
        };
        db.ComponentSerials.Add(created);
        await db.SaveChangesAsync(ct);   // id for the audit row
        AddHistory(created, null, SerialStatus.Received, byUser,
            $"Adopted into serial tracking from {technicianName}'s existing holding", now);
        return created;
    }

    /// <summary>Peer transfer: flip one RECEIVED sender-owned serial to IN_TRANSIT_TECH. Ownership
    /// stays with the sender so cancel rolls back cleanly. Error message or null.</summary>
    public async Task<string?> MarkInTransitTechAsync(
        long serialId, long fromTechnicianId, string toTechnicianName, long byUser, CancellationToken ct)
    {
        var c = await db.ComponentSerials.FirstOrDefaultAsync(x => x.Id == serialId, ct);
        if (c is null) return $"Serial record {serialId} not found.";
        if (c.OwnerType != SerialOwnerType.Technician || c.TechnicianId != fromTechnicianId)
            return $"{c.SerialNumber} is not in your custody.";
        if (c.Status != SerialStatus.Received)
            return $"{c.SerialNumber} cannot be transferred (status {c.Status}).";

        var old = c.Status.ToString();
        var now = DateTime.UtcNow;
        c.Status = SerialStatus.InTransitTech;
        c.LastUpdatedAt = now;
        AddHistory(c, old, SerialStatus.InTransitTech, byUser, $"Transfer initiated to {toTechnicianName}", now);
        return null;
    }

    /// <summary>Receiver acknowledges one transferred serial. RECEIVED / DEFECTIVE flip ownership to
    /// the receiver; MISSING rolls the unit back to the sender.</summary>
    public async Task<string?> AckTransferSerialAsync(
        long serialId, SerialAckStatus ack, long fromTechnicianId, string fromTechnicianName,
        long toTechnicianId, string toTechnicianName, CancellationToken ct)
    {
        var c = await db.ComponentSerials.FirstOrDefaultAsync(x => x.Id == serialId, ct);
        if (c is null) return $"Serial record {serialId} not found.";
        if (c.Status != SerialStatus.InTransitTech)
            return $"{c.SerialNumber} is not in transit (status {c.Status}).";

        var old = c.Status.ToString();
        var now = DateTime.UtcNow;
        if (ack == SerialAckStatus.Missing)
        {
            c.Status = SerialStatus.Received;
            c.OwnerType = SerialOwnerType.Technician;
            c.OwnerRef = fromTechnicianName;
            c.TechnicianId = fromTechnicianId;
            AddHistory(c, old, SerialStatus.Received, toTechnicianId,
                $"Transfer to {toTechnicianName} reported MISSING; custody returned to {fromTechnicianName}", now);
        }
        else
        {
            c.Status = ack == SerialAckStatus.Defective ? SerialStatus.Defective : SerialStatus.Received;
            c.OwnerType = SerialOwnerType.Technician;
            c.OwnerRef = toTechnicianName;
            c.TechnicianId = toTechnicianId;
            AddHistory(c, old, c.Status, toTechnicianId,
                $"Transfer from {fromTechnicianName} acknowledged as {ack}", now);
        }
        c.LastUpdatedAt = now;
        return null;
    }

    /// <summary>Sender cancelled the transfer: roll IN_TRANSIT_TECH serials back to RECEIVED at sender.
    /// Serials that already moved on are skipped silently.</summary>
    public async Task RollbackTransferSerialAsync(
        long serialId, long fromTechnicianId, string fromTechnicianName, long byUser, CancellationToken ct)
    {
        var c = await db.ComponentSerials.FirstOrDefaultAsync(x => x.Id == serialId, ct);
        if (c is null || c.Status != SerialStatus.InTransitTech) return;

        var old = c.Status.ToString();
        var now = DateTime.UtcNow;
        c.Status = SerialStatus.Received;
        c.OwnerType = SerialOwnerType.Technician;
        c.OwnerRef = fromTechnicianName;
        c.TechnicianId = fromTechnicianId;
        c.LastUpdatedAt = now;
        AddHistory(c, old, SerialStatus.Received, byUser, "Transfer cancelled", now);
    }

    /// <summary>Serials this technician holds, for a pick list. <paramref name="returnKind"/> null means
    /// the usage/transfer list (RECEIVED only); otherwise the list is exactly what that kind of return
    /// shipment may carry, so the app cannot offer a unit the server would then refuse to ship.</summary>
    public Task<List<ComponentSerial>> AvailableForTechnicianAsync(
        long technicianId, long? partId, StockReturnKind? returnKind, CancellationToken ct)
    {
        var allowed = returnKind is { } kind ? ShippableFor(kind) : GoodStockShippable;
        var q = db.ComponentSerials.AsNoTracking()
            .Where(c => c.OwnerType == SerialOwnerType.Technician && c.TechnicianId == technicianId
                     && allowed.Contains(c.Status));
        if (partId is { } pid) q = q.Where(c => c.PartId == pid);
        return q.OrderBy(c => c.SerialNumber).ToListAsync(ct);
    }

    /// <summary>Validate a fitted serial for a service/sale line: must exist for the part, be in the
    /// technician's custody, and be RECEIVED. Error message or null.</summary>
    public async Task<string?> ValidateFittedSerialAsync(
        long partId, string serialNumber, long technicianId, CancellationToken ct,
        string? technicianName = null, long? byUser = null, string? itemName = null)
    {
        var c = await FindAsync(partId, serialNumber, ct);
        if (c is null)
        {
            // Never seen this unit, but the technician may be holding stock that predates serial
            // capture. Adopt it rather than refuse: the alternative is telling someone the serial
            // printed on the part in their hand does not exist.
            if (technicianName is not null && byUser is { } uid
                && await UntrackedHoldingAsync(partId, technicianId, ct) > 0)
            {
                await AdoptForTechnicianAsync(partId, serialNumber, itemName, technicianId,
                    technicianName, uid, ct);
                return null;
            }
            return $"Serial '{serialNumber.Trim()}' is not in your inventory for this part.";
        }
        if (c.OwnerType != SerialOwnerType.Technician || c.TechnicianId != technicianId)
            return $"Serial '{c.SerialNumber}' is not in your custody ({c.OwnerType}: {c.OwnerRef}).";
        if (c.Status != SerialStatus.Received)
            return $"Serial '{c.SerialNumber}' is not available (status {c.Status}).";
        return null;
    }

    public Task<ComponentSerial?> FindAsync(long partId, string serialNumber, CancellationToken ct)
        => db.ComponentSerials.FirstOrDefaultAsync(
            c => c.PartId == partId && c.SerialNumber == serialNumber.Trim(), ct);

    /// <summary>Keeps owner_type/owner_ref consistent with a manually-set status (gap: status and
    /// custody used to drift apart on admin changes).</summary>
    private async Task SyncOwnerToStatusAsync(ComponentSerial c, CancellationToken ct)
    {
        switch (c.Status)
        {
            case SerialStatus.Shelfed or SerialStatus.ReturnedToSc or SerialStatus.Repaired
                or SerialStatus.InTransitSc or SerialStatus.UnderRepair or SerialStatus.Scrapped:
                c.OwnerType = SerialOwnerType.ServiceCenter;
                c.OwnerRef = "Service center";
                if (c.Status != SerialStatus.InTransitSc) c.TechnicianId = null;
                break;
            case SerialStatus.Received or SerialStatus.Collected or SerialStatus.Defective
                or SerialStatus.InTransitTech when c.TechnicianId is { } tid:
                c.OwnerType = SerialOwnerType.Technician;
                c.OwnerRef = await db.Users.AsNoTracking()
                    .Where(u => u.Id == tid)
                    .Select(u => u.FullName ?? u.Username)
                    .FirstOrDefaultAsync(ct) ?? c.OwnerRef;
                break;
            case SerialStatus.Used:
                c.OwnerType = SerialOwnerType.Customer;
                break;
            // Missing / Issued: custody stays where it was.
        }
    }

    private void AddHistory(ComponentSerial c, string? oldStatus, SerialStatus newStatus,
        long byUser, string remarks, DateTime at) =>
        db.SerialStatusHistory.Add(new SerialStatusHistory
        {
            ComponentSerialId = c.Id, PartId = c.PartId, SerialNumber = c.SerialNumber,
            OldStatus = oldStatus, NewStatus = newStatus.ToString(),
            ChangedByUserId = byUser, Remarks = remarks, ChangedAt = at,
        });
}
