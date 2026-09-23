namespace PSR.Service.Api.Data.Entities;

/// <summary>One replacement event: a unit went out to the party on a job, and — unless it was written
/// off — a unit came in and stayed. Written for both kinds, so one lookup answers the question the
/// shop actually asks, which is <em>"this unit has come back; was it one of ours, and against what?"</em>
///
/// Deliberately a row of its own rather than something derived from the job or the serial ledger:
///
/// <list type="bullet">
/// <item>it is written whether or not the part is serial-tracked, so a non-tracked replacement is
/// still traceable to its job and its customer — <see cref="ComponentSerial"/> could never do that,
/// because no unit record exists for an untracked part;</item>
/// <item>it holds BOTH ends of the exchange, which neither job row does;</item>
/// <item>it remembers what the original job's status was before the swap, which is the only way a
/// cancellation can put it back where it came from.</item>
/// </list>
///
/// The legacy app answered the same question by string-matching "REPLACEMENT SN:" inside the
/// COMPONENTS blob (service_tracker, <c>api_service.dart</c> <c>searchReplacementSerial</c>). The
/// migration backfills a row for every job already carrying a ReplacementSerialNo, so the lookup
/// covers replacements issued long before any of this existed.
/// </summary>
public class ServiceReplacement
{
    public long Id { get; set; }

    /// <summary>The customer's job: the one the replacement was issued against, and the one that gets
    /// dispatched.</summary>
    public long OriginalServiceJobId { get; set; }

    /// <summary>The job opened on the unit that was kept. Null for a total loss (nothing was kept) and
    /// for backfilled history. Cleared, with the job soft-deleted, if the swap is cancelled.</summary>
    public long? RetainedServiceJobId { get; set; }

    public ReplacementKind Kind { get; set; } = ReplacementKind.AdvanceSwap;

    // --- what went OUT (the unit the customer received) ---
    public long? OutgoingPartId { get; set; }
    public string OutgoingSerialNo { get; set; } = string.Empty;
    public long? OutgoingComponentSerialId { get; set; }

    // --- what came IN and stayed (null for a total loss) ---
    public long? IncomingPartId { get; set; }
    public string? IncomingSerialNo { get; set; }
    public long? IncomingComponentSerialId { get; set; }

    /// <summary>True when the swap is what brought the incoming unit into serial tracking — the shop
    /// had never seen it before. It decides what cancelling does: a record the swap created is
    /// removed, because the shop never owned that unit and leaving a service-centre-owned row behind
    /// would put a customer's machine on the shelf. A record that already existed is left alone.</summary>
    public bool IncomingSerialCreated { get; set; }

    /// <summary>The original job's status immediately before the swap, so cancelling can restore it.
    /// Stored as the enum's own token. Null on rows that were never swaps (total loss, backfill).</summary>
    public string? StatusBeforeSwap { get; set; }

    /// <summary>Whose unit it was. Snapshotted because the job's party can be corrected later and the
    /// question "who did this go to" is about the day it went out.</summary>
    public long? CustomerId { get; set; }
    public long? DealerId { get; set; }

    public string? Reason { get; set; }
    public long ApprovedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Set when the swap was undone. The row is never deleted: a replacement that went out
    /// and came back is a thing that happened, and the shelf count moved twice because of it.</summary>
    public DateTime? CancelledAt { get; set; }
    public long? CancelledByUserId { get; set; }
}
