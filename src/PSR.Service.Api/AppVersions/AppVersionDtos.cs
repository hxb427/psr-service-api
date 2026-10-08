using System.ComponentModel.DataAnnotations;

namespace PSR.Service.Api.AppVersions;

public record AppVersionItem(
    long Id,
    string ClientId,
    string Version,
    int VersionCode,
    string ApkS3Key,
    string? Notes,
    bool Mandatory,
    bool IsActive,
    DateTime PublishedAt);

/// <summary>What /app-versions/latest returns. The app compares <see cref="VersionCode"/> to its own
/// pubspec build number. <see cref="DownloadUrl"/> is a short-lived presigned URL (or a pass-through
/// when the admin stored a direct one).</summary>
public record LatestAppVersionResponse(
    string ClientId,
    string Version,
    int VersionCode,
    string DownloadUrl,
    string? Notes,
    bool Mandatory,
    DateTime PublishedAt);

public record CreateAppVersionRequest(
    [Required, StringLength(40)] string ClientId,
    [Required, StringLength(20)] string Version,
    [Range(1, int.MaxValue)] int VersionCode,
    /// <summary>S3 object key (preferred) OR a full http(s) URL (for testing).</summary>
    [Required, StringLength(500)] string ApkS3Key,
    [StringLength(4000)] string? Notes,
    bool Mandatory);

public record AppVersionListResponse(IReadOnlyList<AppVersionItem> Items);
