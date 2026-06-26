namespace PSR.Service.Api.Services;

// Endpoint handlers are split across partial files by concern:
//   ServicesEndpoints.Queries.cs   — list / summary / overview / technicians / get
//   ServicesEndpoints.Inward.cs    — create + multi-item batch + customer resolve
//   ServicesEndpoints.Workflow.cs  — state transitions (assign → ... → dispatch/replace)
//   ServicesEndpoints.Lines.cs     — add / delete service lines
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
}
