using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PSR.Service.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddFieldCallRegister : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "field_calls",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    call_no = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    call_type = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    engineer_id = table.Column<long>(type: "bigint", nullable: false),
                    call_received_date = table.Column<DateTime>(type: "date", nullable: false),
                    locality = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    customer_name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    customer_id = table.Column<long>(type: "bigint", nullable: true),
                    customer_address = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    call_reference_no = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    machine_model = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    machine_serial = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    software_version = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    attended_date = table.Column<DateTime>(type: "date", nullable: true),
                    call_status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    remarks = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_calls", x => x.id);
                    table.ForeignKey(
                        name: "FK_field_calls_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_calls_users_engineer_id",
                        column: x => x.engineer_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.InsertData(
                table: "number_sequences",
                columns: new[] { "key", "next_value", "prefix", "year" },
                values: new object[] { "FIELD_CALL", 1L, "CAL", null });

            migrationBuilder.CreateIndex(
                name: "IX_field_calls_call_no",
                table: "field_calls",
                column: "call_no",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_calls_call_received_date",
                table: "field_calls",
                column: "call_received_date");

            migrationBuilder.CreateIndex(
                name: "IX_field_calls_call_status",
                table: "field_calls",
                column: "call_status");

            migrationBuilder.CreateIndex(
                name: "IX_field_calls_call_type",
                table: "field_calls",
                column: "call_type");

            migrationBuilder.CreateIndex(
                name: "IX_field_calls_customer_id",
                table: "field_calls",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "IX_field_calls_engineer_id",
                table: "field_calls",
                column: "engineer_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "field_calls");

            migrationBuilder.DeleteData(
                table: "number_sequences",
                keyColumn: "key",
                keyValue: "FIELD_CALL");
        }
    }
}
