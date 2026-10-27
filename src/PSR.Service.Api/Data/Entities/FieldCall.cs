namespace PSR.Service.Api.Data.Entities;

/// <summary>What kind of call an engineer was sent out on.
///
/// Deliberately one flat list rather than a split between selling and servicing: the register this
/// backs is "where did our engineers go and what came of it", and a day that held two demos and a
/// dealer visit is answered by one query over one column. A sales call that turns into a service
/// call is a second record, not an edited first one.</summary>
public enum FieldCallType { SalesCall, ServiceCall, Installation, Demo, DealerVisit }

/// <summary>Whether the call was inside the engineer's own station or a trip out of it. Two values
/// and no third, because this is the distinction that decides whether a journey is claimable, and a
/// "maybe" would be a figure nobody could settle.</summary>
public enum FieldCallLocality { Local, Outstation }

/// <summary>Whether the engineer finished what they went for.
///
/// Advanced in exactly one direction and from exactly one place: a field service filed against the
/// call carries the engineer's own answer to "did I finish", and saving it may move the call from
/// Incomplete to Complete. Never the other way. A call already marked Complete is left alone by a
/// later visit, because one engineer's "not finished yet" must not erase the finished work somebody
/// else already recorded — and because nothing in this register edits or reopens anything, there is
/// no step that would put it back.
///
/// Incomplete is therefore also what makes a call still pickable on the service entry form: the
/// phone asks for the caller's Incomplete calls, and completing one is what takes it off that
/// list.</summary>
public enum FieldCallStatus { Complete, Incomplete }

/// <summary>One call an engineer attended, as a register entry.
///
/// This is a log and nothing more. Unlike <see cref="FieldService"/> and <see cref="FieldSale"/>,
/// which sit next to it in the field portal and look superficially similar, creating one of these
/// moves no stock, transitions no serial, prices nothing and bills nobody — there are no lines, no
/// totals and no ledger write. That is the whole design: the office wants to know which calls were
/// taken and how they went, and making that answer depend on the stock workflow would mean a demo
/// with no parts fitted could not be recorded at all.
///
/// So where a field service names a machine serial and consumes the part fitted to it, this names a
/// machine serial and consumes nothing. The two are kept apart rather than folded together because
/// a record that sometimes moves stock and sometimes does not is the kind of table that quietly
/// stops reconciling against the ledger.</summary>
public class FieldCall
{
    public long Id { get; set; }

    /// <summary>Our own handle for the record (CALnnnnn), generated on save. Distinct from
    /// <see cref="CallReferenceNo"/>, which is whatever reference the call arrived with — the two are
    /// routinely both present and mean different things, so neither can stand in for the other.</summary>
    public string CallNo { get; set; } = string.Empty;

    public FieldCallType CallType { get; set; }

    /// <summary>The engineer whose call this is, always the account that saved it.
    ///
    /// There is no separate created-by column, which is a departure from the sibling field records.
    /// The form fills this from the signed-in user and does not let it be edited, so a second column
    /// would differ from this one on exactly no row ever written. Who keyed it in is already a fact
    /// the audit log holds. If calls are ever entered at a desk on an engineer's behalf, that is the
    /// change that earns the second column, and it should be added then rather than carried empty
    /// until it happens.</summary>
    public long EngineerId { get; set; }

    /// <summary>The day the call came in, on the shop's wall clock — a date someone wrote down, not
    /// a moment, so it goes through <see cref="Common.ShopClock.BusinessDate"/> on the way in and
    /// carries no time of day. Defaults to today when the client leaves it out.</summary>
    public DateTime CallReceivedDate { get; set; }

    public FieldCallLocality Locality { get; set; } = FieldCallLocality.Local;

    public string CustomerName { get; set; } = string.Empty;

    /// <summary>Set when the engineer picked an existing customer rather than typing a name. The free
    /// text above stays authoritative for display; this is the link that makes the register joinable
    /// to the customer master. Same arrangement as <see cref="FieldService.CustomerId"/>, and for the
    /// same reason: an engineer on site meets people who are not on the list yet, and a call that
    /// cannot be logged because the customer is unknown is worse than one logged against a name.</summary>
    public long? CustomerId { get; set; }

    /// <summary>Where the call was, as text, even when <see cref="CustomerId"/> is set.
    ///
    /// Not read off the customer master on display: a customer with three sites is one row with one
    /// address, and the address that matters on a call is the one the engineer actually drove to. The
    /// master seeds this field and then stops owning it.</summary>
    public string? CustomerAddress { get; set; }

    /// <summary>The reference the call arrived with — a dealer's docket, a phone log entry, whatever
    /// the caller quoted. Free text on purpose: it comes from outside and has no shape we control.</summary>
    public string? CallReferenceNo { get; set; }

    /// <summary>The number the call came in on, so the engineer can ring back before setting out.
    ///
    /// Not read off the customer master for the same reason <see cref="CustomerAddress"/> is not: the
    /// person who rang is whoever was standing next to the machine, and their mobile is not the
    /// account's switchboard. Seeded from the master when a customer is picked, then owned here.</summary>
    public string? ContactPhone { get; set; }

    /// <summary>What the caller said was wrong, in their words, taken when the call was logged.
    ///
    /// The same thing <see cref="FieldService.Complaint"/> holds, and that is the point: a service
    /// entry filed against this call starts with this text already in the complaint box instead of
    /// asking the engineer to retype what the office wrote down an hour ago. It stays the call's own
    /// copy — editing the visit's complaint does not rewrite the register, because what the caller
    /// reported and what the engineer eventually wrote up are two statements, not one.</summary>
    public string? ReportedComplaint { get; set; }

    public string? MachineModel { get; set; }

    /// <summary>The machine or part number the call was about. Free text, like the equivalent on a
    /// field service: the unit in front of the engineer may not be in any of our tables, and refusing
    /// the record over an unrecognised serial would lose the call rather than find the machine.</summary>
    public string? MachineSerial { get; set; }

    /// <summary>Firmware or application version found on the unit. Kept as text off the machine
    /// rather than resolved against anything — nothing in this system is a version master, and the
    /// version on a machine in the field is whatever was last flashed onto it.</summary>
    public string? SoftwareVersion { get; set; }

    /// <summary>The day the engineer attended, or null when they have not yet. Nullable because a
    /// call is recorded when it is received as readily as after it is dealt with, and a date invented
    /// to fill the column would be indistinguishable from one that was true.</summary>
    public DateTime? AttendedDate { get; set; }

    public FieldCallStatus CallStatus { get; set; } = FieldCallStatus.Incomplete;

    public string? Remarks { get; set; }

    /// <summary>When the row was written. A real instant and so UTC, unlike the two dates above.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the call was last changed, or null while it still says what it said when it was
    /// logged. A real instant and so UTC, like <see cref="CreatedAt"/>.
    ///
    /// Not a substitute for <see cref="AttendedDate"/>, and both are needed. The attended date is a
    /// day in the field - the day somebody stood at the machine - and is the day the office reports
    /// on. This is a moment on a clock: when the record was last touched. A call attended on the
    /// Monday and written up on the Wednesday carries both, and they say different true things.
    ///
    /// Nullable rather than defaulted to the created stamp, because "nobody has touched this since it
    /// was logged" is a fact worth being able to see. Every row that already exists is null, which is
    /// right rather than a backfill left undone: none of them has been edited, because until the PUT
    /// existed there was nothing that could edit one.
    ///
    /// WHAT changed is the audit log's business and not this column's - audit rows carry the
    /// field-by-field diff. This is here so the phone can say "updated yesterday" without reading an
    /// audit trail no engineer has access to.</summary>
    public DateTime? UpdatedAt { get; set; }
}
