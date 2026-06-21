using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Common;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Stock;

namespace PSR.Service.Api.Services;

public static class ServicesEndpoints
{
    private const int MaxPageSize = 200;

    public static IEndpointRouteBuilder MapServiceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/services").WithTags("services").RequireAuthorization();

        group.MapGet("/", ListAsync);
        group.MapGet("/technicians", TechniciansAsync).RequireAuthorization("ServiceAssign");
        group.MapGet("/{id:long}", GetAsync);
        group.MapPost("/", CreateAsync).RequireAuthorization("InwardManage");
        group.MapPost("/inward-batch", InwardBatchAsync).RequireAuthorization("InwardManage");
        group.MapPost("/{id:long}/assign", AssignAsync).RequireAuthorization("ServiceAssign");
        group.MapPost("/{id:long}/acknowledge", AcknowledgeAsync);   // assigned technician only
        group.MapPost("/{id:long}/start", StartAsync);               // assigned technician only
        group.MapPost("/{id:long}/lines", AddLineAsync);
        group.MapDelete("/{id:long}/lines/{lineId:long}", DeleteLineAsync);
        group.MapPost("/{id:long}/total-loss", MarkTotalLossAsync);
        group.MapPost("/{id:long}/complete", CompleteAsync);
        group.MapPost("/{id:long}/revert", RevertAsync).RequireAuthorization("ServiceManage");
        group.MapPost("/{id:long}/dispatch", DispatchAsync).RequireAuthorization("DispatchManage");
        group.MapPost("/{id:long}/stock", StockJobAsync).RequireAuthorization("DispatchManage");
        group.MapPost("/{id:long}/replace", ReplaceAsync).RequireAuthorization("DispatchManage");
        group.MapPost("/{id:long}/total-loss-close", LeaveTotalLossAsync).RequireAuthorization("DispatchManage");
        group.MapPost("/{id:long}/replacement-reject", RejectReplacementAsync).RequireAuthorization("DispatchManage");
        group.MapPost("/{id:long}/payment", PaymentAsync).RequireAuthorization("PaymentManage");
        group.MapDelete("/{id:long}", SoftDeleteAsync).RequireAuthorization("ServiceDelete");

        return app;
    }

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

    // ---------------------------------------------------------------- create (inward)

    private static async Task<Results<Created<ServiceDetailDto>, BadRequest<string>>> CreateAsync(
        [FromBody] CreateServiceRequest req, ClaimsPrincipal user, AppDbContext db,
        NumberSequenceService seq, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.SerialNo))
            return TypedResults.BadRequest("Serial number is required.");
        if (req.CustomerId is null && string.IsNullOrWhiteSpace(req.CustomerName))
            return TypedResults.BadRequest("Provide an existing customerId or a customerName to create.");
        if (req.DealerId is { } did && !await db.Dealers.AnyAsync(d => d.Id == did, ct))
            return TypedResults.BadRequest("Dealer not found.");

        Enum.TryParse<WarrantyStatus>(req.WarrantyStatus, true, out var warranty);
        var priority = Priority.Normal;
        if (!string.IsNullOrWhiteSpace(req.Priority)) Enum.TryParse(req.Priority, true, out priority);

        user.TryGetUserId(out var uid);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        ServiceJob job;
        try
        {
            var customerId = await ResolveCustomerAsync(db, req.CustomerId, req.CustomerName,
                req.OrganizationName, req.Phone, req.Email, req.Address, ct);
            if (customerId is null) { await tx.RollbackAsync(ct); return TypedResults.BadRequest("Customer not found."); }

            var no = await seq.NextAsync(SequenceKeys.Service, ct);
            job = new ServiceJob
            {
                ServiceNo = no,
                ChallanNo = req.ChallanNo?.Trim(),
                CustomerType = req.CustomerType?.Trim(),
                CustomerId = customerId.Value,
                DealerId = req.DealerId,
                SerialNo = req.SerialNo.Trim(),
                PsCode = req.PsCode?.Trim(),
                ModelName = req.ModelName?.Trim(),
                Description = req.Description?.Trim(),
                ReportedProblem = req.ReportedProblem?.Trim(),
                WarrantyStatus = warranty,
                InwardDcNo = req.InwardDcNo?.Trim(),
                DateReceived = req.DateReceived ?? DateTime.UtcNow,
                Priority = priority,
                ServiceStatus = ServiceStatus.Inward,
                AckStatus = AckStatus.Pending,
                CreatedByUserId = uid,
            };
            db.Services.Add(job);
            await db.SaveChangesAsync(ct);

            db.ServiceStatusHistory.Add(new ServiceStatusHistory
            {
                ServiceId = job.Id, FromStatus = null, ToStatus = ServiceStatus.Inward.ToString(),
                ChangedByUserId = uid, Note = "Inward created",
            });
            audit.Log(uid, "service.create", "service", job.Id, details: no, ip: http.GetIp());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        return TypedResults.Created($"/services/{job.Id}", await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

    private static async Task<Results<Created<InwardBatchResult>, BadRequest<string>>> InwardBatchAsync(
        [FromBody] InwardBatchRequest req, ClaimsPrincipal user, AppDbContext db,
        NumberSequenceService seq, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        if (req.Items is null || req.Items.Count == 0)
            return TypedResults.BadRequest("Add at least one item.");
        if (req.Items.Any(i => string.IsNullOrWhiteSpace(i.SerialNo)))
            return TypedResults.BadRequest("Every item needs a serial number.");

        // Party is a dealer (from the dealers list) or a direct customer, per the customer-type toggle.
        var dealerMode = string.Equals(req.CustomerType?.Trim(), "Dealer", StringComparison.OrdinalIgnoreCase);
        if (dealerMode)
        {
            if (req.DealerId is not { } did || !await db.Dealers.AnyAsync(d => d.Id == did, ct))
                return TypedResults.BadRequest("Select a dealer from the list.");
        }
        else if (req.CustomerId is null && string.IsNullOrWhiteSpace(req.CustomerName))
            return TypedResults.BadRequest("Enter the customer details.");

        var priority = Priority.Normal;
        if (!string.IsNullOrWhiteSpace(req.Priority)) Enum.TryParse(req.Priority, true, out priority);
        var received = req.DateReceived ?? DateTime.UtcNow;
        user.TryGetUserId(out var uid);

        var created = new List<ServiceJob>();
        string? customerName;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            long? customerId = null;
            long? dealerId = null;
            if (dealerMode)
            {
                dealerId = req.DealerId;
                customerName = await db.Dealers.Where(d => d.Id == dealerId).Select(d => d.Name).FirstAsync(ct);
            }
            else
            {
                customerId = await ResolveCustomerAsync(db, req.CustomerId, req.CustomerName,
                    req.OrganizationName, req.Phone, null, req.Address, ct);
                if (customerId is null) { await tx.RollbackAsync(ct); return TypedResults.BadRequest("Customer not found."); }
                customerName = await db.Customers.Where(c => c.Id == customerId).Select(c => c.Name).FirstAsync(ct);
            }

            foreach (var item in req.Items)
            {
                Enum.TryParse<WarrantyStatus>(item.WarrantyStatus, true, out var warranty);
                var no = await seq.NextAsync(SequenceKeys.Service, ct);
                var job = new ServiceJob
                {
                    ServiceNo = no, ChallanNo = req.ChallanNo?.Trim(), CustomerType = req.CustomerType?.Trim(),
                    CustomerId = customerId, DealerId = dealerId, SerialNo = item.SerialNo.Trim(),
                    PsCode = item.PsCode?.Trim(), ModelName = item.ModelName?.Trim(), Description = item.Description?.Trim(),
                    ReportedProblem = item.ReportedProblem?.Trim(), WarrantyStatus = warranty,
                    InwardDcNo = req.InwardDcNo?.Trim(), DateReceived = received, Priority = priority,
                    ServiceStatus = ServiceStatus.Inward, AckStatus = AckStatus.Pending, CreatedByUserId = uid,
                };
                db.Services.Add(job);
                created.Add(job);
            }
            await db.SaveChangesAsync(ct);   // assign Ids

            foreach (var job in created)
                db.ServiceStatusHistory.Add(new ServiceStatusHistory
                {
                    ServiceId = job.Id, FromStatus = null, ToStatus = ServiceStatus.Inward.ToString(),
                    ChangedByUserId = uid, Note = "Inward created (batch)",
                });
            audit.Log(uid, "service.inward-batch", "service", null,
                details: $"{created.Count} item(s), challan {req.ChallanNo}", ip: http.GetIp());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        var jobs = created.Select(j => new ServiceListItemDto(
            j.Id, j.ServiceNo, j.ChallanNo, j.InwardDcNo, j.CustomerId, customerName, j.SerialNo, j.PsCode, j.ModelName, j.Description,
            j.ServiceStatus.ToString(), j.AckStatus.ToString(), j.PaymentStatus.ToString(),
            j.Priority.ToString(), j.WarrantyStatus.ToString(), j.TechnicianId, null, j.DateReceived, j.PromisedDate)).ToList();
        return TypedResults.Created($"/services?challan={req.ChallanNo}", new InwardBatchResult(req.ChallanNo, created.Count, jobs));
    }

    private static async Task<long?> ResolveCustomerAsync(AppDbContext db, long? customerId, string? customerName,
        string? org, string? phone, string? email, string? address, CancellationToken ct)
    {
        if (customerId is { } cid)
            return await db.Customers.AnyAsync(c => c.Id == cid, ct) ? cid : null;
        if (string.IsNullOrWhiteSpace(customerName)) return null;

        var name = customerName.Trim();
        var existing = await db.Customers.FirstOrDefaultAsync(c => c.Name == name && c.IsActive, ct);
        if (existing is not null) return existing.Id;

        var created = new Customer
        {
            Name = name, OrganizationName = org?.Trim(),
            Phone = phone?.Trim(), Email = email?.Trim(), Address = address?.Trim(),
        };
        db.Customers.Add(created);
        await db.SaveChangesAsync(ct);
        return created.Id;
    }

    // ---------------------------------------------------------------- assignment + acknowledgement

    // Statuses from "completed" onward — payment / documents only apply here.
    private static readonly ServiceStatus[] CompletedOrLater =
    {
        ServiceStatus.Completed, ServiceStatus.ReplacementApprovalPending,
        ServiceStatus.Dispatched, ServiceStatus.Stocked, ServiceStatus.Replaced, ServiceStatus.TotalLoss,
    };

    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>>> AssignAsync(
        long id, [FromBody] AssignRequest req, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();
        // Assign at inward; re-assign is allowed only while still Assigned (before the technician acknowledges).
        if (job.ServiceStatus is not (ServiceStatus.Inward or ServiceStatus.Assigned))
            return TypedResults.BadRequest($"A technician can only be (re)assigned before acknowledgement (currently {job.ServiceStatus}).");

        var tech = await db.Users.FirstOrDefaultAsync(u => u.Id == req.TechnicianId, ct);
        if (tech is null || !tech.IsActive) return TypedResults.BadRequest("Technician not found or inactive.");
        if (!await UserHasRoleAsync(db, req.TechnicianId, RoleNames.Technician, ct))
            return TypedResults.BadRequest("Selected user is not a technician.");

        user.TryGetUserId(out var uid);
        var reassign = job.ServiceStatus == ServiceStatus.Assigned;
        job.TechnicianId = req.TechnicianId;
        if (!string.IsNullOrWhiteSpace(req.Priority) && Enum.TryParse<Priority>(req.Priority, true, out var pr))
            job.Priority = pr;
        if (req.PromisedDate is { } pd) job.PromisedDate = pd;
        WriteTransition(db, job, ServiceStatus.Assigned, uid, $"{(reassign ? "Re-assigned" : "Assigned")} to {tech.Username}");
        audit.Log(uid, reassign ? "service.reassign" : "service.assign", "service", job.Id, details: tech.Username, ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>, ForbidHttpResult>> AcknowledgeAsync(
        long id, [FromBody] NoteRequest? req, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();
        if (!ServiceRoles.IsAssignedTechnician(user, job)) return TypedResults.Forbid();   // only the assigned technician
        if (job.ServiceStatus is not ServiceStatus.Assigned)
            return TypedResults.BadRequest($"Only an assigned job can be acknowledged (currently {job.ServiceStatus}).");

        // Acknowledge = the technician confirms receipt; it does NOT start the work (that's /start).
        user.TryGetUserId(out var uid);
        job.AckStatus = AckStatus.Acknowledged;
        WriteTransition(db, job, ServiceStatus.Acknowledged, uid, req?.Note ?? "Received by technician");
        audit.Log(uid, "service.acknowledge", "service", job.Id, ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>, ForbidHttpResult>> StartAsync(
        long id, [FromBody] NoteRequest? req, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();
        if (!ServiceRoles.IsAssignedTechnician(user, job)) return TypedResults.Forbid();
        if (job.ServiceStatus is not ServiceStatus.Acknowledged)
            return TypedResults.BadRequest($"Acknowledge the job before starting service (currently {job.ServiceStatus}).");

        user.TryGetUserId(out var uid);
        WriteTransition(db, job, ServiceStatus.InService, uid, req?.Note ?? "Service started by technician");
        audit.Log(uid, "service.start", "service", job.Id, ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>, ForbidHttpResult>> MarkTotalLossAsync(
        long id, [FromBody] NoteRequest? req, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();
        if (!ServiceRoles.IsAssignedTechnician(user, job)) return TypedResults.Forbid();
        if (job.ServiceStatus is not ServiceStatus.InService)
            return TypedResults.BadRequest($"Total loss can only be set while the job is in service (currently {job.ServiceStatus}).");

        user.TryGetUserId(out var uid);
        job.IsTotalLoss = !job.IsTotalLoss;   // toggle
        job.RowVersion++;
        audit.Log(uid, "service.total-loss", "service", job.Id, details: job.IsTotalLoss ? "marked" : "cleared", ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>>> RevertAsync(
        long id, [FromBody] NoteRequest? req, ClaimsPrincipal user, AppDbContext db,
        StockLedgerService ledger, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var job = await db.Services.Include(s => s.Lines).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();
        if (job.ServiceStatus is not (ServiceStatus.Completed or ServiceStatus.ReplacementApprovalPending))
            return TypedResults.BadRequest($"Only a completed job can be reverted (currently {job.ServiceStatus}).");
        if (job.PaymentStatus != PaymentStatus.Pending)
            return TypedResults.BadRequest("Cannot revert — a payment has already been recorded.");
        // Phase 5: also block once a PI / Invoice / DC has been generated.

        user.TryGetUserId(out var uid);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Completion consumed the technician's parts — reverting returns them, so a re-complete
            // consumes once (not twice).
            if (job.TechnicianId is { } techId)
                foreach (var line in job.Lines.Where(l => l.PartId.HasValue
                    && l.LineType is ServiceLineType.Component or ServiceLineType.Replacement))
                    await ledger.ReverseConsumptionAsync(line.PartId!.Value, techId, line.Qty, uid, "SERVICE", job.Id, ct);

            WriteTransition(db, job, ServiceStatus.InService, uid, req?.Note ?? "Service reverted to in-service");
            audit.Log(uid, "service.revert", "service", job.Id, ip: http.GetIp());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>>> DispatchAsync(
        long id, [FromBody] DispatchRequest req, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.OutwardDcNo)) return TypedResults.BadRequest("Outward DC number is required.");
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();
        if (job.ServiceStatus is not ServiceStatus.Completed)
            return TypedResults.BadRequest($"Only a completed job can be dispatched (currently {job.ServiceStatus}).");

        user.TryGetUserId(out var uid);
        job.OutwardDcNo = req.OutwardDcNo.Trim();
        job.DcDate = req.DcDate ?? DateTime.UtcNow;
        WriteTransition(db, job, ServiceStatus.Dispatched, uid, $"Dispatched DC {job.OutwardDcNo}");
        audit.Log(uid, "service.dispatch", "service", job.Id, details: job.OutwardDcNo, ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

    private static Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>>> StockJobAsync(
        long id, [FromBody] NoteRequest? req, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
        => SimpleTransitionAsync(id, [ServiceStatus.Completed], ServiceStatus.Stocked, "service.stock",
            null, req?.Note, user, db, audit, http, ct);

    // Dispatch role overrides a total-loss call: send the job back to normal Completed (dispatchable).
    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>>> RejectReplacementAsync(
        long id, [FromBody] NoteRequest? req, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();
        if (job.ServiceStatus is not ServiceStatus.ReplacementApprovalPending)
            return TypedResults.BadRequest($"Only a replacement-pending job can be sent back to dispatch (currently {job.ServiceStatus}).");

        user.TryGetUserId(out var uid);
        job.IsTotalLoss = false;   // overridden — treat as a normal completed job
        WriteTransition(db, job, ServiceStatus.Completed, uid, req?.Note ?? "Replacement rejected — dispatch normally");
        audit.Log(uid, "service.replacement-reject", "service", job.Id, ip: http.GetIp());
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

    private static Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>>> LeaveTotalLossAsync(
        long id, [FromBody] NoteRequest? req, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
        => SimpleTransitionAsync(id, [ServiceStatus.ReplacementApprovalPending], ServiceStatus.TotalLoss, "service.discard",
            null, req?.Note ?? "Discarded — total loss, no replacement", user, db, audit, http, ct);

    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>>> PaymentAsync(
        long id, [FromBody] PaymentRequest req, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        if (!Enum.TryParse<PaymentStatus>(req.Status, true, out var ps))
            return TypedResults.BadRequest($"Unknown payment status '{req.Status}'.");
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();
        if (!CompletedOrLater.Contains(job.ServiceStatus))
            return TypedResults.BadRequest("Payment can only be set once the service is completed.");

        user.TryGetUserId(out var uid);
        job.PaymentStatus = ps;
        job.RowVersion++;
        audit.Log(uid, "service.payment", "service", job.Id, details: ps.ToString(), ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

    private static async Task<Results<NoContent, NotFound>> SoftDeleteAsync(
        long id, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();

        user.TryGetUserId(out var uid);
        job.IsDeleted = true;
        job.RowVersion++;
        audit.Log(uid, "service.delete", "service", job.Id, details: job.ServiceNo, ip: http.GetIp());
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    // ---------------------------------------------------------------- lines

    private static async Task<Results<Ok<ServiceLineDto>, NotFound, BadRequest<string>, ForbidHttpResult>> AddLineAsync(
        long id, [FromBody] AddLineRequest req, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();
        if (!ServiceRoles.IsAssignedTechnician(user, job)) return TypedResults.Forbid();
        if (job.ServiceStatus is not ServiceStatus.InService)
            return TypedResults.BadRequest($"Lines can only be added while the job is in service (currently {job.ServiceStatus}).");
        if (job.IsTotalLoss)
            return TypedResults.BadRequest("This job is marked total loss — components/charges cannot be added.");
        if (!Enum.TryParse<ServiceLineType>(req.LineType, true, out var lineType))
            return TypedResults.BadRequest($"Unknown line type '{req.LineType}'.");
        var qty = req.Qty < 1 ? 1 : req.Qty;

        var line = new ServiceLine { ServiceId = job.Id, LineType = lineType, Qty = qty, Description = req.Description?.Trim() };

        if (lineType is ServiceLineType.Component or ServiceLineType.Replacement)
        {
            if (req.PartId is not { } pid) return TypedResults.BadRequest("A part is required for a component/replacement line.");
            var part = await db.Parts.FirstOrDefaultAsync(p => p.Id == pid, ct);
            if (part is null) return TypedResults.BadRequest("Part not found.");
            line.PartId = part.Id;
            line.UnitPrice = part.CustomerRate;                 // server-set; technicians never send price
            if (lineType is ServiceLineType.Replacement) line.ReplacementSerialNo = req.ReplacementSerialNo?.Trim();
        }
        else // ServiceCharge
        {
            if (req.ServiceChargeId is not { } scid) return TypedResults.BadRequest("A service charge is required for a service-charge line.");
            var sc = await db.ServiceCharges.FirstOrDefaultAsync(s => s.Id == scid, ct);
            if (sc is null) return TypedResults.BadRequest("Service charge not found.");
            line.ServiceChargeId = sc.Id;
            line.UnitPrice = sc.Charge;
            line.Description ??= sc.Name;
        }
        line.Amount = line.UnitPrice * qty;

        user.TryGetUserId(out var uid);
        db.ServiceLines.Add(line);
        job.RowVersion++;
        audit.Log(uid, "service.line.add", "service", job.Id, details: lineType.ToString(), ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(await LineToDtoAsync(db, line.Id, ServiceRoles.CanSeePricing(user), ct));
    }

    private static async Task<Results<NoContent, NotFound, BadRequest<string>, ForbidHttpResult>> DeleteLineAsync(
        long id, long lineId, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();
        if (!ServiceRoles.IsAssignedTechnician(user, job)) return TypedResults.Forbid();
        if (job.ServiceStatus is not ServiceStatus.InService)
            return TypedResults.BadRequest($"Lines can only be removed while the job is in service (currently {job.ServiceStatus}).");

        var line = await db.ServiceLines.FirstOrDefaultAsync(l => l.Id == lineId && l.ServiceId == id, ct);
        if (line is null) return TypedResults.NotFound();

        user.TryGetUserId(out var uid);
        db.ServiceLines.Remove(line);
        job.RowVersion++;
        audit.Log(uid, "service.line.delete", "service", job.Id, details: $"line {lineId}", ip: http.GetIp());
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    // ---------------------------------------------------------------- complete (consumes technician stock)

    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>, ForbidHttpResult>> CompleteAsync(
        long id, [FromBody] CompleteRequest? req, ClaimsPrincipal user, AppDbContext db,
        StockLedgerService ledger, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var job = await db.Services.Include(s => s.Lines).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();
        if (!ServiceRoles.IsAssignedTechnician(user, job)) return TypedResults.Forbid();
        if (job.ServiceStatus is not ServiceStatus.InService)
            return TypedResults.BadRequest($"Only an in-service job can be completed (currently {job.ServiceStatus}).");
        if (job.TechnicianId is not { } techId)
            return TypedResults.BadRequest("Assign a technician before completing the job.");

        user.TryGetUserId(out var uid);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Consume the technician's issued stock for each part-bearing line.
            foreach (var line in job.Lines.Where(l => l.PartId.HasValue
                && l.LineType is ServiceLineType.Component or ServiceLineType.Replacement))
                await ledger.ConsumeAsync(line.PartId!.Value, techId, line.Qty, uid, "SERVICE", job.Id, ct);

            if (req?.TechnicianRemarks is { } remarks) job.TechnicianRemarks = remarks.Trim();
            // A total-loss job routes to replacement-approval instead of plain pending-dispatch.
            var to = job.IsTotalLoss ? ServiceStatus.ReplacementApprovalPending : ServiceStatus.Completed;
            WriteTransition(db, job, to, uid, job.IsTotalLoss ? "Completed — total loss, replacement pending" : "Service completed");
            audit.Log(uid, "service.complete", "service", job.Id, details: job.IsTotalLoss ? "total-loss" : null, ip: http.GetIp());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

    // ---------------------------------------------------------------- replace whole unit (decrements warehouse)

    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>>> ReplaceAsync(
        long id, [FromBody] ReplaceRequest req, ClaimsPrincipal user, AppDbContext db,
        StockLedgerService ledger, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ReplacementSerialNo))
            return TypedResults.BadRequest("Replacement serial number is required.");
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();
        if (job.ServiceStatus is not ServiceStatus.ReplacementApprovalPending)
            return TypedResults.BadRequest($"A replacement can only be issued for a total-loss job awaiting replacement (currently {job.ServiceStatus}).");

        var qty = req.Qty < 1 ? 1 : req.Qty;
        Part? part = null;
        if (req.ReplacementPartId is { } pid)
        {
            part = await db.Parts.FirstOrDefaultAsync(p => p.Id == pid, ct);
            if (part is null) return TypedResults.BadRequest("Replacement part not found.");
        }

        user.TryGetUserId(out var uid);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Ship the replacement unit out of the warehouse only when it maps to a catalog part.
            if (part is not null)
                await ledger.ReplacementOutAsync(part.Id, qty, uid, job.Id,
                    req.ReplacementSerialNo.Trim(), $"Replacement for service {job.ServiceNo}", ct);

            job.ReplacementSerialNo = req.ReplacementSerialNo.Trim();
            job.ReplacementPartId = part?.Id;
            WriteTransition(db, job, ServiceStatus.Replaced, uid,
                req.Note ?? $"Unit replaced (SN {job.ReplacementSerialNo})");
            audit.Log(uid, "service.replace", "service", job.Id, details: job.ReplacementSerialNo, ip: http.GetIp());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

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
            job.ReportedProblem, job.WarrantyStatus.ToString(), job.InwardDcNo, job.OutwardDcNo, job.DcDate,
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
