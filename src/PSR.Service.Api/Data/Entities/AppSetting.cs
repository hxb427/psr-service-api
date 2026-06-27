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
}
