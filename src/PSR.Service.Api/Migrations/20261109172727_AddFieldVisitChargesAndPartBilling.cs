using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PSR.Service.Api.Migrations
{
    /// <summary>Three changes to what a field visit may bill, and one data move.
    ///
    /// A visit used to carry exactly one service charge, in four columns of its own. Those columns move
    /// into <c>field_service_charges</c> — one row per charge — and are dropped, so the charges a visit
    /// billed live in one place rather than two that must agree.
    ///
    /// <c>field_service_lines.billable</c> says whether a part's money is on the bill. Backfilled to
    /// what each existing row actually billed (fitted parts yes, collected units no), because the
    /// totals already stored on those visits were computed that way — a blanket default would have to
    /// be wrong for one of the two, and the stored total would stop reconciling with its own lines.
    ///
    /// <c>visit_charge_*</c> is the standing attendance charge. Added, deliberately NOT renamed from
    /// <c>service_charge_amount</c> as the scaffolder proposed: the two are different charges, and a
    /// rename would make every past service charge read as an attendance fee nobody charged.</summary>
    public partial class AddFieldVisitChargesAndPartBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "billable",
                table: "field_service_lines",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            // What each existing line actually billed. A fitted part went on the bill and a collected
            // unit never did, which is exactly the rule the stored visit totals were computed under.
            migrationBuilder.Sql(
                "UPDATE field_service_lines SET billable = (kind = 'Used');");

            migrationBuilder.AddColumn<bool>(
                name: "visit_charge_applied",
                table: "field_services",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "visit_charge_amount",
                table: "field_services",
                type: "decimal(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "field_service_charges",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    field_service_id = table.Column<long>(type: "bigint", nullable: false),
                    service_charge_id = table.Column<long>(type: "bigint", nullable: false),
                    description = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    amount = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: false),
                    tax_percent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    tax_amount = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_service_charges", x => x.id);
                    table.ForeignKey(
                        name: "FK_field_service_charges_field_services_field_service_id",
                        column: x => x.field_service_id,
                        principalTable: "field_services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_field_service_charges_service_charges_service_charge_id",
                        column: x => x.service_charge_id,
                        principalTable: "service_charges",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_field_service_charges_field_service_id_service_charge_id",
                table: "field_service_charges",
                columns: new[] { "field_service_id", "service_charge_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_service_charges_service_charge_id",
                table: "field_service_charges",
                column: "service_charge_id");

            // The one charge each existing visit billed, moved across before its columns go. The tax
            // is recomputed rather than carried, because the old record never stored it per charge —
            // same rounding as FieldVisitRules.Tax, to the paise, which is what the visit's own
            // stored tax figure was built from.
            migrationBuilder.Sql(
                """
                INSERT INTO field_service_charges
                    (field_service_id, service_charge_id, description, amount, tax_percent, tax_amount)
                SELECT id, service_charge_id, service_charge_description,
                       service_charge_amount, service_charge_tax_percent,
                       ROUND(service_charge_amount * service_charge_tax_percent / 100, 2)
                FROM field_services
                WHERE service_charge_id IS NOT NULL;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_field_services_service_charges_service_charge_id",
                table: "field_services");

            migrationBuilder.DropIndex(
                name: "IX_field_services_service_charge_id",
                table: "field_services");

            migrationBuilder.DropColumn(
                name: "service_charge_id",
                table: "field_services");

            migrationBuilder.DropColumn(
                name: "service_charge_description",
                table: "field_services");

            migrationBuilder.DropColumn(
                name: "service_charge_amount",
                table: "field_services");

            migrationBuilder.DropColumn(
                name: "service_charge_tax_percent",
                table: "field_services");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "service_charge_id",
                table: "field_services",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "service_charge_description",
                table: "field_services",
                type: "varchar(200)",
                maxLength: 200,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<decimal>(
                name: "service_charge_amount",
                table: "field_services",
                type: "decimal(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "service_charge_tax_percent",
                table: "field_services",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            // Back into the four columns, rolled up the same way the API rolls charges up for a client
            // that only knows about one: the amount sums, the description joins, and the id and the
            // rate survive only where there was exactly one charge — neither has an honest value
            // across two. A visit that billed several charges therefore comes back as one line for
            // what the customer paid, which is as faithful as a single-charge column can be.
            migrationBuilder.Sql(
                """
                UPDATE field_services f
                JOIN (
                    SELECT field_service_id,
                           SUM(amount) AS amount,
                           GROUP_CONCAT(description ORDER BY id SEPARATOR ' + ') AS description,
                           CASE WHEN COUNT(*) = 1 THEN MIN(service_charge_id) END AS charge_id,
                           CASE WHEN COUNT(*) = 1 THEN MIN(tax_percent) ELSE 0 END AS tax_percent
                    FROM field_service_charges
                    GROUP BY field_service_id
                ) c ON c.field_service_id = f.id
                SET f.service_charge_id = c.charge_id,
                    f.service_charge_description = c.description,
                    f.service_charge_amount = c.amount,
                    f.service_charge_tax_percent = c.tax_percent;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_field_services_service_charge_id",
                table: "field_services",
                column: "service_charge_id");

            migrationBuilder.AddForeignKey(
                name: "FK_field_services_service_charges_service_charge_id",
                table: "field_services",
                column: "service_charge_id",
                principalTable: "service_charges",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.DropTable(
                name: "field_service_charges");

            migrationBuilder.DropColumn(
                name: "visit_charge_amount",
                table: "field_services");

            migrationBuilder.DropColumn(
                name: "visit_charge_applied",
                table: "field_services");

            migrationBuilder.DropColumn(
                name: "billable",
                table: "field_service_lines");
        }
    }
}
