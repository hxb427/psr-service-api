using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PSR.Service.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSpareSaleReturns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "spare_sale_returns",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    spare_sale_id = table.Column<long>(type: "bigint", nullable: false),
                    return_no = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    return_date = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    reason = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    created_by_user_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_spare_sale_returns", x => x.id);
                    table.ForeignKey(
                        name: "FK_spare_sale_returns_spare_sales_spare_sale_id",
                        column: x => x.spare_sale_id,
                        principalTable: "spare_sales",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "spare_sale_return_lines",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    spare_sale_return_id = table.Column<long>(type: "bigint", nullable: false),
                    part_id = table.Column<long>(type: "bigint", nullable: false),
                    item_code = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    qty = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_spare_sale_return_lines", x => x.id);
                    table.ForeignKey(
                        name: "FK_spare_sale_return_lines_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_spare_sale_return_lines_spare_sale_returns_spare_sale_return~",
                        column: x => x.spare_sale_return_id,
                        principalTable: "spare_sale_returns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_spare_sale_return_lines_part_id",
                table: "spare_sale_return_lines",
                column: "part_id");

            migrationBuilder.CreateIndex(
                name: "IX_spare_sale_return_lines_spare_sale_return_id",
                table: "spare_sale_return_lines",
                column: "spare_sale_return_id");

            migrationBuilder.CreateIndex(
                name: "IX_spare_sale_returns_return_no",
                table: "spare_sale_returns",
                column: "return_no",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_spare_sale_returns_spare_sale_id",
                table: "spare_sale_returns",
                column: "spare_sale_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "spare_sale_return_lines");

            migrationBuilder.DropTable(
                name: "spare_sale_returns");
        }
    }
}
