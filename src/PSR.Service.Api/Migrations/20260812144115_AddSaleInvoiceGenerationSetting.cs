using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PSR.Service.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSaleInvoiceGenerationSetting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "app_settings",
                columns: new[] { "key", "value" },
                values: new object[] { "sale_invoice_generation_enabled", "true" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "app_settings",
                keyColumn: "key",
                keyValue: "sale_invoice_generation_enabled");
        }
    }
}
