using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PSR.Service.Api.Migrations
{
    /// <summary>Clears the serial rows sitting on sales that have not been handed over.
    ///
    /// No schema change — this is the data half of moving serial capture from sale ENTRY to MARK-SOLD.
    /// The other half is in SpareSalesEndpoints, where the units now arrive in the mark-sold body.
    ///
    /// On the previous build a sale was entered WITH its serials, and those rows were CLAIMS: a note
    /// that this sale intended to send these boxes, with nothing in the serial ledger behind them. A
    /// row in that table now means the opposite — that the unit went out on this line — so every claim
    /// left on an unsold sale is a statement that is simply false, and it would be read as one: the
    /// sale's detail pane lists these numbers as the units that left.
    ///
    /// Only sales that have NOT been handed over are touched, and the exclusion is the point: a sale
    /// with <c>sold_at</c> set has gone, and its rows are the record of which boxes went. They agree
    /// with the <c>component_serials</c> rows that mark-sold created, and deleting them would throw
    /// away the only link between a unit and the sale it left on. A cancelled or deleted sale never
    /// completed, so its claims are false in the same way an unsold one's are and they go with them —
    /// not named separately below, because "not handed over" already covers them.
    ///
    /// Nothing in the serial ledger is touched either way. A claim never had a
    /// <c>component_serials</c> row behind it, which is exactly why it was safe to type a wrong one on
    /// a draft sale, and exactly why these rows can go.</summary>
    public partial class DropSerialClaimsOnUnsoldSales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Joined up to the sale through the line, because the serial row carries no sale id of its
            // own — see SpareSaleLineSerial for why it is reached this way.
            migrationBuilder.Sql(@"
DELETE `s`
FROM `spare_sale_line_serials` AS `s`
JOIN `spare_sale_lines` AS `l` ON `l`.`id` = `s`.`spare_sale_line_id`
JOIN `spare_sales` AS `sa` ON `sa`.`id` = `l`.`spare_sale_id`
WHERE `sa`.`sold_at` IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty. These rows said which boxes a sale INTENDED to send, and that
            // intention is recorded nowhere else — there is nothing to rebuild them from. Rolling back
            // leaves those sales naming no units, which is what the previous build showed for any sale
            // entered before it asked for serials at all.
        }
    }
}
