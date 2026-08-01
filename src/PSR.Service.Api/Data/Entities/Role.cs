namespace PSR.Service.Api.Data.Entities;

public class Role
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public List<UserRole> UserRoles { get; set; } = new();
}

public static class RoleNames
{
    public const string Admin = "admin";
    public const string Manager = "manager";
    public const string Viewer = "viewer";
    public const string Supervisor = "supervisor";
    public const string Technician = "technician";
    public const string StoreManager = "store_manager";
    public const string Accounts = "accounts";

    /// <summary>Receiving desk. Holds exactly one permission — booking inward entries — and is the only
    /// role that grants no read access to the rest of the workflow. Deliberately NOT the old
    /// inward_manager (id 5): that one was a full workflow role, so it keeps its retired id and its gap.</summary>
    public const string Inward = "inward";

    // inward_manager (was id 5) and dispatch_manager (was id 7) removed — folded into manager/supervisor.
    public static readonly string[] All =
    [
        Admin, Manager, Viewer, Supervisor, Technician, StoreManager, Accounts, Inward
    ];

    // Seed ids are FIXED (never re-indexed) so existing user_roles never silently remap to a different role.
    public static readonly (int Id, string Name)[] Seed =
    [
        (1, Admin), (2, Manager), (3, Viewer), (4, Supervisor),
        (6, Technician), (8, StoreManager), (9, Accounts), (10, Inward)
    ];
}
