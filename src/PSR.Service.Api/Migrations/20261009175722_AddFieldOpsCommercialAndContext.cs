using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PSR.Service.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddFieldOpsCommercialAndContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "amount_payable",
                table: "field_services",
                type: "decimal(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "cause",
                table: "field_services",
                type: "varchar(1000)",
                maxLength: 1000,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "district_id",
                table: "field_services",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "latitude",
                table: "field_services",
                type: "decimal(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "location_label",
                table: "field_services",
                type: "varchar(200)",
                maxLength: 200,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<decimal>(
                name: "longitude",
                table: "field_services",
                type: "decimal(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payment_method",
                table: "field_services",
                type: "varchar(20)",
                maxLength: 20,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "rate_type",
                table: "field_services",
                type: "varchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Customer")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<decimal>(
                name: "service_charge_amount",
                table: "field_services",
                type: "decimal(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "service_charge_description",
                table: "field_services",
                type: "varchar(200)",
                maxLength: 200,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<long>(
                name: "service_charge_id",
                table: "field_services",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "service_charge_tax_percent",
                table: "field_services",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "state_id",
                table: "field_services",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "tax_amount",
                table: "field_services",
                type: "decimal(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "taxable_amount",
                table: "field_services",
                type: "decimal(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "total_amount",
                table: "field_services",
                type: "decimal(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "warranty_status",
                table: "field_services",
                type: "varchar(10)",
                maxLength: 10,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<decimal>(
                name: "gst_percent",
                table: "field_service_lines",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "tax_amount",
                table: "field_service_lines",
                type: "decimal(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "district_id",
                table: "field_sales",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "latitude",
                table: "field_sales",
                type: "decimal(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "location_label",
                table: "field_sales",
                type: "varchar(200)",
                maxLength: 200,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<decimal>(
                name: "longitude",
                table: "field_sales",
                type: "decimal(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payment_method",
                table: "field_sales",
                type: "varchar(20)",
                maxLength: 20,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "rate_type",
                table: "field_sales",
                type: "varchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Customer")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "state_id",
                table: "field_sales",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "tax_amount",
                table: "field_sales",
                type: "decimal(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "taxable_amount",
                table: "field_sales",
                type: "decimal(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "total_amount",
                table: "field_sales",
                type: "decimal(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "gst_percent",
                table: "field_sale_lines",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "tax_amount",
                table: "field_sale_lines",
                type: "decimal(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "indian_states",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    sort_order = table.Column<int>(type: "int", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_indian_states", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "districts",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    state_id = table.Column<int>(type: "int", nullable: false),
                    name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_districts", x => x.id);
                    table.ForeignKey(
                        name: "FK_districts_indian_states_state_id",
                        column: x => x.state_id,
                        principalTable: "indian_states",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_field_services_district_id",
                table: "field_services",
                column: "district_id");

            migrationBuilder.CreateIndex(
                name: "IX_field_services_service_charge_id",
                table: "field_services",
                column: "service_charge_id");

            migrationBuilder.CreateIndex(
                name: "IX_field_services_state_id",
                table: "field_services",
                column: "state_id");

            migrationBuilder.CreateIndex(
                name: "IX_field_sales_district_id",
                table: "field_sales",
                column: "district_id");

            migrationBuilder.CreateIndex(
                name: "IX_field_sales_state_id",
                table: "field_sales",
                column: "state_id");

            migrationBuilder.CreateIndex(
                name: "IX_districts_state_id_name",
                table: "districts",
                columns: new[] { "state_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_indian_states_name",
                table: "indian_states",
                column: "name",
                unique: true);

            // ---------------------------------------------------------------- reference seed
            //
            // Ids are EXPLICIT, and they are the ids hsrtech's own seed happened to produce: it inserted
            // the states alphabetically into an AUTO_INCREMENT table with sort_order 1..36, so every id
            // equals its sort order and Kerala is 17 in both systems. Pinning them here means a legacy
            // service_records row carrying state_id 17 still means Kerala after it is brought across,
            // and it means a dev database, staging and production all agree on what 17 is.
            //
            // Districts are seeded for Kerala only, which is where the shop works. The other 35 states
            // exist so a record from outside Kerala can still name its state; their districts can be
            // seeded the day somebody files a visit in one.
            migrationBuilder.InsertData(
                table: "indian_states",
                columns: new[] { "id", "name", "sort_order" },
                values: new object[,]
                {
                    { 1, "Andaman and Nicobar Islands", 1 },
                    { 2, "Andhra Pradesh", 2 },
                    { 3, "Arunachal Pradesh", 3 },
                    { 4, "Assam", 4 },
                    { 5, "Bihar", 5 },
                    { 6, "Chandigarh", 6 },
                    { 7, "Chhattisgarh", 7 },
                    { 8, "Dadra and Nagar Haveli and Daman and Diu", 8 },
                    { 9, "Delhi", 9 },
                    { 10, "Goa", 10 },
                    { 11, "Gujarat", 11 },
                    { 12, "Haryana", 12 },
                    { 13, "Himachal Pradesh", 13 },
                    { 14, "Jammu and Kashmir", 14 },
                    { 15, "Jharkhand", 15 },
                    { 16, "Karnataka", 16 },
                    { 17, "Kerala", 17 },
                    { 18, "Ladakh", 18 },
                    { 19, "Lakshadweep", 19 },
                    { 20, "Madhya Pradesh", 20 },
                    { 21, "Maharashtra", 21 },
                    { 22, "Manipur", 22 },
                    { 23, "Meghalaya", 23 },
                    { 24, "Mizoram", 24 },
                    { 25, "Nagaland", 25 },
                    { 26, "Odisha", 26 },
                    { 27, "Puducherry", 27 },
                    { 28, "Punjab", 28 },
                    { 29, "Rajasthan", 29 },
                    { 30, "Sikkim", 30 },
                    { 31, "Tamil Nadu", 31 },
                    { 32, "Telangana", 32 },
                    { 33, "Tripura", 33 },
                    { 34, "Uttar Pradesh", 34 },
                    { 35, "Uttarakhand", 35 },
                    { 36, "West Bengal", 36 }
                });

            migrationBuilder.InsertData(
                table: "districts",
                columns: new[] { "id", "name", "state_id" },
                values: new object[,]
                {
                    { 1, "Thiruvananthapuram", 17 },
                    { 2, "Kollam", 17 },
                    { 3, "Pathanamthitta", 17 },
                    { 4, "Alappuzha", 17 },
                    { 5, "Kottayam", 17 },
                    { 6, "Idukki", 17 },
                    { 7, "Ernakulam", 17 },
                    { 8, "Thrissur", 17 },
                    { 9, "Palakkad", 17 },
                    { 10, "Malappuram", 17 },
                    { 11, "Kozhikode", 17 },
                    { 12, "Wayanad", 17 },
                    { 13, "Kannur", 17 },
                    { 14, "Kasaragod", 17 }
                });

            // ---------------------------------------------------------------- backfill existing totals
            //
            // The totals above are stored, not summed on read, so rows written before this migration
            // would otherwise all report zero - and a historical visit reading as free is worse than one
            // reading as unpriced.
            //
            // Tax stays zero for them, deliberately. Those lines were priced at the customer rate with
            // no GST percentage captured, and the part's CURRENT rate is not evidence of what was
            // charged a year ago: inventing a tax figure from today's master would make the old record
            // look authoritative about something nobody recorded. Taxable and total therefore agree, and
            // amount_payable follows the total because warranty status was not captured either.
            migrationBuilder.Sql("""
                UPDATE field_services f
                   SET f.taxable_amount = COALESCE((
                           SELECT SUM(l.amount) FROM field_service_lines l
                            WHERE l.field_service_id = f.id), 0),
                       f.tax_amount = 0,
                       f.total_amount = CEIL(COALESCE((
                           SELECT SUM(l.amount) FROM field_service_lines l
                            WHERE l.field_service_id = f.id), 0)),
                       f.amount_payable = CEIL(COALESCE((
                           SELECT SUM(l.amount) FROM field_service_lines l
                            WHERE l.field_service_id = f.id), 0))
                """);

            migrationBuilder.Sql("""
                UPDATE field_sales s
                   SET s.taxable_amount = COALESCE((
                           SELECT SUM(l.amount) FROM field_sale_lines l
                            WHERE l.field_sale_id = s.id), 0),
                       s.tax_amount = 0,
                       s.total_amount = CEIL(COALESCE((
                           SELECT SUM(l.amount) FROM field_sale_lines l
                            WHERE l.field_sale_id = s.id), 0))
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_field_sales_districts_district_id",
                table: "field_sales",
                column: "district_id",
                principalTable: "districts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_field_sales_indian_states_state_id",
                table: "field_sales",
                column: "state_id",
                principalTable: "indian_states",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_field_services_districts_district_id",
                table: "field_services",
                column: "district_id",
                principalTable: "districts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_field_services_indian_states_state_id",
                table: "field_services",
                column: "state_id",
                principalTable: "indian_states",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_field_services_service_charges_service_charge_id",
                table: "field_services",
                column: "service_charge_id",
                principalTable: "service_charges",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_field_sales_districts_district_id",
                table: "field_sales");

            migrationBuilder.DropForeignKey(
                name: "FK_field_sales_indian_states_state_id",
                table: "field_sales");

            migrationBuilder.DropForeignKey(
                name: "FK_field_services_districts_district_id",
                table: "field_services");

            migrationBuilder.DropForeignKey(
                name: "FK_field_services_indian_states_state_id",
                table: "field_services");

            migrationBuilder.DropForeignKey(
                name: "FK_field_services_service_charges_service_charge_id",
                table: "field_services");

            migrationBuilder.DropTable(
                name: "districts");

            migrationBuilder.DropTable(
                name: "indian_states");

            migrationBuilder.DropIndex(
                name: "IX_field_services_district_id",
                table: "field_services");

            migrationBuilder.DropIndex(
                name: "IX_field_services_service_charge_id",
                table: "field_services");

            migrationBuilder.DropIndex(
                name: "IX_field_services_state_id",
                table: "field_services");

            migrationBuilder.DropIndex(
                name: "IX_field_sales_district_id",
                table: "field_sales");

            migrationBuilder.DropIndex(
                name: "IX_field_sales_state_id",
                table: "field_sales");

            migrationBuilder.DropColumn(
                name: "amount_payable",
                table: "field_services");

            migrationBuilder.DropColumn(
                name: "cause",
                table: "field_services");

            migrationBuilder.DropColumn(
                name: "district_id",
                table: "field_services");

            migrationBuilder.DropColumn(
                name: "latitude",
                table: "field_services");

            migrationBuilder.DropColumn(
                name: "location_label",
                table: "field_services");

            migrationBuilder.DropColumn(
                name: "longitude",
                table: "field_services");

            migrationBuilder.DropColumn(
                name: "payment_method",
                table: "field_services");

            migrationBuilder.DropColumn(
                name: "rate_type",
                table: "field_services");

            migrationBuilder.DropColumn(
                name: "service_charge_amount",
                table: "field_services");

            migrationBuilder.DropColumn(
                name: "service_charge_description",
                table: "field_services");

            migrationBuilder.DropColumn(
                name: "service_charge_id",
                table: "field_services");

            migrationBuilder.DropColumn(
                name: "service_charge_tax_percent",
                table: "field_services");

            migrationBuilder.DropColumn(
                name: "state_id",
                table: "field_services");

            migrationBuilder.DropColumn(
                name: "tax_amount",
                table: "field_services");

            migrationBuilder.DropColumn(
                name: "taxable_amount",
                table: "field_services");

            migrationBuilder.DropColumn(
                name: "total_amount",
                table: "field_services");

            migrationBuilder.DropColumn(
                name: "warranty_status",
                table: "field_services");

            migrationBuilder.DropColumn(
                name: "gst_percent",
                table: "field_service_lines");

            migrationBuilder.DropColumn(
                name: "tax_amount",
                table: "field_service_lines");

            migrationBuilder.DropColumn(
                name: "district_id",
                table: "field_sales");

            migrationBuilder.DropColumn(
                name: "latitude",
                table: "field_sales");

            migrationBuilder.DropColumn(
                name: "location_label",
                table: "field_sales");

            migrationBuilder.DropColumn(
                name: "longitude",
                table: "field_sales");

            migrationBuilder.DropColumn(
                name: "payment_method",
                table: "field_sales");

            migrationBuilder.DropColumn(
                name: "rate_type",
                table: "field_sales");

            migrationBuilder.DropColumn(
                name: "state_id",
                table: "field_sales");

            migrationBuilder.DropColumn(
                name: "tax_amount",
                table: "field_sales");

            migrationBuilder.DropColumn(
                name: "taxable_amount",
                table: "field_sales");

            migrationBuilder.DropColumn(
                name: "total_amount",
                table: "field_sales");

            migrationBuilder.DropColumn(
                name: "gst_percent",
                table: "field_sale_lines");

            migrationBuilder.DropColumn(
                name: "tax_amount",
                table: "field_sale_lines");
        }
    }
}
