using System.Linq.Expressions;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Services;

/// <summary>
/// Bulk counterparts to the single-job workflow actions.
///
/// The desk routinely applies one action to a whole page of jobs. Doing that as N sequential POSTs
/// meant N independent chances for a lost packet to strand the operation — and one did: a bulk
/// acknowledge of 50 jobs left job 19 unacknowledged after its request never reached the server and
/// the client burned its full 30s timeout waiting. One request for the whole selection removes that
/// exposure, and cuts ~500 queries (each single call rebuilt a full detail DTO the grid discarded)
/// down to a couple of dozen.
///
/// Every action here is idempotent: a job already in the requested state counts as a success rather
/// than an error, so replaying a bulk call after a lost response is safe and reports honestly.
///
/// The per-job guard/mutate logic lives in the Apply* methods below and is shared with the
/// single-job handlers in ServicesEndpoints.Workflow.cs, so the two can never drift apart.
/// </summary>
public static partial class ServicesEndpoints
{
    /// <summary>Matches the list page size — a selection cannot exceed one page.</summary>
    private const int MaxBulkIds = 200;

    // ---------------------------------------------------------------- per-job apply results

    internal enum ApplyStatus { Applied, Forbidden, Invalid }

    /// <summary>Outcome of applying one action to one already-loaded job. <see cref="ApplyStatus.Applied"/>
    /// covers both "changed it" and "it was already in that state" — the callers treat them alike, which
    /// is what makes every bulk action safe to retry.</summary>
    internal readonly record struct ApplyResult(ApplyStatus Status, string? Error)
    {
        public static ApplyResult Applied => new(ApplyStatus.Applied, null);
        public static ApplyResult Forbidden(string message) => new(ApplyStatus.Forbidden, message);
        public static ApplyResult Invalid(string message) => new(ApplyStatus.Invalid, message);
    }

    /// <summary>Maps a per-job outcome onto the single-job endpoints' HTTP shape.</summary>
    private static Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>, ForbidHttpResult>? ToProblem(
        this ApplyResult result) => result.Status switch
        {
            ApplyStatus.Forbidden => TypedResults.Forbid(),
            ApplyStatus.Invalid => TypedResults.BadRequest(result.Error!),
            _ => null,
        };

    // ---------------------------------------------------------------- bulk plumbing

    /// <summary>Predicate selecting exactly the requested jobs.
    ///
    /// Built as an explicit OR-chain rather than <c>ids.Contains(s.Id)</c>: that hits the EF Core 9 +
    /// .NET 10 funcletizer bug this codebase works around everywhere else, and the alternative the
    /// other call sites use — materialize the table and filter in memory — is not an option for a
    /// table this size. Ids are longs read off a route-bound DTO, so inlining them as constants is
    /// safe; it costs query-plan reuse, which a bulk call makes no use of anyway.</summary>
    internal static Expression<Func<ServiceJob, bool>> BulkIdPredicate(IReadOnlyList<long> ids)
    {
        var param = Expression.Parameter(typeof(ServiceJob), "s");
        var idProperty = Expression.Property(param, nameof(ServiceJob.Id));
        Expression body = Expression.Equal(idProperty, Expression.Constant(ids[0]));
        for (var i = 1; i < ids.Count; i++)
            body = Expression.OrElse(body, Expression.Equal(idProperty, Expression.Constant(ids[i])));

        return Expression.Lambda<Func<ServiceJob, bool>>(body, param);
    }

    /// <summary>The selection query itself, exposed so a test can force the provider to translate it.</summary>
    internal static IQueryable<ServiceJob> BulkIdPredicateQuery(AppDbContext db, IReadOnlyList<long> ids) =>
        db.Services.Where(BulkIdPredicate(ids));

    /// <summary>Load a whole selection in one round trip.</summary>
    private static async Task<List<ServiceJob>> LoadForBulkAsync(
        AppDbContext db, IReadOnlyList<long> ids, CancellationToken ct)
        => ids.Count == 0 ? [] : await BulkIdPredicateQuery(db, ids).ToListAsync(ct);

    /// <summary>Apply one action across a selection and save once.
    ///
    /// Failures are per job and never abort the run: the desk gets back exactly which jobs moved and
    /// why the rest did not, which is the behaviour the old client-side loop had — minus the N round
    /// trips. The single SaveChanges at the end means the whole batch commits or none of it does, so a
    /// half-applied batch cannot survive a crash mid-loop.</summary>
    private static async Task<Results<Ok<BulkActionResultDto>, BadRequest<string>>> RunBulkAsync(
        long[] requestedIds, AppDbContext db, CancellationToken ct,
        Func<ServiceJob, Task<ApplyResult>> apply)
    {
        var ids = requestedIds.Distinct().ToList();
        if (ids.Count == 0) return TypedResults.BadRequest("No jobs selected.");
        if (ids.Count > MaxBulkIds)
            return TypedResults.BadRequest($"Select at most {MaxBulkIds} jobs at a time (got {ids.Count}).");

        var jobs = await LoadForBulkAsync(db, ids, ct);
        var byId = jobs.ToDictionary(j => j.Id);

        var succeeded = new List<long>();
        var failed = new List<BulkFailureDto>();

        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var job))
            {
                failed.Add(new BulkFailureDto(id, "", "Job not found."));
                continue;
            }

            var result = await apply(job);
            if (result.Status == ApplyStatus.Applied) succeeded.Add(id);
            else failed.Add(new BulkFailureDto(id, job.ServiceNo,
                result.Error ?? "You don't have permission for that."));
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // One SaveChanges for the batch means a job someone else edited mid-run takes the whole
            // batch down rather than just itself — the price of all-or-nothing, and the safer side of
            // the trade: nothing is half-applied. Say so plainly instead of returning a 500, because
            // the fix is simply to look again and repeat, and the actions are idempotent so repeating
            // is free.
            return TypedResults.BadRequest(
                "Someone else changed one of these jobs while this was running — nothing was applied. "
                + "Refresh and try again.");
        }

        return TypedResults.Ok(new BulkActionResultDto(succeeded, failed));
    }

    // ---------------------------------------------------------------- bulk handlers

    private static async Task<Results<Ok<BulkActionResultDto>, BadRequest<string>>> BulkAssignAsync(
        [FromBody] BulkAssignRequest req, ClaimsPrincipal user, AppDbContext db,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        // The technician is the same for the whole selection, so validate once here rather than per
        // job — checking it inside the loop would put back the N-queries this endpoint exists to remove.
        // A bad technician fails the request outright: it is wrong for every job, not for some of them.
        var tech = await db.Users.FirstOrDefaultAsync(u => u.Id == req.TechnicianId, ct);
        if (tech is null || !tech.IsActive) return TypedResults.BadRequest("Technician not found or inactive.");
        if (!await UserHasRoleAsync(db, req.TechnicianId, RoleNames.Technician, ct))
            return TypedResults.BadRequest("Selected user is not a technician.");

        user.TryGetUserId(out var uid);
        var inner = new AssignRequest(req.TechnicianId, req.Priority, req.PromisedDate);
        return await RunBulkAsync(req.Ids, db, ct, job =>
            Task.FromResult(ApplyAssign(job, inner, tech.Username, uid, db, audit, http.GetIp())));
    }

    private static Task<Results<Ok<BulkActionResultDto>, BadRequest<string>>> BulkAcknowledgeAsync(
        [FromBody] BulkNoteRequest req, ClaimsPrincipal user, AppDbContext db,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        user.TryGetUserId(out var uid);
        return RunBulkAsync(req.Ids, db, ct, job =>
            Task.FromResult(ApplyAcknowledge(job, user, req.Note, uid, db, audit, http.GetIp())));
    }

    private static Task<Results<Ok<BulkActionResultDto>, BadRequest<string>>> BulkStartAsync(
        [FromBody] BulkNoteRequest req, ClaimsPrincipal user, AppDbContext db,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        user.TryGetUserId(out var uid);
        return RunBulkAsync(req.Ids, db, ct, job =>
            Task.FromResult(ApplyStart(job, user, req.Note, uid, db, audit, http.GetIp())));
    }

    private static Task<Results<Ok<BulkActionResultDto>, BadRequest<string>>> BulkDispatchAsync(
        [FromBody] BulkDispatchRequest req, ClaimsPrincipal user, AppDbContext db,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        user.TryGetUserId(out var uid);
        var inner = new DispatchRequest(req.ReferenceNo, req.OutwardDcNo, req.DcDate);
        return RunBulkAsync(req.Ids, db, ct, job =>
            Task.FromResult(ApplyDispatch(job, inner, uid, db, audit, http.GetIp())));
    }

    private static Task<Results<Ok<BulkActionResultDto>, BadRequest<string>>> BulkStockAsync(
        [FromBody] BulkNoteRequest req, ClaimsPrincipal user, AppDbContext db,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        user.TryGetUserId(out var uid);
        return RunBulkAsync(req.Ids, db, ct, job =>
            Task.FromResult(ApplyStock(job, req.Note, uid, db, audit, http.GetIp())));
    }

    private static Task<Results<Ok<BulkActionResultDto>, BadRequest<string>>> BulkPaymentAsync(
        [FromBody] BulkPaymentRequest req, ClaimsPrincipal user, AppDbContext db,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        if (!Enum.TryParse<PaymentStatus>(req.Status, true, out var status))
            return Task.FromResult<Results<Ok<BulkActionResultDto>, BadRequest<string>>>(
                TypedResults.BadRequest($"Unknown payment status '{req.Status}'."));

        user.TryGetUserId(out var uid);
        return RunBulkAsync(req.Ids, db, ct, job =>
            Task.FromResult(ApplyPayment(job, status, uid, db, audit, http.GetIp())));
    }

    private static Task<Results<Ok<BulkActionResultDto>, BadRequest<string>>> BulkOutwardReferenceAsync(
        [FromBody] BulkOutwardReferenceRequest req, ClaimsPrincipal user, AppDbContext db,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ReferenceNo))
            return Task.FromResult<Results<Ok<BulkActionResultDto>, BadRequest<string>>>(
                TypedResults.BadRequest("Reference number is required."));

        user.TryGetUserId(out var uid);
        var inner = new OutwardReferenceRequest(req.ReferenceNo, req.OutwardDcNo);
        return RunBulkAsync(req.Ids, db, ct, job =>
            Task.FromResult(ApplyOutwardReference(job, inner, uid, db, audit, http.GetIp())));
    }

    private static Task<Results<Ok<BulkActionResultDto>, BadRequest<string>>> BulkInvoiceNoAsync(
        [FromBody] BulkInvoiceNoRequest req, ClaimsPrincipal user, AppDbContext db,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.InvNo))
            return Task.FromResult<Results<Ok<BulkActionResultDto>, BadRequest<string>>>(
                TypedResults.BadRequest("Invoice number is required."));

        user.TryGetUserId(out var uid);
        var inner = new InvoiceNoRequest(req.InvNo, req.InvDate);
        return RunBulkAsync(req.Ids, db, ct, job =>
            ApplyInvoiceNoAsync(job, inner, uid, db, audit, http.GetIp(), ct));
    }

    private static Task<Results<Ok<BulkActionResultDto>, BadRequest<string>>> BulkDeleteAsync(
        [FromBody] BulkIdsRequest req, ClaimsPrincipal user, AppDbContext db,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        user.TryGetUserId(out var uid);
        return RunBulkAsync(req.Ids, db, ct, job =>
            ApplyDeleteAsync(job, uid, db, audit, http.GetIp(), ct));
    }
}
