# Serial-tracked unit: lifecycle review

Written 2026-10-15, walking a serial-tracked unit from the warehouse shelf to every ending it has, and
asking at each step: can a unit get stuck here, and can the two ledgers disagree about it?

There are two ledgers and they are supposed to say the same thing in different words:

* the **quantity ledger** — `stock_balances` plus the append-only `stock_movements` — which counts.
* the **serial ledger** — `component_serials` plus `serial_status_history` — which names.

Almost every defect below is one of those two moving without the other.

**Updated 2026-10-17.** The shelf is now a pool of its own, counted by unit rather than as part of
the warehouse quantity - see the shelf section of
[advance-replacement-and-stocking.md](advance-replacement-and-stocking.md). That closes gap 6 below
by construction rather than by arithmetic: a unit joins the shelf by reaching `SHELFED` and leaves it
by leaving `SHELFED`, so there is no second number for the status to disagree with. The entry is kept
because the failure it describes is the one this whole document is about, and because the +1/-1 rule
it records is gone and nobody should go looking for it.

Companion reading: [serial-custody-and-field-returns.md](serial-custody-and-field-returns.md) for the
design, and `field-portal-android/docs/HSRTECH_PARITY_AND_SERVICE_CYCLE.md` for the phone's side.

## The lifecycle as built

```
                      ┌──────────────── AcquireToServiceCentre / inward ────────────────┐
                      │                                                                 │
  (new unit) ──► ISSUED ──ack──► RECEIVED ──fit/sell──► USED ──collect──► COLLECTED
                   │   └─ack: missing──► MISSING     (customer)                │
                   │   └─ack: defective──► DEFECTIVE ◄───────────────────────────── │
                   │                          │                                     │
                   │                   ship (Faulty)                          ship (Faulty)
                   │                          ▼                                     ▼
                   │                    IN_TRANSIT_SC ──receive──► UNDER_REPAIR ──┬──► SHELFED ──► (issuable)
                   │                          ▲                                   └──► SCRAPPED (terminal)
          ship (GoodStock) from RECEIVED ──────┘
                                              └──receive, good──► SHELFED ──► (issuable)
                                              └──receive, lost──► MISSING
  peer transfer:   RECEIVED ──send──► IN_TRANSIT_TECH ──ack──► RECEIVED (receiver) | rolled back to sender
```

Re-issuable is exactly `{SHELFED}` (`SerialService.ReIssuable`), and those units ARE the shelf.
Everything else is deliberately not offered on the next issue, which is the rule that keeps a broken
unit off the shelf.

`REPAIRED` and `RETURNED_TO_SC` used to be in that set, and replacing the pair with one status is
what made the shelf readable: "repaired" says a bench has finished with a unit and "returned to SC"
says a box arrived, and neither says anybody put the thing away. A unit is on the shelf because
somebody shelved it. `REPAIRED` still exists for a unit a bench has finished with and nobody has put
away yet; `RETURNED_TO_SC` is kept only for the units that passed through it before `SHELFED`
existed.

## Closed

### 1. A shipment could only be received in full, or not at all — FIXED

`POST /stock-returns/{id}/acknowledge` took no body and booked in `StockReturn.Qty` in one movement.
A box of ten that arrived with eight in it had two possible answers, both wrong: acknowledge it and
invent two units on the warehouse shelf, or mark the whole shipment missing and throw away the eight
sitting on the counter. The serial ledger took the same lie — all ten units went `RETURNED_TO_SC` and
became re-issuable, including the two nobody could find.

Now each unit carries its own verdict (`stock_return_serials.ack_status`), the shipment stays
`Pending` until its last unit is resolved, and a quantity-only shipment takes a received count capped
at what was sent. `StockReturnsEndpoints.PlanUnitReceipt` and `QuantityReceiptError` hold the two
rules; `PartialReturnReceiptTests` pins them.

### 2. The receiver could not say "it arrived broken" — FIXED

`Defective` on a return line meant only *what the technician declared when shipping*. There was no way
to record what the counter actually found, so a unit that arrived smashed on an unused-stock shipment
went onto the warehouse shelf as sellable stock.

A `Defective` verdict now opens a repair job for the unit exactly as a faulty shipment does — the
envelope a unit travelled in does not change what it is — moves it to `UNDER_REPAIR`, and credits the
warehouse nothing. The quantity reaches the shelf when the job is stocked, which is the same path a
faulty return already took.

### 3. Marking a shipment missing wrote nothing off, on older shipments — FIXED

`MissingAsync` wrote its `LossInTransit` row only `if (r.TechnicianDebitedOnShip)`. That flag is false
for every shipment raised before dispatch and receipt were split out — and those are precisely the
shipments whose quantity is still sitting on the technician. So reporting one missing left the
technician holding stock that demonstrably did not exist, with no movement explaining it.

`ReturnLostAsync` now debits the holder when the shipment never did.

### 4. Marking a shipment missing wrote off units already on the shelf — FIXED

With per-unit receipt it became possible to book eight units in and then report the shipment missing.
The write-off counted `r.Qty`, so it would have taken ten units of stock out of a building that
contains eight of them. Only units still outstanding are written off now.

### 5. Collected units were unreturnable unless the technician also held stock — FIXED (phone)

`GET /stock-requests/inventory/me` filters `OnHand > 0`, and the old ship-return form could only offer
parts from that list. A unit collected from a customer was never on the technician's balance, and a
unit that arrived faulty was never credited as usable — so for a part the technician held none of,
neither appeared anywhere on the form. Those units had no way home: not fittable (not `RECEIVED`), not
shippable (no part to pick), just permanently in a bag.

The rebuilt ship-returns screen builds its two sections from the serial pick lists
(`GET /serials/available?returnKind=…`) rather than from the quantity holding, so a unit is offered
because the technician holds it, not because its part happens to have a balance.

### 6. A manual status change never moved the quantity — FIXED 2026-10-16

`POST /serials/{id}/status` and `POST /serials/{id}/receive` (the desktop's SN management screen)
wrote the serial ledger and the history row, synced custody to match the new status, and touched
`stock_balances` and `stock_movements` not at all.

Two transitions crossed a write-off and silently disagreed with the count:

* **Found after missing.** A unit reported missing had its quantity written off as `LossInTransit`.
  Flipping it to `RETURNED_TO_SC` made it re-issuable again — but no quantity came back, so the
  warehouse count stayed one short for ever. The next issue of that part either failed the guarded
  decrement (a refusal nobody could explain from the screen) or ran the balance down against a unit
  that was physically on the shelf.
* **Scrapping a unit the shelf still counts.** `SCRAPPED` means the unit is gone; the count kept it.

**The rule was:** the shelf counts exactly the units it may issue, so the count follows the status.
A manual change onto the shelf added one to the warehouse, a change off it subtracted one, and
`SerialService.WarehouseDeltaFor` wrote it as a signed `Adjustment` referencing the unit. Without the
minus-one half, marking a unit missing, then found, then missing, then found minted a unit of stock
on every lap.

**The rule now:** nothing. Separating the shelf from the warehouse (2026-10-17) removed the second
number the first one could disagree with. A unit found after being written off joins the SHELF, and
the shelf is whichever units are `SHELFED` - so saying the unit is back IS saying the shelf has it,
and no lap of missing and found can mint anything. `WarehouseDeltaFor` and
`SerialStatusCorrectionAsync` are both gone. A manual change onto the shelf writes a `ShelfIn` row,
so the shelf's history reads the same however a unit got there, and that row moves no balance.

**One transition is still refused:** shelf stock straight to `RECEIVED`. It is the only change where
a unit would leave the shelf and arrive on no balance at all, because a technician's usable holding
is credited by issuing. The error says to issue the unit instead.

**Still not followed: the technician side.** A manual change into `RECEIVED` from an off-shelf status
(say `MISSING` → `RECEIVED`) credits nobody, so the unit is in a technician's hands and on no
balance. `MovementType.Adjustment` cannot carry it — the stock report reads every Adjustment as a
warehouse movement — so this needs a movement type of its own. The refusal above covers the case
where that would have lost a unit from the shelf; the rest is open.

## Open, and why they need a decision rather than a patch

### 7. Untracked residue of a tracked part can be adopted by fitting it, never by returning it

`SerialService.ValidateFittedSerialAsync` adopts a serial the shop has never seen when the technician
has unaccounted-for quantity of that part (`UntrackedHoldingAsync` > 0) — which is how stock issued
before serial capture was switched on gets into tracking.

Returning has no equivalent. `POST /stock-returns` refuses a serial-tracked part unless the request
names one serial per unit, and the pick list only offers units that already have records. So residue
can go *out* to a customer but can never go *back* to the warehouse. The technician's balance carries
it until they fit it on something.

Fix shape: let a return name an unknown serial for a tracked part and adopt it against the residue,
exactly as fitting does — the same guard (`UntrackedHoldingAsync`) already makes it impossible to
invent stock. The new ship-returns screen caps a tracked part at its pick list, so today this shows up
as a part the technician cannot fully return rather than as a server error.

### 8. Nothing ages a unit that is in transit

`IN_TRANSIT_SC` and `IN_TRANSIT_TECH` have no clock on them. A shipment nobody books in leaves its
units in transit indefinitely: out of the technician's holding (so they cannot fit them) and not on
the warehouse (so nobody is short of them on paper). The desktop lists pending shipments but never
sorts or flags by age, so the ones that need chasing look exactly like the ones that left yesterday.

Not a correctness defect — the data is all there — but it is the gap that turns the other ones into
losses, because nothing makes anybody look.

### 9. By design, worth writing down

* A unit on an unacknowledged issue is `ISSUED` and still the service centre's. The technician cannot
  fit it. That is the point of the acknowledgement step, not a defect — but it is the most common
  "why can't I use this part" on the phone, and the answer is always *Incoming stock*.
* A `DEFECTIVE` unit in a technician's custody is in no quantity balance at all. It is theirs to send
  in for service and nothing else, and it will not appear in their inventory count.
* A faulty return moves no quantity in either direction. The unit rejoins the warehouse count only
  when its repair job is stocked (`MarkRepairedAsync`), or never, if it is scrapped.
