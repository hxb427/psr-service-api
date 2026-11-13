using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Common;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Stock;

/// <summary>Turnaround over the call register: how long a call takes to finish, whose calls take it,
/// and which ones have not been finished at all.
///
/// Apart from the register's own file for the reason the consumption report sits apart from the rest
/// of the reports. That file is five handlers that each write or read one call; this is one handler
/// that reads thousands and hands back no calls at all, but a set of figures. Nothing here writes.
///
/// WHY THIS IS AN ENDPOINT, when the field activity page derives its other summary (Today's log) in
/// the client: the register is read a page at a time, newest received first, so the oldest open calls
/// are the first rows to fall off the end of that page. Those are exactly the calls an alert exists
/// to find. A count derived from the loaded page would be right today and quietly short the day the
/// register outgrew one page — and a backlog figure that shrinks as the backlog grows is worse than
/// no figure at all.</summary>
public static partial class FieldCallsEndpoints
{
    /// <summary>How many calls the two aggregate reads pull before they stop.
    ///
    /// A guard, not a window: the projections behind it are two and four small columns, so twenty
    /// thousand rows is a few megabytes and several years of this register. It is here so that no
    /// query in this file is unbounded, and when it does bite the answer says so (see
    /// <see cref="FieldCallTatDto.WindowFull"/>) rather than reporting a fraction as a total.</summary>
    private const int TatWindow = 20_000;

    /// <summary>How many alert rows come back to be listed. The count beside them is exact and
    /// unaffected: this caps what is shown, not what is known, and the client can see the difference
    /// between the two numbers.</summary>
    private const int CriticalListLimit = 500;

    /// <summary>The turnaround section for the field activity page.
    ///
    /// Scoped exactly like <see cref="ListAsync"/>: a caller without the manage roles gets their own
    /// figures whatever they asked for, because one engineer's turnaround is not another's to read. A
    /// manager gets everyone's and can narrow to one name.
    ///
    /// Filtered on the received date like the register itself, and not on created_at — "last month's
    /// turnaround" means the calls that came in last month, however late somebody keyed them in. That
    /// is also what makes this section agree row for row with the Calls tab beside it, which is the
    /// only way the two can be read together.</summary>
    internal static async Task<Ok<FieldCallTatDto>> TatAsync(
        AppDbContext db, ClaimsPrincipal user,
        long? engineerId, DateTime? from, DateTime? to, int? criticalDays, CancellationToken ct)
    {
        user.TryGetUserId(out var uid);
        var seeAll = StockRoles.CanViewFieldActivity(user);
        var ownerOnly = seeAll ? engineerId : uid;

        var days = FieldCallTat.CriticalDays(criticalDays);
        // One reading of the clock for the whole answer. The cutoff that selects the alert rows and
        // the ages printed beside them have to agree, and two UtcNow calls three queries apart are
        // free to disagree by exactly the row sitting on the boundary — which would show a list of
        // seven-day calls with a six-day one in it.
        var now = DateTime.UtcNow;
        var cutoff = FieldCallTat.Cutoff(now, days);

        var closed = await ClosedQuery(db, ownerOnly, from, to, TatWindow).ToListAsync(ct);
        var open = await OpenQuery(db, ownerOnly, from, to, TatWindow).ToListAsync(ct);
        var critical = await CriticalQuery(db, ownerOnly, from, to, cutoff, CriticalListLimit).ToListAsync(ct);

        // Every name the answer will need, in one round trip rather than one per engineer.
        var names = await EngineerNamesAsync(
            db,
            closed.Select(c => c.EngineerId)
                .Concat(open.Select(o => o.EngineerId))
                .Concat(critical.Select(c => c.EngineerId)),
            ct);

        var closedBy = closed.GroupBy(c => c.EngineerId).ToDictionary(g => g.Key, g => g.ToList());
        var openBy = open.GroupBy(o => o.EngineerId).ToDictionary(g => g.Key, g => g.ToList());

        // The union of both sides, so an engineer who has closed nothing but is sitting on four open
        // calls still gets a row. Leaving them out would hide the only thing worth seeing about them.
        var engineers = closedBy.Keys.Union(openBy.Keys)
            .Select(id =>
            {
                List<FieldCallClosedRow> theirClosed = closedBy.TryGetValue(id, out var cs) ? cs : [];
                List<FieldCallOpenRow> theirOpen = openBy.TryGetValue(id, out var os) ? os : [];
                return new FieldCallTatEngineerRow(
                    id, names.GetValueOrDefault(id, $"#{id}"),
                    FieldCallTat.Stat(theirClosed),
                    theirOpen.Count,
                    theirOpen.Count(o => o.CreatedAt < cutoff));
            })
            .OrderBy(e => e.EngineerName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The whole call on each alert row rather than a summary of it: the office reading this list
        // is about to ring the customer, and the number to ring is on the record.
        var alerts = critical
            .Select(c => new FieldCallCriticalRow(
                FieldCallTat.DaysOpen(c.CreatedAt, now),
                ToDto(c, names.GetValueOrDefault(c.EngineerId))))
            .ToList();

        return TypedResults.Ok(new FieldCallTatDto(
            days,
            FieldCallTat.Stat(closed),
            open.Count,
            open.Count(o => o.CreatedAt < cutoff),
            engineers,
            alerts,
            closed.Count >= TatWindow || open.Count >= TatWindow));
    }

    // ---------------------------------------------------------------- the three reads

    /// <summary>The filters this section shares with the register, said once.
    ///
    /// Deliberately NOT <see cref="RegisterQuery"/>, which also takes the type and status filters and
    /// imposes the register's own calendar sort: this section needs neither, and three reads that each
    /// threw away two filters and a sort would be harder to follow than four lines said once. The
    /// received-date choice is the same one, for the reason given there.</summary>
    private static IQueryable<FieldCall> TatSelection(
        AppDbContext db, long? engineerId, DateTime? from, DateTime? to)
    {
        var q = db.FieldCalls.AsNoTracking().AsQueryable();
        if (engineerId is { } eid) q = q.Where(c => c.EngineerId == eid);
        if (ShopClock.BusinessDate(from) is { } f) q = q.Where(c => c.CallReceivedDate >= f);
        if (ShopClock.BusinessDate(to) is { } t) q = q.Where(c => c.CallReceivedDate <= t);
        return q;
    }

    /// <summary>The calls a turnaround can be measured on: the ones that have closed.
    ///
    /// Selected on completed_at rather than on the status, and the two are not quite the same set.
    /// Every call closed since the stamp existed carries both, and so does a row closed before it and
    /// backfilled. What the status would ALSO let in is a Complete call whose stamp is null for some
    /// reason nobody has thought of yet — and a call with no completion moment has no turnaround, so
    /// it belongs out of the average rather than in it at zero.
    ///
    /// Internal, like <see cref="RegisterQuery"/>, so a test can make the provider translate it
    /// without a database: the null filter, the <c>.Value</c> on a nullable column and the projection
    /// into a record constructor are each things that only fail against real MySQL.</summary>
    internal static IQueryable<FieldCallClosedRow> ClosedQuery(
        AppDbContext db, long? engineerId, DateTime? from, DateTime? to, int take) =>
        TatSelection(db, engineerId, from, to)
            .Where(c => c.CompletedAt != null)
            // Newest first, so a window that does bite keeps the recent calls rather than the
            // founding ones: a turnaround figure is about how the team is working now.
            .OrderByDescending(c => c.Id)
            .Select(c => new FieldCallClosedRow(c.EngineerId, c.CallNo, c.CreatedAt, c.CompletedAt!.Value))
            .Take(take);

    /// <summary>Every call still open, reduced to whose it is and how old. Both alert counts are taken
    /// off this one read rather than asked for separately, so the total and the critical share of it
    /// cannot disagree with each other.</summary>
    internal static IQueryable<FieldCallOpenRow> OpenQuery(
        AppDbContext db, long? engineerId, DateTime? from, DateTime? to, int take) =>
        TatSelection(db, engineerId, from, to)
            .Where(c => c.CallStatus == FieldCallStatus.Incomplete)
            // Oldest first here, the opposite of the closed read: if this window bites, the rows worth
            // keeping are the ones that have been waiting longest.
            .OrderBy(c => c.CreatedAt)
            .Select(c => new FieldCallOpenRow(c.EngineerId, c.CreatedAt))
            .Take(take);

    /// <summary>The alert list: open calls logged before the cutoff, longest-waiting first.
    ///
    /// Whole entities, unlike the two reads above, because these rows are shown as records rather than
    /// counted. Which is also why this one is capped far lower — a list nobody can reach the end of is
    /// not a list.</summary>
    internal static IQueryable<FieldCall> CriticalQuery(
        AppDbContext db, long? engineerId, DateTime? from, DateTime? to, DateTime cutoff, int take) =>
        TatSelection(db, engineerId, from, to)
            .Where(c => c.CallStatus == FieldCallStatus.Incomplete && c.CreatedAt < cutoff)
            .OrderBy(c => c.CreatedAt)
            .Take(take);
}
