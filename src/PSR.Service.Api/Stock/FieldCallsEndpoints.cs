using System.Globalization;
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

/// <summary>The engineer call register: which calls came in, who went, and how each ended.
///
/// A register and not a workflow. Creating an entry writes one row and nothing else — no stock
/// movement, no serial transition, no pricing, no document. That is why this sits in its own file
/// instead of joining <see cref="FieldOpsEndpoints"/>, whose every handler exists to drive the
/// ledger: nothing a half-finished save here could leave inconsistent. It still opens a transaction,
/// but only for the call number — <see cref="NumberSequenceService"/> holds its row lock, and so
/// keeps its promise of a unique number, for exactly as long as an enclosing transaction and no
/// longer. It refuses to hand one out without it.
///
/// A call can be corrected here, and the PUT is narrow on purpose. Three things it will not touch:
/// the call number, because every reference to the call is written in it; the engineer, because
/// whose day a call was is not a typo to fix; and a Complete status, because this register has no
/// step that reopens anything and the edit is not about to be the first
/// (<see cref="FieldCallRules.Reopening"/>).
///
/// It CAN close an open call, which is the one departure worth stating plainly. A field service
/// filed against a call still completes it, in that endpoint's transaction, next to the stock it
/// moved - see <see cref="FieldOpsEndpoints"/> and <see cref="FieldCallStatus"/> - and that remains
/// the path for a service call. But a demo, a dealer visit or a sales call never gets a field
/// service, because there is no stock for one to move: a call logged the morning it came in and
/// attended that afternoon would otherwise sit open for ever, and sit in the service form's picker
/// offering work that is finished. Logging a call Complete has always been allowed on the way in;
/// this is what lets an engineer say so afterwards rather than only in advance.
///
/// The <c>callStatus=Incomplete</c> filter on the list is therefore load-bearing, not a convenience:
/// it is what the service entry form asks for to offer the engineer their still-open calls.</summary>
public static class FieldCallsEndpoints
{
    public static IEndpointRouteBuilder MapFieldCallEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/field-calls").WithTags("field-calls").RequireAuthorization();

        group.MapGet("/", ListAsync);

        // The same policy the sibling field records use, and deliberately WITHOUT the
        // is_field_technician check they add on top of it.
        //
        // That check exists on a field service because the handler consumes the caller's stock: a
        // bench technician holding a balance could otherwise book an on-site job nobody attended and
        // take real parts out to do it. Nothing of the sort is reachable here — the worst an in-house
        // technician can do with this endpoint is record a call they attended, which is the point of
        // it. Requiring the flag would instead refuse an engineer who carries no parts, and demos and
        // dealer visits are exactly the calls where nobody does.
        group.MapPost("/", CreateAsync).RequireAuthorization("FieldOpsRecord");

        // The same policy as the create, for the same reason, and the handler below narrows it
        // further: a technician corrects their own entries. The oversight screens read this register
        // and do not write it.
        group.MapPut("/{id:long}", UpdateAsync).RequireAuthorization("FieldOpsRecord");

        // One tap for "I went", which the PUT above cannot be: see MarkAttendedAsync.
        group.MapPost("/{id:long}/attended", MarkAttendedAsync).RequireAuthorization("FieldOpsRecord");

        return app;
    }

    /// <summary>The register, newest call first.
    ///
    /// Scoped like the field service and sale lists: a caller without the manage roles sees only
    /// their own calls, because one engineer has no business reading another's day. A manager sees
    /// everyone's and can narrow to one engineer.</summary>
    private static async Task<Ok<List<FieldCallDto>>> ListAsync(
        AppDbContext db, ClaimsPrincipal user,
        long? engineerId, string? callType, string? callStatus,
        DateTime? from, DateTime? to, int? limit, CancellationToken ct)
    {
        user.TryGetUserId(out var uid);
        var manage = StockRoles.CanManage(user);
        var take = limit is null or < 1 or > 500 ? 100 : limit.Value;

        // Scope is decided here rather than inside the query builder, because it is the one filter
        // that is not the caller's to ask for.
        var ownerOnly = manage ? engineerId : uid;

        var rows = await RegisterQuery(db, ownerOnly, callType, callStatus, from, to, take).ToListAsync(ct);

        var names = await EngineerNamesAsync(db, rows.Select(r => r.EngineerId), ct);
        return TypedResults.Ok(rows.Select(r => ToDto(r, names.GetValueOrDefault(r.EngineerId))).ToList());
    }

    /// <summary>The register selection, as one query.
    ///
    /// Internal and separate so a test can make the provider translate it without a database. Both
    /// halves of this are things that only fail against real MySQL: the enum filters go through a
    /// string conversion, and the two bounds compare against a `date` column.</summary>
    internal static IQueryable<FieldCall> RegisterQuery(
        AppDbContext db, long? engineerId, string? callType, string? callStatus,
        DateTime? from, DateTime? to, int take)
    {
        var q = db.FieldCalls.AsNoTracking().AsQueryable();
        if (engineerId is { } eid) q = q.Where(c => c.EngineerId == eid);

        if (FieldCallRules.ParseFilter<FieldCallType>(callType) is { } type) q = q.Where(c => c.CallType == type);
        if (FieldCallRules.ParseFilter<FieldCallStatus>(callStatus) is { } status) q = q.Where(c => c.CallStatus == status);

        // Filtered on the received date, which is the day the office means by "last week's calls" —
        // not created_at, which is the day somebody got round to typing it in. Both bounds are
        // inclusive, and both are read as shop days, so a caller sending an instant still gets the
        // day they meant.
        if (ShopClock.BusinessDate(from) is { } f) q = q.Where(c => c.CallReceivedDate >= f);
        if (ShopClock.BusinessDate(to) is { } t) q = q.Where(c => c.CallReceivedDate <= t);

        // Received date first so the register reads as a calendar, with the row id breaking ties
        // inside a day to keep the order stable — several calls share a date routinely, and without
        // the tie-break MySQL is free to return them differently on each call.
        return q.OrderByDescending(c => c.CallReceivedDate).ThenByDescending(c => c.Id).Take(take);
    }

    /// <summary>Internal for the same reason as <see cref="RegisterQuery"/>: so a test can run the
    /// handler without a server. What only shows up when it runs is the transaction below — the
    /// sequence refuses a number without one, and no compiler or route check can see that.</summary>
    internal static async Task<Results<Created<FieldCallDto>, BadRequest<string>>> CreateAsync(
        [FromBody] CreateFieldCallRequest req, ClaimsPrincipal user, AppDbContext db,
        NumberSequenceService seq, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        user.TryGetUserId(out var uid);

        // Everything that can be refused without touching the database is refused before the
        // sequence is drawn from. A number taken and then abandoned leaves a visible gap in the
        // register, and a gap invites somebody to go looking for the call that is missing.
        var (callType, typeErr) = FieldCallRules.ParseRequired<FieldCallType>(req.CallType, "call type");
        if (typeErr is not null) return TypedResults.BadRequest(typeErr);
        var (locality, localityErr) =
            FieldCallRules.ParseOptional(req.Locality, FieldCallLocality.Local, "local/outstation");
        if (localityErr is not null) return TypedResults.BadRequest(localityErr);
        // Incomplete when unsaid: the understatement rather than the overstatement. A register that
        // defaulted to Complete would quietly claim work was finished because a dropdown was left
        // alone.
        var (status, statusErr) =
            FieldCallRules.ParseOptional(req.CallStatus, FieldCallStatus.Incomplete, "call status");
        if (statusErr is not null) return TypedResults.BadRequest(statusErr);

        var customerName = req.CustomerName?.Trim();
        if (string.IsNullOrEmpty(customerName)) return TypedResults.BadRequest("Name the customer.");

        var (received, attended, dateErr) = FieldCallRules.Dates(req.CallReceivedDate, req.AttendedDate);
        if (dateErr is not null) return TypedResults.BadRequest(dateErr);

        var call = new FieldCall
        {
            CallType = callType,
            // The caller, never anything the request named. The form shows the signed-in name and
            // does not let it be edited; the server is where that is actually true.
            EngineerId = uid,
            CallReceivedDate = received,
            Locality = locality,
            CustomerName = customerName,
            CustomerId = await ResolveCustomerIdAsync(db, req.CustomerId, ct),
            CustomerAddress = Trimmed(req.CustomerAddress),
            CallReferenceNo = Trimmed(req.CallReferenceNo),
            ContactPhone = Trimmed(req.ContactPhone),
            ReportedComplaint = Trimmed(req.ReportedComplaint),
            MachineModel = Trimmed(req.MachineModel),
            MachineSerial = Trimmed(req.MachineSerial),
            SoftwareVersion = Trimmed(req.SoftwareVersion),
            AttendedDate = attended,
            CallStatus = status,
            Remarks = Trimmed(req.Remarks),
        };

        if (FieldCallRules.Lengths(call) is { } lengthErr) return TypedResults.BadRequest(lengthErr);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            call.CallNo = await seq.NextAsync(SequenceKeys.FieldCall, ct);
            db.FieldCalls.Add(call);
            // Saved before the audit row so that row can carry the id of the call it describes.
            await db.SaveChangesAsync(ct);

            audit.Log(uid, "field-call.create", "field_call", call.Id,
                details: $"{call.CallNo} {call.CallType} {call.CallStatus} '{call.CustomerName}'",
                ip: http.GetIp());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        return TypedResults.Created($"/field-calls/{call.Id}",
            ToDto(call, await EngineerNameAsync(db, uid, ct)));
    }

    /// <summary>Corrects what a call SAYS. Internal for the same reason as the two above.
    ///
    /// A PUT rather than a PATCH because the form behind it holds the whole call: the engineer is
    /// looking at every box at once, so an empty one means that field is now empty. The dates and the
    /// status are the exception, and <see cref="UpdateFieldCallRequest"/> says why.
    ///
    /// No transaction, unlike <see cref="CreateAsync"/>. That one opens one because the number
    /// sequence refuses to hand out a number without it; this draws no number, and the corrected row
    /// and its audit row leave in a single SaveChanges, which EF already wraps in a transaction of
    /// its own.</summary>
    internal static async Task<Results<Ok<FieldCallDto>, NotFound, BadRequest<string>>> UpdateAsync(
        long id, [FromBody] UpdateFieldCallRequest req, ClaimsPrincipal user, AppDbContext db,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        user.TryGetUserId(out var uid);

        var call = await db.FieldCalls.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (call is null) return TypedResults.NotFound();

        // Own calls only, with no widening for a manage role - the same gate, and the same sentence,
        // that FieldOpsEndpoints.ResolveFieldCallAsync puts in front of a visit filed against a call.
        // The list widens for a manager because reading the register is oversight; rewriting somebody
        // else's account of their own day is not, and there is no screen that asks to.
        if (call.EngineerId != uid)
            return TypedResults.BadRequest($"{call.CallNo} was logged by another engineer.");

        var (callType, typeErr) = FieldCallRules.ParseRequired<FieldCallType>(req.CallType, "call type");
        if (typeErr is not null) return TypedResults.BadRequest(typeErr);
        var (locality, localityErr) =
            FieldCallRules.ParseOptional(req.Locality, call.Locality, "local/outstation");
        if (localityErr is not null) return TypedResults.BadRequest(localityErr);
        // The fallback is what the call already says, not Incomplete. On a create, silence means "not
        // finished yet"; here it means the request did not mention the status, and resetting a closed
        // call because a field was left out would be the opposite of a correction.
        var (status, statusErr) =
            FieldCallRules.ParseOptional(req.CallStatus, call.CallStatus, "call status");
        if (statusErr is not null) return TypedResults.BadRequest(statusErr);
        if (FieldCallRules.Reopening(call.CallStatus, status) is { } reopenErr)
            return TypedResults.BadRequest(reopenErr);

        var customerName = req.CustomerName?.Trim();
        if (string.IsNullOrEmpty(customerName)) return TypedResults.BadRequest("Name the customer.");

        // An omitted received date keeps the day the call already carries. FieldCallRules.Dates reads
        // a null as today, which is right on the way in and wrong here - it would drag a call logged
        // last Tuesday forward to this morning because somebody fixed a spelling in the remarks.
        var (received, attended, dateErr) =
            FieldCallRules.Dates(req.CallReceivedDate ?? call.CallReceivedDate, req.AttendedDate);
        if (dateErr is not null) return TypedResults.BadRequest(dateErr);

        // What the edit would store, assembled and length-checked before any of it is applied. The
        // diff below assigns as it compares, so a refusal partway through it would leave half the
        // correction on the tracked call and half of it nowhere.
        var edited = new FieldCall
        {
            CustomerName = customerName,
            CustomerAddress = Trimmed(req.CustomerAddress),
            CallReferenceNo = Trimmed(req.CallReferenceNo),
            ContactPhone = Trimmed(req.ContactPhone),
            ReportedComplaint = Trimmed(req.ReportedComplaint),
            MachineModel = Trimmed(req.MachineModel),
            MachineSerial = Trimmed(req.MachineSerial),
            SoftwareVersion = Trimmed(req.SoftwareVersion),
            Remarks = Trimmed(req.Remarks),
        };
        if (FieldCallRules.Lengths(edited) is { } lengthErr) return TypedResults.BadRequest(lengthErr);

        // Re-resolved rather than carried over: the engineer may have linked a customer they had only
        // typed before, or dropped a link that pointed at the wrong account. An id that no longer
        // exists lands as null here exactly as it does on a create, and the typed name still says who
        // it was.
        var customerId = await ResolveCustomerIdAsync(db, req.CustomerId, ct);

        // Field by field, so the audit row says what moved rather than only that something did. In the
        // order the form shows the boxes, so somebody reading the log reads it in the order the
        // engineer saw them.
        var diff = new AuditDiff();
        diff.Set("call type", call.CallType, callType, v => call.CallType = v);
        // Named explicitly: both sides are a non-nullable DateTime here, and inference cannot get
        // from that to Moved's T? on its own.
        Moved<DateTime>(diff, "received", call.CallReceivedDate, received,
            v => call.CallReceivedDate = v!.Value);
        diff.Set("local/outstation", call.Locality, locality, v => call.Locality = v);
        diff.Set("customer", call.CustomerName, edited.CustomerName,
            v => call.CustomerName = v ?? call.CustomerName);
        Moved(diff, "customer link", call.CustomerId, customerId, v => call.CustomerId = v);
        diff.Set("address", call.CustomerAddress, edited.CustomerAddress, v => call.CustomerAddress = v);
        diff.Set("phone", call.ContactPhone, edited.ContactPhone, v => call.ContactPhone = v);
        diff.Set("call reference", call.CallReferenceNo, edited.CallReferenceNo,
            v => call.CallReferenceNo = v);
        diff.Set("serial", call.MachineSerial, edited.MachineSerial, v => call.MachineSerial = v);
        diff.Set("model", call.MachineModel, edited.MachineModel, v => call.MachineModel = v);
        diff.Set("software", call.SoftwareVersion, edited.SoftwareVersion, v => call.SoftwareVersion = v);
        diff.Set("reported complaint", call.ReportedComplaint, edited.ReportedComplaint,
            v => call.ReportedComplaint = v);
        Moved(diff, "attended", call.AttendedDate, attended, v => call.AttendedDate = v);
        diff.Set("status", call.CallStatus, status, v => call.CallStatus = v);
        diff.Set("remarks", call.Remarks, edited.Remarks, v => call.Remarks = v);

        var engineerName = await EngineerNameAsync(db, call.EngineerId, ct);
        // Handed back as read when nothing moved, and no audit row written for it. A form re-posted
        // untouched - Save tapped on a call the engineer only opened to look at - is not an event in
        // the life of the call.
        if (!diff.HasChanges) return TypedResults.Ok(ToDto(call, engineerName));

        call.UpdatedAt = DateTime.UtcNow;
        audit.Log(uid, "field-call.update", "field_call", call.Id,
            details: diff.Describe(call.CallNo), ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(ToDto(call, engineerName));
    }

    /// <summary>Stamps today onto a call as the day the engineer attended, and changes nothing else.
    ///
    /// The status is untouched, and that is the feature rather than an omission. Attending and
    /// finishing are two different claims: an engineer who drove out, found the part was wrong and
    /// has to come back on Thursday has attended the call and not completed it. Until this existed
    /// the only ways to record the first were to open the whole form or to overstate the second, and
    /// a register collects what it makes easy.
    ///
    /// Deliberately not <see cref="UpdateAsync"/> with an attended date in it. This is tapped off a
    /// list row, where the only thing the engineer is asserting is that they went - and a PUT
    /// assembled from a list row would rewrite every other field from whatever that row was holding,
    /// which on a stale list means quietly undoing somebody's correction.
    ///
    /// First attendance wins, as it does in <see cref="FieldOpsEndpoints"/>'s close-out: a call that
    /// already carries a day keeps it, and comes back unchanged rather than refused. So tapping twice
    /// is the same as tapping once, which is what a button on a list has to be - the engineer cannot
    /// see whether the first tap landed until the list reloads, and a handset on a field connection
    /// is the one place a request gets sent twice.</summary>
    internal static async Task<Results<Ok<FieldCallDto>, NotFound, BadRequest<string>>> MarkAttendedAsync(
        long id, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http,
        CancellationToken ct)
    {
        user.TryGetUserId(out var uid);

        var call = await db.FieldCalls.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (call is null) return TypedResults.NotFound();
        // The same gate, and the same sentence, as the correction above.
        if (call.EngineerId != uid)
            return TypedResults.BadRequest($"{call.CallNo} was logged by another engineer.");

        var engineerName = await EngineerNameAsync(db, call.EngineerId, ct);
        if (call.AttendedDate is not null) return TypedResults.Ok(ToDto(call, engineerName));

        var today = ShopClock.Today;
        call.AttendedDate = today;
        call.UpdatedAt = DateTime.UtcNow;
        audit.Log(uid, "field-call.attended", "field_call", call.Id,
            details: $"{call.CallNo}: attended {today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
            ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(ToDto(call, engineerName));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A call customer may be an existing account picked from the list or a name typed on
    /// the spot. Only the first kind can be linked, and an id that no longer resolves is dropped
    /// rather than rejected — the free-text name still says who it was. Same bargain as
    /// <see cref="FieldService.CustomerId"/>.</summary>
    private static async Task<long?> ResolveCustomerIdAsync(AppDbContext db, long? customerId, CancellationToken ct)
        => customerId is { } cid && await db.Customers.AsNoTracking().AnyAsync(c => c.Id == cid, ct) ? cid : null;

    private static string? Trimmed(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>What <see cref="AuditDiff.Set{T}"/> would do, for the fields it cannot take.
    ///
    /// Its value-type overload is constrained to <c>struct</c>, and in C# that means a NON-nullable
    /// value type - so a <c>DateTime?</c> or a <c>long?</c> does not bind to it. Without this they
    /// silently pick the string overload instead and fail to compile, which is the good outcome: the
    /// bad one would be an overload that took them and compared them as text.
    ///
    /// Dates are written as plain days rather than through the diff's own formatter, which would
    /// print a received date as a midnight timestamp. A register entry's date is a day somebody wrote
    /// down (see <see cref="Data.Entities.FieldCall.CallReceivedDate"/>), and the audit row should
    /// read the way the form showed it.</summary>
    private static void Moved<T>(AuditDiff diff, string label, T? current, T? next, Action<T?> apply)
        where T : struct
    {
        if (Nullable.Equals(current, next)) return;
        diff.Note($"{label} {Show(current)} → {Show(next)}");
        apply(next);
    }

    private static string Show<T>(T? v) where T : struct => v switch
    {
        null => "(empty)",
        DateTime d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => v.Value.ToString() ?? "(empty)",
    };

    private static Task<string?> EngineerNameAsync(AppDbContext db, long id, CancellationToken ct)
        => db.Users.AsNoTracking().Where(u => u.Id == id)
            .Select(u => (string?)(u.FullName ?? u.Username)).FirstOrDefaultAsync(ct);

    /// <summary>Engineer names for a page of the register, in one round trip rather than one per row.</summary>
    private static async Task<Dictionary<long, string>> EngineerNamesAsync(
        AppDbContext db, IEnumerable<long> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        if (wanted.Count == 0) return [];
        return await db.Users.AsNoTracking().Where(u => wanted.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName ?? u.Username, ct);
    }

    private static FieldCallDto ToDto(FieldCall c, string? engineerName) =>
        new(c.Id, c.CallNo, c.CallType.ToString(), c.EngineerId, engineerName,
            c.CallReceivedDate, c.Locality.ToString(), c.CustomerName, c.CustomerId, c.CustomerAddress,
            c.CallReferenceNo, c.ContactPhone, c.ReportedComplaint,
            c.MachineModel, c.MachineSerial, c.SoftwareVersion,
            c.AttendedDate, c.CallStatus.ToString(), c.Remarks, c.CreatedAt, c.UpdatedAt);
}
