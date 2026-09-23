namespace PSR.Service.Api.Services;

// Endpoint handlers are split across partial files by concern:
//   ServicesEndpoints.Queries.cs   — list / summary / overview / technicians / get
//   ServicesEndpoints.Inward.cs    — create + multi-item batch + customer resolve
//   ServicesEndpoints.Workflow.cs  — state transitions (assign → ... → dispatch/replace)
//   ServicesEndpoints.Swap.cs      — advance replacement: issue, cancel, and the replacement trail
//   ServicesEndpoints.Units.cs     — registering an inward item in the serial ledger + its history
//   ServicesEndpoints.Lines.cs     — add / delete service lines
//   ServicesEndpoints.Edit.cs      — correct a booked job's descriptive fields (admin-switched)
//   ServicesEndpoints.Mapping.cs   — shared helpers (detail/line mapping, transition write)
public static partial class ServicesEndpoints
{
    private const int MaxPageSize = 200;

    public static IEndpointRouteBuilder MapServiceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/services").WithTags("services").RequireAuthorization();

        group.MapGet("/", ListAsync);
        group.MapGet("/technicians", TechniciansAsync).RequireAuthorization("ServiceAssign");
        group.MapGet("/summary", SummaryAsync);
        group.MapGet("/overview", OverviewAsync);
        group.MapGet("/{id:long}", GetAsync);
        group.MapPost("/", CreateAsync).RequireAuthorization("InwardManage");
        group.MapPost("/inward-batch", InwardBatchAsync).RequireAuthorization("InwardManage");
        group.MapPost("/{id:long}/assign", AssignAsync).RequireAuthorization("ServiceAssign");
        group.MapPost("/{id:long}/acknowledge", AcknowledgeAsync);   // assigned technician only
        group.MapPost("/{id:long}/start", StartAsync);               // assigned technician only
        group.MapPost("/{id:long}/lines", AddLineAsync);
        // Same rules as the single-line route, applied to a whole selection in one transaction.
        group.MapPost("/{id:long}/lines/batch", AddLinesAsync);
        group.MapDelete("/{id:long}/lines/{lineId:long}", DeleteLineAsync);
        group.MapPost("/{id:long}/total-loss", MarkTotalLossAsync);
        group.MapPost("/{id:long}/complete", CompleteAsync);
        group.MapPost("/{id:long}/revert", RevertAsync).RequireAuthorization("ServiceManage");
        group.MapPost("/{id:long}/dispatch", DispatchAsync).RequireAuthorization("DispatchManage");
        group.MapPost("/{id:long}/stock", StockJobAsync).RequireAuthorization("DispatchManage");
        group.MapPost("/{id:long}/replace", ReplaceAsync).RequireAuthorization("DispatchManage");
        // Advance replacement — the customer leaves with a unit off the shelf today and theirs stays
        // behind on a job of its own. DispatchManage is admin/manager/supervisor, the same people who
        // already decide a total-loss replacement; giving away shelf stock is one decision, not two.
        group.MapPost("/{id:long}/swap", SwapAsync).RequireAuthorization("DispatchManage");
        group.MapPost("/{id:long}/swap/cancel", CancelSwapAsync).RequireAuthorization("DispatchManage");
        // "This unit has come back — was it one of ours?" Readable by anyone who can see jobs: it is
        // the counter's lookup, and it carries nothing a job row does not already show.
        group.MapGet("/replacements", ReplacementsAsync);
        // Asked as the serial is keyed in at the counter, BEFORE the job is booked - which is the only
        // moment a misread serial or a unit that has changed hands can still be sorted out cheaply.
        group.MapGet("/unit-history", UnitHistoryAsync);
        group.MapPost("/{id:long}/total-loss-close", LeaveTotalLossAsync).RequireAuthorization("DispatchManage");
        group.MapPost("/{id:long}/replacement-reject", RejectReplacementAsync).RequireAuthorization("DispatchManage");
        group.MapPost("/{id:long}/payment", PaymentAsync).RequireAuthorization("PaymentManage");
        // Manual stamps that do NOT move the workflow (legacy "Set Outward Reference" / "Set Invoice No").
        group.MapPost("/{id:long}/outward-reference", SetOutwardReferenceAsync).RequireAuthorization("DispatchManage");
        group.MapPost("/{id:long}/invoice-no", SetInvoiceNoAsync).RequireAuthorization("DocumentManage");
        // Legacy Global Search "Edit Service Record" — descriptive fields only, and only while the
        // admin switch is on (checked in the handler, since the answer is a message not a 403).
        group.MapPut("/{id:long}/record", UpdateRecordAsync).RequireAuthorization("ServiceRecordEdit");
        group.MapDelete("/{id:long}", SoftDeleteAsync).RequireAuthorization("ServiceDelete");

        // Bulk counterparts (ServicesEndpoints.Bulk.cs). The desk applies these to a whole page of
        // jobs at a time; as N sequential POSTs that was N chances for one lost packet to strand the
        // run. Same authorization as the single-job route each one mirrors, and the per-job rules are
        // literally the same code — both call the shared Apply* helpers.
        var bulk = group.MapGroup("/bulk");
        bulk.MapPost("/assign", BulkAssignAsync).RequireAuthorization("ServiceAssign");
        bulk.MapPost("/acknowledge", BulkAcknowledgeAsync);   // assigned technician only, per job
        bulk.MapPost("/start", BulkStartAsync);               // assigned technician only, per job
        bulk.MapPost("/dispatch", BulkDispatchAsync).RequireAuthorization("DispatchManage");
        bulk.MapPost("/stock", BulkStockAsync).RequireAuthorization("DispatchManage");
        bulk.MapPost("/payment", BulkPaymentAsync).RequireAuthorization("PaymentManage");
        bulk.MapPost("/outward-reference", BulkOutwardReferenceAsync).RequireAuthorization("DispatchManage");
        bulk.MapPost("/invoice-no", BulkInvoiceNoAsync).RequireAuthorization("DocumentManage");
        bulk.MapPost("/delete", BulkDeleteAsync).RequireAuthorization("ServiceDelete");

        return app;
    }
}
