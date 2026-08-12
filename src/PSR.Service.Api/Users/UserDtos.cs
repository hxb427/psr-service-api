using System.ComponentModel.DataAnnotations;

namespace PSR.Service.Api.Users;

public record UserListItemDto(
    long Id,
    string Username,
    string? FullName,
    string? Email,
    bool IsActive,
    bool IsFieldTechnician,
    bool MustChangePassword,
    DateTime? LastLoginAt,
    string[] Roles,
    /// <summary>Whether the caller outranks this account. The desktop app greys its row actions off
    /// this instead of re-deriving the hierarchy client-side and drifting from the server.</summary>
    bool CanManage);

public record UserDetailDto(
    long Id,
    string Username,
    string? FullName,
    string? Email,
    bool IsActive,
    bool IsFieldTechnician,
    bool MustChangePassword,
    DateTime? LastLoginAt,
    DateTime CreatedAt,
    string[] Roles,
    bool CanManage);

public record CreateUserRequest(
    [Required, StringLength(50, MinimumLength = 3)] string Username,
    [Required, StringLength(100, MinimumLength = 6)] string Password,
    [StringLength(200)] string? FullName,
    [EmailAddress, StringLength(200)] string? Email,
    string[] Roles,
    bool IsFieldTechnician = false);

public record UpdateUserRequest(
    [StringLength(200)] string? FullName,
    [EmailAddress, StringLength(200)] string? Email,
    bool IsFieldTechnician = false);

public record ResetPasswordRequest(
    [Required, StringLength(100, MinimumLength = 6)] string NewPassword);

public record AssignRolesRequest(string[] Roles);

public record RoleDto(int Id, string Name, string? Description);

public record AuditLogItemDto(
    long Id,
    long? UserId,
    string? Username,
    string Action,
    string? Entity,
    long? EntityId,
    string? Details,
    string? IpAddress,
    DateTime CreatedAt);
