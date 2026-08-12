using System.ComponentModel.DataAnnotations;

namespace PSR.Service.Api.Auth;

public record LoginRequest(
    [Required, StringLength(50)] string Username,
    [Required, StringLength(100, MinimumLength = 1)] string Password);

public record LoginResponse(
    string Token,
    DateTime ExpiresAt,
    long UserId,
    string Username,
    string? FullName,
    string[] Roles,
    bool MustChangePassword,
    /// <summary>Field technicians carry stock off-site; in-house ones work at the bench. Roles alone
    /// cannot tell the two apart — both are "technician" — so the flag has to travel with the login
    /// for the client to know which navigation to show.</summary>
    bool IsFieldTechnician);

public record ChangePasswordRequest(
    [Required, StringLength(100, MinimumLength = 1)] string CurrentPassword,
    [Required, StringLength(100, MinimumLength = 8)] string NewPassword);

public record ChangePasswordResponse(
    string Token,
    DateTime ExpiresAt);
