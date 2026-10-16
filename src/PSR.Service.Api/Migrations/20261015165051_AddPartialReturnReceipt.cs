using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PSR.Service.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPartialReturnReceipt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "qty_missing",
                table: "stock_returns",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "qty_received",
                table: "stock_returns",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ack_status",
                table: "stock_return_serials",
                type: "varchar(16)",
                maxLength: 16,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "acked_at",
                table: "stock_return_serials",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "acked_by_user_id",
                table: "stock_return_serials",
                type: "bigint",
                nullable: true);

            // Shipments booked in before partial receipt existed were all-or-nothing, so what each
            // one received is knowable from the status it closed with. Left unfilled they would every
            // one of them read as "0 of n received" on the desktop, which is a worse lie than the
            // all-or-nothing they actually were.
            migrationBuilder.Sql("UPDATE stock_returns SET qty_received = qty WHERE status = 'Stocked';");
            migrationBuilder.Sql("UPDATE stock_returns SET qty_missing = qty WHERE status = 'Missing';");
            migrationBuilder.Sql(
                "UPDATE stock_return_serials s JOIN stock_returns r ON r.id = s.stock_return_id "
                + "SET s.ack_status = CASE r.status WHEN 'Stocked' THEN 'Received' ELSE 'Missing' END "
                + "WHERE r.status <> 'Pending';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "qty_missing",
                table: "stock_returns");

            migrationBuilder.DropColumn(
                name: "qty_received",
                table: "stock_returns");

            migrationBuilder.DropColumn(
                name: "ack_status",
                table: "stock_return_serials");

            migrationBuilder.DropColumn(
                name: "acked_at",
                table: "stock_return_serials");

            migrationBuilder.DropColumn(
                name: "acked_by_user_id",
                table: "stock_return_serials");
        }
    }
}
