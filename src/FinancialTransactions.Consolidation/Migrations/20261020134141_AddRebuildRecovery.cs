using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialTransactions.Consolidation.Migrations
{
    /// <inheritdoc />
    public partial class AddRebuildRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rebuild_capture_item",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<short>(type: "smallint", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    effective_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    payload_diverges = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rebuild_capture_item", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "rebuild_operation",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    projection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_label = table.Column<string>(type: "text", nullable: false),
                    source_database = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    origin_bootstrap_id = table.Column<Guid>(type: "uuid", nullable: true),
                    origin_member_count = table.Column<long>(type: "bigint", nullable: true),
                    origin_captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    capture_snapshot = table.Column<string>(type: "text", nullable: true),
                    capture_count = table.Column<long>(type: "bigint", nullable: true),
                    capture_digest = table.Column<string>(type: "text", nullable: true),
                    payload_divergences = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sealed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    replay_completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reconciled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    enabled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    promoted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rebuild_operation", x => x.id);
                    table.CheckConstraint("ck_rebuild_operation_single_row", "id = 1");
                    table.CheckConstraint("ck_rebuild_operation_state", "state IN ('prepared', 'captured', 'replayed', 'reconciled', 'enabled', 'promoted', 'cancelled')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_rebuild_capture_item_sequence",
                table: "rebuild_capture_item",
                column: "sequence");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rebuild_capture_item");

            migrationBuilder.DropTable(
                name: "rebuild_operation");
        }
    }
}
