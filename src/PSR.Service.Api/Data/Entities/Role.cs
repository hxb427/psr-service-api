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
    public const string InwardManager = "inward_manager";
    public const string Technician = "technician";
    public const string DispatchManager = "dispatch_manager";
    public const string StoreManager = "store_manager";
    public const string Accounts = "accounts";

    public static readonly string[] All =
    [
        Admin, Manager, Viewer, Supervisor, InwardManager,
        Technician, DispatchManager, StoreManager, Accounts
    ];
}
