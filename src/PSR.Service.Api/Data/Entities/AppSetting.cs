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
    /// <summary>Whether a tax invoice may be raised against SERVICE jobs. Kept under its original key
    /// so an existing setting keeps its meaning.</summary>
    public const string InvoiceGenerationEnabled = "invoice_generation_enabled";

    /// <summary>Whether a tax invoice may be raised against SPARE SALES. Separate from the service
    /// switch because the two are different books — stopping counter-sale billing while service billing
    /// continues (or the reverse) is the whole point of having a switch.</summary>
    public const string SaleInvoiceGenerationEnabled = "sale_invoice_generation_enabled";

    /// <summary>Oldest WPF client version allowed to talk to this API. Clients below it get
    /// 426 Upgrade Required on everything except /health and /app-version, which is what makes a
    /// mandatory update actually mandatory — the app is useless until updated. "0.0.0" = no floor.</summary>
    public const string MinClientVersion = "min_client_version";

    /// <summary>Warranty length in months to assume when a machine's dealer cannot be resolved — a
    /// direct-customer job, or a warranty check typed against a serial that was never inwarded here.
    /// 0 = no fallback, which leaves the verdict UNKNOWN exactly as before. Same convention as
    /// Dealer.WarrantyMonths, where 0 also means "not known".</summary>
    public const string DefaultWarrantyMonths = "default_warranty_months";
}
