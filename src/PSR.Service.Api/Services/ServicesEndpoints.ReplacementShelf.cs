using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Stock;

namespace PSR.Service.Api.Services;

/// <summary>The units a replacement can be filled from, for the form that issues one.
///
/// A replacement comes out of one of two pools and the person at the job cannot see either of them.
/// The warehouse is a quantity, so there is nothing to list — a serial of warehouse stock is named as
/// it goes out and the shop has never seen it before. The SHELF is the opposite: it is a register of
/// named units in hand, every one of which is a unit somebody could pick up right now, and until this
/// existed the only way to find out which was to go to the shelf page, which a technician cannot
/// reach (<c>/serials</c> is behind StockManage).
///
/// So this is the shelf, read through the job. Job-scoped for two reasons and not just for tidiness:
/// it is what lets the assigned technician read it without being given a blanket right to the serial
/// register, and it is what lets each unit say whether it is the same item as the one being replaced —
/// which is the answer in nearly every case, and the one the form should offer first.
///
/// Every shelf unit is listed, not only the matching ones. A total loss is the one route where the
/// replacement may legitimately be a DIFFERENT item: the incoming unit is written off rather than
/// kept, so there is no "same item at both ends" rule to hold to the way a swap has one.
/// </summary>
public static partial class ServicesEndpoints
{
    /// <summary>A dropdown, not a register: no paging, and a stop well past any real shelf. The WPF
    /// shelf page reasons the same way about the same set — one service centre's units in hand are a
    /// few hundred rows at most.</summary>
    private const int ShelfOptionCap = 500;

    private static async Task<Results<Ok<ReplacementShelfDto>, NotFound, ForbidHttpResult>> ReplacementShelfAsync(
        long id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        var jobRow = await db.Services.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (jobRow is null) return TypedResults.NotFound();

        // Who may READ this is who may work or oversee the job — the assigned technician or a
        // supervisory role — which is the same test the detail pane itself uses. Deliberately wider
        // than who may ISSUE a replacement: seeing what is on the shelf is not taking anything off it,
        // and narrowing it to the two issuing routes would hide the list from the dispatch desk until
        // the job happened to be sitting in the one status they can act from.
        if (!ServiceRoles.CanProcess(user, jobRow)) return TypedResults.Forbid();

        // The item the job itself is, resolved the same way ReplaceAsync resolves it — by PS code,
        // with no IsActive filter, because a retired code with units still on the rack is exactly the
        // case that has to work. Null is not an error here: it is the warning the form needs, since a
        // job whose code resolves to nothing can only be replaced by picking a part by hand.
        var code = string.IsNullOrWhiteSpace(jobRow.PsCode) ? null : jobRow.PsCode.Trim();
        var jobPart = code is null
            ? null
            : await db.Parts.AsNoTracking()
                .Where(p => p.ItemCode == code)
                .Select(p => new { p.Id, p.ItemCode, p.Name })
                .FirstOrDefaultAsync(ct);

        // Hoisted to a plain long? rather than read off the anonymous type inside the query: a
        // property access on a captured object is the shape this codebase keeps hitting the EF Core /
        // .NET 10 funcletizer on, and the comparison wants a parameter either way.
        var jobPartId = jobPart?.Id;

        // The shelf, defined exactly as the server defines it everywhere else: SHELFED and in the
        // service centre's custody. Asking in those terms rather than keeping a list is what stops
        // this offering a unit the issue would then refuse.
        var units = await (from c in db.ComponentSerials.AsNoTracking()
                           join p in db.Parts on c.PartId equals p.Id
                           where c.Status == SerialStatus.Shelfed
                                 && c.OwnerType == SerialOwnerType.ServiceCenter
                           select new ShelfReplacementUnitDto(
                               c.Id, c.PartId, p.ItemCode, p.Name, c.SerialNumber,
                               c.LastUpdatedAt ?? c.CreatedAt,
                               c.PartId == jobPartId))
            .ToListAsync(ct);

        // Same item first, then by code, then by serial. The sort is the whole of the help this gives
        // the person picking: the unit they almost certainly want is the first row.
        var ordered = units
            .OrderByDescending(u => u.MatchesJob)
            .ThenBy(u => u.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(u => u.SerialNumber, StringComparer.OrdinalIgnoreCase)
            .Take(ShelfOptionCap)
            .ToList();

        var warning = jobPart is null
            ? code is null
                ? "This job carries no PS code, so a replacement can only be issued by picking the part by hand."
                : $"No catalogue item matches PS code {code}, so a replacement off warehouse stock needs the part picked by hand."
            : null;

        return TypedResults.Ok(new ReplacementShelfDto(
            jobPart?.Id, jobPart?.ItemCode, jobPart?.Name, warning, ordered));
    }
}
