# Advance replacement, and what "Keep in stock" means

How a customer gets a working unit on day one without waiting for their own to be repaired, what
happens to the unit they hand over, and why stocking a finished job now moves the warehouse count.
Read this before changing `ApplyStockAsync`, `ReplaceAsync` or anything that writes
`service_replacements`.

Designed 2026-09-23. Decisions in here are the shop's, not the code's — where a rule looks arbitrary
it is because somebody chose it, and the choice is recorded.

## The problem

An item books in, gets assigned, and then sits. The customer is down for a fortnight while their own
unit waits its turn on the bench. The shop wants to hand them a working unit off the shelf on day
one, keep the faulty one, and repair it at its own pace — after which the repaired unit is the
shop's, not the customer's, and goes back on the shelf to be issued to a field technician.

Until now the only way to hand out a replacement was to write the unit off as a total loss:
`InService` → complete with `IsTotalLoss` → `ReplacementApprovalPending` → `ReplaceAsync`. That is
the wrong shape for this. The unit is not a loss — it is repairable, and the shop wants it.

## The move: a swap splits the job in two

A swap has two physical outcomes, and one job row can only end in one place:

- the replacement unit goes **out** to the customer, so its job must end `Dispatched`
- the customer's unit **stays**, gets serviced, and must end `Stocked`

So the swap splits the job.

```
SVC-1   customer's job, sitting twelve days in In-Service
  │
  │  ── swap ──▶  warehouse −1  ·  unit B serial captured
  │                unit B: owner CUSTOMER, status USED
  ▼
SVC-1 → Completed ──▶ PI / Invoice / DC ──▶ Dispatched        customer served, day one
  │
  └── opens ──▶ SVC-2   internal job, carries unit A
                  │   unit A serial created: owner SERVICE_CENTER, status UNDER_REPAIR
                  │
                  ▼
               assign → acknowledge → in service → complete
                  │
                  ├── Add to stock ──▶ warehouse +1, unit A → REPAIRED (re-issuable)
                  └── total loss   ──▶ unit A → SCRAPPED (terminal)
```

Unit **A** is the customer's; unit **B** is the one off the shelf. The names are used throughout.

## No new statuses

This is the part worth protecting. Every state the swap needs already exists and already means the
right thing:

| Moment | Job status | Unit A | Unit B |
|---|---|---|---|
| Before the swap | Inward … InService | — | warehouse stock |
| Swap issued | SVC-1 → `Completed` | `UnderRepair`, owner ServiceCenter | `Used`, owner Customer |
| SVC-1 dispatched | `Dispatched` | unchanged | unchanged |
| SVC-2 worked | `Assigned` … `Completed` | unchanged | — |
| SVC-2 stocked | `Stocked` | `Repaired`, owner ServiceCenter | — |
| SVC-2 written off | `TotalLoss` | `Scrapped` | — |

`UnderRepair` already means *at the service centre, a job is open on it, deliberately not
re-issuable*. `Repaired` already means *back on the shelf, re-issuable*. `ApplyStockAsync` already
credits the warehouse and calls `MarkRepairedAsync` for a job carrying a source serial. The swap
does not invent a state machine; it routes units through the one the field-return loop already built
(`docs/serial-custody-and-field-returns.md`).

## "Keep in stock" now credits the warehouse

**This changes an existing button for every job, not only swapped ones.**

Stocking an ordinary job used to be a pure status change. The reasoning was that an ordinary job
holds a customer's machine rather than a catalogue part, so crediting the shelf would invent stock
that does not exist. That reasoning had one thing wrong with it: by the time anybody presses *Keep in
stock*, the shop has decided to keep the machine. It is not the customer's any more. It is a unit on
a rack with nothing in the system saying so.

The legacy app knew this. `service_tracker/lib/pages/pending_dispatch_page.dart`
`_addSelectedToStock` matched the job's PSCODE against the stock table, incremented `Total_stock` by
one, wrote a `stock_input` row with source `"Service Return"`, and only then marked
`DISPATCH = 'STOCKED'`. The rewrite kept the status change and dropped the three lines that made it
mean anything, so every unit kept since has been off the books.

So, as of this change:

- stocking any completed job credits the warehouse **+1**
- the unit gets a `component_serials` row — owner `ServiceCenter`, status `Repaired` — and stays the
  service centre's until it is issued or used somewhere, exactly like any other shelf stock
- a job raised off a field return keeps doing what it already did; that path is now the general path
  rather than a special case

Three consequences, none of them cosmetic:

**Stocking can now fail.** The unit has to resolve to a catalogue part by the job's PS code. A job whose PS code matches nothing cannot be
stocked and says so, naming the code. Before, the button always succeeded, because it was not
claiming anything.

**Bulk stock becomes partial.** `BulkActionResultDto` already carries per-job failures, so a page of
jobs where three have no catalogue match stocks the rest and names those three. No new shape needed.

**The guard test inverts.** `BulkWorkflowTests.Stocking_an_ordinary_job_records_no_stock_movement`
asserts the behaviour being removed. It is replaced by its opposite, plus a test that a job with an
unresolvable PS code is refused rather than silently stocked.

## Registering an inward item in the serial ledger

Added 2026-09-23, alongside the above.

A part booked in over the counter now joins `component_serials` — `UnderRepair`, in the shop's
custody, with the job stamped on it and the customer still on the record. That last part is the whole
point: a unit that comes back next year under a different name is only visible because the last name
is still there.

**Only parts, never whole machines.** Registration happens when the job's PS code matches a catalogue
part that is marked serial-tracked, and not otherwise. Machines sold by the OEM are already
registered in the factory records (`passtestdata`), which this system only reads — inventing ledger
rows for them would duplicate a register somebody else owns, and that database can be switched off
without the shop losing anything. A part has no such register, and a part is what actually circulates.

Nothing about inward gets stricter. The registration is skipped silently for anything the ledger
cannot hold, so no booking starts failing over a PS code.

What the counter sees, as the serial is keyed in and before the job is booked: how many times the
unit has been here, who held it last, and whether it ever went out as a replacement
(`GET /services/unit-history`). A change of holder is shown and also written to the job's history, but
it never blocks the booking — a genuine resale is ordinary, and refusing it would only teach the desk
to type a different serial.

### What this changes downstream

An ordinary job now carries `SourceComponentSerialId`, which until today meant "raised off a faulty
field return". Every serial-aware branch therefore starts firing on ordinary jobs, which is the
intent: dispatch hands the unit back to the party, stocking puts it on the shelf, a written-off job
scraps it.

One of those branches had to be corrected in the same change. **Dispatching a job that issued a
replacement must not hand back the unit on the job** — the customer is walking out with the
replacement, and that unit is still on the shop's rack. Booking it back to them would put a unit at a
customer who never received it. `DispatchReturnsUnitToParty` is the rule; the kept unit stays in the
shop's custody, not re-issuable, with its job link cleared.

What happens to that unit afterwards is deliberately left open. It is somebody's judgement whether it
is repaired onto the shelf or scrapped, made after looking at it, from the serials screen.

## Payment: Pending or Paid

`Partial` is no longer offered on service jobs. It carries no amount, so it recorded that money had
changed hands without recording how much, and nothing downstream could act on it. The enum and the
search filters keep it so jobs already sitting on it stay findable; it simply cannot be chosen.

## A swap asks for a serial and nothing else

Simplified 2026-09-24, after the first build reached the counter.

A swap is the SAME item going out that came in: the customer brought a thing and is handed another of
that thing. So the item is the job's own PS code, at both ends, and the only fact the job does not
already hold is which physical unit the customer walked out with.

The first build offered two part pickers — one for the unit going out, one for what the kept unit was
catalogued as — each defaulting to the job's PS code. Both asked the counter to re-answer a question
the job had already answered, and every wrong answer was a unit taken off the wrong shelf. They are
gone. So is the free-text note; the reason covers it and is now optional.

`SwapRequest` is `(ReplacementSerialNo, Reason)`.

One consequence: a job whose PS code is missing or matches no catalogue item **cannot be swapped at
all**, where before the counter could work around it by picking a part. That is the right way round.
The PS code is what the shelf is decremented by and what the retained job is stocked as, so a job
that cannot answer it has a data problem to fix on the job, not a choice to make at the counter.

**A different item going back is not a swap.** That is a total loss and a replacement, which keeps its
own route, its own picker and its own reason for existing: there the incoming unit is written off, so
what goes back legitimately may be something else.

## Ownership moves at the swap; quantity moves at the stocking

These are two separate events and must stay separate.

The moment the swap is issued, unit A is the shop's — owner flips to `ServiceCenter` and the unit
goes `UnderRepair`. Nothing is credited to the warehouse count, because a broken unit on the bench is
not stock anyone can issue. The quantity arrives when SVC-2 is stocked and the unit becomes
`Repaired`.

This is the same rule the field-return loop runs on, and for the same reason: *only what has arrived
AND is usable is counted*. Crediting at the swap would offer a unit still in pieces on the next
issue.

Dealer jobs and direct-customer jobs run identically. There is no special case: `PartyLabelAsync`
already resolves either, and the shop's answer was that the procedure is the same.

## The tracking row

`service_replacements`, one row per replacement, whichever kind:

| Column | Holds |
|---|---|
| `OriginalServiceJobId` | SVC-1 |
| `RetainedServiceJobId` | SVC-2, null when the unit was written off |
| `OutgoingPartId`, `OutgoingSerialNo`, `OutgoingComponentSerialId` | unit B, the one the customer got |
| `IncomingPartId`, `IncomingSerialNo`, `IncomingComponentSerialId` | unit A, the one we kept |
| `Kind` | `TotalLoss` or `AdvanceSwap` |
| `StatusBeforeSwap` | what SVC-1 was, so a cancellation can put it back |
| `CustomerId`, `Reason`, `ApprovedByUserId`, `CreatedAt` | who, why, when |

This is what answers *"this unit came back — was it one of ours?"*. A lookup by serial returns the
job, the customer, the date, and the unit that came in exchange, and both halves open from there.

The legacy app answered the same question by string-matching `"REPLACEMENT SN:"` inside the
`COMPONENTS` blob (`service_tracker/lib/services/api_service.dart`, `searchReplacementSerial`). The
row replaces the substring search.

Two things follow from it being a row rather than a serial-ledger entry. It is written whether or not
the part is serial-tracked, so a non-tracked replacement is still traceable to its job and customer —
something `component_serials` could never do, because no unit record exists. And the migration
backfills it from every job already carrying a `ReplacementSerialNo`, so one lookup covers the total
-loss replacements issued before any of this existed.

## Cancelling a swap

Allowed until SVC-1 is dispatched. After that the customer has the unit, and getting it back is a
stock return, not an undo.

Cancelling reverses all four effects: warehouse +1 and unit B back to re-issuable, SVC-1 back to
`StatusBeforeSwap` with its replacement fields cleared, any lines that moved to SVC-2 moved back, and
SVC-2 soft-deleted with unit A's serial record dropped (or, when the unit was already tracked,
reverted to the customer).

It is refused when any of that has already been built on:

- SVC-1 has a PI, invoice or DC generated — the same freeze `RevertAsync` already enforces
- SVC-2 is past `Assigned`, so work or parts are on it
- SVC-2 is already stocked or written off, so the unit has moved on

`RevertAsync` needs no change. It already refuses any job carrying a `ReplacementSerialNo`, which is
what stops a swapped job going back to `InService` and being completed a second time — and that is
precisely what makes the billing line below safe.

## Billing

An out-of-warranty swap pre-fills one editable `Replacement` line on SVC-1 for unit B, qty 1, at the
customer or dealer rate. An in-warranty job gets no line: there is nothing to charge for.

The line is billing-only and never consumes anyone's stock. `CompleteAsync` is what consumes a
job's part-bearing lines, and a swapped job never runs it — the swap moves SVC-1 straight to
`Completed`. Unit B left the warehouse through its own `Replacement` movement at the moment of the
swap. The revert guard above is what keeps it that way permanently.

Today's total-loss replacement adds no line at all, so the unit goes out unbilled. That is a gap this
closes rather than a behaviour it preserves.

Lines a technician had already added to SVC-1 describe work on unit A, which the customer is not
getting, so they move to SVC-2 — but only when SVC-1 had not yet been completed. Once it has, those
parts are already consumed from the technician's balance, and moving them would consume them twice
when SVC-2 completes.

## What must be refused

A retained unit is the shop's own stock sitting on a job. Two routes have to be closed or it leaks
back out as though it were still the customer's:

- **`ApplyDispatchAsync` refuses a `SwapRetained` job.** It can be stocked or written off, nothing
  else. If the unit genuinely has to go out, it goes out as a stock issue or a spare sale, through
  the door that records a sale.
- **`BillingService` refuses a `SwapRetained` job.** There is no customer to bill for work on our own
  unit.

`ServiceJob.JobKind` (`Customer` | `FieldReturn` | `SwapRetained`, defaulting to `Customer`) is what
both read. It also fixes something already wrong: field-return repair jobs currently count as
ordinary inward jobs in the lists and the turnaround figures, inflating both. The migration backfills
`FieldReturn` wherever `SourceComponentSerialId` is set.

## Client lockstep

The desktop gates the current replacement button on `ServiceStatus == "ReplacementApprovalPending"`
(`ServiceJobsViewModel.CanReplace`). The swap is allowed from `Inward`, `Assigned`, `Acknowledged`,
`InService` and `Completed`, so it needs its own flag rather than a widened one — the total-loss
button and the swap button mean different things and the shop should see both names.

Unlike every other action on that pane, the swap button is **not scoped to a section**. `ShowDispatchAction`,
`ShowStockAction` and the rest are all `Section("completed") && role`, because the legacy app was one
page per stage and each stage only ever offered its own actions. A swap does not belong to a stage: an
item that has been sitting turns up in Inward, In-service and the pending-dispatch queue alike, and the
decision can be taken at any of them. `CanSwap` therefore checks the role and the job's status and
nothing about which tab is open.

Authorisation is `DispatchManage`, the policy that already governs who sets a replacement serial. No
new policy, and therefore one fewer role list to drift. `SessionState` still has to mirror it: that
list has silently disagreed with the server's policies before.

The confirmation dialog states the three consequences plainly — warehouse −1, a new internal job
opens, the unit becomes ours — because none of them are reversible from the customer's side once the
job is dispatched.

## Known gaps

**Finding the jobs that need swapping.** The feature is manual by design, but nothing on the desk
says which items have been sitting. A days-open column and a "sitting more than N days" filter on the
Inward and In-service tabs is a `/services` query change, and is the difference between a button that
exists and a button that gets used.

**Historic stocked jobs.** Jobs stocked before this change credited nothing, and the units are on the
racks unrecorded. The migration deliberately does not invent stock for them: a backfill would guess
at quantities nobody counted. Closing that is a physical stock-take, not a migration.
