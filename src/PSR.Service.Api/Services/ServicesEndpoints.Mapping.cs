using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Services;

public static partial class ServicesEndpoints
{
    // ---------------------------------------------------------------- helpers

    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>>> SimpleTransitionAsync(
        long id, ServiceStatus[] allowedFrom, ServiceStatus to, string auditAction, Action<ServiceJob>? mutate,
        string? note, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();
        if (!allowedFrom.Contains(job.ServiceStatus))
            return TypedResults.BadRequest($"Cannot move a {job.ServiceStatus} job to {to}.");

        user.TryGetUserId(out var uid);
        mutate?.Invoke(job);
        WriteTransition(db, job, to, uid, note);
        audit.Log(uid, auditAction, "service", job.Id, ip: http.GetIp());
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

    private static void WriteTransition(AppDbContext db, ServiceJob job, ServiceStatus to, long uid, string? note)
    {
        db.ServiceStatusHistory.Add(new ServiceStatusHistory
        {
            ServiceId = job.Id, FromStatus = job.ServiceStatus.ToString(), ToStatus = to.ToString(),
            ChangedByUserId = uid, Note = note,
        });
        job.ServiceStatus = to;
        job.RowVersion++;
    }

    private static async Task<bool> UserHasRoleAsync(AppDbContext db, long userId, string roleName, CancellationToken ct)
    {
        // Avoid Array.Contains in EF (the EF Core 9 + .NET 10 funcletizer bug) — single equality is fine.
        return await (from ur in db.UserRoles
                      join r in db.Roles on ur.RoleId equals r.Id
                      where ur.UserId == userId && r.Name == roleName
                      select ur.UserId).AnyAsync(ct);
    }

    private static async Task<ServiceDetailDto> BuildDetailAsync(AppDbContext db, ServiceJob job, bool pricing, CancellationToken ct)
    {
        var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == job.CustomerId, ct);
        string? dealerName = job.DealerId is { } did
            ? await db.Dealers.AsNoTracking().Where(d => d.Id == did).Select(d => d.Name).FirstOrDefaultAsync(ct) : null;
        string? techName = job.TechnicianId is { } tid
            ? await db.Users.AsNoTracking().Where(u => u.Id == tid).Select(u => u.Username).FirstOrDefaultAsync(ct) : null;
        string? replPartName = job.ReplacementPartId is { } rpid
            ? await db.Parts.AsNoTracking().Where(p => p.Id == rpid).Select(p => p.Name).FirstOrDefaultAsync(ct) : null;

        var lines = await (from l in db.ServiceLines.AsNoTracking()
                           where l.ServiceId == job.Id
                           join p in db.Parts on l.PartId equals p.Id into pg
                           from p in pg.DefaultIfEmpty()
                           join sc in db.ServiceCharges on l.ServiceChargeId equals sc.Id into scg
                           from sc in scg.DefaultIfEmpty()
                           orderby l.Id
                           select new { l, PartCode = p != null ? p.ItemCode : null, PartName = p != null ? p.Name : null,
                               ScName = sc != null ? sc.Name : null })
            .ToListAsync(ct);

        var lineDtos = lines.Select(x => new ServiceLineDto(
            x.l.Id, x.l.LineType.ToString(), x.l.PartId, x.PartCode, x.PartName,
            x.l.ServiceChargeId, x.ScName, x.l.Description, x.l.Qty,
            pricing ? x.l.UnitPrice : null, pricing ? x.l.Amount : null, x.l.ReplacementSerialNo)).ToList();

        decimal? total = pricing ? lines.Sum(x => x.l.Amount) : null;

        var history = await (from h in db.ServiceStatusHistory.AsNoTracking()
                             where h.ServiceId == job.Id
                             join u in db.Users on h.ChangedByUserId equals u.Id into ug
                             from u in ug.DefaultIfEmpty()
                             orderby h.Id
                             select new ServiceHistoryDto(h.Id, h.FromStatus, h.ToStatus, h.ChangedByUserId,
                                 u != null ? u.Username : null, h.Note, h.ChangedAt))
            .ToListAsync(ct);

        return new ServiceDetailDto(
            job.Id, job.ServiceNo, job.ChallanNo, job.CustomerType, job.CustomerId, customer?.Name, customer?.Phone,
            job.DealerId, dealerName, job.SerialNo, job.PsCode, job.ModelName, job.Description,
            job.ReportedProblem, job.WarrantyStatus.ToString(), job.InwardDcNo, job.OutwardDcNo, job.OutwardReferenceNo, job.DcDate,
            job.PiNo, job.InvNo,
            job.DateReceived, job.PromisedDate, job.TechnicianId, techName, job.Priority.ToString(), job.AckStatus.ToString(),
            job.ServiceStatus.ToString(), job.PaymentStatus.ToString(), job.TechnicianRemarks, job.IsTotalLoss,
            job.ReplacementSerialNo, job.ReplacementPartId, replPartName,
            total, job.RowVersion, lineDtos, history);
    }

    private static async Task<ServiceLineDto> LineToDtoAsync(AppDbContext db, long lineId, bool pricing, CancellationToken ct)
    {
        var x = await (from l in db.ServiceLines.AsNoTracking()
                       where l.Id == lineId
                       join p in db.Parts on l.PartId equals p.Id into pg
                       from p in pg.DefaultIfEmpty()
                       join sc in db.ServiceCharges on l.ServiceChargeId equals sc.Id into scg
                       from sc in scg.DefaultIfEmpty()
                       select new { l, PartCode = p != null ? p.ItemCode : null, PartName = p != null ? p.Name : null,
                           ScName = sc != null ? sc.Name : null })
            .FirstAsync(ct);
        return new ServiceLineDto(x.l.Id, x.l.LineType.ToString(), x.l.PartId, x.PartCode, x.PartName,
            x.l.ServiceChargeId, x.ScName, x.l.Description, x.l.Qty,
            pricing ? x.l.UnitPrice : null, pricing ? x.l.Amount : null, x.l.ReplacementSerialNo);
    }
}
