using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Stock;

using PSR.Service.Api.Common;

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
            var party = await ResolveCustomerAsync(db, req.CustomerId, req.CustomerName,
                req.OrganizationName, req.Phone, req.Email, req.Address, ct, audit, uid, http.GetIp());
            if (party.CustomerId is not { } customerId)
            {
                await tx.RollbackAsync(ct);
                return TypedResults.BadRequest(party.Error ?? "Customer not found.");
            }

            var no = await seq.NextAsync(SequenceKeys.Service, ct);
            job = new ServiceJob
            {
                ServiceNo = no,
                ChallanNo = req.ChallanNo?.Trim(),
                CustomerType = req.CustomerType?.Trim(),
                CustomerId = customerId,
                DealerId = req.DealerId,
                SerialNo = req.SerialNo.Trim(),
                PsCode = req.PsCode?.Trim(),
                ModelName = req.ModelName?.Trim(),
                Description = req.Description?.Trim(),
                ReportedProblem = req.ReportedProblem?.Trim(),
                WarrantyStatus = warranty,
                InwardDcNo = req.InwardDcNo?.Trim(),
                // Through BusinessDate, not used raw: a client that sends a zone-bearing date
                // arrives here as the previous evening. See ShopClock.BusinessDate.
                DateReceived = ShopClock.BusinessDate(req.DateReceived) ?? ShopClock.Today,
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
        var received = ShopClock.BusinessDate(req.DateReceived) ?? ShopClock.Today;
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
                var party = await ResolveCustomerAsync(db, req.CustomerId, req.CustomerName,
                    req.OrganizationName, req.Phone, null, req.Address, ct, audit, uid, http.GetIp());
                if (party.CustomerId is null)
                {
                    await tx.RollbackAsync(ct);
                    return TypedResults.BadRequest(party.Error ?? "Customer not found.");
                }
                customerId = party.CustomerId;
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
            j.Id, j.ServiceNo, j.ChallanNo, j.InwardDcNo, j.CustomerId, j.DealerId, customerName, j.SerialNo, j.PsCode, j.ModelName, j.Description,
            j.ServiceStatus.ToString(), j.AckStatus.ToString(), j.PaymentStatus.ToString(),
            j.Priority.ToString(), j.WarrantyStatus.ToString(), j.TechnicianId, null, j.DateReceived, j.PromisedDate,
            j.PiNo, j.InvNo, j.OutwardDcNo)).ToList();
        return TypedResults.Created($"/services?challan={req.ChallanNo}", new InwardBatchResult(req.ChallanNo, created.Count, jobs));
    }

    /// <summary>Match an existing customer by name or create one. The create is audited — inward is the only
    /// path that adds customers implicitly, so without this a customer master row appears from nowhere.</summary>
    /// <summary>Outcome of resolving the direct customer a job or sale is billed to.
    ///
    /// A reason rather than a bare null, because the two ways this fails need different words at the
    /// counter: "nothing to go on" is the caller's own message, while "that name belongs to a dealer"
    /// has to name the dealer and say what to do instead.</summary>
    internal readonly record struct CustomerResolution(long? CustomerId, string? Error)
    {
        /// <summary>Nothing identified a customer. The caller supplies its own wording.</summary>
        public static readonly CustomerResolution Missing = new(null, null);

        public static CustomerResolution Found(long id) => new(id, null);
        public static CustomerResolution Refused(string error) => new(null, error);
    }

    /// <summary>Match a customer by id, else by exact active name, else create one. Shared with the
    /// spare-sales module so a walk-in typed at the counter lands in the same master as one typed at the
    /// inward desk.
    ///
    /// Refuses outright when the name is already on the dealer list. One business must be one party
    /// record: a document bills a single (CustomerId, DealerId) pair, so the moment the same firm exists
    /// as both a dealer and a direct customer its jobs split across two records and no PI can cover them
    /// together. That is not hypothetical — it happened, the shop spent an hour retrying a document that
    /// could never be raised, and the duplicate had been created here weeks earlier by someone typing a
    /// dealer's name into the customer box.
    ///
    /// The check covers the id path as well as the typed one. Refusing only new names would leave any
    /// shadow record already in the master usable, and picking it from the list would keep splitting the
    /// same firm's jobs — which is exactly how the second one goes unnoticed.</summary>
    internal static async Task<CustomerResolution> ResolveCustomerAsync(AppDbContext db, long? customerId, string? customerName,
        string? org, string? phone, string? email, string? address, CancellationToken ct,
        IAuditService? audit = null, long uid = 0, string? ip = null, string origin = "inward")
    {
        // The name this resolves to, whichever way the caller identified it. Needed before anything is
        // matched or created, because the dealer check below is on the name.
        string name;
        if (customerId is { } cid)
        {
            var picked = await db.Customers.Where(c => c.Id == cid).Select(c => c.Name).FirstOrDefaultAsync(ct);
            if (picked is null) return CustomerResolution.Missing;
            name = picked;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(customerName)) return CustomerResolution.Missing;
            name = customerName.Trim();
        }

        var dealer = await db.Dealers.Where(d => d.Name == name && d.IsActive)
            .Select(d => d.Name).FirstOrDefaultAsync(ct);
        if (dealer is not null)
            return CustomerResolution.Refused(
                $"“{dealer}” is on the dealer list. Book this to the dealer rather than as a direct "
                + "customer — entered both ways, the same firm's jobs land on two different party records "
                + "and cannot go on one PI or invoice.");

        if (customerId is { } okId) return CustomerResolution.Found(okId);

        var existing = await db.Customers.FirstOrDefaultAsync(c => c.Name == name && c.IsActive, ct);
        if (existing is not null) return CustomerResolution.Found(existing.Id);

        var created = new Customer
        {
            Name = name, OrganizationName = org?.Trim(),
            Phone = phone?.Trim(), Email = email?.Trim(), Address = address?.Trim(),
        };
        db.Customers.Add(created);
        await db.SaveChangesAsync(ct);
        audit?.Log(uid, "customer.create", "customer", created.Id, details: $"auto-created at {origin}: {name}", ip: ip);
        return CustomerResolution.Found(created.Id);
    }
}
