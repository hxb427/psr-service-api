using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace PSR.Service.Api.AppVersions;

/// <summary>Issues short-lived presigned GET URLs for the in-app updater.
///
/// The bucket stays PRIVATE — the app never authenticates against S3. Instead /app-versions/latest
/// calls <see cref="GetPresignedDownloadUrlAsync"/>, which signs a URL with the API server's own IAM
/// credentials (the EC2 instance profile). That URL works without a JWT, which matters because a
/// client blocked by the version gate cannot log in to fetch its own update.
///
/// Presigned rather than a public bucket because the APK carries the pinned cert thumbprint and
/// whatever else is compiled into the build — a leaked URL would hand anyone the binary. Presigned
/// URLs expire and are scoped to one key.</summary>
public sealed class S3PresignService(
    IAmazonS3 s3, IOptions<S3Options> options, ILogger<S3PresignService> logger)
{
    private readonly S3Options _options = options.Value;

    /// <summary>A full http(s) value is passed through unchanged, so an admin can point a release at a
    /// CDN or a throwaway URL while testing. Anything else is an S3 key and gets presigned.</summary>
    public Task<string> ResolveDownloadUrlAsync(string keyOrUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(keyOrUrl))
            throw new ArgumentException("Empty S3 key / URL.", nameof(keyOrUrl));

        if (keyOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            keyOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(keyOrUrl);
        }

        return GetPresignedDownloadUrlAsync(keyOrUrl, ct);
    }

    public Task<string> GetPresignedDownloadUrlAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Bucket))
        {
            // Misconfiguration is loud — better an error than handing the updater an empty URL it
            // would report to the technician as a failed download.
            throw new InvalidOperationException(
                "S3:Bucket is not configured. Set it in appsettings or environment.");
        }

        var req = new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            Expires = DateTime.UtcNow.AddMinutes(_options.PresignMinutes),
            Verb = HttpVerb.GET,
        };
        logger.LogDebug("Presigning S3 GET s3://{Bucket}/{Key} for {Minutes} min",
            _options.Bucket, key, _options.PresignMinutes);
        return s3.GetPreSignedURLAsync(req);
    }
}

public sealed class S3Options
{
    public const string SectionName = "S3";

    /// <summary>Bucket holding signed release APKs. Private; read by the API's instance role.</summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>How long presigned URLs stay valid. 15min is plenty for an APK over mobile data.</summary>
    public int PresignMinutes { get; set; } = 15;

    public string Region { get; set; } = "ap-south-1";
}
