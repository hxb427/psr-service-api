using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Common;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Reports;

/// <summary>Phase 6 reporting. All aggregation is server-side over the enum/status-history/ledger tables
/// (the legacy app scanned whole tables client-side). Queries filter narrow projections in SQL and group
/// small result sets in memory — deliberately avoids EF GroupBy/Contains translation pitfalls.</summary>
public static class ReportsEndpoints
{
    public static IEndpointRouteBuilder MapReportsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/reports").WithTags("reports").RequireAuthorization();

        // Technicians may see their OWN performance / parts-used (server-forced); the rest are staff-wide.
        group.MapGet("/technician-performance", TechPerformanceAsync);
        group.MapGet("/technician-performance/{technicianId:long}", TechPerformanceDetailAsync);
        group.MapGet("/parts-used", PartsUsedAsync);
        group.MapGet("/held-items", HeldItemsAsync).RequireAuthorization("ReportsFull");
        group.MapGet("/service-register", ServiceRegisterAsync).RequireAuthorization("ReportsFull");
        group.MapGet("/daily-summary", DailySummaryAsync).RequireAuthorization("ReportsFull");
        group.MapGet("/tat", TatAsync).RequireAuthorization("ReportsFull");

        return app;
    }

    private static readonly ServiceStatus[] NotYetDispatched =
        { ServiceStatus.Inward, ServiceStatus.Assigned, ServiceStatus.Acknowledged, ServiceStatus.InService,
          ServiceStatus.Completed, ServiceStatus.ReplacementApprovalPending, ServiceStatus.PendingDispatch };

    /// <summary>Technician callers are always scoped to themselves regardless of the filter they pass.</summary>
    private static long? ScopeTechnician(ClaimsPrincipal user, long? requested)
    {
        if (user.IsInRole(RoleNames.Technician)
            && !user.IsInRole(RoleNames.Admin) && !user.IsInRole(RoleNames.Manager) && !user.IsInRole(RoleNames.Supervisor))
        {
            user.TryGetUserId(out var uid);
            return uid;
        }
        return requested;
    }

    // ---------------------------------------------------------------- technician performance

    private static async Task<Results<Ok<List<TechPerformanceRow>>, FileContentHttpResult>> TechPerformanceAsync(
        AppDbContext db, ClaimsPrincipal user, DateTime? from, DateTime? to, long? technicianId, string? format, CancellationToken ct)
    {
        var scope = ScopeTechnician(user, technicianId);
        var toEx = to?.Date.AddDays(1);

        // Completed events joined to the job's technician (narrow projection, filtered in SQL).
        var completedQ = from h in db.ServiceStatusHistory.AsNoTracking()
                         where h.ToStatus == "Completed"
                         join s in db.Services on h.ServiceId equals s.Id
                         where !s.IsDeleted && s.TechnicianId != null
                         select new { TechId = s.TechnicianId!.Value, h.ServiceId, h.ChangedAt };
        if (from is { } f1) completedQ = completedQ.Where(x => x.ChangedAt >= f1.Date);
        if (toEx is { } t1) completedQ = completedQ.Where(x => x.ChangedAt < t1);
        if (scope is { } sc1) completedQ = completedQ.Where(x => x.TechId == sc1);
        var completed = await completedQ.ToListAsync(ct);

        // Net parts consumption per technician (Consumption − ConsumptionReversal).
        var movQ = db.StockMovements.AsNoTracking().Where(m => m.TechnicianId != null
            && (m.MovementType == MovementType.Consumption || m.MovementType == MovementType.ConsumptionReversal));
        if (from is { } f2) movQ = movQ.Where(m => m.CreatedAt >= f2.Date);
        if (toEx is { } t2) movQ = movQ.Where(m => m.CreatedAt < t2);
        if (scope is { } sc2) movQ = movQ.Where(m => m.TechnicianId == sc2);
        var movements = await movQ.Select(m => new { TechId = m.TechnicianId!.Value, m.MovementType, m.Quantity }).ToListAsync(ct);

        var techNames = await TechNamesAsync(db, ct);

        var rows = completed.GroupBy(x => x.TechId)
            .Select(g => new
            {
                TechId = g.Key,
                Jobs = g.Select(x => x.ServiceId).Distinct().Count(),
                Days = g.Select(x => x.ChangedAt.Date).Distinct().Count(),
            })
            .ToDictionary(x => x.TechId);
        var consumed = movements.GroupBy(m => m.TechId).ToDictionary(g => g.Key,
            g => g.Sum(m => m.MovementType == MovementType.Consumption ? m.Quantity : -m.Quantity));

        var result = rows.Keys.Union(consumed.Keys)
            .Select(id => new TechPerformanceRow(
                id, techNames.GetValueOrDefault(id, $"#{id}"),
                rows.TryGetValue(id, out var r) ? r.Jobs : 0,
                rows.TryGetValue(id, out var r2) ? r2.Days : 0,
                consumed.GetValueOrDefault(id, 0)))
            .OrderBy(x => x.TechnicianName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (IsXlsx(format))
            return TypedResults.File(XlsxBuilder.Build("Technician performance",
                new[] { "Technician", "Completed jobs", "Distinct work days", "Parts consumed" },
                result.Select(x => (IReadOnlyList<object?>)new object?[] { x.TechnicianName, x.CompletedJobs, x.DistinctWorkDays, x.PartsConsumed })),
                XlsxMime, "technician-performance.xlsx");
        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<TechPerformanceDetail>, NotFound, ForbidHttpResult>> TechPerformanceDetailAsync(
        long technicianId, AppDbContext db, ClaimsPrincipal user, DateTime? from, DateTime? to, CancellationToken ct)
    {
        var scope = ScopeTechnician(user, technicianId);
        if (scope != technicianId) return TypedResults.Forbid();   // a technician asked for someone else
        var toEx = to?.Date.AddDays(1);

        var name = await db.Users.AsNoTracking().Where(u => u.Id == technicianId).Select(u => u.Username).FirstOrDefaultAsync(ct);
        if (name is null) return TypedResults.NotFound();

        var movQ = db.StockMovements.AsNoTracking().Where(m => m.TechnicianId == technicianId);
        if (from is { } f) movQ = movQ.Where(m => m.CreatedAt >= f.Date);
        if (toEx is { } t) movQ = movQ.Where(m => m.CreatedAt < t);
        var movements = await movQ.Select(m => new { m.MovementType, m.Quantity }).ToListAsync(ct);

        int Sum(MovementType mt) => movements.Where(m => m.MovementType == mt).Sum(m => m.Quantity);
        var consumedNet = Sum(MovementType.Consumption) - Sum(MovementType.ConsumptionReversal);

        var complQ = from h in db.ServiceStatusHistory.AsNoTracking()
                     where h.ToStatus == "Completed"
                     join s in db.Services on h.ServiceId equals s.Id
                     where !s.IsDeleted && s.TechnicianId == technicianId
                     select new { h.ServiceId, h.ChangedAt };
        if (from is { } f3) complQ = complQ.Where(x => x.ChangedAt >= f3.Date);
        if (toEx is { } t3) complQ = complQ.Where(x => x.ChangedAt < t3);
        var completions = await complQ.ToListAsync(ct);

        // Recent jobs (newest first, capped) with the party name resolved.
        var recentQ = from s in db.Services.AsNoTracking()
                      where !s.IsDeleted && s.TechnicianId == technicianId
                      join c in db.Customers on s.CustomerId equals c.Id into cg
                      from c in cg.DefaultIfEmpty()
                      join d in db.Dealers on s.DealerId equals d.Id into dg
                      from d in dg.DefaultIfEmpty()
                      orderby s.Id descending
                      select new { s.Id, s.ServiceNo, Party = c != null ? c.Name : (d != null ? d.Name : null), s.Description, s.SerialNo, s.ServiceStatus };
        var recent = (await recentQ.Take(25).ToListAsync(ct))
            .Select(x => new TechRecentJobRow(x.Id, x.ServiceNo, x.Party, x.Description, x.SerialNo, x.ServiceStatus.ToString(),
                completions.Where(cp => cp.ServiceId == x.Id).Select(cp => (DateTime?)cp.ChangedAt).FirstOrDefault()))
            .ToList();

        return TypedResults.Ok(new TechPerformanceDetail(
            technicianId, name,
            Sum(MovementType.Issue), consumedNet, Sum(MovementType.Return), Sum(MovementType.Adjustment),
            completions.Select(x => x.ServiceId).Distinct().Count(),
            completions.Select(x => x.ChangedAt.Date).Distinct().Count(),
            recent));
    }

    // ---------------------------------------------------------------- parts used

    private static async Task<Results<Ok<List<PartsUsedReportRow>>, FileContentHttpResult>> PartsUsedAsync(
        AppDbContext db, ClaimsPrincipal user, DateTime? from, DateTime? to, long? technicianId, string? search, string? format, CancellationToken ct)
    {
        var scope = ScopeTechnician(user, technicianId);
        var toEx = to?.Date.AddDays(1);

        var q = from m in db.StockMovements.AsNoTracking()
                where m.TechnicianId != null
                      && (m.MovementType == MovementType.Consumption || m.MovementType == MovementType.ConsumptionReversal)
                join p in db.Parts on m.PartId equals p.Id
                select new { TechId = m.TechnicianId!.Value, m.MovementType, m.Quantity, m.CreatedAt, p.ItemCode, PartName = p.Name };
        if (from is { } f) q = q.Where(x => x.CreatedAt >= f.Date);
        if (toEx is { } t) q = q.Where(x => x.CreatedAt < t);
        if (scope is { } sc) q = q.Where(x => x.TechId == sc);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            q = q.Where(x => x.ItemCode.Contains(term) || x.PartName.Contains(term));
        }
        var rows = await q.ToListAsync(ct);
        var techNames = await TechNamesAsync(db, ct);

        var result = rows.GroupBy(x => new { x.TechId, x.ItemCode, x.PartName })
            .Select(g => new PartsUsedReportRow(
                g.Key.TechId, techNames.GetValueOrDefault(g.Key.TechId, $"#{g.Key.TechId}"),
                g.Key.ItemCode, g.Key.PartName,
                g.Sum(x => x.MovementType == MovementType.Consumption ? x.Quantity : -x.Quantity)))
            .Where(x => x.Quantity != 0)
            .OrderBy(x => x.TechnicianName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.ItemCode)
            .ToList();

        if (IsXlsx(format))
            return TypedResults.File(XlsxBuilder.Build("Parts used",
                new[] { "Technician", "Item code", "Part", "Quantity" },
                result.Select(x => (IReadOnlyList<object?>)new object?[] { x.TechnicianName, x.ItemCode, x.PartName, x.Quantity })),
                XlsxMime, "parts-used.xlsx");
        return TypedResults.Ok(result);
    }

    // ---------------------------------------------------------------- held items (nonzero technician balances)

    private static async Task<Results<Ok<List<HeldItemRow>>, FileContentHttpResult>> HeldItemsAsync(
        AppDbContext db, long? technicianId, string? search, string? format, CancellationToken ct)
    {
        var q = from b in db.StockBalances.AsNoTracking()
                where b.TechnicianId != StockBalance.Warehouse && b.OnHand != 0
                join p in db.Parts on b.PartId equals p.Id
                join u in db.Users on b.TechnicianId equals u.Id
                select new { b.TechnicianId, TechName = u.Username, p.ItemCode, PartName = p.Name, b.OnHand };
        if (technicianId is { } tid) q = q.Where(x => x.TechnicianId == tid);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            q = q.Where(x => x.ItemCode.Contains(term) || x.PartName.Contains(term));
        }
        var rows = (await q.ToListAsync(ct))
            .Select(x => new HeldItemRow(x.TechnicianId, x.TechName, x.ItemCode, x.PartName, x.OnHand))
            .OrderBy(x => x.TechnicianName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.ItemCode)
            .ToList();

        if (IsXlsx(format))
            return TypedResults.File(XlsxBuilder.Build("Held items",
                new[] { "Technician", "Item code", "Part", "On hand" },
                rows.Select(x => (IReadOnlyList<object?>)new object?[] { x.TechnicianName, x.ItemCode, x.PartName, x.OnHand })),
                XlsxMime, "held-items.xlsx");
        return TypedResults.Ok(rows);
    }

    // ---------------------------------------------------------------- service register (master export / global search)

    private static async Task<Results<Ok<PagedResult<ServiceRegisterRow>>, FileContentHttpResult>> ServiceRegisterAsync(
        AppDbContext db,
        DateTime? from, DateTime? to, string? status, long? technicianId, string? customer, string? serial,
        string? challan, string? inwardDc, string? outwardDc, string? piNo, string? invNo,
        string? warranty, string? payment, string? search, int? page, int? pageSize, string? format, CancellationToken ct)
    {
        var q = from s in db.Services.AsNoTracking()
                where !s.IsDeleted
                join c in db.Customers on s.CustomerId equals c.Id into cg
                from c in cg.DefaultIfEmpty()
                join d in db.Dealers on s.DealerId equals d.Id into dg
                from d in dg.DefaultIfEmpty()
                join u in db.Users on s.TechnicianId equals u.Id into ug
                from u in ug.DefaultIfEmpty()
                select new { s, Party = c != null ? c.Name : (d != null ? d.Name : null), TechName = u != null ? u.Username : null };

        if (from is { } f) q = q.Where(x => x.s.DateReceived >= f.Date);
        if (to is { } t) q = q.Where(x => x.s.DateReceived < t.Date.AddDays(1));
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<ServiceStatus>(status, true, out var st))
            q = q.Where(x => x.s.ServiceStatus == st);
        if (technicianId is { } tid) q = q.Where(x => x.s.TechnicianId == tid);
        if (!string.IsNullOrWhiteSpace(warranty) && Enum.TryParse<WarrantyStatus>(warranty, true, out var ws))
            q = q.Where(x => x.s.WarrantyStatus == ws);
        if (!string.IsNullOrWhiteSpace(payment) && Enum.TryParse<PaymentStatus>(payment, true, out var ps))
            q = q.Where(x => x.s.PaymentStatus == ps);
        if (!string.IsNullOrWhiteSpace(customer)) { var v = customer.Trim(); q = q.Where(x => x.Party != null && x.Party.Contains(v)); }
        if (!string.IsNullOrWhiteSpace(serial)) { var v = serial.Trim(); q = q.Where(x => x.s.SerialNo.Contains(v)); }
        if (!string.IsNullOrWhiteSpace(challan)) { var v = challan.Trim(); q = q.Where(x => x.s.ChallanNo != null && x.s.ChallanNo.Contains(v)); }
        if (!string.IsNullOrWhiteSpace(inwardDc)) { var v = inwardDc.Trim(); q = q.Where(x => x.s.InwardDcNo != null && x.s.InwardDcNo.Contains(v)); }
        if (!string.IsNullOrWhiteSpace(outwardDc)) { var v = outwardDc.Trim(); q = q.Where(x => x.s.OutwardDcNo != null && x.s.OutwardDcNo.Contains(v)); }
        if (!string.IsNullOrWhiteSpace(piNo)) { var v = piNo.Trim(); q = q.Where(x => x.s.PiNo != null && x.s.PiNo.Contains(v)); }
        if (!string.IsNullOrWhiteSpace(invNo)) { var v = invNo.Trim(); q = q.Where(x => x.s.InvNo != null && x.s.InvNo.Contains(v)); }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var v = search.Trim();
            q = q.Where(x => x.s.ServiceNo.Contains(v)
                          || (x.s.PsCode != null && x.s.PsCode.Contains(v))
                          || (x.s.Description != null && x.s.Description.Contains(v))
                          || (x.s.ModelName != null && x.s.ModelName.Contains(v)));
        }

        static ServiceRegisterRow Map(ServiceJob s, string? party, string? tech) => new(
            s.Id, s.ServiceNo, s.ChallanNo, s.InwardDcNo, party, s.CustomerType,
            s.SerialNo, s.PsCode, s.ModelName, s.Description, s.ReportedProblem,
            s.ServiceStatus.ToString(), s.WarrantyStatus.ToString(), s.PaymentStatus.ToString(), s.Priority.ToString(), s.IsTotalLoss,
            s.PiNo, s.PiDate, s.InvNo, s.InvDate, s.OutwardDcNo, s.OutwardReferenceNo, s.DcDate,
            tech, s.DateReceived, s.TechnicianRemarks);

        if (IsXlsx(format))
        {
            var all = await q.OrderByDescending(x => x.s.Id).Take(10_000).ToListAsync(ct);
            var mapped = all.Select(x => Map(x.s, x.Party, x.TechName)).ToList();
            return TypedResults.File(XlsxBuilder.Build("Service records",
                new[] { "Service no", "Challan", "Inward DC", "Customer", "Type", "Serial", "PS code", "Model", "Description",
                        "Problem", "Status", "Warranty", "Payment", "Priority", "Total loss", "PI no", "PI date", "Invoice no",
                        "Invoice date", "Outward DC", "Outward ref", "DC date", "Technician", "Received", "Remarks" },
                mapped.Select(x => (IReadOnlyList<object?>)new object?[] { x.ServiceNo, x.ChallanNo, x.InwardDcNo, x.CustomerName,
                    x.CustomerType, x.SerialNo, x.PsCode, x.ModelName, x.Description, x.ReportedProblem, x.ServiceStatus,
                    x.WarrantyStatus, x.PaymentStatus, x.Priority, x.IsTotalLoss, x.PiNo, x.PiDate, x.InvNo, x.InvDate,
                    x.OutwardDcNo, x.OutwardReferenceNo, x.DcDate, x.TechnicianName, x.DateReceived, x.TechnicianRemarks })),
                XlsxMime, "service-register.xlsx");
        }

        var pageNum = page is null or < 1 ? 1 : page.Value;
        var size = pageSize is null or < 1 or > 200 ? 50 : pageSize.Value;
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.s.Id).Skip((pageNum - 1) * size).Take(size).ToListAsync(ct);
        var items = rows.Select(x => Map(x.s, x.Party, x.TechName)).ToList();
        return TypedResults.Ok(new PagedResult<ServiceRegisterRow>(items, pageNum, size, total));
    }

    // ---------------------------------------------------------------- daily summary

    private static async Task<Ok<DailySummaryDto>> DailySummaryAsync(
        AppDbContext db, DateTime? date, CancellationToken ct)
    {
        var day = (date ?? DateTime.UtcNow).Date;
        var next = day.AddDays(1);

        // Faithful to the legacy page: the whole summary is scoped to jobs RECEIVED on the chosen day.
        var jobsQ = from s in db.Services.AsNoTracking()
                    where !s.IsDeleted && s.DateReceived >= day && s.DateReceived < next
                    join u in db.Users on s.TechnicianId equals u.Id into ug
                    from u in ug.DefaultIfEmpty()
                    select new { s.Id, s.ServiceStatus, s.PiNo, s.OutwardDcNo, s.PaymentStatus, s.Description, TechName = u != null ? u.Username : null };
        var jobs = await jobsQ.ToListAsync(ct);

        bool PreComplete(ServiceStatus st) => st is ServiceStatus.Inward or ServiceStatus.Assigned
            or ServiceStatus.Acknowledged or ServiceStatus.InService;
        bool NotDispatched(ServiceStatus st) => st is not (ServiceStatus.Dispatched or ServiceStatus.Stocked
            or ServiceStatus.Replaced or ServiceStatus.TotalLoss);

        var servicePending = jobs.Count(j => PreComplete(j.ServiceStatus));
        var piPending = jobs.Count(j => j.ServiceStatus == ServiceStatus.Completed
            && string.IsNullOrEmpty(j.PiNo) && string.IsNullOrEmpty(j.OutwardDcNo));
        var paymentPending = jobs.Count(j => !string.IsNullOrEmpty(j.PiNo) && j.PaymentStatus != PaymentStatus.Paid);
        var dispatchPending = jobs.Count(j => NotDispatched(j.ServiceStatus));

        // Dispatched-today within the received-today set (legacy semantics).
        var ids = jobs.Select(j => j.Id).ToHashSet();
        var dispatchedEvents = await db.ServiceStatusHistory.AsNoTracking()
            .Where(h => h.ToStatus == "Dispatched" && h.ChangedAt >= day && h.ChangedAt < next)
            .Select(h => h.ServiceId).ToListAsync(ct);
        var dispatchedToday = dispatchedEvents.Where(ids.Contains).Distinct().Count();

        var techs = jobs.Where(j => j.TechName != null)
            .GroupBy(j => j.TechName!)
            .Select(g => new DailyTechBreakdownRow(g.Key, g.Count(),
                g.GroupBy(j => string.IsNullOrWhiteSpace(j.Description) ? "Unknown item" : j.Description!.Trim())
                 .Select(ig => new DailyTechItemRow(ig.Key, ig.Count()))
                 .OrderByDescending(i => i.Count).ToList()))
            .OrderByDescending(t => t.Count).ToList();

        return TypedResults.Ok(new DailySummaryDto(
            day, jobs.Count, servicePending, piPending, paymentPending, dispatchPending, dispatchedToday, techs));
    }

    // ---------------------------------------------------------------- TAT analysis

    private static async Task<Ok<TatReportDto>> TatAsync(
        AppDbContext db, DateTime? from, DateTime? to, long? technicianId, string? customer, CancellationToken ct)
    {
        var toEx = to?.Date.AddDays(1);

        var jobsQ = from s in db.Services.AsNoTracking()
                    where !s.IsDeleted
                    join c in db.Customers on s.CustomerId equals c.Id into cg
                    from c in cg.DefaultIfEmpty()
                    join d in db.Dealers on s.DealerId equals d.Id into dg
                    from d in dg.DefaultIfEmpty()
                    join u in db.Users on s.TechnicianId equals u.Id into ug
                    from u in ug.DefaultIfEmpty()
                    select new { s.Id, s.ServiceNo, s.Description, s.DateReceived, s.TechnicianId,
                                 Party = c != null ? c.Name : (d != null ? d.Name : null), TechName = u != null ? u.Username : null };
        if (from is { } f) jobsQ = jobsQ.Where(x => x.DateReceived >= f.Date);
        if (toEx is { } t) jobsQ = jobsQ.Where(x => x.DateReceived < t);
        if (technicianId is { } tid) jobsQ = jobsQ.Where(x => x.TechnicianId == tid);
        if (!string.IsNullOrWhiteSpace(customer)) { var v = customer.Trim(); jobsQ = jobsQ.Where(x => x.Party != null && x.Party.Contains(v)); }
        var jobs = await jobsQ.OrderByDescending(x => x.Id).Take(2_000).ToListAsync(ct);
        if (jobs.Count == 0) return TypedResults.Ok(new TatReportDto(new(), new()));

        // First event per (job, status) — the legacy app parsed EDITLOGS text; we read real history rows.
        var minId = jobs.Min(x => x.Id);
        var events = await db.ServiceStatusHistory.AsNoTracking()
            .Where(h => h.ServiceId >= minId
                && (h.ToStatus == "InService" || h.ToStatus == "Completed" || h.ToStatus == "Dispatched"))
            .Select(h => new { h.ServiceId, h.ToStatus, h.ChangedAt })
            .ToListAsync(ct);
        var firstEvent = events.GroupBy(e => (e.ServiceId, e.ToStatus))
            .ToDictionary(g => g.Key, g => g.Min(e => e.ChangedAt));

        var rows = new List<TatJobRow>();
        foreach (var j in jobs)
        {
            DateTime? Ev(string st) => firstEvent.TryGetValue((j.Id, st), out var v) ? v : null;
            var started = Ev("InService");
            var completedAt = Ev("Completed");
            var dispatched = Ev("Dispatched");

            static double? Leg(DateTime? a, DateTime? b)
                => a is { } x && b is { } y && y >= x ? Math.Round((y - x).TotalHours, 1) : null;

            var r2d = Leg(j.DateReceived, dispatched);
            var r2c = Leg(j.DateReceived, completedAt);
            var s2c = Leg(started, completedAt);
            var c2d = Leg(completedAt, dispatched);
            if (r2d is null && r2c is null && s2c is null && c2d is null) continue;

            rows.Add(new TatJobRow(j.Id, j.ServiceNo, j.Description, j.Party, j.TechName,
                j.DateReceived, started, completedAt, dispatched, r2d, r2c, s2c, c2d));
        }

        var legs = new List<TatLegStat>();
        void AddLeg(string key, string label, Func<TatJobRow, double?> pick)
        {
            var vals = rows.Select(pick).Where(v => v.HasValue).Select(v => v!.Value).ToList();
            if (vals.Count > 0)
                legs.Add(new TatLegStat(key, label, vals.Count,
                    Math.Round(vals.Average(), 1), vals.Min(), vals.Max()));
            else legs.Add(new TatLegStat(key, label, 0, 0, 0, 0));
        }
        AddLeg("received_to_dispatch", "Received to dispatch", r => r.ReceivedToDispatchHours);
        AddLeg("received_to_completion", "Received to completion", r => r.ReceivedToCompletionHours);
        AddLeg("started_to_completed", "Started to completed", r => r.StartedToCompletedHours);
        AddLeg("completed_to_dispatch", "Completed to dispatch", r => r.CompletedToDispatchHours);

        return TypedResults.Ok(new TatReportDto(legs, rows));
    }

    // ---------------------------------------------------------------- helpers

    private const string XlsxMime = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private static bool IsXlsx(string? format) => string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase);

    private static async Task<Dictionary<long, string>> TechNamesAsync(AppDbContext db, CancellationToken ct)
        => await db.Users.AsNoTracking().Select(u => new { u.Id, u.Username }).ToDictionaryAsync(x => x.Id, x => x.Username, ct);
}
