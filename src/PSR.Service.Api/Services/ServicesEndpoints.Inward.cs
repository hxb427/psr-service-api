using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Stock;

namespace PSR.Service.Api.Services;

public static partial class ServicesEndpoints
{
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
                req.OrganizationName, req.Phone, req.Email, req.Address, ct, audit, uid, http.GetIp());
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
                    req.OrganizationName, req.Phone, null, req.Address, ct, audit, uid, http.GetIp());
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
            j.Priority.ToString(), j.WarrantyStatus.ToString(), j.TechnicianId, null, j.DateReceived, j.PromisedDate,
            j.PiNo, j.InvNo, j.OutwardDcNo)).ToList();
        return TypedResults.Created($"/services?challan={req.ChallanNo}", new InwardBatchResult(req.ChallanNo, created.Count, jobs));
    }

    /// <summary>Match an existing customer by name or create one. The create is audited — inward is the only
    /// path that adds customers implicitly, so without this a customer master row appears from nowhere.</summary>
    private static async Task<long?> ResolveCustomerAsync(AppDbContext db, long? customerId, string? customerName,
        string? org, string? phone, string? email, string? address, CancellationToken ct,
        IAuditService? audit = null, long uid = 0, string? ip = null)
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
        audit?.Log(uid, "customer.create", "customer", created.Id, details: $"auto-created at inward: {name}", ip: ip);
        return created.Id;
    }
}
