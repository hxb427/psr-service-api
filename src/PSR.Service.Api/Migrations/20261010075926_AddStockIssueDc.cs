using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PSR.Service.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddStockIssueDc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "dc_no",
                table: "stock_requests",
                type: "varchar(40)",
                maxLength: 40,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.InsertData(
                table: "number_sequences",
                columns: new[] { "key", "next_value", "prefix", "year" },
                values: new object[] { "STOCK_ISSUE_DC", 1L, "SDC", 2026 });

            migrationBuilder.CreateIndex(
                name: "IX_stock_requests_dc_no",
                table: "stock_requests",
                column: "dc_no");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_stock_requests_dc_no",
                table: "stock_requests");

            migrationBuilder.DeleteData(
                table: "number_sequences",
                keyColumn: "key",
                keyValue: "STOCK_ISSUE_DC");

            migrationBuilder.DropColumn(
                name: "dc_no",
                table: "stock_requests");
        }
    }
}
