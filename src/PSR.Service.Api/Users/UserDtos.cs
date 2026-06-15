using System.ComponentModel.DataAnnotations;

namespace PSR.Service.Api.Users;

public record UserListItemDto(
    long Id,
    string Username,
    string? FullName,
    string? Email,
    bool IsActive,
    bool MustChangePassword,
    DateTime? LastLoginAt,
    string[] Roles);

public record UserDetailDto(
    long Id,
    string Username,
    string? FullName,
    string? Email,
    bool IsActive,
    bool MustChangePassword,
    DateTime? LastLoginAt,
    DateTime CreatedAt,
    string[] Roles);

public record CreateUserRequest(
    [Required, StringLength(50, MinimumLength = 3)] string Username,
    [Required, StringLength(100, MinimumLength = 6)] string Password,
    [StringLength(200)] string? FullName,
    [EmailAddress, StringLength(200)] string? Email,
    string[] Roles);

public record UpdateUserRequest(
    [StringLength(200)] string? FullName,
    [EmailAddress, StringLength(200)] string? Email);

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
