using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PSR.Service.Api.Migrations
{
    /// <summary>Serial numbers on a counter sale, so a serial-tracked item sold over the counter is
    /// traceable the way one issued to a technician already was.
    ///
    /// One table, and it holds CLAIMS rather than tracked units. The counter names the boxes while the
    /// customer is still standing there, which is long before anybody presses Mark as sold — and
    /// mark-sold is where tracking starts, turning each row here into a <c>component_serials</c> unit
    /// deployed at the party. A sale that is never marked sold therefore leaves the serial ledger
    /// untouched, which is what makes a mistyped serial on a draft a correction and not a hunt.
    ///
    /// Deliberately NOT unique on (part_id, serial_number): two unsold sales are allowed to hold the
    /// same claim for as long as it takes the counter to decide which keeps it, and a constraint here
    /// would turn that conversation into a 500. The uniqueness that matters already exists, on
    /// <c>component_serials</c>, which is where a claim becomes a unit.
    ///
    /// No backfill, because there is nothing to backfill from: warehouse stock of a tracked part is
    /// bought in as a quantity and has never been named unit by unit, so no past sale knows which
    /// boxes went out. Sales already on the books keep their quantities; an UNSOLD one of a tracked
    /// part now has to be edited to name its units before it can be handed over, which is the point.</summary>
    public partial class AddSpareSaleSerials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "spare_sale_line_serials",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    spare_sale_line_id = table.Column<long>(type: "bigint", nullable: false),
                    part_id = table.Column<long>(type: "bigint", nullable: false),
                    serial_number = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_spare_sale_line_serials", x => x.id);
                    table.ForeignKey(
                        name: "FK_spare_sale_line_serials_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_spare_sale_line_serials_spare_sale_lines_spare_sale_line_id",
                        column: x => x.spare_sale_line_id,
                        principalTable: "spare_sale_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_spare_sale_line_serials_part_id_serial_number",
                table: "spare_sale_line_serials",
                columns: new[] { "part_id", "serial_number" });

            migrationBuilder.CreateIndex(
                name: "IX_spare_sale_line_serials_spare_sale_line_id",
                table: "spare_sale_line_serials",
                column: "spare_sale_line_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "spare_sale_line_serials");
        }
    }
}
