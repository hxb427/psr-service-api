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

namespace PSR.Service.Api.Documents;

public static class DocumentsEndpoints
{
    private const int MaxPageSize = 200;

    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var docs = app.MapGroup("/documents").WithTags("documents").RequireAuthorization("DocumentView");
        docs.MapPost("/preview", PreviewAsync).RequireAuthorization("DocumentManage");   // watermarked, NOT saved
        docs.MapPost("/", GenerateAsync).RequireAuthorization("DocumentManage");          // multi-job generate (saves)
        docs.MapPost("/sale/preview", PreviewSaleAsync).RequireAuthorization("DocumentManage");
        docs.MapPost("/sale", GenerateSaleAsync).RequireAuthorization("DocumentManage");  // spare-sale PI / invoice
        docs.MapGet("/", ListAsync);
        docs.MapGet("/{id:long}", GetAsync);
        docs.MapGet("/{id:long}/pdf", PdfAsync);

        // Documents that cover a given service job.
        app.MapGet("/services/{serviceId:long}/documents", ListForServiceAsync)
            .WithTags("documents").RequireAuthorization("DocumentView");

        // Documents raised against a given spare sale.
        app.MapGet("/spare-sales/{saleId:long}/documents", ListForSaleAsync)
            .WithTags("documents").RequireAuthorization("DocumentView");

        return app;
    }

    private static async Task<Results<FileContentHttpResult, BadRequest<string>>> PreviewAsync(
        [FromBody] GenerateDocumentRequest req, ClaimsPrincipal user,
        BillingService billing, CompanyInfo company, CancellationToken ct)
    {
        user.TryGetUserId(out var uid);
        try
        {
            // Compute only — nothing is saved and no document number is allocated for a preview.
            var built = await billing.BuildAsync(req, uid, ct);
            var sourcePi = built.Doc.DocType == DocumentType.Invoice
                ? built.Jobs.Select(j => j.PiNo).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) : null;
            var bytes = DocumentPdf.Render(built.Doc, company, "PREVIEW — NOT SAVED", sourcePi);
            return TypedResults.File(bytes, "application/pdf", "document-preview.pdf");
        }
        catch (BillingException ex) { return TypedResults.BadRequest(ex.Message); }
    }

    private static async Task<Results<Created<DocumentDto>, BadRequest<string>>> GenerateAsync(
        [FromBody] GenerateDocumentRequest req, ClaimsPrincipal user,
        AppDbContext db, BillingService billing, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        user.TryGetUserId(out var uid);
        long docId;
        try
        {
            docId = await billing.GenerateAsync(req, uid, ct);
        }
        catch (BillingException ex) { return TypedResults.BadRequest(ex.Message); }

        var dto = await BuildDtoAsync(db, docId, ct);
        audit.Log(uid, "document.generate", "service_document", docId,
            details: $"{dto.DocType} {dto.DocNo} over {dto.ServiceIds.Count} job(s)", ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.Created($"/documents/{docId}", dto);
    }

    private static async Task<Results<FileContentHttpResult, BadRequest<string>>> PreviewSaleAsync(
        [FromBody] GenerateSaleDocumentRequest req, ClaimsPrincipal user,
        BillingService billing, CompanyInfo company, CancellationToken ct)
    {
        user.TryGetUserId(out var uid);
        try
        {
            var built = await billing.BuildSaleAsync(req, uid, ct);
            var sourcePi = built.Doc.DocType == DocumentType.Invoice ? built.Sale.PiNo : null;
            var bytes = DocumentPdf.Render(built.Doc, company, "PREVIEW — NOT SAVED", sourcePi);
            return TypedResults.File(bytes, "application/pdf", "sale-document-preview.pdf");
        }
        catch (BillingException ex) { return TypedResults.BadRequest(ex.Message); }
    }

    private static async Task<Results<Created<DocumentDto>, BadRequest<string>>> GenerateSaleAsync(
        [FromBody] GenerateSaleDocumentRequest req, ClaimsPrincipal user,
        AppDbContext db, BillingService billing, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        user.TryGetUserId(out var uid);
        long docId;
        try
        {
            docId = await billing.GenerateSaleAsync(req, uid, ct);
        }
        catch (BillingException ex) { return TypedResults.BadRequest(ex.Message); }
        // Generating a sale document no longer moves stock, so nothing here should raise this. Kept
        // because the ledger is one throw away from any billing path that grows a movement later, and a
        // stock message reaching the user beats a 500 with nothing in it.
        catch (StockException ex) { return TypedResults.BadRequest(ex.Message); }

        var dto = await BuildDtoAsync(db, docId, ct);
        audit.Log(uid, "document.generate", "service_document", docId,
            details: $"{dto.DocType} {dto.DocNo} for sale {dto.SaleNo}", ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.Created($"/documents/{docId}", dto);
    }

    private static async Task<Ok<List<DocumentListItemDto>>> ListForSaleAsync(long saleId, AppDbContext db, CancellationToken ct)
    {
        var rows = await db.ServiceDocuments.AsNoTracking()
            .Where(d => d.SpareSaleId == saleId).OrderByDescending(d => d.Id).ToListAsync(ct);
        var items = new List<DocumentListItemDto>();
        foreach (var d in rows)
            items.Add(await ToListItemAsync(db, d, ct));
        return TypedResults.Ok(items);
    }

    private static async Task<Ok<List<DocumentListItemDto>>> ListForServiceAsync(long serviceId, AppDbContext db, CancellationToken ct)
    {
        // Join lines→documents (no List.Contains — that hits the EF Core 9 funcletizer bug).
        var rows = await (from l in db.ServiceDocumentLines.AsNoTracking()
                          where l.ServiceJobId == serviceId
                          join d in db.ServiceDocuments on l.DocumentId equals d.Id
                          select d).Distinct().OrderByDescending(d => d.Id).ToListAsync(ct);
        var items = new List<DocumentListItemDto>();
        foreach (var d in rows)
            items.Add(await ToListItemAsync(db, d, ct));
        return TypedResults.Ok(items);
    }

    private static async Task<Ok<PagedResult<DocumentListItemDto>>> ListAsync(
        AppDbContext db, string? docType, string? search, int? page, int? pageSize, CancellationToken ct)
    {
        var pageNum = page is null or < 1 ? 1 : page.Value;
        var size = pageSize is null or < 1 or > MaxPageSize ? 50 : pageSize.Value;

        var q = db.ServiceDocuments.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(docType) && Enum.TryParse<DocumentType>(docType, true, out var dt))
            q = q.Where(d => d.DocType == dt);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            q = q.Where(d => d.DocNo.Contains(term) || d.PartyName.Contains(term));
        }

        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(d => d.Id).Skip((pageNum - 1) * size).Take(size).ToListAsync(ct);
        var items = new List<DocumentListItemDto>();
        foreach (var d in rows)
            items.Add(await ToListItemAsync(db, d, ct));
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
        var doc = await db.ServiceDocuments.AsNoTracking().Include(d => d.Lines).FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc is null) return TypedResults.NotFound();
        // A tax invoice prints the source PI number — from the sale it bills, or from any covered job.
        string? sourcePi = doc.DocType != DocumentType.Invoice ? null
            : doc.SpareSaleId is { } sid
                ? await db.SpareSales.AsNoTracking().Where(s => s.Id == sid).Select(s => s.PiNo).FirstOrDefaultAsync(ct)
                : await (from l in db.ServiceDocumentLines.AsNoTracking()
                         where l.DocumentId == id && l.ServiceJobId != null
                         join s in db.Services on l.ServiceJobId equals s.Id
                         where s.PiNo != null
                         select s.PiNo).FirstOrDefaultAsync(ct);
        var bytes = DocumentPdf.Render(doc, company, sourcePiNo: sourcePi);
        return TypedResults.File(bytes, "application/pdf", $"{doc.DocNo}.pdf");
    }

    // ---- helpers ----

    private static async Task<DocumentListItemDto> ToListItemAsync(AppDbContext db, ServiceDocument d, CancellationToken ct)
    {
        var jobCount = await db.ServiceDocumentLines.AsNoTracking()
            .Where(l => l.DocumentId == d.Id && l.ServiceJobId != null)
            .Select(l => l.ServiceJobId).Distinct().CountAsync(ct);
        var saleNo = d.SpareSaleId is { } sid
            ? await db.SpareSales.AsNoTracking().Where(s => s.Id == sid).Select(s => s.SaleNo).FirstOrDefaultAsync(ct)
            : null;
        return new DocumentListItemDto(d.Id, d.DocType.ToString(), d.DocNo, d.DocDate, jobCount, d.PartyName,
            d.TotalAmount, d.SpareSaleId is null ? "Service" : "Sale", saleNo);
    }

    private static async Task<DocumentDto> BuildDtoAsync(AppDbContext db, long id, CancellationToken ct)
    {
        var doc = await db.ServiceDocuments.AsNoTracking().Include(d => d.Lines).FirstAsync(d => d.Id == id, ct);
        var lines = doc.Lines.OrderBy(l => l.Id).Select(l => new DocumentLineDto(
            l.Id, l.ServiceJobId, l.Description, l.Warranty, l.ServiceChallan, l.HsnCode,
            l.Qty, l.UnitRate, l.TaxableAmount, l.GstPercent, l.TaxAmount, l.LineTotal, l.Remarks, l.PartId)).ToList();

        // Join lines→services for the covered job numbers (no List.Contains — funcletizer bug).
        var jobs = await (from l in db.ServiceDocumentLines.AsNoTracking()
                          where l.DocumentId == id && l.ServiceJobId != null
                          join s in db.Services on l.ServiceJobId equals s.Id
                          select new { s.Id, s.ServiceNo }).Distinct().ToListAsync(ct);
        var serviceIds = jobs.Select(j => j.Id).ToList();
        var serviceNos = jobs.Select(j => j.ServiceNo).ToList();

        var saleNo = doc.SpareSaleId is { } sid
            ? await db.SpareSales.AsNoTracking().Where(s => s.Id == sid).Select(s => s.SaleNo).FirstOrDefaultAsync(ct)
            : null;

        return new DocumentDto(
            doc.Id, doc.DocType.ToString(), doc.DocNo, doc.DocDate, serviceIds, serviceNos,
            doc.PartyName, doc.PartyAddress, doc.PartyGstin, doc.PartyState, doc.PartyStateCode, doc.IsInterState,
            doc.TaxableAmount, doc.CgstAmount, doc.SgstAmount, doc.IgstAmount, doc.CourierCharges, doc.TotalAmount,
            doc.CourierMode, doc.Remarks, doc.CreatedAt, lines, doc.SpareSaleId, saleNo);
    }
}
