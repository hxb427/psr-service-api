using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Stock;

namespace PSR.Service.Api.Services;

/// <summary>Registering an inward item in the serial ledger, so a part that circulates can be followed.
///
/// Only items whose PS code matches a serial-tracked catalogue part are registered. That split is not
/// a limitation, it is the point: whole machines sold by the OEM live in the factory records
/// (passtestdata) which the shop only reads, and inventing ledger rows for them would duplicate a
/// register somebody else owns. A part booked in over the counter has no such register, and is exactly
/// the thing that comes back around.
///
/// The unit goes UNDER_REPAIR in the shop's custody with the job stamped on it, the customer stays on
/// the record, and the rest of the workflow already knows what to do with a job carrying a unit:
/// dispatch hands it back, stocking puts it on the shelf, total loss scraps it.
/// </summary>
public static partial class ServicesEndpoints
{
    /// <summary>Register the job's own item in the serial ledger, when it is one the ledger can hold.
    /// Returns a message for the counter when the unit has been here before under another name, or
    /// null. Does nothing at all for an item with no matching serial-tracked part.</summary>
    private static async Task<string?> RegisterInwardUnitAsync(
        AppDbContext db, SerialService serial, ServiceJob job, long uid, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(job.PsCode) || string.IsNullOrWhiteSpace(job.SerialNo)) return null;

        var code = job.PsCode.Trim();
        var part = await db.Parts.AsNoTracking().FirstOrDefaultAsync(p => p.ItemCode == code, ct);
        // Not serial-tracked means the shop does not follow this item unit by unit, and registering it
        // here would start a ledger nobody maintains anywhere else in the system.
        if (part is null || !part.IsSerialTracked) return null;

        var party = await PartyLabelAsync(db, job, ct);
        var result = await serial.ReceiveForServiceAsync(part.Id, job.SerialNo, job.Description ?? part.Name,
            job.CustomerId, party, uid, $"Booked in for service on {job.ServiceNo}", ct, job.Id);
        if (result is not { } r) return null;

        job.SourceComponentSerialId = r.Unit.Id;
        if (!r.WasKnown) return null;

        // The unit has been here before. Whether that matters depends on whether it came back to the
        // same party — a repeat visit is ordinary, a change of hands is the thing worth a second look,
        // and the counter is the only place anybody is holding the unit while there is still time to
        // check the serial was read correctly.
        var visits = await db.Services.AsNoTracking()
            .CountAsync(s => s.SerialNo == job.SerialNo.Trim() && s.Id != job.Id && !s.IsDeleted, ct);
        if (r.PreviousCustomerId is { } prev && prev != job.CustomerId)
        {
            var prevName = await db.Customers.AsNoTracking().Where(c => c.Id == prev)
                .Select(c => c.Name).FirstOrDefaultAsync(ct) ?? $"customer #{prev}";
            var note = $"Unit {job.SerialNo.Trim()} was last held by {prevName} — now booked in for {party}.";
            WriteNote(db, job, "UnitOwnerChanged", uid, note);
            return visits > 0
                ? $"{note} It has been here {visits} time(s) before."
                : note;
        }

        return visits > 0
            ? $"Unit {job.SerialNo.Trim()} has been here {visits} time(s) before."
            : null;
    }

    /// <summary>What the shop already knows about a serial, asked BEFORE the job is booked. The counter
    /// is holding the unit at that moment, which is the only point where a misread serial or a unit
    /// that has changed hands can still be sorted out cheaply.</summary>
    private static async Task<Ok<UnitHistoryDto>> UnitHistoryAsync(
        AppDbContext db, string? serial, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(serial))
            return TypedResults.Ok(new UnitHistoryDto(null, null, null, 0, false, new List<UnitVisitDto>()));

        var sn = serial.Trim();

        var visits = await (from s in db.Services.AsNoTracking()
                            where s.SerialNo == sn && !s.IsDeleted
                            join c in db.Customers on s.CustomerId equals c.Id into cg
                            from c in cg.DefaultIfEmpty()
                            join d in db.Dealers on s.DealerId equals d.Id into dg
                            from d in dg.DefaultIfEmpty()
                            orderby s.DateReceived descending, s.Id descending
                            select new UnitVisitDto(s.Id, s.ServiceNo, s.DateReceived,
                                s.ServiceStatus.ToString(),
                                c != null ? c.Name : (d != null ? d.Name : null),
                                s.ReportedProblem))
            .Take(20).ToListAsync(ct);

        // The ledger row, when the item is one the ledger holds. Its owner is the authoritative
        // "who has it now"; the visit list above is only where it has been seen.
        var unit = await (from u in db.ComponentSerials.AsNoTracking()
                          where u.SerialNumber == sn
                          join c in db.Customers on u.CustomerId equals (long?)c.Id into cg
                          from c in cg.DefaultIfEmpty()
                          orderby u.LastUpdatedAt descending
                          select new { u.Status, u.OwnerType, u.OwnerRef, CustomerName = c != null ? c.Name : null })
            .FirstOrDefaultAsync(ct);

        // A replacement that went out under this serial. Answers "was this one of ours?" in the same
        // breath as "have we seen it before", because at the counter they are one question.
        var wasReplacement = await db.ServiceReplacements.AsNoTracking()
            .AnyAsync(r => r.OutgoingSerialNo == sn && r.CancelledAt == null, ct);

        return TypedResults.Ok(new UnitHistoryDto(
            unit?.Status.ToString(),
            unit?.OwnerType.ToString(),
            unit?.CustomerName ?? unit?.OwnerRef,
            visits.Count,
            wasReplacement,
            visits));
    }
}

/// <summary>What the counter is shown when a serial is keyed in at inward.</summary>
public record UnitHistoryDto(
    string? CurrentStatus, string? CurrentOwnerType, string? CurrentOwner,
    int VisitCount, bool WasIssuedAsReplacement, List<UnitVisitDto> Visits);

public record UnitVisitDto(
    long ServiceJobId, string ServiceNo, DateTime DateReceived, string ServiceStatus,
    string? PartyName, string? ReportedProblem);
