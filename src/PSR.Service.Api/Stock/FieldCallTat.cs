namespace PSR.Service.Api.Stock;

/// <summary>One closed call, reduced to what a turnaround figure needs.
///
/// A named record rather than an anonymous projection because the query that builds it is handed to
/// a test, and a method cannot return an anonymous type. Four columns and no more: the register's
/// rows are small, but the turnaround read is the one query here that is allowed to pull thousands of
/// them, and pulling whole entities to subtract two dates would be a page of wasted bytes per row.
///
/// <paramref name="CompletedAt"/> is non-nullable even though the column is, because the query that
/// fills this has already excluded the open calls — a null here would mean the filter was dropped.</summary>
internal record FieldCallClosedRow(long EngineerId, string CallNo, DateTime CreatedAt, DateTime CompletedAt);

/// <summary>One still-open call, reduced to what the alert counts need: whose it is and how old.
/// Two columns, for the same reason as above.</summary>
internal record FieldCallOpenRow(long EngineerId, DateTime CreatedAt);

/// <summary>The turnaround arithmetic, apart from the endpoint that serves it.
///
/// Separated for the reason <see cref="FieldCallRules"/> is: the figures the office will quote in a
/// meeting should be a function a test can pin, rather than something that emerges from the middle of
/// a handler. Everything here is pure — no clock, no database — and the one thing that reads the
/// clock (the critical cutoff) takes it as an argument.</summary>
internal static class FieldCallTat
{
    /// <summary>What the alert threshold falls back to when the client sends nothing, or nonsense.
    ///
    /// A week, because that is the span the office already thinks in: a call taken on the Monday and
    /// still open the following Monday has been through a full cycle of everybody's working week
    /// without being finished, and there is no day in between that anybody could defend as the line.</summary>
    public const int DefaultCriticalDays = 7;

    /// <summary>The threshold to actually apply.
    ///
    /// Clamped rather than refused, because this arrives off a dropdown and a value outside the range
    /// can only be a stale client or a hand-made URL — neither is worth a 400 on a read. Zero would
    /// make every open call critical the moment it was logged, which is a label that means nothing;
    /// a year is past the point where "critical" and "open" are different questions.</summary>
    public static int CriticalDays(int? requested) =>
        requested is { } d && d >= 1 && d <= 365 ? d : DefaultCriticalDays;

    /// <summary>The instant a call must have been logged before to count as critical.
    ///
    /// UTC against UTC: created_at is an instant, not a shop day, so this is elapsed time and not a
    /// calendar comparison. "Seven days open" means seven twenty-four-hour days since it was written
    /// down, which is the same answer at nine in the morning and at seven in the evening — where a
    /// count of calendar days would tick over at midnight and make a call logged last night a day old
    /// by breakfast.</summary>
    public static DateTime Cutoff(DateTime nowUtc, int criticalDays) => nowUtc.AddDays(-criticalDays);

    /// <summary>How long one call took, in hours to a tenth, or null when the pair cannot be read as
    /// a duration.
    ///
    /// Null rather than a negative figure for a completion that predates the logging. It should not
    /// happen — all three closing paths stamp the completion from the clock, after the row exists —
    /// but a backfilled row carries whatever the old data said, and one bad pair must not be able to
    /// drag an average below zero where nobody could tell whether the report or the data was wrong.</summary>
    public static double? Hours(DateTime registered, DateTime completed) =>
        completed >= registered ? Math.Round((completed - registered).TotalHours, 1) : null;

    /// <summary>Nothing measurable: zeros, and no call numbers to point at. Not null — the section
    /// still has tiles to draw, and a filter that matched nothing has a true answer.</summary>
    public static readonly FieldCallTatStat Empty = new(0, 0, 0, 0, null, null);

    /// <summary>Average, fastest and slowest over a set of closed calls, with the call number at each
    /// extreme.
    ///
    /// The extremes are taken from the same measured set as the average, so the three figures always
    /// describe the same calls. Rows that will not read as a duration are dropped by
    /// <see cref="Hours"/> and are not counted — so the count here is "calls this average is over",
    /// which is the only count that makes the average checkable.</summary>
    public static FieldCallTatStat Stat(IEnumerable<FieldCallClosedRow> rows)
    {
        var measured = rows
            .Select(r => (r.CallNo, Hours: Hours(r.CreatedAt, r.CompletedAt)))
            .Where(x => x.Hours is not null)
            .Select(x => (x.CallNo, Hours: x.Hours!.Value))
            .ToList();
        if (measured.Count == 0) return Empty;

        var fastest = measured.MinBy(x => x.Hours);
        var slowest = measured.MaxBy(x => x.Hours);
        return new FieldCallTatStat(
            measured.Count,
            Math.Round(measured.Average(x => x.Hours), 1),
            fastest.Hours, slowest.Hours,
            fastest.CallNo, slowest.CallNo);
    }

    /// <summary>How many whole days a call has been open, for the alert list.
    ///
    /// Floored, so a call at six days and twenty hours reads as six rather than seven: the reader is
    /// comparing this against the threshold they just picked, and a figure rounded up would show a
    /// six-day call sitting under a seven-day alert. Elapsed, like <see cref="Cutoff"/>, and for the
    /// same reason.</summary>
    public static int DaysOpen(DateTime registered, DateTime nowUtc) =>
        (int)Math.Floor((nowUtc - registered).TotalDays);
}
