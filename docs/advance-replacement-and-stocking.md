# Advance replacement, and what "Keep in stock" means

How a customer gets a working unit on day one without waiting for their own to be repaired, what
happens to the unit they hand over, and where a kept unit actually goes. Read this before changing
`ApplyStockAsync`, `ReplaceAsync` or anything that writes `service_replacements`.

**Superseded in part, 2026-10-17.** The "Keep in stock credits the warehouse" section below records a
decision that has since been reversed: a kept unit goes on the **shelf**, which is now a pool of its
own, and the warehouse count of its PS code does not move. The section is kept because the reasoning
that led to it is still half right - a kept machine must be on the books - and because the half that
was wrong is worth not repeating. See [the shelf](#the-shelf-2026-10-17) at the end.

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
                  ├── Add to stock ──▶ shelf +1 unit, unit A → SHELFED (issuable)
                  └── total loss   ──▶ unit A → SCRAPPED (terminal)
```

Unit **A** is the customer's; unit **B** is the one off the shelf. The names are used throughout.

## No new statuses

This is the part worth protecting. Every state the swap needs already exists and already means the
right thing:

| Moment | Job status | Unit A | Unit B |
|---|---|---|---|
| Before the swap | Inward … InService | — | warehouse stock, or a shelf unit |
| Swap issued | SVC-1 → `Completed` | `UnderRepair`, owner ServiceCenter | `Used`, owner Customer |
| SVC-1 dispatched | `Dispatched` | unchanged | unchanged |
| SVC-2 worked | `Assigned` … `Completed` | unchanged | — |
| SVC-2 stocked | `Stocked` | `Shelfed`, owner ServiceCenter | — |
| SVC-2 written off | `TotalLoss` | `Scrapped` | — |

`UnderRepair` already means *at the service centre, a job is open on it, deliberately not
re-issuable*. The swap does not invent a state machine; it routes units through the one the
field-return loop already built (`docs/serial-custody-and-field-returns.md`).

One status WAS added later, and the reason is in [the shelf](#the-shelf-2026-10-17): `Repaired` read
as *a bench has finished with this*, which is not the same claim as *somebody has put it away*, and
the shelf needed the second one. `Shelfed` is it.

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

## The shelf (2026-10-17)

Stocking a finished job no longer credits the warehouse. The unit goes on the **shelf**, which is a
second pool, and the warehouse count of the job's PS code does not move.

### Why the earlier decision was wrong

Not the half it got right: a machine the shop has decided to keep must be on the books, and before
the September change it was on nothing at all. The half it got wrong is *which* book.

Crediting `stock_balances` made a serviced machine indistinguishable from one off a supplier's
invoice. One number, against one PS code, holding both — and that number is what gets reordered
against, sold across the counter on a spare sale, quoted to a customer as new, and billed at the
price-list rate. The shop keeps serviced units for a narrower purpose than that: a replacement for
the next customer with the same fault, or stock for a field technician. Those two uses are the whole
reason a unit is kept, and neither of them is "sell it as new".

### Two pools

| | Warehouse | Shelf |
|---|---|---|
| What it holds | stock bought in | machines kept off finished jobs; unused stock booked back from a technician |
| Counted as | a quantity, in `stock_balances` | units, as the `component_serials` rows that are `Shelfed` and owned by the service centre |
| Named unit by unit | only as it goes out | always — a unit cannot be on it otherwise |
| Can be issued to a technician | yes | yes |
| Can be handed over as a replacement | yes | yes |
| Can be sold as goods | yes | **no** |

The shelf has no row in `stock_balances`, and that is deliberate rather than unfinished. A second
quantity for the same units is a second number to keep in step with the first, which is the drift
`docs/serial-lifecycle-gaps.md` is a list of. The units *are* the count.

### What this changes

- **`ApplyStockAsync`** writes no `Receipt`. It shelves the unit (`Shelfed`, owner `ServiceCenter`)
  and writes one `ShelfIn` ledger row, which moves no balance and exists so the shelf has a readable
  history rather than only a status trail.
- **Every stocked job must name its unit**, not just a serial-tracked one. The shelf holds units and
  is picked from by serial; a job with a blank serial would shelve nothing, anywhere. This is a
  tightening — it used to be allowed for an untracked part, where the warehouse quantity was the
  whole of what moved and there is now no quantity to fall back on.
- **An issue can be filled from either pool, or both.** Which one is not a choice on a form: a serial
  that is `Shelfed` is a shelf unit, a serial the shop has never seen is warehouse stock being named
  as it goes out (`SerialService.ClaimIssueSourcesAsync`). One movement either way, carrying
  `shelf_qty` — it is one consignment, with one acknowledgement and one set of serial links.
- **A replacement off the shelf** debits nothing. `service_replacements.outgoing_from_shelf` records
  which pool it came from, because cancelling has to put it back in that one — the unit's status
  cannot be read for it, since `Shelfed` is where a cancelled unit lands either way.
- **Unused stock sent back by a technician** comes home to the shelf, not to the warehouse it was
  issued from. It is a named unit in hand from then on. A quantity-only return has no unit to shelve,
  so it still credits the warehouse.
- **A manual status change moves no quantity at all.** It used to apply a +1/-1 to the warehouse,
  because the shelf was part of that count and the two could disagree. They cannot now: the status
  *is* the shelf.
- **The consumption report stays warehouse-only**, which is what it has always claimed to be. A
  shelf issue appears nowhere on it. The cost is that its Technicians column reads lower than what
  technicians actually received; `StockAnalysis` is the report for that question.

### The migration

`20261017150105_SeparateShelfFromWarehouseStock` moves each part's shelved-unit count off its
warehouse balance, capped at what the balance holds, with one `ShelfTransfer` row per part
explaining it. Warehouse figures drop by the number of units on the shelf, which is the intended
outcome: those units were being counted twice the moment the pools separated, and an issue off the
shelf would have debited neither.

`ShelfTransfer` is the one movement type whose warehouse outflow is deliberately absent from the
consumption report. Nothing left the building and nobody received it, so counting it would put a
one-off spike of phantom consumption in the month of the deploy and leave it there for ever.
`ConsumptionRuleTests` names the exemption so it stays a decision.

## The technician's own route into a replacement (2026-10-18)

Until now a replacement could only be issued from `ReplacementApprovalPending` by a dispatch role.
The technician's part was to condemn the unit, complete the job, and wait. The shop's answer is that
the waiting served nothing: the person who has just taken the machine apart is the person who knows
it is finished, and the replacement is a unit off a rack ten feet away.

So `ReplaceAsync` has **two doors**, and which one a caller comes through decides what they must be:

| Job status | Who may | What else it does |
|---|---|---|
| `ReplacementApprovalPending` | the dispatch roles — unchanged | nothing; completion already ran |
| `InService` **and** `IsTotalLoss` | the **assigned technician**, or a dispatch role | also finishes the work on the job |

`ResolveReplaceRoute` is that rule, pure and tested, because it is the thing that changed.

### Why it is not a widening of anything else

- **The total-loss mark is required**, not merely an in-service job. Without it a technician could
  hand a unit off the shelf to any customer whose job they happened to hold — which is a different
  feature, and the one the swap route already is.
- **The job must be theirs.** `IsAssignedTechnician`, the same test behind acknowledge, start, lines,
  complete and the total-loss mark itself.
- **It still lands on `Completed` and still cannot dispatch.** The customer receives the unit through
  the dispatch door with the billing that raises. What widened is *who may take a unit off the shelf*.
- **A technician still cannot use the approval door.** A job parked there has been handed over.

The route carries no `RequireAuthorization` any more, and that is forced rather than chosen: "the
technician this row is assigned to" is a fact about the row, which a role policy cannot express.
`ServiceRoles.Manage` is the same three roles `DispatchManage` names, so the approval door admits
exactly who it admitted before — and `SessionState.DispatchManageRoles` already mirrors that list, so
there is no new list to drift.

### The bench route owes the completion's stock movements

This is the part that is quietly load-bearing. The bench route reaches `Completed` **without going
through `CompleteAsync`**, so it has to do what completion does to stock: consume the technician's
part-bearing lines and install any serials fitted to the customer. `ApplyCompletionEffectsAsync` is
shared between the two for that reason.

Leaving it out would not look like a bug. The job would reach `Completed` reading perfectly normally
while parts the technician had already booked stayed on their balance for ever — re-offered on the
next job and counted as stock the shop could issue. A total-loss job refuses *new* lines, but the mark
is a toggle, so lines added before it was set are ordinary and real.

Order matters: the lines are consumed **before** the replacement's billing line is added, so that line
stays billing-only. It is the same rule the swap route runs on, and `RevertAsync` refuses any job
carrying a `ReplacementSerialNo`, which is what makes it permanent.

## Three things the replacement route was getting wrong (2026-10-18)

Found while adding the above, all on the path a replacement already took.

### A shelf unit of an untracked part never left the shelf

`InstallToCustomerAsync` was behind `part.IsSerialTracked`. For a unit taken **off the shelf** that
call is not bookkeeping — it *is* the movement, because the shelf is not a quantity anywhere. It is
exactly the `component_serials` rows that are `Shelfed` in the service centre's custody, so the status
change is the whole of how a unit leaves that pool, and a shelf issue deliberately debits nothing.

And the shelf genuinely holds units of untracked parts: `ApplyStockAsync` shelves whatever a stocked
job names, on purpose — "the flag is a statement about how a part is ISSUED", not about what may be
remembered off a bench.

So for an untracked shelf unit the old test debited nothing **and** left the row on `Shelfed`. The
same machine could be handed out as a replacement again, and again, with a `Replacement` row in the
ledger each time and the shelf never going down. The test is now
`ReplacementUnitLeavesSerialTrail(isSerialTracked, fromShelf)`, and the swap route calls the same
helper — the two are the same handover of the same unit out of the same two pools, so they must not be
able to decide it differently.

### A serial already somewhere else was accepted

The swap route validated its outgoing serial with `FindIssueConflictsAsync`; the total-loss route did
not. Naming a unit the shop knows is installed at another customer, or in a technician's hands,
re-pointed that unit's record at this job's customer — so the system stopped knowing where the first
one went — while the warehouse was still debited for a unit nobody took off a rack.

It is now checked on both routes, and **for every part rather than only a serial-tracked one**,
because the shelf holds untracked units too. An unknown serial still passes, which is the ordinary
case and the only way purchased stock of a tracked part enters tracking at all.

### Total-loss replacements stopped being written to `service_replacements`

`ServiceReplacement` says in its own summary that it is written for both kinds, and the migration
backfills a row for every job that already carried a `ReplacementSerialNo`. Only `SwapAsync` ever
wrote one. So the backfill covered the history and then the live path stopped feeding it: every
total-loss replacement issued since has been invisible to the lookup that answers *"this unit has come
back — was it one of ours?"*, which is the row's entire reason for existing.

`ReplaceAsync` now writes it: `Kind = TotalLoss`, the outgoing unit, `OutgoingFromShelf` recording
which pool, and no incoming unit or `StatusBeforeSwap` — a total loss keeps nothing and has no undo.

## The shelf, read through a job (2026-10-18)

`GET /services/{id}/replacement-shelf` is the replacement form's pick list.

A replacement comes out of one of two pools and the person at the job can see neither. The warehouse
is a quantity, so there is nothing to list — a serial of warehouse stock is named as it leaves and the
shop has never seen it before. The shelf is the opposite: a register of named units in hand, every one
of which somebody could pick up right now.

It is job-scoped rather than a read of `/serials`, and that is what makes it reachable at all:
`/serials` is behind `StockManage`, which a technician does not have, so the shelf page is a screen
they cannot open. Authorisation here is `ServiceRoles.CanProcess` — the assigned technician or a
supervisory role, the same test the detail pane uses. Deliberately wider than who may *issue* a
replacement: seeing what is on the shelf is not taking anything off it, and narrowing it to the two
issuing doors would hide the list from the dispatch desk until the job happened to be sitting in the
one status they can act from.

**Every shelf unit is listed, not only the matching ones**, with `MatchesJob` as a sort key and a
label rather than a filter. A total loss is the one route where the replacement may legitimately be a
different item — the incoming unit is written off rather than kept, so there is no "same item at both
ends" rule to hold to the way a swap has one. That is also why this route keeps its part picker and
the swap route has none.

### ~~Known gap, not addressed here~~ Closed, same day

**Nothing stopped two people issuing the same shelf unit at once.** The conflict check and the shelf
lookup both read before the transaction wrote, so two simultaneous replacements naming one unit both
passed them. The second `InstallToCustomerAsync` overwrote the first, leaving one job's replacement
recorded against a unit the other job had also physically handed over — and one of the two units never
left the building.

It was never only about replacements. The issue paths had the same hole and a worse consequence: the
read-only `SplitIssueSourcesAsync` counted which named serials were on the shelf, so two stores
filling two issues off one shelf unit each counted it as theirs, each debited **one fewer** from the
warehouse than it actually took, and the unit went out twice.

## Claiming a unit off the shelf (2026-10-18)

`SerialService.ClaimFromShelfAsync` is the guard, and `ClaimIssueSourcesAsync` is its multi-unit form.
Both WRITE, so both are called inside the caller's transaction.

### Why the warehouse needed nothing and the shelf did

The warehouse cannot be over-issued because its quantity is taken by a conditional UPDATE that fails
when the balance would go negative — `StockLedgerService.GuardedDecrementAsync`, whose
`WHERE … AND on_hand >= qty` is the whole guard.

The shelf has no quantity to guard. It *is* the `component_serials` rows that are `Shelfed` in the
service centre's custody, so there was no number for a conditional UPDATE to point at. The answer was
to point the same idiom at the row:

```sql
UPDATE component_serials SET last_updated_at = UTC_TIMESTAMP(6)
WHERE part_id = ? AND serial_number = ?
  AND status = 'Shelfed' AND owner_type = 'ServiceCenter'
```

Nought rows affected is the refusal. Same shape as the warehouse's guard, which is why it was chosen
over a `SELECT … FOR UPDATE`: one idiom in the codebase for "take a thing, or be told you cannot",
and the condition reads off the WHERE in both.

### What makes it a lock and not merely a second read

Both properties come from InnoDB rather than from anything in this repository, and both are needed:

- an UPDATE takes an **exclusive lock** on the row and holds it until the transaction ends, so the
  second caller's identical UPDATE *blocks* rather than racing;
- an UPDATE performs a **current read** — the latest committed row, not the transaction's snapshot —
  so when it unblocks it judges its WHERE against what the winner actually wrote. The status is no
  longer `Shelfed`, nought rows match, and the second caller is refused.

If the winner rolls back instead, the lock lifts with the row still `Shelfed` and the waiter takes it.
That is also right, and it is why the guard belongs inside the transaction rather than in front of it.

### It writes no status, on purpose

The obvious implementation moves the row to its destination here. That would leave the real transition
— `InstallToCustomerAsync`, `CaptureOnIssueAsync` — writing a history row whose old status was already
the new one, so the trail would stop saying the unit came off the shelf. That trail is the only record
the shelf has, since the shelf is not a quantity anywhere. Touching `last_updated_at` takes the same
lock and answers the same question, and `ShelfClaimTests` asserts on the statement itself that no
status is written.

### The four outcomes, and the one wrong answer

`ShelfClaimLostReason(promisedShelf, claimed, serialNo)` is the decision, pure and tested. Only one
combination refuses:

| Form was offered | Claim got it | Outcome |
|---|---|---|
| a shelf unit | yes | ordinary shelf handover |
| a shelf unit | **no** | **refused** — somebody else took it; nothing was handed over |
| an unknown serial | no | warehouse stock, named as it goes out — unchanged |
| an unknown serial | yes | somebody shelved it in between; take it off the shelf |

The wrong answer worth naming is the second row **falling back to the warehouse**. It reads as
resilience — not on the shelf, so take it out of stock instead — and it is silently expensive: the
warehouse never held that unit, so the debit corresponds to nothing on a rack, the shop ends up one
unit short on paper, and the customer has been handed nothing. `ClaimIssueSourcesAsync` refuses the
same case by name rather than re-counting the unit as warehouse stock the shop does not have.

The claim is also the **authority on which pool the unit came from**, replacing the pre-transaction
read everywhere downstream of it: the ledger's `fromShelf`, the serial trail, and
`service_replacements.outgoing_from_shelf` — which decides where a cancelled swap puts the unit back,
and so must record the pool it actually came out of rather than the one the form expected.

### The read-only split is gone, not merely uncalled

`SplitIssueSourcesAsync` was deleted rather than left for other callers. It is indistinguishable from
`ClaimIssueSourcesAsync` at the call site, so keeping it would keep the bug one autocomplete away —
the same reasoning that removed the status parameter from `InstallToCustomerAsync` when one state had
come to be written two ways. `IsOnShelfAsync` stays, because deciding what to show on a form is a
genuinely different job from taking a unit, and its summary now says so.

### What is not covered by tests

The race itself. This suite opens no database, and what makes the claim safe is the server's locking
behaviour, so there is nothing here that could reproduce two concurrent transactions. `ShelfClaimTests`
pins the decision table, and that the statement guards on `status` and `owner_type` without writing a
status. The locking claims above are asserted by argument, not by test, and are written down here for
that reason.
