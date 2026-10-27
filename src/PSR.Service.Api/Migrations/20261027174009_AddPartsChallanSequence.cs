using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PSR.Service.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPartsChallanSequence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "number_sequences",
                columns: new[] { "key", "next_value", "prefix", "year" },
                values: new object[] { "PARTS_DC", 1L, "PDC", 2026 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "number_sequences",
                keyColumn: "key",
                keyValue: "PARTS_DC");
        }
    }
}
