using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PSR.Service.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSpareSaleSoldStamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "sold_at",
                table: "spare_sales",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "sold_by_user_id",
                table: "spare_sales",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_spare_sales_sold_at",
                table: "spare_sales",
                column: "sold_at");

            // Backfill. Until now the tax invoice WAS the moment the goods left the warehouse, so every
            // already-invoiced sale has had its stock drawn down. Without this they would all read as
            // unsold, and the availability calculation — which now treats an unsold sale as an
            // outstanding claim on the shelf — would count that stock as owed a second time, forever.
            // inv_date is the date it actually went out; sale_date only stands in where the stamp is
            // missing. The user who entered the sale is the closest attribution the old rows carry.
            migrationBuilder.Sql(@"
UPDATE `spare_sales`
SET `sold_at` = COALESCE(`inv_date`, `sale_date`), `sold_by_user_id` = `created_by_user_id`
WHERE `status` = 'Invoiced'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_spare_sales_sold_at",
                table: "spare_sales");

            migrationBuilder.DropColumn(
                name: "sold_at",
                table: "spare_sales");

            migrationBuilder.DropColumn(
                name: "sold_by_user_id",
                table: "spare_sales");
        }
    }
}
