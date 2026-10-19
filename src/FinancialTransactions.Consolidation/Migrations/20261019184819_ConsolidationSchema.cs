using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialTransactions.Consolidation.Migrations
{
    /// <inheritdoc />
    public partial class ConsolidationSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "consolidation_bootstrap",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    bootstrap_id = table.Column<Guid>(type: "uuid", nullable: false),
                    phase = table.Column<string>(type: "text", nullable: false),
                    member_count = table.Column<long>(type: "bigint", nullable: false),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reconciled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_consolidation_bootstrap", x => x.id);
                    table.CheckConstraint("ck_consolidation_bootstrap_phase", "phase IN ('loading', 'live')");
                    table.CheckConstraint("ck_consolidation_bootstrap_single_row", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "daily_account_consolidation",
                columns: table => new
                {
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_day = table.Column<DateOnly>(type: "date", nullable: false),
                    credits = table.Column<decimal>(type: "numeric(24,2)", nullable: false),
                    debits = table.Column<decimal>(type: "numeric(24,2)", nullable: false),
                    net = table.Column<decimal>(type: "numeric(24,2)", nullable: false),
                    movement_count = table.Column<long>(type: "bigint", nullable: false),
                    max_applied_sequence = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_daily_account_consolidation", x => new { x.account_id, x.business_day });
                    table.CheckConstraint("ck_daily_account_consolidation_credits", "credits >= 0");
                    table.CheckConstraint("ck_daily_account_consolidation_debits", "debits >= 0");
                    table.CheckConstraint("ck_daily_account_consolidation_movement_count", "movement_count >= 0");
                    table.CheckConstraint("ck_daily_account_consolidation_net", "net = credits - debits");
                });

            migrationBuilder.CreateTable(
                name: "processed_events",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_processed_events", x => x.event_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_processed_events_account_id",
                table: "processed_events",
                column: "account_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "consolidation_bootstrap");

            migrationBuilder.DropTable(
                name: "daily_account_consolidation");

            migrationBuilder.DropTable(
                name: "processed_events");
        }
    }
}
