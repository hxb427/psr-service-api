using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Common;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Reports;

/// <summary>Monthly stock consumption — what the business actually used up, split by the side of the
/// business that used it, with every figure traceable to the movements behind it.
///
/// Why this is not the stock ledger with different column headings: the ledger is a shelf's
/// arithmetic, so stock issued to a technician is off the shelf the day it is handed over. That is
/// right for a shelf and wrong for consumption — those parts are in a van, not in a machine, and they
/// are consumed on the day they are fitted, which may be next month or never. Issues are therefore
/// not counted here at all, and the two reports do not agree on a total. See
/// <see cref="ConsumptionRules"/> for the full rule and for what else is excluded.
///
/// The sales/service split is the other half of the point. Both sides consume out of the same
/// warehouse and the same ledger, and before this there was no way to ask how much of a month's usage
/// was repair work and how much was goods sold over the counter.</summary>
public static partial class ReportsEndpoints
{
    /// <summary>How many movements the drill-down lists. A fast-moving part over a year of months runs
    /// to thousands of rows, and nobody reads past the first screen of them — the figures above the
    /// list are the answer, and this is the evidence for them, newest first.</summary>
    private const int MaxConsumptionEvents = 400;

    /// <summary>Three running totals for one bucket — a month, an item, or the page's own strip.
    /// Mutable and deliberately so: a quarter of a million ledger rows are added to their month, their
    /// item and the totals in one pass over the list rather than three LINQ groupings over it.</summary>
    private sealed class StreamTally
    {
        public int Service { get; private set; }
        public int Sales { get; private set; }
        public int Other { get; private set; }

        public int Total => Service + Sales + Other;

        /// <summary>Whether this bucket is worth printing. Not "did anything happen" — a month in
        /// which one part was fitted and the same part handed back nets to zero, and a row of zeroes
        /// is noise whichever way it got there.</summary>
        public bool Any => Service != 0 || Sales != 0 || Other != 0;

        public void Add(ConsumptionStream stream, int qty)
        {
            switch (stream)
            {
                case ConsumptionStream.Service: Service += qty; break;
                case ConsumptionStream.Sales: Sales += qty; break;
                default: Other += qty; break;
            }
        }

        public void Add(StreamTally other)
        {
            Service += other.Service;
            Sales += other.Sales;
            Other += other.Other;
        }
    }

    // ---------------------------------------------------------------- the report

    /// <param name="stream">"service", "sales", "other", or "all" / absent for everything combined.
    /// Filters what counts: in a single-stream window the other columns read zero and an item consumed
    /// only by the other side drops out of the list entirely, which is the point of asking.</param>
    /// <param name="format">"xlsx" writes the month-by-item matrix as a workbook instead — the whole
    /// report on one sheet, because that is what somebody asking for it in Excel is going to pivot.</param>
    private static async Task<Results<Ok<ConsumptionReportDto>, FileContentHttpResult>> ConsumptionAsync(
        AppDbContext db, IOptions<JwtOptions> shop,
        DateTime? from, DateTime? to, string? stream, string? search, string? format, CancellationToken ct)
    {
        var offset = shop.Value.LocalUtcOffsetHours;
        // Through ShopClock first: what arrives is not reliably the day the person picked, and a
        // desktop build that sends midnight IST as "...T00:00:00+05:30" lands here as the previous
        // evening. On a report whose rows are months that is a whole month mislabelled at the edges.
        var (windowFrom, windowTo) = ConsumptionRules.Window(
            ShopClock.BusinessDate(from), ShopClock.BusinessDate(to), offset);
        var (storedFrom, storedTo) = ConsumptionRules.StoredRange(windowFrom, windowTo, offset);
        var wanted = ConsumptionRules.ParseStream(stream);

        // The parts master comes down whole (or search-filtered) and is matched in memory, which is
        // how every other report on this page reaches part names: an ids.Contains() over the movement
        // rows hits the funcletizer bug this codebase works around everywhere.
        var partsQ = db.Parts.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var v = search.Trim();
            partsQ = partsQ.Where(p => p.ItemCode.Contains(v) || p.Name.Contains(v));
        }
        var parts = await partsQ.Select(p => new { p.Id, p.ItemCode, p.Name, p.Unit }).ToListAsync(ct);
        var partById = parts.ToDictionary(p => p.Id);

        var moves = await ConsumingMovements(db, storedFrom, storedTo)
            .Select(m => new { m.PartId, m.MovementType, m.Quantity, m.ReferenceType, m.CreatedAt })
            .ToListAsync(ct);

        // One cell per (month, part). Everything else on the report is this dictionary read a
        // different way, so the month strip, the item list and the totals cannot disagree.
        var cells = new Dictionary<(DateTime Month, long PartId), StreamTally>();
        foreach (var m in moves)
        {
            if (!partById.ContainsKey(m.PartId)) continue;
            if (ConsumptionRules.Effect(m.MovementType, m.ReferenceType, m.Quantity) is not { } effect) continue;
            if (effect.Consumed == 0) continue;
            if (wanted is { } only && effect.Stream != only) continue;

            var key = (ConsumptionRules.MonthOf(m.CreatedAt, offset), m.PartId);
            if (!cells.TryGetValue(key, out var cell)) cells[key] = cell = new StreamTally();
            cell.Add(effect.Stream, effect.Consumed);
        }

        var onHand = await WarehouseOnHandAsync(db, ct);
        var window = new StreamTally();

        // Every month in the window, quiet ones included — see ConsumptionRules.MonthsIn.
        var months = new List<ConsumptionMonthRow>();
        foreach (var month in ConsumptionRules.MonthsIn(windowFrom, windowTo))
        {
            var tally = new StreamTally();
            var distinctItems = 0;
            foreach (var (key, cell) in cells)
            {
                if (key.Month != month || !cell.Any) continue;
                tally.Add(cell);
                distinctItems++;
            }
            window.Add(tally);
            months.Add(new ConsumptionMonthRow(month, ConsumptionRules.MonthLabel(month),
                tally.Service, tally.Sales, tally.Other, tally.Total, distinctItems));
        }

        // Per month and per part. Biggest consumer first within a month: a report of what was used is
        // read from the top, and alphabetical order buries the one row somebody is looking for.
        var items = cells.Where(c => c.Value.Any)
            .Select(c => ItemRow(c.Key.Month, partById[c.Key.PartId].Id, partById[c.Key.PartId].ItemCode,
                partById[c.Key.PartId].Name, partById[c.Key.PartId].Unit, c.Value, onHand))
            .OrderBy(r => r.Month)
            .ThenByDescending(r => r.Total)
            .ThenBy(r => r.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The same cells summed per part over the whole window, so the list shown with no month picked
        // is a figure from the server rather than the client re-adding the month rows and getting a
        // second answer (the stock ledger's totals strip is here for the same reason).
        var perPart = new Dictionary<long, StreamTally>();
        foreach (var (key, cell) in cells)
        {
            if (!perPart.TryGetValue(key.PartId, out var total)) perPart[key.PartId] = total = new StreamTally();
            total.Add(cell);
        }

        var itemTotals = perPart.Where(p => p.Value.Any)
            .Select(p => ItemRow(null, partById[p.Key].Id, partById[p.Key].ItemCode,
                partById[p.Key].Name, partById[p.Key].Unit, p.Value, onHand))
            .OrderByDescending(r => r.Total)
            .ThenBy(r => r.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (IsXlsx(format))
        {
            var label = wanted is null ? "Consumption" : $"Consumption ({ConsumptionRules.StreamName(wanted)})";
            return TypedResults.File(XlsxBuilder.Build(label,
                new[] { "Month", "Item code", "Part", "Unit", "Service", "Sales", "Other", "Total", "In stock" },
                items.Select(x => (IReadOnlyList<object?>)new object?[] { x.MonthLabel, x.ItemCode, x.PartName,
                    x.Unit, x.Service, x.Sales, x.Other, x.Total, x.OnHand })),
                XlsxMime, "stock-consumption.xlsx");
        }

        return TypedResults.Ok(new ConsumptionReportDto(
            windowFrom, windowTo, ConsumptionRules.StreamName(wanted),
            window.Service, window.Sales, window.Other, window.Total,
            months, items, itemTotals));
    }

    private static ConsumptionItemRow ItemRow(DateTime? month, long partId, string itemCode, string partName,
        string? unit, StreamTally tally, IReadOnlyDictionary<long, int> onHand) =>
        new(month, month is { } m ? ConsumptionRules.MonthLabel(m) : null,
            partId, itemCode, partName, unit,
            tally.Service, tally.Sales, tally.Other, tally.Total, onHand.GetValueOrDefault(partId));

    // ---------------------------------------------------------------- one item, in full

    /// <summary>Everything behind one part's figure: its month-by-month trend over the window, who
    /// consumed it, and the individual movements with the document each one happened on.
    ///
    /// Its own endpoint rather than part of the report, because this is the movement rows themselves
    /// and fetching them for every part in a year would be most of the ledger to answer a question
    /// about the one part somebody clicked on.</summary>
    /// <param name="month">Scope the consumer and movement lists to one month — the one picked on the
    /// report. The trend above them stays the whole window, because the reason for looking at one
    /// month is usually that it differs from the others.</param>
    private static async Task<Results<Ok<ConsumptionItemDetailDto>, NotFound>> ConsumptionItemAsync(
        long partId, AppDbContext db, IOptions<JwtOptions> shop,
        DateTime? from, DateTime? to, string? stream, DateTime? month, CancellationToken ct)
    {
        var part = await db.Parts.AsNoTracking()
            .Where(p => p.Id == partId)
            .Select(p => new { p.Id, p.ItemCode, p.Name, p.Unit })
            .FirstOrDefaultAsync(ct);
        if (part is null) return TypedResults.NotFound();

        var offset = shop.Value.LocalUtcOffsetHours;
        var (windowFrom, windowTo) = ConsumptionRules.Window(
            ShopClock.BusinessDate(from), ShopClock.BusinessDate(to), offset);
        var (storedFrom, storedTo) = ConsumptionRules.StoredRange(windowFrom, windowTo, offset);
        var wanted = ConsumptionRules.ParseStream(stream);
        var pickedMonth = ShopClock.BusinessDate(month) is { } mo
            ? new DateTime(mo.Year, mo.Month, 1)
            : (DateTime?)null;

        var moves = await ConsumingMovements(db, storedFrom, storedTo)
            .Where(m => m.PartId == partId)
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => new
            {
                m.MovementType, m.Quantity, m.ReferenceType, m.ReferenceId,
                m.TechnicianId, m.PerformedByUserId, m.Remarks, m.CreatedAt,
            })
            .ToListAsync(ct);

        var names = await TechNamesAsync(db, ct);
        var documents = await ConsumptionDocumentsAsync(db, partId, storedFrom, storedTo, ct);

        var byMonth = new Dictionary<DateTime, StreamTally>();
        var byConsumer = new Dictionary<(string Who, string Kind, ConsumptionStream Stream), int>();
        var events = new List<ConsumptionEventRow>();
        // The figures printed over the lists. Scoped to the picked month when there is one, so the
        // headline matches the row that was clicked to get here: the item list shows one month's
        // figure, and a panel answering it with a year's would read as a disagreement.
        var scoped = new StreamTally();
        var movementCount = 0;

        foreach (var m in moves)
        {
            if (ConsumptionRules.Effect(m.MovementType, m.ReferenceType, m.Quantity) is not { } effect) continue;
            if (effect.Consumed == 0) continue;
            if (wanted is { } only && effect.Stream != only) continue;

            // The trend is always the whole window, picked month or not: the reason for looking at one
            // month is usually that it differs from the others, and a single-row trend shows nothing.
            var bucket = ConsumptionRules.MonthOf(m.CreatedAt, offset);
            if (!byMonth.TryGetValue(bucket, out var cell)) byMonth[bucket] = cell = new StreamTally();
            cell.Add(effect.Stream, effect.Consumed);

            // Everything below here is the picked month only.
            if (pickedMonth is { } picked && bucket != picked) continue;
            scoped.Add(effect.Stream, effect.Consumed);

            // Who it was: the technician for anything off a technician's own stock, and whoever moved
            // it for anything that went straight off the warehouse shelf — a counter sale, a
            // replacement unit, a write-down. One of the two is always set.
            var whoId = m.TechnicianId is > 0 ? m.TechnicianId.Value : m.PerformedByUserId;
            var who = names.GetValueOrDefault(whoId, $"#{whoId}");
            var consumerKey = (who, effect.Kind, effect.Stream);
            byConsumer[consumerKey] = byConsumer.GetValueOrDefault(consumerKey) + effect.Consumed;

            // Counted whether or not it makes the list, so the page can say the list is the latest
            // 400 of 1,200 rather than letting someone try to add a shortened list up to the total.
            movementCount++;
            if (events.Count < MaxConsumptionEvents)
            {
                var doc = m.ReferenceId is { } refId
                    ? documents.GetValueOrDefault((m.ReferenceType ?? string.Empty, refId))
                    : default;
                events.Add(new ConsumptionEventRow(
                    m.CreatedAt, effect.Kind, StreamLabel(effect.Stream), effect.Consumed,
                    who, doc.Number, doc.Party, m.Remarks));
            }
        }

        var months = ConsumptionRules.MonthsIn(windowFrom, windowTo)
            .Select(mo =>
            {
                var cell = byMonth.GetValueOrDefault(mo) ?? new StreamTally();
                return new ConsumptionMonthRow(mo, ConsumptionRules.MonthLabel(mo),
                    cell.Service, cell.Sales, cell.Other, cell.Total, cell.Any ? 1 : 0);
            })
            .ToList();

        var consumers = byConsumer.Where(c => c.Value != 0)
            .Select(c => new ConsumptionConsumerRow(c.Key.Who, c.Key.Kind, StreamLabel(c.Key.Stream), c.Value))
            .OrderByDescending(c => c.Qty)
            .ThenBy(c => c.Who, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var warehouse = await db.StockBalances.AsNoTracking()
            .Where(b => b.PartId == partId && b.TechnicianId == StockBalance.Warehouse)
            .Select(b => b.OnHand)
            .FirstOrDefaultAsync(ct);

        return TypedResults.Ok(new ConsumptionItemDetailDto(
            part.Id, part.ItemCode, part.Name, part.Unit, warehouse,
            windowFrom, windowTo, ConsumptionRules.StreamName(wanted),
            scoped.Service, scoped.Sales, scoped.Other, scoped.Total,
            months, consumers, events, movementCount));
    }

    private static string StreamLabel(ConsumptionStream stream) => stream switch
    {
        ConsumptionStream.Service => "Service",
        ConsumptionStream.Sales => "Sales",
        _ => "Other",
    };

    /// <summary>The warehouse's current holding per part. Now, not as at the window's end: what is on
    /// the shelf is a fact about today, and it is the figure that makes a consumption number
    /// actionable — forty used last month with three left is a reorder, forty with ninety left is not.</summary>
    private static async Task<Dictionary<long, int>> WarehouseOnHandAsync(AppDbContext db, CancellationToken ct) =>
        await db.StockBalances.AsNoTracking()
            .Where(b => b.TechnicianId == StockBalance.Warehouse && b.OnHand != 0)
            .ToDictionaryAsync(b => b.PartId, b => b.OnHand, ct);

    /// <summary>The document each of one part's movements happened on, keyed by the reference the
    /// movement carries: job, field visit, field sale, counter sale or sale return, with the customer
    /// or dealer it went to where the document names one.</summary>
    private static async Task<Dictionary<(string Type, long Id), (string? Number, string? Party)>>
        ConsumptionDocumentsAsync(AppDbContext db, long partId, DateTime storedFrom, DateTime storedTo,
            CancellationToken ct)
    {
        var map = new Dictionary<(string, long), (string?, string?)>();
        foreach (var (type, query) in DocumentQueries(db, partId, storedFrom, storedTo))
            foreach (var row in await query.ToListAsync(ct))
                map[(type, row.Id)] = (row.Number, row.Party);
        return map;
    }

    /// <summary>One document a consuming movement points at: the number somebody would quote, and the
    /// party it went to where the document names one.</summary>
    internal record ConsumptionDocumentRow(long Id, string Number, string? Party);

    /// <summary>Every reference lookup the drill-down runs, as queries rather than results.
    ///
    /// Five narrow queries rather than one five-way join, each joining the movement rows of this one
    /// part to the table its reference type points at. A single query would have to left-join all five
    /// tables on the same id column and then pick the right one, which both asks the database to match
    /// a job id against four unrelated tables and reads as if a movement could belong to several
    /// documents at once. Keyed on the reference TYPE as well as the id for the same reason: job 412
    /// and field sale 412 are different documents, and only the pair identifies one.
    ///
    /// Handed back unexecuted so the provider can be made to translate all five in a test. They are
    /// the only part of this report that can fail on the real database and nowhere else - each joins
    /// a nullable reference column to a non-nullable key and left-joins two party tables behind it,
    /// and a shape MySQL will not take fails in front of whoever clicked the row.</summary>
    internal static IReadOnlyList<(string Type, IQueryable<ConsumptionDocumentRow> Query)> DocumentQueries(
        AppDbContext db, long partId, DateTime storedFrom, DateTime storedTo)
    {
        IQueryable<StockMovement> Refs(string type) => db.StockMovements.AsNoTracking()
            .Where(m => m.PartId == partId && m.CreatedAt >= storedFrom && m.CreatedAt < storedTo
                     && m.ReferenceType == type && m.ReferenceId != null);

        return new (string, IQueryable<ConsumptionDocumentRow>)[]
        {
            ("SERVICE", from m in Refs("SERVICE")
                        join j in db.Services on m.ReferenceId equals (long?)j.Id
                        join c in db.Customers on j.CustomerId equals c.Id into cg
                        from c in cg.DefaultIfEmpty()
                        join d in db.Dealers on j.DealerId equals d.Id into dg
                        from d in dg.DefaultIfEmpty()
                        select new ConsumptionDocumentRow(
                            j.Id, j.ServiceNo, c != null ? c.Name : d != null ? d.Name : null)),

            ("FIELD_SERVICE", from m in Refs("FIELD_SERVICE")
                              join f in db.FieldServices on m.ReferenceId equals (long?)f.Id
                              select new ConsumptionDocumentRow(f.Id, f.ServiceNo, f.CustomerName)),

            ("FIELD_SALE", from m in Refs("FIELD_SALE")
                           join f in db.FieldSales on m.ReferenceId equals (long?)f.Id
                           select new ConsumptionDocumentRow(f.Id, f.SaleNo, f.CustomerName)),

            ("SPARE_SALE", from m in Refs("SPARE_SALE")
                           join s in db.SpareSales on m.ReferenceId equals (long?)s.Id
                           join c in db.Customers on s.CustomerId equals c.Id into cg
                           from c in cg.DefaultIfEmpty()
                           join d in db.Dealers on s.DealerId equals d.Id into dg
                           from d in dg.DefaultIfEmpty()
                           select new ConsumptionDocumentRow(
                               s.Id, s.SaleNo, c != null ? c.Name : d != null ? d.Name : null)),

            // Two hops: a return names no party of its own, so it is read through the sale it came back on.
            ("SPARE_SALE_RETURN", from m in Refs("SPARE_SALE_RETURN")
                                  join r in db.SpareSaleReturns on m.ReferenceId equals (long?)r.Id
                                  join s in db.SpareSales on r.SpareSaleId equals s.Id
                                  join c in db.Customers on s.CustomerId equals c.Id into cg
                                  from c in cg.DefaultIfEmpty()
                                  join d in db.Dealers on s.DealerId equals d.Id into dg
                                  from d in dg.DefaultIfEmpty()
                                  select new ConsumptionDocumentRow(
                                      r.Id, r.ReturnNo, c != null ? c.Name : d != null ? d.Name : null)),
        };
    }

    /// <summary>The ledger rows this report can possibly count, over a window of stored instants. One
    /// definition for both halves of the report, so the drill-down cannot end up explaining a figure
    /// the list arrived at from a different set of rows.</summary>
    internal static IQueryable<StockMovement> ConsumingMovements(
        AppDbContext db, DateTime storedFrom, DateTime storedTo) =>
        db.StockMovements.AsNoTracking()
            .Where(ConsumptionRules.IsConsuming)
            .Where(m => m.CreatedAt >= storedFrom && m.CreatedAt < storedTo);
}
