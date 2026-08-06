namespace PSR.Service.Api.Data.Entities;

/// <summary>Key/value application settings — admin-editable feature toggles (e.g. whether invoice
/// generation is allowed). Read by everyone; written only by admins.</summary>
public class AppSetting
{
    public string Key { get; set; } = string.Empty;   // PK
    public string Value { get; set; } = string.Empty;
}

public static class SettingKeys
{
    public const string InvoiceGenerationEnabled = "invoice_generation_enabled";

    /// <summary>Oldest WPF client version allowed to talk to this API. Clients below it get
    /// 426 Upgrade Required on everything except /health and /app-version, which is what makes a
    /// mandatory update actually mandatory — the app is useless until updated. "0.0.0" = no floor.</summary>
    public const string MinClientVersion = "min_client_version";
}
