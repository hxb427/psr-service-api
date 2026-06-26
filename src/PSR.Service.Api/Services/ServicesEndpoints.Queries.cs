using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Common;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Services;

public static partial class ServicesEndpoints
{
    // ---------------------------------------------------------------- list / detail

    private static async Task<Ok<PagedResult<ServiceListItemDto>>> ListAsync(
        AppDbContext db, ClaimsPrincipal user,
        string? status, string? section, long? technicianId, string? search, DateTime? fromDate, DateTime? toDate,
        string? warranty, string? payment, string? sort, int? page, int? pageSize, CancellationToken ct)
    {
        var pageNum = page is null or < 1 ? 1 : page.Value;
        var size = pageSize is null or < 1 or > MaxPageSize ? 50 : pageSize.Value;

        var q = from s in db.Services.AsNoTracking()
                join c in db.Customers on s.CustomerId equals c.Id into cg
                from c in cg.DefaultIfEmpty()
                join d in db.Dealers on s.DealerId equals d.Id into dg
                from d in dg.DefaultIfEmpty()
                join u in db.Users on s.TechnicianId equals u.Id into ug
                from u in ug.DefaultIfEmpty()
                // Party is the direct customer, or the dealer when it's a dealer-type job.
                select new { s, CustomerName = c != null ? c.Name : (d != null ? d.Name : null), TechName = u != null ? u.Username : null };

        q = q.Where(x => !x.s.IsDeleted);

        // Technicians (without a supervisory role) see only jobs assigned to them.
        if (!ServiceRoles.CanManage(user) && ServiceRoles.IsTechnician(user) && user.TryGetUserId(out var myId))
            q = q.Where(x => x.s.TechnicianId == myId);

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<ServiceStatus>(status, true, out var st))
            q = q.Where(x => x.s.ServiceStatus == st);
        // Section = a named group of statuses (explicit ORs to avoid the EF Contains funcletizer bug).
        if (!string.IsNullOrWhiteSpace(section))
            q = section.ToLowerInvariant() switch
            {
                "inward" => q.Where(x => x.s.ServiceStatus == ServiceStatus.Inward
                    || x.s.ServiceStatus == ServiceStatus.Assigned || x.s.ServiceStatus == ServiceStatus.Acknowledged),
                "assigned" => q.Where(x => x.s.ServiceStatus == ServiceStatus.Assigned
                    || x.s.ServiceStatus == ServiceStatus.Acknowledged),
                "inservice" => q.Where(x => x.s.ServiceStatus == ServiceStatus.InService),
                "completed" => q.Where(x => x.s.ServiceStatus == ServiceStatus.Completed
                    || x.s.ServiceStatus == ServiceStatus.ReplacementApprovalPending),
                "closed" => q.Where(x => x.s.ServiceStatus == ServiceStatus.Dispatched
                    || x.s.ServiceStatus == ServiceStatus.Stocked || x.s.ServiceStatus == ServiceStatus.Replaced
                    || x.s.ServiceStatus == ServiceStatus.TotalLoss),
                "techdone" => q.Where(x => x.s.ServiceStatus == ServiceStatus.Completed
                    || x.s.ServiceStatus == ServiceStatus.ReplacementApprovalPending
                    || x.s.ServiceStatus == ServiceStatus.Dispatched || x.s.ServiceStatus == ServiceStatus.Stocked
                    || x.s.ServiceStatus == ServiceStatus.Replaced || x.s.ServiceStatus == ServiceStatus.TotalLoss),
                _ => q,
            };
        if (technicianId is { } tid and > 0)
            q = q.Where(x => x.s.TechnicianId == tid);
        if (technicianId is 0)   // explicit "unassigned" filter
            q = q.Where(x => x.s.TechnicianId == null);
        if (fromDate is { } fd) q = q.Where(x => x.s.DateReceived >= fd);
        if (toDate is { } td) q = q.Where(x => x.s.DateReceived < td.AddDays(1));
        if (!string.IsNullOrWhiteSpace(warranty) && Enum.TryParse<WarrantyStatus>(warranty, true, out var ws))
            q = q.Where(x => x.s.WarrantyStatus == ws);
        if (!string.IsNullOrWhiteSpace(payment) && Enum.TryParse<PaymentStatus>(payment, true, out var ps))
            q = q.Where(x => x.s.PaymentStatus == ps);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            q = q.Where(x => x.s.SerialNo.Contains(term) || x.s.ServiceNo.Contains(term)
                          || (x.s.ChallanNo != null && x.s.ChallanNo.Contains(term))
                          || (x.s.InwardDcNo != null && x.s.InwardDcNo.Contains(term))
                          || (x.s.OutwardDcNo != null && x.s.OutwardDcNo.Contains(term))
                          || (x.s.PsCode != null && x.s.PsCode.Contains(term))
                          || (x.s.Description != null && x.s.Description.Contains(term))
                          || (x.CustomerName != null && x.CustomerName.Contains(term)));
        }

        var ordered = sort switch
        {
            "arrived_asc" => q.OrderBy(x => x.s.DateReceived),
            "arrived_desc" => q.OrderByDescending(x => x.s.DateReceived),
            "assigned_asc" => q.OrderBy(x => x.s.PromisedDate),
            "assigned_desc" => q.OrderByDescending(x => x.s.PromisedDate),
            _ => q.OrderByDescending(x => x.s.Id),
        };
        var total = await q.CountAsync(ct);
        var rows = await ordered.Skip((pageNum - 1) * size).Take(size).ToListAsync(ct);

        var items = rows.Select(x => new ServiceListItemDto(
            x.s.Id, x.s.ServiceNo, x.s.ChallanNo, x.s.InwardDcNo, x.s.CustomerId, x.CustomerName, x.s.SerialNo, x.s.PsCode, x.s.ModelName, x.s.Description,
            x.s.ServiceStatus.ToString(), x.s.AckStatus.ToString(), x.s.PaymentStatus.ToString(),
            x.s.Priority.ToString(), x.s.WarrantyStatus.ToString(),
            x.s.TechnicianId, x.TechName, x.s.DateReceived, x.s.PromisedDate)).ToList();

        return TypedResults.Ok(new PagedResult<ServiceListItemDto>(items, pageNum, size, total));
    }

    private static async Task<Ok<ServiceSummaryDto>> SummaryAsync(AppDbContext db, ClaimsPrincipal user, CancellationToken ct)
    {
        // Technicians see only their own jobs / completions; everyone else sees all.
        var ownOnly = !ServiceRoles.CanManage(user) && ServiceRoles.IsTechnician(user);
        user.TryGetUserId(out var uid);

        var q = db.Services.AsNoTracking().Where(s => !s.IsDeleted);
        if (ownOnly) q = q.Where(s => s.TechnicianId == uid);

        // Explicit equality counts (reliable — no GroupBy translation risk).
        var inward = await q.CountAsync(s => s.ServiceStatus == ServiceStatus.Inward
            || s.ServiceStatus == ServiceStatus.Assigned || s.ServiceStatus == ServiceStatus.Acknowledged, ct);
        var inService = await q.CountAsync(s => s.ServiceStatus == ServiceStatus.InService, ct);
        var replPending = await q.CountAsync(s => s.ServiceStatus == ServiceStatus.ReplacementApprovalPending, ct);
        var pendingDispatch = await q.CountAsync(s => s.ServiceStatus == ServiceStatus.Completed, ct);
        var closed = await q.CountAsync(s => s.ServiceStatus == ServiceStatus.Dispatched
            || s.ServiceStatus == ServiceStatus.Stocked || s.ServiceStatus == ServiceStatus.Replaced
            || s.ServiceStatus == ServiceStatus.TotalLoss, ct);

        // "Serviced" = distinct jobs whose status reached Completed in the period (status-history is written
        // on every transition, so this is reliably stored). Timestamps are UTC.
        var now = DateTime.UtcNow;
        var dayStart = now.Date;
        var weekStart = now.Date.AddDays(-(int)now.DayOfWeek);
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var hist = db.ServiceStatusHistory.AsNoTracking().Where(h => h.ToStatus == "Completed");
        if (ownOnly) hist = hist.Where(h => h.ChangedByUserId == uid);
        var today = await hist.Where(h => h.ChangedAt >= dayStart).Select(h => h.ServiceId).Distinct().CountAsync(ct);
        var week = await hist.Where(h => h.ChangedAt >= weekStart).Select(h => h.ServiceId).Distinct().CountAsync(ct);
        var month = await hist.Where(h => h.ChangedAt >= monthStart).Select(h => h.ServiceId).Distinct().CountAsync(ct);

        var pendingReq = await db.StockRequests.AsNoTracking()
            .CountAsync(r => r.Status == StockRequestStatus.Pending || r.Status == StockRequestStatus.Partial, ct);

        return TypedResults.Ok(new ServiceSummaryDto(
            inward, inService, replPending, pendingDispatch, closed, today, week, month, pendingReq));
    }

    private static async Task<Ok<ServiceOverviewDto>> OverviewAsync(AppDbContext db, ClaimsPrincipal user, CancellationToken ct)
    {
        var ownOnly = !ServiceRoles.CanManage(user) && ServiceRoles.IsTechnician(user);
        user.TryGetUserId(out var uid);

        var now = DateTime.UtcNow;
        var thisStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var nextStart = thisStart.AddMonths(1);
        var lastStart = thisStart.AddMonths(-1);

        // Serviced = a job reached a terminal stage (Dispatched/Stocked/Replaced/TotalLoss) in the window.
        // TAT = days from the job's DateReceived to that terminal timestamp. All from stored data.
        async Task<(int count, double avgTat)> StatsAsync(DateTime start, DateTime end)
        {
            var rows = await (from h in db.ServiceStatusHistory.AsNoTracking()
                              join s in db.Services on h.ServiceId equals s.Id
                              where (h.ToStatus == "Dispatched" || h.ToStatus == "Stocked"
                                     || h.ToStatus == "Replaced" || h.ToStatus == "TotalLoss")
                                    && h.ChangedAt >= start && h.ChangedAt < end
                                    && (!ownOnly || s.TechnicianId == uid)
                              select new { h.ServiceId, h.ChangedAt, s.DateReceived }).ToListAsync(ct);

            // First terminal event per job; TAT clamped at >= 0.
            var perJob = rows.GroupBy(r => r.ServiceId)
                .Select(g => g.OrderBy(x => x.ChangedAt).First())
                .Select(x => Math.Max(0, (x.ChangedAt - x.DateReceived).TotalDays))
                .ToList();
            return (perJob.Count, perJob.Count > 0 ? Math.Round(perJob.Average(), 1) : 0.0);
        }

        var (tc, tt) = await StatsAsync(thisStart, nextStart);
        var (lc, lt) = await StatsAsync(lastStart, thisStart);

        return TypedResults.Ok(new ServiceOverviewDto(
            thisStart.ToString("MMM yyyy"), tc, tt,
            lastStart.ToString("MMM yyyy"), lc, lt));
    }

    private static async Task<Ok<List<TechnicianOptionDto>>> TechniciansAsync(AppDbContext db, CancellationToken ct)
    {
        // Role-scoped picker for assignment — avoids exposing the admin-only /users list to managers.
        var rows = await (from u in db.Users
                          join ur in db.UserRoles on u.Id equals ur.UserId
                          join r in db.Roles on ur.RoleId equals r.Id
                          where u.IsActive && r.Name == RoleNames.Technician
                          orderby u.Username
                          select new TechnicianOptionDto(u.Id, u.Username, u.FullName)).ToListAsync(ct);
        return TypedResults.Ok(rows);
    }

    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, ForbidHttpResult>> GetAsync(
        long id, AppDbContext db, ClaimsPrincipal user, CancellationToken ct)
    {
        var job = await db.Services.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();
        if (!ServiceRoles.CanProcess(user, job) && !ServiceRoles.CanSeePricing(user)
            && !user.IsInRole(RoleNames.InwardManager) && !user.IsInRole(RoleNames.DispatchManager)
            && !user.IsInRole(RoleNames.StoreManager) && !user.IsInRole(RoleNames.Accounts))
            return TypedResults.Forbid();

        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }
}
