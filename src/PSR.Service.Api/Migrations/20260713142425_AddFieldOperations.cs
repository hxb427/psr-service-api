using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace PSR.Service.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddFieldOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "courier",
                table: "stock_returns",
                type: "varchar(80)",
                maxLength: 80,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "tracking_no",
                table: "stock_returns",
                type: "varchar(80)",
                maxLength: 80,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "field_sales",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    sale_no = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    technician_id = table.Column<long>(type: "bigint", nullable: false),
                    customer_name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    phone = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    place = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    remarks = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    created_by_user_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_sales", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "field_services",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    service_no = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    technician_id = table.Column<long>(type: "bigint", nullable: false),
                    customer_name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    phone = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    place = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    machine_serial = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    complaint = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    work_done = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    remarks = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    created_by_user_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_services", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "stock_issue_acks",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    stock_movement_id = table.Column<long>(type: "bigint", nullable: false),
                    qty_received = table.Column<int>(type: "int", nullable: false),
                    qty_defective = table.Column<int>(type: "int", nullable: false),
                    qty_missing = table.Column<int>(type: "int", nullable: false),
                    remarks = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    acked_by_user_id = table.Column<long>(type: "bigint", nullable: false),
                    acked_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_issue_acks", x => x.id);
                    table.ForeignKey(
                        name: "FK_stock_issue_acks_stock_movements_stock_movement_id",
                        column: x => x.stock_movement_id,
                        principalTable: "stock_movements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "stock_issue_serials",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    stock_movement_id = table.Column<long>(type: "bigint", nullable: false),
                    component_serial_id = table.Column<long>(type: "bigint", nullable: false),
                    ack_status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_issue_serials", x => x.id);
                    table.ForeignKey(
                        name: "FK_stock_issue_serials_component_serials_component_serial_id",
                        column: x => x.component_serial_id,
                        principalTable: "component_serials",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_stock_issue_serials_stock_movements_stock_movement_id",
                        column: x => x.stock_movement_id,
                        principalTable: "stock_movements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "stock_return_serials",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    stock_return_id = table.Column<long>(type: "bigint", nullable: false),
                    component_serial_id = table.Column<long>(type: "bigint", nullable: false),
                    defective = table.Column<bool>(type: "tinyint(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_return_serials", x => x.id);
                    table.ForeignKey(
                        name: "FK_stock_return_serials_component_serials_component_serial_id",
                        column: x => x.component_serial_id,
                        principalTable: "component_serials",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_stock_return_serials_stock_returns_stock_return_id",
                        column: x => x.stock_return_id,
                        principalTable: "stock_returns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "technician_transfers",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    transfer_no = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    from_technician_id = table.Column<long>(type: "bigint", nullable: false),
                    to_technician_id = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    remarks = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    acknowledged_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_technician_transfers", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "field_sale_lines",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    field_sale_id = table.Column<long>(type: "bigint", nullable: false),
                    part_id = table.Column<long>(type: "bigint", nullable: false),
                    qty = table.Column<int>(type: "int", nullable: false),
                    unit_price = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: false),
                    amount = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: false),
                    serial_no = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_sale_lines", x => x.id);
                    table.ForeignKey(
                        name: "FK_field_sale_lines_field_sales_field_sale_id",
                        column: x => x.field_sale_id,
                        principalTable: "field_sales",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_field_sale_lines_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "field_service_lines",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    field_service_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    part_id = table.Column<long>(type: "bigint", nullable: false),
                    qty = table.Column<int>(type: "int", nullable: false),
                    unit_price = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: false),
                    amount = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: false),
                    serial_no = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    defective = table.Column<bool>(type: "tinyint(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_service_lines", x => x.id);
                    table.ForeignKey(
                        name: "FK_field_service_lines_field_services_field_service_id",
                        column: x => x.field_service_id,
                        principalTable: "field_services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_field_service_lines_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "technician_transfer_lines",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    transfer_id = table.Column<long>(type: "bigint", nullable: false),
                    part_id = table.Column<long>(type: "bigint", nullable: false),
                    qty = table.Column<int>(type: "int", nullable: false),
                    qty_received = table.Column<int>(type: "int", nullable: true),
                    qty_defective = table.Column<int>(type: "int", nullable: true),
                    qty_missing = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_technician_transfer_lines", x => x.id);
                    table.ForeignKey(
                        name: "FK_technician_transfer_lines_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_technician_transfer_lines_technician_transfers_transfer_id",
                        column: x => x.transfer_id,
                        principalTable: "technician_transfers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "technician_transfer_serials",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    transfer_line_id = table.Column<long>(type: "bigint", nullable: false),
                    component_serial_id = table.Column<long>(type: "bigint", nullable: false),
                    ack_status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_technician_transfer_serials", x => x.id);
                    table.ForeignKey(
                        name: "FK_technician_transfer_serials_component_serials_component_seri~",
                        column: x => x.component_serial_id,
                        principalTable: "component_serials",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_technician_transfer_serials_technician_transfer_lines_transf~",
                        column: x => x.transfer_line_id,
                        principalTable: "technician_transfer_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.InsertData(
                table: "number_sequences",
                columns: new[] { "key", "next_value", "prefix", "year" },
                values: new object[,]
                {
                    { "FIELD_SALE", 1L, "FSL", null },
                    { "FIELD_SERVICE", 1L, "FSV", null },
                    { "TRANSFER", 1L, "TRF", null }
                });

            migrationBuilder.CreateIndex(
                name: "IX_field_sale_lines_field_sale_id",
                table: "field_sale_lines",
                column: "field_sale_id");

            migrationBuilder.CreateIndex(
                name: "IX_field_sale_lines_part_id",
                table: "field_sale_lines",
                column: "part_id");

            migrationBuilder.CreateIndex(
                name: "IX_field_sales_created_at",
                table: "field_sales",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_field_sales_sale_no",
                table: "field_sales",
                column: "sale_no",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_sales_technician_id",
                table: "field_sales",
                column: "technician_id");

            migrationBuilder.CreateIndex(
                name: "IX_field_service_lines_field_service_id",
                table: "field_service_lines",
                column: "field_service_id");

            migrationBuilder.CreateIndex(
                name: "IX_field_service_lines_part_id",
                table: "field_service_lines",
                column: "part_id");

            migrationBuilder.CreateIndex(
                name: "IX_field_services_created_at",
                table: "field_services",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_field_services_service_no",
                table: "field_services",
                column: "service_no",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_services_technician_id",
                table: "field_services",
                column: "technician_id");

            migrationBuilder.CreateIndex(
                name: "IX_stock_issue_acks_stock_movement_id",
                table: "stock_issue_acks",
                column: "stock_movement_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_stock_issue_serials_component_serial_id",
                table: "stock_issue_serials",
                column: "component_serial_id");

            migrationBuilder.CreateIndex(
                name: "IX_stock_issue_serials_stock_movement_id",
                table: "stock_issue_serials",
                column: "stock_movement_id");

            migrationBuilder.CreateIndex(
                name: "IX_stock_return_serials_component_serial_id",
                table: "stock_return_serials",
                column: "component_serial_id");

            migrationBuilder.CreateIndex(
                name: "IX_stock_return_serials_stock_return_id",
                table: "stock_return_serials",
                column: "stock_return_id");

            migrationBuilder.CreateIndex(
                name: "IX_technician_transfer_lines_part_id",
                table: "technician_transfer_lines",
                column: "part_id");

            migrationBuilder.CreateIndex(
                name: "IX_technician_transfer_lines_transfer_id",
                table: "technician_transfer_lines",
                column: "transfer_id");

            migrationBuilder.CreateIndex(
                name: "IX_technician_transfer_serials_component_serial_id",
                table: "technician_transfer_serials",
                column: "component_serial_id");

            migrationBuilder.CreateIndex(
                name: "IX_technician_transfer_serials_transfer_line_id",
                table: "technician_transfer_serials",
                column: "transfer_line_id");

            migrationBuilder.CreateIndex(
                name: "IX_technician_transfers_from_technician_id",
                table: "technician_transfers",
                column: "from_technician_id");

            migrationBuilder.CreateIndex(
                name: "IX_technician_transfers_status",
                table: "technician_transfers",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_technician_transfers_to_technician_id",
                table: "technician_transfers",
                column: "to_technician_id");

            migrationBuilder.CreateIndex(
                name: "IX_technician_transfers_transfer_no",
                table: "technician_transfers",
                column: "transfer_no",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "field_sale_lines");

            migrationBuilder.DropTable(
                name: "field_service_lines");

            migrationBuilder.DropTable(
                name: "stock_issue_acks");

            migrationBuilder.DropTable(
                name: "stock_issue_serials");

            migrationBuilder.DropTable(
                name: "stock_return_serials");

            migrationBuilder.DropTable(
                name: "technician_transfer_serials");

            migrationBuilder.DropTable(
                name: "field_sales");

            migrationBuilder.DropTable(
                name: "field_services");

            migrationBuilder.DropTable(
                name: "technician_transfer_lines");

            migrationBuilder.DropTable(
                name: "technician_transfers");

            migrationBuilder.DeleteData(
                table: "number_sequences",
                keyColumn: "key",
                keyValue: "FIELD_SALE");

            migrationBuilder.DeleteData(
                table: "number_sequences",
                keyColumn: "key",
                keyValue: "FIELD_SERVICE");

            migrationBuilder.DeleteData(
                table: "number_sequences",
                keyColumn: "key",
                keyValue: "TRANSFER");

            migrationBuilder.DropColumn(
                name: "courier",
                table: "stock_returns");

            migrationBuilder.DropColumn(
                name: "tracking_no",
                table: "stock_returns");
        }
    }
}
