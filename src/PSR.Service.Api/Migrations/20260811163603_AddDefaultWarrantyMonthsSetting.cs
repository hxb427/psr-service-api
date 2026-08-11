using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PSR.Service.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDefaultWarrantyMonthsSetting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Seeded at 0 = no fallback, so the warranty verdict behaves exactly as it did until an
            // admin sets the house figure. Guessing a term here would mark machines in warranty that
            // are not.
            migrationBuilder.InsertData(
                table: "app_settings",
                columns: new[] { "key", "value" },
                values: new object[] { "default_warranty_months", "0" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "app_settings",
                keyColumn: "key",
                keyValue: "default_warranty_months");
        }
    }
}
