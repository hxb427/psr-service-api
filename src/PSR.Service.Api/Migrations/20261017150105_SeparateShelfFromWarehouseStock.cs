using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PSR.Service.Api.Migrations
{
    /// <summary>The shelf stops being part of the warehouse count.
    ///
    /// Until now a unit kept off a finished job added one to the warehouse quantity of its PS code,
    /// and SHELFED was simply the name for "warehouse stock that happens to be named". That made a
    /// serviced machine indistinguishable from one off a supplier's invoice: the same number, against
    /// the same code, reordered against and sold across the counter as new.
    ///
    /// Two pools from here on. The WAREHOUSE is a quantity - stock bought in, in stock_balances, not
    /// named unit by unit until it goes out. The SHELF is a set of units and nothing else: whichever
    /// component_serials rows are SHELFED in the service centre's custody. Both can be issued to a
    /// technician or handed over as a replacement; only warehouse stock can be sold as goods.
    ///
    /// The backfill below is the half that cannot be skipped. Every unit already on the shelf got
    /// there through a path that credited the warehouse, so leaving the quantity where it is would
    /// count each of those machines twice from here on - once as a named unit on the shelf, once as
    /// an anonymous quantity in the warehouse - and issuing one off the shelf would debit neither.
    /// The warehouse figure for an affected part drops by the number of units on its shelf. That is
    /// the intended outcome, not a loss: the units are in the building and on the Shelf page.</summary>
    public partial class SeparateShelfFromWarehouseStock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "shelf_qty",
                table: "stock_movements",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "outgoing_from_shelf",
                table: "service_replacements",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            // One ledger row per affected part FIRST, while the balances still say what is being
            // given up. Written before the UPDATE because it reads the figure the UPDATE is about to
            // change: LEAST(on_hand, shelved) is what will actually move, and asking for it afterwards
            // would get the already-reduced number.
            //
            // The quantity is capped at what the warehouse actually holds. A part whose count is
            // already lower than its shelf - possible on data from before the manual status change
            // moved the count with it - gives up what it has and no more, rather than going negative
            // and taking the guarded decrement with it on the next issue.
            //
            // performed_by_user_id 0 is the system: no person pressed anything here.
            migrationBuilder.Sql(@"
INSERT INTO `stock_movements`
    (`part_id`, `movement_type`, `quantity`, `shelf_qty`, `reference_type`,
     `performed_by_user_id`, `credited_on_issue`, `remarks`, `created_at`)
SELECT b.`part_id`, 'ShelfTransfer', LEAST(b.`on_hand`, s.`shelved`), 0, 'MANUAL',
       0, 0,
       CONCAT('Shelf separated from warehouse stock: ', LEAST(b.`on_hand`, s.`shelved`),
              ' unit(s) on the shelf are no longer counted as warehouse stock'),
       UTC_TIMESTAMP(6)
FROM `stock_balances` b
JOIN (
    SELECT `part_id`, COUNT(*) AS `shelved`
    FROM `component_serials`
    WHERE `status` = 'Shelfed' AND `owner_type` = 'ServiceCenter'
    GROUP BY `part_id`
) s ON s.`part_id` = b.`part_id`
WHERE b.`technician_id` = 0 AND b.`on_hand` > 0;");

            // Then the balances themselves, by the same capped figure, so the ledger rows above and
            // the counts below cannot disagree.
            migrationBuilder.Sql(@"
UPDATE `stock_balances` b
JOIN (
    SELECT `part_id`, COUNT(*) AS `shelved`
    FROM `component_serials`
    WHERE `status` = 'Shelfed' AND `owner_type` = 'ServiceCenter'
    GROUP BY `part_id`
) s ON s.`part_id` = b.`part_id`
SET b.`on_hand` = b.`on_hand` - LEAST(b.`on_hand`, s.`shelved`),
    b.`updated_at` = UTC_TIMESTAMP(6)
WHERE b.`technician_id` = 0 AND b.`on_hand` > 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rolling back means the shelf IS the warehouse count again, so the warehouse has to be
            // given back everything the two-pool rule kept off it. That is read out of the ledger
            // rows, not by re-counting the shelf: by the time anyone rolls this back the shelf has
            // moved on - units issued, units added - and re-counting it would credit the warehouse
            // for units that are no longer on it.
            //
            // Four terms, one per way the two pools diverged while this was in force:
            //   + ShelfIn.quantity        a unit went on the shelf and the warehouse was not credited
            //   + ShelfTransfer.quantity  this migration took it off the warehouse on the way in
            //   - shelf_qty on the outflows   a shelf unit left and the warehouse was not debited
            //   + shelf_qty on the inflows    a shelf unit came back and the warehouse was not credited
            //
            // A unit stocked and then issued while this was in force therefore nets to nothing, which
            // is what old code would have recorded for it: a receipt and an issue.
            //
            // Clamped at zero because this is raw SQL with no guarded decrement behind it, and a
            // balance driven negative would fail every subsequent issue on a guard nobody could
            // explain from the screen.
            migrationBuilder.Sql(@"
UPDATE `stock_balances` b
JOIN (
    SELECT `part_id`, SUM(
        CASE
            WHEN `movement_type` IN ('ShelfIn', 'ShelfTransfer') THEN `quantity`
            WHEN `movement_type` IN ('Issue', 'Replacement', 'Sale') THEN -`shelf_qty`
            ELSE `shelf_qty`
        END) AS `delta`
    FROM `stock_movements`
    WHERE `movement_type` IN ('ShelfIn', 'ShelfTransfer')
       OR `shelf_qty` > 0
    GROUP BY `part_id`
) m ON m.`part_id` = b.`part_id`
SET b.`on_hand` = GREATEST(0, b.`on_hand` + m.`delta`), b.`updated_at` = UTC_TIMESTAMP(6)
WHERE b.`technician_id` = 0;");

            // A part whose warehouse row was deleted, or never existed, between the two directions.
            migrationBuilder.Sql(@"
INSERT INTO `stock_balances` (`part_id`, `technician_id`, `on_hand`, `created_at`, `updated_at`)
SELECT m.`part_id`, 0, GREATEST(0, SUM(
        CASE
            WHEN m.`movement_type` IN ('ShelfIn', 'ShelfTransfer') THEN m.`quantity`
            WHEN m.`movement_type` IN ('Issue', 'Replacement', 'Sale') THEN -m.`shelf_qty`
            ELSE m.`shelf_qty`
        END)), UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
FROM `stock_movements` m
WHERE (m.`movement_type` IN ('ShelfIn', 'ShelfTransfer') OR m.`shelf_qty` > 0)
  AND NOT EXISTS (
      SELECT 1 FROM `stock_balances` b
      WHERE b.`part_id` = m.`part_id` AND b.`technician_id` = 0)
GROUP BY m.`part_id`;");

            // The two new movement types go out of the enum with the rollback, so a row still naming
            // one would fail to materialise at all. ShelfIn becomes the Receipt old code wrote for a
            // unit kept off a job, sourced the same way, so its history still reads as what happened.
            // ShelfTransfer has no equivalent - it describes an event that only exists because the
            // pools were separated - so those rows go, and the balance statements above have already
            // taken their quantity into account.
            migrationBuilder.Sql(
                "UPDATE `stock_movements` SET `movement_type` = 'Receipt', `source` = 'SERVICE_RETURN' "
                + "WHERE `movement_type` = 'ShelfIn';");
            migrationBuilder.Sql("DELETE FROM `stock_movements` WHERE `movement_type` = 'ShelfTransfer';");

            migrationBuilder.DropColumn(
                name: "shelf_qty",
                table: "stock_movements");

            migrationBuilder.DropColumn(
                name: "outgoing_from_shelf",
                table: "service_replacements");
        }
    }
}
