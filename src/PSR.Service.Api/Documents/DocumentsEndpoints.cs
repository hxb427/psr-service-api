using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Common;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Documents;

public static class DocumentsEndpoints
{
    private const int MaxPageSize = 200;

    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        // Job-scoped: generate + list the documents of one service job.
        var jobDocs = app.MapGroup("/services/{serviceId:long}/documents").WithTags("documents").RequireAuthorization();
        jobDocs.MapPost("/", GenerateAsync).RequireAuthorization("DocumentManage");
        jobDocs.MapGet("/", ListForServiceAsync).RequireAuthorization("DocumentView");

        // Global document register.
        var docs = app.MapGroup("/documents").WithTags("documents").RequireAuthorization("DocumentView");
        docs.MapGet("/", ListAsync);
        docs.MapGet("/{id:long}", GetAsync);
        docs.MapGet("/{id:long}/pdf", PdfAsync);

        return app;
    }

    private static async Task<Results<Created<DocumentDto>, BadRequest<string>, NotFound>> GenerateAsync(
        long serviceId, [FromBody] GenerateDocumentRequest req, ClaimsPrincipal user,
        AppDbContext db, BillingService billing, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        user.TryGetUserId(out var uid);
        ServiceDocument doc;
        try
        {
            doc = await billing.GenerateAsync(serviceId, req, uid, ct);
        }
        catch (BillingException ex) { return TypedResults.BadRequest(ex.Message); }

        audit.Log(uid, "document.generate", "service_document", doc.Id, details: $"{doc.DocType} {doc.DocNo}", ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.Created($"/documents/{doc.Id}", await BuildDtoAsync(db, doc.Id, ct));
    }

    private static async Task<Ok<List<DocumentListItemDto>>> ListForServiceAsync(long serviceId, AppDbContext db, CancellationToken ct)
    {
        var rows = await (from d in db.ServiceDocuments.AsNoTracking()
                          where d.ServiceId == serviceId
                          join s in db.Services on d.ServiceId equals s.Id into sg
                          from s in sg.DefaultIfEmpty()
                          orderby d.Id descending
                          select new { Doc = d, ServiceNo = s != null ? s.ServiceNo : null }).ToListAsync(ct);
        return TypedResults.Ok(rows.Select(x => ToListItem(x.Doc, x.ServiceNo)).ToList());
    }

    private static async Task<Ok<PagedResult<DocumentListItemDto>>> ListAsync(
        AppDbContext db, string? docType, string? search, int? page, int? pageSize, CancellationToken ct)
    {
        var pageNum = page is null or < 1 ? 1 : page.Value;
        var size = pageSize is null or < 1 or > MaxPageSize ? 50 : pageSize.Value;

        // Filter on the document entity (never project enum.ToString() into SQL — EF can't translate it).
        var q = db.ServiceDocuments.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(docType) && Enum.TryParse<DocumentType>(docType, true, out var dt))
            q = q.Where(d => d.DocType == dt);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            q = q.Where(d => d.DocNo.Contains(term) || d.PartyName.Contains(term));
        }

        var total = await q.CountAsync(ct);
        // Project the entity + joined service no into an anonymous type as the final step, then map in memory.
        var rows = await (from d in q
                          join s in db.Services on d.ServiceId equals s.Id into sg
                          from s in sg.DefaultIfEmpty()
                          orderby d.Id descending
                          select new { Doc = d, ServiceNo = s != null ? s.ServiceNo : null })
            .Skip((pageNum - 1) * size).Take(size).ToListAsync(ct);

        var items = rows.Select(x => ToListItem(x.Doc, x.ServiceNo)).ToList();
        return TypedResults.Ok(new PagedResult<DocumentListItemDto>(items, pageNum, size, total));
    }

    private static async Task<Results<Ok<DocumentDto>, NotFound>> GetAsync(long id, AppDbContext db, CancellationToken ct)
    {
        if (!await db.ServiceDocuments.AnyAsync(d => d.Id == id, ct)) return TypedResults.NotFound();
        return TypedResults.Ok(await BuildDtoAsync(db, id, ct));
    }

    private static async Task<Results<FileContentHttpResult, NotFound>> PdfAsync(
        long id, AppDbContext db, CompanyInfo company, CancellationToken ct)
    {
        var doc = await db.ServiceDocuments.Include(d => d.Lines).FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc is null) return TypedResults.NotFound();

        var job = doc.ServiceId is { } sid
            ? await db.Services.AsNoTracking().Where(s => s.Id == sid)
                .Select(s => new { s.ServiceNo, s.SerialNo, s.ModelName }).FirstOrDefaultAsync(ct)
            : null;

        var bytes = DocumentPdf.Render(doc, company, job?.ServiceNo, job?.SerialNo, job?.ModelName);
        return TypedResults.File(bytes, "application/pdf", $"{doc.DocNo}.pdf");
    }

    // ---- helpers ----

    // enum→string mapping happens here, client-side, after the query has materialized.
    private static DocumentListItemDto ToListItem(ServiceDocument d, string? serviceNo)
        => new(d.Id, d.DocType.ToString(), d.DocNo, d.DocDate, d.ServiceId, serviceNo, d.PartyName, d.TotalAmount);

    private static async Task<DocumentDto> BuildDtoAsync(AppDbContext db, long id, CancellationToken ct)
    {
        var doc = await db.ServiceDocuments.AsNoTracking().Include(d => d.Lines).FirstAsync(d => d.Id == id, ct);
        string? serviceNo = doc.ServiceId is { } sid
            ? await db.Services.AsNoTracking().Where(s => s.Id == sid).Select(s => s.ServiceNo).FirstOrDefaultAsync(ct)
            : null;

        var lines = doc.Lines.OrderBy(l => l.Id).Select(l => new DocumentLineDto(
            l.Id, l.Description, l.HsnCode, l.Qty, l.UnitRate, l.TaxableAmount, l.GstPercent, l.TaxAmount, l.LineTotal)).ToList();

        return new DocumentDto(
            doc.Id, doc.DocType.ToString(), doc.DocNo, doc.DocDate, doc.ServiceId, serviceNo,
            doc.PartyName, doc.PartyAddress, doc.PartyGstin, doc.PartyState, doc.PartyStateCode, doc.IsInterState,
            doc.TaxableAmount, doc.CgstAmount, doc.SgstAmount, doc.IgstAmount, doc.CourierCharges, doc.TotalAmount,
            doc.CourierMode, doc.Remarks, doc.CreatedAt, lines);
    }
}
