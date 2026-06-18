using System.Security.Claims;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Services;

internal static class ServiceRoles
{
    // Pricing visibility mirrors Parts: admin/manager/supervisor/viewer see rates; technician/store/etc don't.
    public static readonly string[] Pricing =
        { RoleNames.Admin, RoleNames.Manager, RoleNames.Supervisor, RoleNames.Viewer };

    // Supervisory roles that may act on any job (as opposed to only the assigned technician).
    public static readonly string[] Manage =
        { RoleNames.Admin, RoleNames.Manager, RoleNames.Supervisor };

    public static bool CanSeePricing(ClaimsPrincipal u) => Pricing.Any(u.IsInRole);
    public static bool CanManage(ClaimsPrincipal u) => Manage.Any(u.IsInRole);
    public static bool IsTechnician(ClaimsPrincipal u) => u.IsInRole(RoleNames.Technician);

    /// <summary>The assigned technician, or any supervisory role, may work a job (add lines / complete).</summary>
    public static bool CanProcess(ClaimsPrincipal u, ServiceJob job)
    {
        if (CanManage(u)) return true;
        return u.TryGetUserId(out var uid) && job.TechnicianId == uid;
    }
}
