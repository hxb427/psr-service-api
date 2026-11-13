using System.Security.Claims;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Stock;

internal static class StockRoles
{
    public static readonly string[] Manage =
        { RoleNames.Admin, RoleNames.Manager, RoleNames.Supervisor, RoleNames.StoreManager };

    /// <summary>Who sees the WHOLE field register rather than only their own rows.
    ///
    /// Split out of <see cref="Manage"/> because that list does two jobs at once: it decides scope on
    /// the listing endpoints ("every technician's visits, or just mine?") and it decides authority on
    /// a couple of others — cancelling someone else's transfer, opening someone else's stock request.
    /// The sales desk is shown the field register and must not gain either of those, so widening
    /// <see cref="Manage"/> was not an option: it would have handed a counter clerk the power to
    /// cancel a transfer.
    ///
    /// Used ONLY where the question is scope, and identical to <see cref="Manage"/> for every
    /// pre-existing role, so nobody's view of the register changes. Mirrored by ShowFieldOpsNav in the
    /// desktop app's SessionState, which is split off its CanManageStock for the same reason.</summary>
    public static readonly string[] FieldActivityView =
        { RoleNames.Admin, RoleNames.Manager, RoleNames.Supervisor, RoleNames.StoreManager, RoleNames.Sales };

    public static bool CanManage(ClaimsPrincipal user) => Manage.Any(user.IsInRole);

    public static bool CanViewFieldActivity(ClaimsPrincipal user) => FieldActivityView.Any(user.IsInRole);
}
