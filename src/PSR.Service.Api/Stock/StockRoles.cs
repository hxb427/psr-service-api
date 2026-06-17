using System.Security.Claims;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Stock;

internal static class StockRoles
{
    public static readonly string[] Manage =
        { RoleNames.Admin, RoleNames.Manager, RoleNames.Supervisor, RoleNames.StoreManager };

    public static bool CanManage(ClaimsPrincipal user) => Manage.Any(user.IsInRole);
}
