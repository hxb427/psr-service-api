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
/// longer. It refuses to hand one out without it.</summary>
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

    // ---------------------------------------------------------------- helpers

    /// <summary>A call customer may be an existing account picked from the list or a name typed on
    /// the spot. Only the first kind can be linked, and an id that no longer resolves is dropped
    /// rather than rejected — the free-text name still says who it was. Same bargain as
    /// <see cref="FieldService.CustomerId"/>.</summary>
    private static async Task<long?> ResolveCustomerIdAsync(AppDbContext db, long? customerId, CancellationToken ct)
        => customerId is { } cid && await db.Customers.AsNoTracking().AnyAsync(c => c.Id == cid, ct) ? cid : null;

    private static string? Trimmed(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

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
            c.CallReferenceNo, c.MachineModel, c.MachineSerial, c.SoftwareVersion,
            c.AttendedDate, c.CallStatus.ToString(), c.Remarks, c.CreatedAt);
}
