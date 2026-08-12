using System.Security.Claims;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Users;

/// <summary>Who may see and change whose account.
///
/// Admin, manager and supervisor all reach the user list now, so "can call the endpoint" is no longer
/// the whole answer — every route has to ask about the specific target as well. The rule is rank:
/// you act only on accounts below your own, which stops a manager disabling an admin and a supervisor
/// touching a manager or another supervisor.
///
/// Enforced here, on the server. The desktop app hides what a user cannot do, but that is a courtesy
/// — the JWT carries the roles and these checks are what actually holds.</summary>
public static class UserHierarchy
{
    public const int RankAdmin = 3;
    public const int RankManager = 2;
    public const int RankSupervisor = 1;
    public const int RankOther = 0;

    /// <summary>Roles that reach the user-management pages at all. Mirrored by the "UserManage" policy.</summary>
    public static readonly string[] ManageRoles = [RoleNames.Admin, RoleNames.Manager, RoleNames.Supervisor];

    public static int RankOf(IEnumerable<string> roles)
    {
        var rank = RankOther;
        foreach (var r in roles)
        {
            if (string.Equals(r, RoleNames.Admin, StringComparison.OrdinalIgnoreCase)) return RankAdmin;
            if (string.Equals(r, RoleNames.Manager, StringComparison.OrdinalIgnoreCase)) rank = Math.Max(rank, RankManager);
            else if (string.Equals(r, RoleNames.Supervisor, StringComparison.OrdinalIgnoreCase)) rank = Math.Max(rank, RankSupervisor);
        }
        return rank;
    }

    public static int RankOf(User user) => RankOf(user.UserRoles.Select(ur => ur.Role.Name));

    public static int RankOf(ClaimsPrincipal principal) =>
        RankOf(principal.FindAll(ClaimTypes.Role).Select(c => c.Value));

    /// <summary>Whether the actor may see the target at all.
    ///
    /// Admin and manager see the whole list — a manager is expected to know an admin exists, they just
    /// cannot touch the account. A supervisor sees only the ranks below them, so managers and other
    /// supervisors are absent from their list entirely.</summary>
    public static bool CanView(int actorRank, int targetRank) => actorRank switch
    {
        RankAdmin or RankManager => true,
        RankSupervisor => targetRank < RankSupervisor,
        _ => false,
    };

    /// <summary>Whether the actor may change the target: create-as, edit, reset password, activate,
    /// deactivate, set roles.
    ///
    /// Admin keeps full reach, including over other admins — the last-active-admin and not-yourself
    /// guards already stop the damaging cases, and removing that would strand an estate with two
    /// admins and a forgotten password. Everyone else acts strictly downward.</summary>
    public static bool CanManage(int actorRank, int targetRank) =>
        actorRank == RankAdmin || (actorRank > RankOther && targetRank < actorRank);

    /// <summary>Whether the actor may hand out this role. Without it a manager could grant admin to a
    /// spare account and log back in as one — the ceiling on granting has to match the ceiling on
    /// acting, or the hierarchy is decorative.</summary>
    public static bool CanGrant(int actorRank, string roleName) =>
        actorRank == RankAdmin || RankOf([roleName]) < actorRank;

    /// <summary>Explains a refusal in the terms the person reading it thinks in.</summary>
    public static string DenialMessage(int actorRank) => actorRank switch
    {
        RankManager => "Managers can only manage supervisors and users below them.",
        RankSupervisor => "Supervisors can only manage users below them.",
        _ => "You do not have permission to manage this account.",
    };
}
