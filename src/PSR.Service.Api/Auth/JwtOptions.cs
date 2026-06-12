namespace PSR.Service.Api.Auth;

public class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "psr-service";
    public string Audience { get; set; } = "psr-service-wpf";
    public string Signing { get; set; } = string.Empty;
    public int ExpiryHours { get; set; } = 24;
}
