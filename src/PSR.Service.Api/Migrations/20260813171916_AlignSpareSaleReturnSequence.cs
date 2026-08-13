using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PSR.Service.Api.Migrations
{
    /// <summary>
    /// Realigns the model snapshot with the SPARE_SALE_RETURN sequence row.
    ///
    /// The row was created by AddSpareSaleReturns but its key was never added to the HasData list in
    /// StockConfigurations, so the snapshot did not know about it. Adding it there is what this
    /// migration accompanies — without it, the next migration generated for any unrelated change
    /// would have emitted a DeleteData for this row and quietly broken spare-sale returns
    /// ("Number sequence 'SPARE_SALE_RETURN' is not configured").
    ///
    /// The insert EF generated here is replaced by an idempotent one: every existing database
    /// already has this row, so a plain INSERT would fail on the duplicate key. The counter is left
    /// alone on conflict — resetting a live sequence to 1 would hand out numbers already in use.
    /// </summary>
    public partial class AlignSpareSaleReturnSequence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                INSERT INTO `number_sequences` (`key`, `next_value`, `prefix`, `year`)
                VALUES ('SPARE_SALE_RETURN', 1, 'SRT', NULL)
                ON DUPLICATE KEY UPDATE `key` = `key`;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty. AddSpareSaleReturns owns this row; deleting it on the way down
            // would leave a database that still runs the spare-sale-returns code without a sequence.
        }
    }
}
