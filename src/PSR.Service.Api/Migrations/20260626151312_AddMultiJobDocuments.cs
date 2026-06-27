using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PSR.Service.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiJobDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "inv_date",
                table: "services",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "inv_no",
                table: "services",
                type: "varchar(40)",
                maxLength: 40,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "pi_date",
                table: "services",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pi_no",
                table: "services",
                type: "varchar(40)",
                maxLength: 40,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "remarks",
                table: "service_document_lines",
                type: "varchar(300)",
                maxLength: 300,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "service_challan",
                table: "service_document_lines",
                type: "varchar(50)",
                maxLength: 50,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<long>(
                name: "service_job_id",
                table: "service_document_lines",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "warranty",
                table: "service_document_lines",
                type: "varchar(30)",
                maxLength: 30,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_services_pi_no",
                table: "services",
                column: "pi_no");

            migrationBuilder.CreateIndex(
                name: "IX_service_document_lines_service_job_id",
                table: "service_document_lines",
                column: "service_job_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_services_pi_no",
                table: "services");

            migrationBuilder.DropIndex(
                name: "IX_service_document_lines_service_job_id",
                table: "service_document_lines");

            migrationBuilder.DropColumn(
                name: "inv_date",
                table: "services");

            migrationBuilder.DropColumn(
                name: "inv_no",
                table: "services");

            migrationBuilder.DropColumn(
                name: "pi_date",
                table: "services");

            migrationBuilder.DropColumn(
                name: "pi_no",
                table: "services");

            migrationBuilder.DropColumn(
                name: "remarks",
                table: "service_document_lines");

            migrationBuilder.DropColumn(
                name: "service_challan",
                table: "service_document_lines");

            migrationBuilder.DropColumn(
                name: "service_job_id",
                table: "service_document_lines");

            migrationBuilder.DropColumn(
                name: "warranty",
                table: "service_document_lines");
        }
    }
}
