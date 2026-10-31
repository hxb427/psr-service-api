using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Common;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Stock;

/// <summary>Field operations by field technicians (legacy service_records / sales_transactions):
/// on-site services and direct sales. No state machine — creating one is a completed fact that
/// consumes technician stock and drives serial transitions. Pricing is server-set and stripped
/// from responses for non-pricing roles.</summary>
public static class FieldOpsEndpoints
{
    public static IEndpointRouteBuilder MapFieldOpsEndpoints(this IEndpointRouteBuilder app)
    {
        var services = app.MapGroup("/field-services").WithTags("field-services").RequireAuthorization();
        services.MapGet("/", ListServicesAsync);
        services.MapGet("/by-ticket/{ticketId}", TicketServiceStatusAsync);
        services.MapPost("/", CreateServiceAsync).RequireAuthorization("FieldOpsRecord");

        var sales = app.MapGroup("/field-sales").WithTags("field-sales").RequireAuthorization();
        sales.MapGet("/", ListSalesAsync);
        sales.MapPost("/", CreateSaleAsync).RequireAuthorization("FieldOpsRecord");

        // Pick list: serials the caller has in hand (RECEIVED) for a part.
        app.MapGet("/serials/available", AvailableSerialsAsync).RequireAuthorization();

        return app;
    }

    // ---------------------------------------------------------------- field services

    /// <param name="from">Earliest shop day to include, inclusive. Filtered on the day the visit was
    /// filed. Without it the list is the newest <paramref name="limit"/> visits and nothing older,
    /// which is a window and not a search: an office looking for last quarter's work needs the range
    /// rather than a bigger page.</param>
    /// <param name="to">Latest shop day to include, also inclusive.</param>
    private static async Task<Ok<List<FieldServiceDto>>> ListServicesAsync(
        AppDbContext db, ClaimsPrincipal user, long? technicianId,
        DateTime? from, DateTime? to, int? limit, CancellationToken ct)
    {
        user.TryGetUserId(out var uid);
        var manage = StockRoles.CanManage(user);
        var take = limit is null or < 1 or > 500 ? 100 : limit.Value;

        // Decided here rather than inside the query builder, because it is the one filter that is
        // not the caller's to ask for — the same split as FieldCallsEndpoints.ListAsync.
        var ownerOnly = manage ? technicianId : uid;

        var rows = await VisitQuery(db, ownerOnly, from, to, take).ToListAsync(ct);
        var pricing = CanSeePricing(user);
        var dtos = new List<FieldServiceDto>();
        foreach (var f in rows) dtos.Add(await ServiceToDtoAsync(db, f, pricing, ct));
        return TypedResults.Ok(dtos);
    }

    /// <summary>The visit selection, as one query.
    ///
    /// Internal and separate so a test can make the provider translate it without a database, the
    /// way <see cref="FieldCallsEndpoints.RegisterQuery"/> is. What only fails against real MySQL
    /// here is the date window: created_at is a UTC timestamp, not a day, and the bounds are shop
    /// days — see <see cref="ShopClock.UtcWindow"/> for why that is not a comparison against
    /// midnight.</summary>
    internal static IQueryable<FieldService> VisitQuery(
        AppDbContext db, long? technicianId, DateTime? from, DateTime? to, int take)
    {
        var q = db.FieldServices.AsNoTracking().Include(f => f.Lines).AsQueryable();
        if (technicianId is { } tid) q = q.Where(f => f.TechnicianId == tid);

        var (fromUtc, toUtc) = ShopClock.UtcWindow(from, to);
        if (fromUtc is { } fu) q = q.Where(f => f.CreatedAt >= fu);
        // Exclusive, and so the start of the day AFTER the one asked for: that is what makes the
        // upper bound inclusive of its own day without naming a last instant to miss things past.
        if (toUtc is { } tu) q = q.Where(f => f.CreatedAt < tu);

        return q.OrderByDescending(f => f.Id).Take(take);
    }

    /// <summary>Has this ticket been serviced yet, by the caller? Scoped to the caller's own records
    /// for the same reason the list is: one technician has no business learning what another has
    /// booked. A manager asking sees every record against the ticket.</summary>
    private static async Task<Ok<TicketServiceStatusDto>> TicketServiceStatusAsync(
        string ticketId, AppDbContext db, ClaimsPrincipal user, CancellationToken ct)
    {
        user.TryGetUserId(out var uid);
        var id = ticketId.Trim();
        var q = db.FieldServices.AsNoTracking().Where(f => f.TicketId == id);
        if (!StockRoles.CanManage(user)) q = q.Where(f => f.TechnicianId == uid);

        var nos = await q.OrderBy(f => f.Id).Select(f => f.ServiceNo).ToListAsync(ct);
        return TypedResults.Ok(new TicketServiceStatusDto(id, nos.Count > 0, nos));
    }

    private static async Task<Results<Created<FieldServiceDto>, BadRequest<string>, ForbidHttpResult>> CreateServiceAsync(
        [FromBody] CreateFieldServiceRequest req, ClaimsPrincipal user, AppDbContext db,
        NumberSequenceService seq, StockLedgerService ledger, SerialService serial,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        user.TryGetUserId(out var uid);
        var tech = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == uid, ct);
        if (tech is null) return TypedResults.BadRequest("User not found.");
        if (!CarriesStockOffSite(tech)) return TypedResults.Forbid();
        var techName = tech.FullName ?? tech.Username;
        var customerId = await ResolveCustomerIdAsync(db, req.CustomerId, ct);

        // Everything that can be refused without touching the database is refused first, so a bad
        // enum spelling cannot leave a half-written visit behind to be rolled back.
        var (rateType, rateErr) = FieldVisitRules.ParseRateType(req.RateType);
        if (rateErr is not null) return TypedResults.BadRequest(rateErr);
        var (payment, payErr) = FieldVisitRules.ParsePaymentMethod(req.PaymentMethod);
        if (payErr is not null) return TypedResults.BadRequest(payErr);
        var (warranty, warrErr) = FieldVisitRules.ParseWarrantyStatus(req.WarrantyStatus);
        if (warrErr is not null) return TypedResults.BadRequest(warrErr);
        if (FieldVisitRules.ValidateFix(req.Latitude, req.Longitude) is { } fixErr)
            return TypedResults.BadRequest(fixErr);
        if (await ValidatePlaceAsync(db, req.StateId, req.DistrictId, ct) is { } placeErr)
            return TypedResults.BadRequest(placeErr);
        var (charge, chargeErr) = await ResolveServiceChargeAsync(db, req.ServiceChargeId, ct);
        if (chargeErr is not null) return TypedResults.BadRequest(chargeErr);
        var (call, callErr) = await ResolveFieldCallAsync(db, req.FieldCallId, uid, ct);
        if (callErr is not null) return TypedResults.BadRequest(callErr);
        // Incomplete when unsaid, matching the register's own default: a visit that says nothing
        // about finishing has not claimed to have finished.
        var (callStatus, callStatusErr) =
            FieldCallRules.ParseOptional(req.CallStatus, FieldCallStatus.Incomplete, "call status");
        if (callStatusErr is not null) return TypedResults.BadRequest(callStatusErr);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        FieldService fs;
        try
        {
            var no = await seq.NextAsync(SequenceKeys.FieldService, ct);
            fs = new FieldService
            {
                ServiceNo = no, TechnicianId = uid, CustomerName = req.CustomerName.Trim(),
                CustomerId = customerId,
                TicketId = NormalizeTicketRef(req.TicketId),
                TicketNumber = NormalizeTicketRef(req.TicketNumber),
                FieldCallId = call?.Id,
                Phone = req.Phone?.Trim(), Place = req.Place?.Trim(), MachineSerial = req.MachineSerial?.Trim(),
                Complaint = req.Complaint?.Trim(), Cause = req.Cause?.Trim(),
                WorkDone = req.WorkDone?.Trim(), Remarks = req.Remarks?.Trim(),
                RateType = rateType,
                ServiceChargeId = charge?.Id,
                // Snapshotted, not joined on read: the charge may be renamed or repriced tomorrow and
                // this record has to keep saying what the customer was told today.
                ServiceChargeDescription = charge?.Name,
                ServiceChargeAmount = charge?.Charge ?? 0m,
                ServiceChargeTaxPercent = charge?.TaxPercent ?? 0m,
                PaymentMethod = payment,
                WarrantyStatus = warranty,
                LocationLabel = Trimmed(req.LocationLabel),
                Latitude = req.Latitude, Longitude = req.Longitude,
                StateId = req.StateId, DistrictId = req.DistrictId,
                CreatedByUserId = uid,
            };
            db.FieldServices.Add(fs);
            await db.SaveChangesAsync(ct);   // id for line FKs + reference id

            var lines = new List<FieldServiceLine>();
            foreach (var lineReq in req.Lines ?? [])
            {
                var (line, err) = await BuildLineAsync(db, ledger, serial, lineReq.Kind, lineReq.PartId,
                    lineReq.Qty, lineReq.SerialNo, lineReq.Defective, uid, techName,
                    fs.CustomerName, customerId, "FIELD_SERVICE", fs.Id, rateType, ct);
                if (err is not null) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(err); }
                line!.FieldServiceId = fs.Id;
                db.FieldServiceLines.Add(line);
                lines.Add(line);
            }

            // Computed here from the lines the server just priced, never from anything the client sent.
            var totals = FieldVisitRules.Totals(
                lines.Select(l => (l.Amount, l.TaxAmount)),
                fs.ServiceChargeAmount, fs.ServiceChargeTaxPercent, warranty);
            fs.TaxableAmount = totals.Taxable;
            fs.TaxAmount = totals.Tax;
            fs.TotalAmount = totals.Total;
            fs.AmountPayable = totals.Payable;

            // Inside the transaction on purpose. "The call is updated along with the service" has to
            // mean both rows or neither: a call completed with no visit behind it sends nobody back,
            // and a visit whose call still sits open gets attended twice.
            CloseOutCall(call, callStatus);

            audit.Log(uid, "field-service.create", "field_service", fs.Id,
                details: $"{fs.ServiceNo} {fs.RateType} total {fs.TotalAmount:0.##} payable {fs.AmountPayable:0.##}"
                    + (call is null ? string.Empty : $" call {call.CallNo} {call.CallStatus}"),
                ip: http.GetIp());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        var loaded = await db.FieldServices.AsNoTracking().Include(f => f.Lines).FirstAsync(f => f.Id == fs.Id, ct);
        return TypedResults.Created($"/field-services/{fs.Id}", await ServiceToDtoAsync(db, loaded, CanSeePricing(user), ct));
    }

    /// <summary>The call register entry this visit is being filed against, or why it cannot be.
    ///
    /// Tracked rather than <c>AsNoTracking</c>: where the save completes the call, that update has to
    /// ride the same <c>SaveChanges</c> and the same transaction as the visit itself.
    ///
    /// Own calls only. The register is self-scoped on read for the same reason - one engineer's day
    /// is not another's to write up - so the picker never offered anyone else's call, and an id that
    /// names one is a stale list or a hand-made request rather than something to honour.</summary>
    private static async Task<(FieldCall? call, string? error)> ResolveFieldCallAsync(
        AppDbContext db, long? fieldCallId, long uid, CancellationToken ct)
    {
        if (fieldCallId is not { } id) return (null, null);
        var call = await db.FieldCalls.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (call is null) return (null, $"Call {id} was not found.");
        if (call.EngineerId != uid) return (null, $"{call.CallNo} was logged by another engineer.");
        return (call, null);
    }

    /// <summary>Writes the visit back onto the call it answered.
    ///
    /// Two separate facts, and only one of them is a status. The attended date is set because a visit
    /// IS the engineer having attended, but only where the call had none - a call logged with the day
    /// they actually drove out keeps that day, which is not always the day somebody wrote it up.
    ///
    /// What the status becomes is <see cref="FieldCallRules.AfterVisit"/>'s to say, and it only ever
    /// moves forward. Kept there rather than inlined here so the one rule that decides whether an
    /// engineer gets sent back out is a function a test can pin.</summary>
    private static void CloseOutCall(FieldCall? call, FieldCallStatus status)
    {
        if (call is null) return;
        var touched = false;
        if (call.AttendedDate is null)
        {
            call.AttendedDate = ShopClock.Today;
            touched = true;
        }

        var next = FieldCallRules.AfterVisit(call.CallStatus, status);
        // Assigned only where it actually moves, which - the rule being forward-only - means only
        // ever to Complete. Writing the status back unchanged would be a lost update waiting to
        // happen: two visits saved at the same moment both read the call as Incomplete, and the
        // second to commit would put Incomplete back over the first one's Complete.
        if (next != call.CallStatus)
        {
            call.CallStatus = next;
            touched = true;
        }

        // Stamped only where something actually moved, for the same reason the status is. A second
        // visit to a call that was already attended and already closed changes nothing about the
        // call, and the register should not claim it was touched.
        if (touched) call.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Validates + consumes stock + drives serials for one Used/Collected line.
    ///
    /// Serial rules follow the part, not the technician: a tracked unit fitted at a customer is a
    /// tracked unit wherever the person who fitted it is based.</summary>
    private static async Task<(FieldServiceLine? line, string? error)> BuildLineAsync(
        AppDbContext db, StockLedgerService ledger, SerialService serial,
        string kindRaw, long partId, int qty, string? serialNo, bool defective,
        long uid, string techName, string customerName, long? customerId,
        string referenceType, long referenceId, FieldRateType rateType, CancellationToken ct)
    {
        if (!Enum.TryParse<FieldLineKind>(kindRaw, true, out var kind))
            return (null, $"Unknown line kind '{kindRaw}'.");
        var part = await db.Parts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == partId, ct);
        if (part is null) return (null, $"Part {partId} not found.");

        var sn = serialNo?.Trim();

        // What the line is allowed to say is decided in one place (FieldLineRules), so the rule
        // reads as a rule rather than as the shape of the ifs below.
        var ruleError = FieldLineRules.Validate(kind, part, qty, sn);
        if (ruleError is not null) return (null, ruleError);

        var line = new FieldServiceLine
        {
            Kind = kind, PartId = partId, Qty = qty, SerialNo = sn, Defective = defective,
            // A collected unit is not billed: it is the customer's own broken part coming off their
            // machine, and it rides the record for traceability rather than for money.
            UnitPrice = kind == FieldLineKind.Used ? FieldVisitRules.UnitRate(part, rateType) : 0m,
            GstPercent = kind == FieldLineKind.Used ? part.GstPercent : 0m,
        };
        line.Amount = Math.Round(line.UnitPrice * qty, 2);
        line.TaxAmount = FieldVisitRules.Tax(line.Amount, line.GstPercent);

        if (kind == FieldLineKind.Used)
        {
            if (part.IsSerialTracked)
            {
                var err = await serial.ValidateFittedSerialAsync(partId, sn!, uid, ct, techName, uid, part.Name);
                if (err is not null) return (null, err);
            }
            await ledger.ConsumeAsync(partId, uid, qty, uid, referenceType, referenceId, ct);
            if (part.IsSerialTracked)
                await serial.InstallToCustomerAsync(partId, sn!, part.Name, customerName,
                    uid, ct, customerId);
        }
        else // Collected — faulty unit taken from the customer; no stock consumption.
        {
            // An unnamed unit is recorded as a quantity on the line and stays outside serial
            // tracking — see FieldLineRules for why that is allowed.
            if (FieldLineRules.CollectedEntersSerialTracking(part, sn))
                await serial.CollectFromCustomerAsync(partId, sn!, part.Name, uid, techName, defective, uid, ct, customerId);
        }
        return (line, null);
    }

    // ---------------------------------------------------------------- field sales

    /// <param name="from">Earliest shop day to include, inclusive — the day the sale was filed.</param>
    /// <param name="to">Latest shop day to include, also inclusive.</param>
    private static async Task<Ok<List<FieldSaleDto>>> ListSalesAsync(
        AppDbContext db, ClaimsPrincipal user, long? technicianId,
        DateTime? from, DateTime? to, int? limit, CancellationToken ct)
    {
        user.TryGetUserId(out var uid);
        var manage = StockRoles.CanManage(user);
        var take = limit is null or < 1 or > 500 ? 100 : limit.Value;

        var ownerOnly = manage ? technicianId : uid;

        var rows = await SaleQuery(db, ownerOnly, from, to, take).ToListAsync(ct);
        var pricing = CanSeePricing(user);
        var dtos = new List<FieldSaleDto>();
        foreach (var f in rows) dtos.Add(await SaleToDtoAsync(db, f, pricing, ct));
        return TypedResults.Ok(dtos);
    }

    /// <summary>The sale selection, as one query. Internal for the same reason as
    /// <see cref="VisitQuery"/>, and the same shop-day window over a UTC timestamp.</summary>
    internal static IQueryable<FieldSale> SaleQuery(
        AppDbContext db, long? technicianId, DateTime? from, DateTime? to, int take)
    {
        var q = db.FieldSales.AsNoTracking().Include(f => f.Lines).AsQueryable();
        if (technicianId is { } tid) q = q.Where(f => f.TechnicianId == tid);

        var (fromUtc, toUtc) = ShopClock.UtcWindow(from, to);
        if (fromUtc is { } fu) q = q.Where(f => f.CreatedAt >= fu);
        if (toUtc is { } tu) q = q.Where(f => f.CreatedAt < tu);

        return q.OrderByDescending(f => f.Id).Take(take);
    }

    private static async Task<Results<Created<FieldSaleDto>, BadRequest<string>, ForbidHttpResult>> CreateSaleAsync(
        [FromBody] CreateFieldSaleRequest req, ClaimsPrincipal user, AppDbContext db,
        NumberSequenceService seq, StockLedgerService ledger, SerialService serial,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        user.TryGetUserId(out var uid);
        var tech = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == uid, ct);
        if (tech is null) return TypedResults.BadRequest("User not found.");
        if (!CarriesStockOffSite(tech)) return TypedResults.Forbid();
        var techName = tech.FullName ?? tech.Username;
        var customerId = await ResolveCustomerIdAsync(db, req.CustomerId, ct);

        var (rateType, rateErr) = FieldVisitRules.ParseRateType(req.RateType);
        if (rateErr is not null) return TypedResults.BadRequest(rateErr);
        var (payment, payErr) = FieldVisitRules.ParsePaymentMethod(req.PaymentMethod);
        if (payErr is not null) return TypedResults.BadRequest(payErr);
        if (FieldVisitRules.ValidateFix(req.Latitude, req.Longitude) is { } fixErr)
            return TypedResults.BadRequest(fixErr);
        if (await ValidatePlaceAsync(db, req.StateId, req.DistrictId, ct) is { } placeErr)
            return TypedResults.BadRequest(placeErr);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        FieldSale sale;
        try
        {
            var no = await seq.NextAsync(SequenceKeys.FieldSale, ct);
            sale = new FieldSale
            {
                SaleNo = no, TechnicianId = uid, CustomerName = req.CustomerName.Trim(),
                CustomerId = customerId,
                Phone = req.Phone?.Trim(), Place = req.Place?.Trim(), Remarks = req.Remarks?.Trim(),
                RateType = rateType, PaymentMethod = payment,
                LocationLabel = Trimmed(req.LocationLabel),
                Latitude = req.Latitude, Longitude = req.Longitude,
                StateId = req.StateId, DistrictId = req.DistrictId,
                CreatedByUserId = uid,
            };
            db.FieldSales.Add(sale);
            await db.SaveChangesAsync(ct);

            var saleLines = new List<FieldSaleLine>();
            foreach (var lineReq in req.Lines)
            {
                var part = await db.Parts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == lineReq.PartId, ct);
                if (part is null) { await tx.RollbackAsync(ct); return TypedResults.BadRequest($"Part {lineReq.PartId} not found."); }
                var sn = lineReq.SerialNo?.Trim();

                if (part.IsSerialTracked)
                {
                    if (string.IsNullOrWhiteSpace(sn))
                    { await tx.RollbackAsync(ct); return TypedResults.BadRequest($"{part.ItemCode} is serial-tracked — name the sold serial."); }
                    if (lineReq.Qty != 1)
                    { await tx.RollbackAsync(ct); return TypedResults.BadRequest($"{part.ItemCode} is serial-tracked — one line per unit (qty 1)."); }
                    var err = await serial.ValidateFittedSerialAsync(
                        lineReq.PartId, sn!, uid, ct, techName, uid, part.Name);
                    if (err is not null) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(err); }
                }

                await ledger.ConsumeAsync(lineReq.PartId, uid, lineReq.Qty, uid, "FIELD_SALE", sale.Id, ct);
                if (part.IsSerialTracked)
                    await serial.InstallToCustomerAsync(lineReq.PartId, sn!, part.Name, sale.CustomerName,
                        uid, ct, customerId);

                var unit = FieldVisitRules.UnitRate(part, rateType);
                var taxable = Math.Round(unit * lineReq.Qty, 2);
                saleLines.Add(new FieldSaleLine
                {
                    FieldSaleId = sale.Id, PartId = lineReq.PartId, Qty = lineReq.Qty,
                    SerialNo = sn, UnitPrice = unit, Amount = taxable,
                    GstPercent = part.GstPercent,
                    TaxAmount = FieldVisitRules.Tax(taxable, part.GstPercent),
                });
            }
            db.FieldSaleLines.AddRange(saleLines);

            // A sale carries no warranty, so its total IS what the customer pays.
            var totals = FieldVisitRules.Totals(
                saleLines.Select(l => (l.Amount, l.TaxAmount)), 0m, 0m, warranty: null);
            sale.TaxableAmount = totals.Taxable;
            sale.TaxAmount = totals.Tax;
            sale.TotalAmount = totals.Total;

            audit.Log(uid, "field-sale.create", "field_sale", sale.Id,
                details: $"{sale.SaleNo} {sale.RateType} total {sale.TotalAmount:0.##}", ip: http.GetIp());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        var loaded = await db.FieldSales.AsNoTracking().Include(f => f.Lines).FirstAsync(f => f.Id == sale.Id, ct);
        return TypedResults.Created($"/field-sales/{sale.Id}", await SaleToDtoAsync(db, loaded, CanSeePricing(user), ct));
    }

    /// <summary>The chosen service charge, read from the catalogue so the record is priced by the
    /// server. An id that does not resolve is refused rather than dropped, unlike a stale customer id
    /// below: a customer id that no longer exists loses a link, while a charge id that no longer
    /// exists loses money off the bill, and the technician has already quoted that figure.</summary>
    private static async Task<(ServiceCharge? charge, string? error)> ResolveServiceChargeAsync(
        AppDbContext db, long? chargeId, CancellationToken ct)
    {
        if (chargeId is not { } id) return (null, null);
        var sc = await db.ServiceCharges.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (sc is null) return (null, $"Service charge {id} was not found.");
        // An inactive charge is still honoured if somehow asked for: it was on the list when the
        // technician opened the form, and refusing the save over a catalogue change made between the
        // quote and the drive home would be a strange way to find out.
        return (sc, null);
    }

    /// <summary>Why the state/district pair cannot be accepted, or null when it can.
    ///
    /// The pair is what is checked, not each half: a district id that belongs to a different state is
    /// the one error worth catching here, because every "how is this technician's district doing"
    /// question is answered by grouping on these two, and a mismatched pair poisons the grouping
    /// silently rather than failing.</summary>
    private static async Task<string?> ValidatePlaceAsync(
        AppDbContext db, int? stateId, int? districtId, CancellationToken ct)
    {
        if (stateId is { } sid && !await db.IndianStates.AsNoTracking().AnyAsync(x => x.Id == sid, ct))
            return $"State {sid} was not found.";
        if (districtId is not { } did) return null;

        var district = await db.Districts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == did, ct);
        if (district is null) return $"District {did} was not found.";
        if (stateId is null) return "Name the state the district belongs to.";
        if (district.StateId != stateId)
            return $"{district.Name} is not a district of the state you picked.";
        return null;
    }

    private static string? Trimmed(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>A field customer may be an existing account picked from the list, or a name typed on
    /// the spot. Only the first kind can be linked, and an id that no longer resolves is dropped
    /// rather than rejected - the free-text name on the record still says who it was.</summary>
    private static async Task<long?> ResolveCustomerIdAsync(AppDbContext db, long? customerId, CancellationToken ct)
        => customerId is { } cid && await db.Customers.AsNoTracking().AnyAsync(c => c.Id == cid, ct) ? cid : null;

    // ---------------------------------------------------------------- available serials

    private static async Task<Ok<List<AvailableSerialDto>>> AvailableSerialsAsync(
        AppDbContext db, ClaimsPrincipal user, SerialService serial, long? partId, bool? forReturn,
        string? returnKind, CancellationToken ct)
    {
        user.TryGetUserId(out var uid);
        // forReturn is the older flag and now means the good-stock list; returnKind is explicit and
        // wins when sent, so a client that knows about faulty returns can ask for that list instead.
        StockReturnKind? kind = null;
        if (!string.IsNullOrWhiteSpace(returnKind) && Enum.TryParse<StockReturnKind>(returnKind, true, out var k))
            kind = k;
        else if (forReturn == true) kind = StockReturnKind.GoodStock;

        var rows = await serial.AvailableForTechnicianAsync(uid, partId, kind, ct);
        var partIds = rows.Select(r => r.PartId).Distinct().ToList();
        var codes = await db.Parts.AsNoTracking().Where(p => partIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.ItemCode, ct);
        return TypedResults.Ok(rows.Select(r => new AvailableSerialDto(
            r.Id, r.PartId, codes.GetValueOrDefault(r.PartId, $"#{r.PartId}"),
            r.ItemName, r.SerialNumber, r.Status.ToString())).ToList());
    }

    // ---------------------------------------------------------------- mapping

    /// <summary>Whether this caller reads the money on a field record.
    ///
    /// A technician now does, where before they read every figure as null. The widening is safe here
    /// without any extra scoping, and that is worth saying out loud: the list queries above already
    /// narrow a non-manage caller to <c>TechnicianId == uid</c>, so the only priced records a
    /// technician can reach are the ones they wrote. They are also the person who quoted those figures
    /// to the customer and took the money for them, so there is nothing on their own record they have
    /// not already seen.
    ///
    /// Purchase rates are a different matter and never appear here — a field line carries a selling
    /// rate and its tax, not what the shop paid. See <see cref="PricingVisibility"/>.</summary>
    private static bool CanSeePricing(ClaimsPrincipal user) =>
        PricingVisibility.For(user) != PricingScope.None;

    /// <summary>The second half of the field-ops gate. The route's policy has already established that
    /// the caller is a technician; this establishes that they are the kind who carries stock off-site.
    ///
    /// It lives here rather than in a policy because is_field_technician is an account column and never
    /// reaches the token — the claims a session carries are its roles, and both kinds of technician hold
    /// the same one. An in-house technician therefore satisfies the policy exactly as a field technician
    /// does, while holding a balance and serial custody that this endpoint would happily consume: without
    /// this check the bench could book an on-site job against a customer nobody visited, and take the
    /// parts out of stock to do it.</summary>
    internal static bool CarriesStockOffSite(User account) => account.IsFieldTechnician;

    private static async Task<FieldServiceDto> ServiceToDtoAsync(
        AppDbContext db, FieldService f, bool pricing, CancellationToken ct)
    {
        var techName = await TechNameAsync(db, f.TechnicianId, ct);
        var parts = await PartsForAsync(db, f.Lines.Select(l => l.PartId), ct);
        var (stateName, districtName) = await PlaceNamesAsync(db, f.StateId, f.DistrictId, ct);
        // One extra round trip, and only for a visit that actually named a call.
        var call = f.FieldCallId is { } callId
            ? await db.FieldCalls.AsNoTracking().Where(c => c.Id == callId)
                .Select(c => new { c.CallNo, c.CallStatus }).FirstOrDefaultAsync(ct)
            : null;
        var lines = f.Lines.Select(l => new FieldServiceLineDto(
            l.Id, l.Kind.ToString(), l.PartId,
            parts.TryGetValue(l.PartId, out var p) ? p.ItemCode : $"#{l.PartId}", p?.Name ?? string.Empty,
            l.Qty, l.SerialNo, l.Defective,
            pricing ? l.UnitPrice : null, pricing ? l.Amount : null,
            pricing ? l.GstPercent : null, pricing ? l.TaxAmount : null,
            pricing ? l.Amount + l.TaxAmount : null,
            // HSN comes off the live part rather than a snapshot: a part's HSN is a tax
            // classification that does not change with a price, and carrying a column for it would
            // be a column that never differs from the master.
            pricing ? p?.HsnCode : null)).ToList();
        return new FieldServiceDto(
            f.Id, f.ServiceNo, f.TechnicianId, techName, f.CustomerName, f.Phone, f.Place,
            f.MachineSerial, f.Complaint, f.WorkDone, f.Remarks, f.CreatedAt,
            pricing ? f.TotalAmount : null, lines,
            f.TicketId, f.TicketNumber,
            f.Cause,
            f.RateType.ToString(),
            f.ServiceChargeId, f.ServiceChargeDescription,
            pricing ? f.ServiceChargeAmount : null,
            pricing ? f.ServiceChargeTaxPercent : null,
            f.PaymentMethod?.ToString(), f.WarrantyStatus?.ToString(),
            pricing ? f.TaxableAmount : null, pricing ? f.TaxAmount : null,
            pricing ? f.AmountPayable : null,
            f.LocationLabel, f.Latitude, f.Longitude,
            f.StateId, stateName, f.DistrictId, districtName,
            f.FieldCallId, call?.CallNo, call?.CallStatus.ToString());
    }

    /// <summary>State and district names for a record, in one round trip each. Resolved on read rather
    /// than snapshotted onto the record: unlike a rate, a district is not renamed to mean something
    /// different, and an id that still points at the same place is the more honest thing to store.</summary>
    private static async Task<(string? state, string? district)> PlaceNamesAsync(
        AppDbContext db, int? stateId, int? districtId, CancellationToken ct)
    {
        var state = stateId is { } sid
            ? await db.IndianStates.AsNoTracking().Where(x => x.Id == sid)
                .Select(x => (string?)x.Name).FirstOrDefaultAsync(ct)
            : null;
        var district = districtId is { } did
            ? await db.Districts.AsNoTracking().Where(x => x.Id == did)
                .Select(x => (string?)x.Name).FirstOrDefaultAsync(ct)
            : null;
        return (state, district);
    }

    /// <summary>Trims a ticket reference, and treats an all-whitespace one as absent.
    ///
    /// The distinction matters more than it looks. A client that sends "" for a job with no ticket
    /// would otherwise store an empty string, and an empty string equals every other empty string:
    /// the by-ticket lookup for one ticketless service would then return every ticketless service on
    /// record, which the app reads as "this ticket has been serviced" for a ticket nobody attended —
    /// and that is the one thing standing between an OTP and a close.</summary>
    internal static string? NormalizeTicketRef(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static async Task<FieldSaleDto> SaleToDtoAsync(
        AppDbContext db, FieldSale f, bool pricing, CancellationToken ct)
    {
        var techName = await TechNameAsync(db, f.TechnicianId, ct);
        var parts = await PartsForAsync(db, f.Lines.Select(l => l.PartId), ct);
        var (stateName, districtName) = await PlaceNamesAsync(db, f.StateId, f.DistrictId, ct);
        var lines = f.Lines.Select(l => new FieldSaleLineDto(
            l.Id, l.PartId,
            parts.TryGetValue(l.PartId, out var p) ? p.ItemCode : $"#{l.PartId}", p?.Name ?? string.Empty,
            l.Qty, l.SerialNo,
            pricing ? l.UnitPrice : null, pricing ? l.Amount : null,
            pricing ? l.GstPercent : null, pricing ? l.TaxAmount : null,
            pricing ? l.Amount + l.TaxAmount : null,
            pricing ? p?.HsnCode : null)).ToList();
        return new FieldSaleDto(
            f.Id, f.SaleNo, f.TechnicianId, techName, f.CustomerName, f.Phone, f.Place, f.Remarks, f.CreatedAt,
            pricing ? f.TotalAmount : null, lines,
            f.RateType.ToString(), f.PaymentMethod?.ToString(),
            pricing ? f.TaxableAmount : null, pricing ? f.TaxAmount : null,
            f.LocationLabel, f.Latitude, f.Longitude,
            f.StateId, stateName, f.DistrictId, districtName);
    }

    private static Task<string?> TechNameAsync(AppDbContext db, long id, CancellationToken ct)
        => db.Users.AsNoTracking().Where(u => u.Id == id)
            .Select(u => (string?)(u.FullName ?? u.Username)).FirstOrDefaultAsync(ct);

    private static Task<Dictionary<long, Part>> PartsForAsync(
        AppDbContext db, IEnumerable<long> partIds, CancellationToken ct)
    {
        var ids = partIds.Distinct().ToList();
        return db.Parts.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
    }
}
