# Serial custody and field returns

How a serial-tracked unit is followed from the warehouse to a customer and back, and what each app
does at each step. Written when the field loop was closed; read this before changing anything in
`SerialService`, `StockReturnsEndpoints` or the `Stocked` / `Dispatched` transitions.

## The loop

```
                 issue (serials captured)
   warehouse  ──────────────────────────▶  technician
       ▲                                       │
       │                                       │ fitted / sold
       │ job Stocked                           ▼
   REPAIRED  ◀── repair job ◀── SC ack ──  customer
                UNDER_REPAIR      ▲            │
                                  │            │ collected (faulty)
                                  └────────────┘
                                   faulty return
```

Status by stage:

| Stage | Status | Owner | Quantity effect |
|---|---|---|---|
| Issued to a field technician (courier) | `Issued` | ServiceCenter | warehouse −1 only |
| Issued in house (counter handover) | `Received` | Technician | warehouse −1, technician +1 |
| Field technician acknowledges | `Received` | Technician | technician **+ (received − defective)** |
| Arrived faulty | `Defective` | Technician | none — held, not usable stock |
| Never arrived | `Missing` | ServiceCenter | none — recorded as `LossInTransit` |
| Fitted in a service | `Installed` | Customer | technician −1 |
| Sold | `Used` | Customer | technician −1 |
| Collected back from a customer | `Collected` / `Defective` | Technician | **none** |
| Shipped in for service (faulty) | `InTransitSc` | Technician | none |
| Service center acknowledges (faulty) | `UnderRepair` | ServiceCenter | **none** |
| Repair job stocked | `Repaired` | ServiceCenter | warehouse **+1** |
| Kept from a customer on an advance replacement | `UnderRepair` | ServiceCenter | none — ownership only |
| Any finished job stocked | `Repaired` | ServiceCenter | warehouse **+1** |
| Repair job dispatched instead | `Installed` | Customer | none |
| Repair job written off | `Scrapped` | ServiceCenter | none — terminal |
| Unused stock shipped back | `InTransitSc` | Technician | technician **−1** |
| Unused stock acknowledged at SC | `ReturnedToSc` | ServiceCenter | warehouse **+1** |
| Peer transfer sent | `InTransitTech` | Technician (sender) | sender **−1** |
| Peer transfer acknowledged | `Received` | Technician (receiver) | receiver **+1** |

`Repaired` and `ReturnedToSc` are the only re-issuable statuses (`SerialService.ReIssuable`).
`UnderRepair` is deliberately not one: a unit waiting on a repair job must never be offered on the
next issue.

## The two kinds of return

`StockReturn.Kind` exists because two physically different journeys used to share one code path, and
the shared path was wrong for one of them.

**`GoodStock`** — unused stock going back. It was issued to the technician, so it is on their
balance: shipping it debits them, and acknowledging it credits the warehouse (see *Ownership moves on
acknowledgement* below). It is the default, so any client that does not send a kind gets it.

**`Faulty`** — customers' units collected in the field, sent in for service. These were **never** on
the technician's balance. Acknowledging moves no quantity: it flips custody to the service center and
opens one repair job per unit. The quantity only returns when that job is stocked.

Before the split, a faulty return ran the good-stock path and called
`ReturnToStockAsync(part, technician, qty)` against a balance the technician did not have. Either the
guarded decrement failed and the return stuck at `Pending` forever with its serials stranded in
`InTransitSc`, or — if the technician happened to hold good stock of that part — it silently consumed
their good stock and booked a broken unit onto the warehouse shelf as sellable. A faulty shipment now
moves no quantity at either end, so there is nothing to get wrong.

The legacy system knew this. `hsrtech/docs/TECHNICIAN_RETURN_DISPATCH_MYSQL.sql`, note 2: *"Central
stock (stock.Total_stock) is NOT updated by this flow — only component_serial_master
ownership/status."* The rewrite merged the two flows and inherited the bug.

Shipping validates the kind against each unit's status (`SerialService.ShippableFor`), so a mixed
shipment is refused at the point it is created rather than at acknowledgement. Faulty returns are
serial-tracked parts only, the same scope the legacy flow had.

## Repair jobs raised from a return

`StockReturnsEndpoints.OpenRepairJobsAsync` creates one `ServiceJob` per received unit — one job
carries one serial, one verdict and one set of lines, and the shipment is only how the units
travelled. The legacy app made the same choice.

Nothing is keyed in by hand. The unit's own record says which part it is and which customer it came
from, so the job is raised against **that** customer. This is what `ComponentSerial.CustomerId` is
for, and why `CollectFromCustomerAsync` does not clear it when the unit passes through the
technician's hands on the way in. Only when the customer is genuinely unknown — units that entered
tracking before this existed — does it fall back to a per-technician `Technician Return - <name>`
account.

The job is an ordinary inward job from there on: `Inward` → assign → acknowledge → in service →
complete, and then either stocked or dispatched.

## What keeps the existing service-center workflow unchanged

`ServiceJob.SourceComponentSerialId` is null on every job that is not a field return, and every
serial-aware branch is behind it:

- **`ApplyStockAsync`** — ~~an ordinary job holds a customer's machine, not a catalogue part, so
  stocking it stays a pure status change. Only a job carrying a source serial credits the warehouse.~~
  **Superseded 2026-09-23.** Pressing *Keep in stock* means the shop has decided to keep the machine,
  so every stocked job now credits the warehouse and puts the unit into the service centre's custody.
  The source-serial case stopped being the exception and became the general path. See
  `advance-replacement-and-stocking.md`.
- **`ApplyDispatchAsync`** — same. A return-sourced job that is dispatched hands the unit back to the
  party on the job (`Installed`, owner Customer) rather than leaving it stranded at `UnderRepair`
  pointing at a finished job.
- **`LeaveTotalLossAsync`** — a written-off return unit reaches `Scrapped`. Without it the unit would
  sit forever as something the service center is still working on.

`RunBulkAsync` now opens a transaction. The ledger's balance updates are raw SQL that runs when
called while the movement rows wait for the single `SaveChanges`, so without one a failed save would
leave balances moved with nothing recording why.

## Serial capture is decided by the part, not the holder

It used to be `part.IsSerialTracked && holder.IsFieldTechnician`, on the reasoning that in-house
stock never leaves the building. It does: a unit fitted in house leaves on the customer's machine
exactly as one fitted in the field does. The exemption also made the system contradict itself —
peer transfers demanded serials from everyone, and a fitted serial was validated against custody that
in-house issues never created, so an in-house technician could neither transfer a tracked part nor
record the unit they fitted.

This was flagged as a Priority-1 must-do in
`hsrtech/docs/ITEM_LIFECYCLE_CROSS_APP_REPORT.md` (issue 2) and never fixed; it was carried into the
rewrite verbatim.

Two things make removing it safe:

**Counter handovers are their own receipt.** A field issue travels by courier and is confirmed on
arrival — that confirmation is why the acknowledgement step exists. An in-house issue is handed
across the counter: there is no journey to confirm, and the desktop has no acknowledgement screen.
`CaptureOnIssueAsync(inTransit: false)` marks those units `Received` in the technician's custody on
the spot and writes the movement-level ack, because otherwise they would sit at `Issued` forever and
fitting requires `Received` — an in-house technician would be handed parts they could never book.

**Existing holdings are adopted on first use.** Stock already in technicians' hands when capture was
switched on has a quantity balance and no serial records. `ValidateFittedSerialAsync` adopts an
unknown serial when `UntrackedHoldingAsync` shows that technician has unaccounted-for quantity of
that part, so it cannot invent stock. The residue self-closes as stock cycles.

A serial-tracked component line must now name its unit and carry qty 1. Leaving it blank used to be
accepted, which is how a unit could leave on a customer's machine with the quantity decremented and
nothing recording where the unit went.

## Client lockstep

The WPF serial-capture dialog was gated on `IsFieldTechnician` in both issue paths
(`StockRequestsViewModel`). That gate is removed in the same change: if the API drops it and the
desktop does not, every in-house issue of a serial-tracked part fails with "Enter exactly N serial
number(s)".

The Android ship-returns screen picks the kind and asks for the matching pick list
(`/serials/available?returnKind=…`), so it cannot offer a unit the server would refuse. `forReturn=true`
still works and means `GoodStock`.

## Ownership moves on acknowledgement, both directions

Stock belongs to whoever has acknowledged it. Nothing is credited to a balance on the strength of
somebody else's intention to send it.

The rule is symmetric, and it is two separate events, not one:

- **It leaves the sender when it physically leaves.** The warehouse is debited as an issue is
  dispatched; a technician is debited as they hand a return to the courier or a transfer to a peer.
- **It arrives at the receiver when they acknowledge it.** The technician is credited on
  acknowledging an issue or an incoming transfer; the warehouse is credited when the service center
  acknowledges a return.

In between, the quantity is on nobody's balance. That is the honest position: it is in a van.

Only what arrived AND is usable is credited — `received − defective`, the same figure the legacy app
derived (`inventory_provider.dart`, `effectiveIssuedQty`: an unacknowledged issue counts 0, and after
acknowledgement defective units are excluded). A defective unit is in the technician's hands and can
be sent in for service, but it is not stock anyone can fit, so counting it on-hand would offer it on
the next job.

Nothing is silently dropped. `LossInTransit` and `DefectiveOnArrival` account for the difference
between what was dispatched and what became usable stock, so the gap is a row somebody can look at.

Before this, balances were credited at dispatch and acknowledgements were declarative — recorded and
then ignored. A shipment that arrived three short was written down as three short and still counted
as complete, and the serial ledger (which *did* follow the acknowledgement) disagreed with the
quantity ledger from that moment on. On a serial-tracked issue the two accounts must now agree:
`StockAcksEndpoints.SerialQuantityMismatch` refuses quantities that contradict the per-unit verdicts,
so a shipment cannot be booked as three received while all three units are marked missing.

Splitting dispatch from receipt also removed a stranding bug on the way out. A technician who shipped
stock back and then fitted it on a job before the service center acknowledged used to make the
acknowledgement fail on a balance they had since spent, leaving the shipment stuck `Pending` with
nothing either side could do from the desk. The quantity now leaves them as the shipment does.

### Compatibility with rows written before the split

Three flags mark which convention a row was written under, all defaulting to the old behaviour so
existing data keeps describing what it actually did:

| Flag | Default | Meaning when false/true |
|---|---|---|
| `StockMovement.CreditedOnIssue` | `true` (backfilled) | true = already credited at dispatch, so acknowledging must not credit again |
| `StockReturn.TechnicianDebitedOnShip` | `false` | false = quantity still on the technician, so acknowledging must debit it |
| `TechnicianTransfer.SenderDebitedOnSend` | `false` | false = quantity still on the sender, so acknowledging moves both sides (legacy `Transfer` movement) |

So an issue, return or transfer that is already in flight when this deploys settles correctly under
the rules it was created with. Nothing needs draining first.

## Known gap, not addressed here

~~**In-transit stock has no home on the desk.**~~ **Closed 2026-09-23.** The store screen now carries
two columns and a footer total: *Out (in transit)* — issues dispatched and not yet acknowledged — and
*In (returning)* — good-stock returns shipped and not yet acknowledged. Both are read by
`StockEndpoints.InTransitAsync`, and `GET /stock/in-transit` gives the warehouse-wide totals.

Neither is ever added to On hand. The quantity is on nobody's balance, and counting it would offer
units that are still in a van. Faulty returns are excluded from the incoming figure on purpose: they
move no quantity at either end, so promising the shelf those units would be wrong — they only arrive
if the repair job on them is stocked.

**Non-serial faulty returns.** Out of scope, as in legacy. A non-tracked part collected from a
customer is not shippable through the faulty flow; `Collected` lines no longer demand a serial for
them, but there is nothing to book in at the other end.
