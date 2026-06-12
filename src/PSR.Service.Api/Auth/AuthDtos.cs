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
    bool MustChangePassword);

public record ChangePasswordRequest(
    [Required, StringLength(100, MinimumLength = 1)] string CurrentPassword,
    [Required, StringLength(100, MinimumLength = 8)] string NewPassword);

public record ChangePasswordResponse(
    string Token,
    DateTime ExpiresAt);
