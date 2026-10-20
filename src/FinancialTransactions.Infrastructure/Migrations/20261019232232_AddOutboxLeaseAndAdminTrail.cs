using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FinancialTransactions.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxLeaseAndAdminTrail : Migration
    {
        private static readonly string[] AdminLogOperationEventColumns = ["operation_id", "event_id"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "lease_expires_at",
                table: "consolidation_outbox",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "lease_owner",
                table: "consolidation_outbox",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "lease_token",
                table: "consolidation_outbox",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_eligible_at",
                table: "consolidation_outbox",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "consolidation_outbox_admin_operation",
                columns: table => new
                {
                    operation_id = table.Column<string>(type: "text", nullable: false),
                    selection_digest = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    event_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_consolidation_outbox_admin_operation", x => x.operation_id);
                    table.CheckConstraint("ck_consolidation_outbox_admin_operation_count", "event_count >= 1");
                });

            migrationBuilder.CreateTable(
                name: "consolidation_outbox_admin_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    operation_id = table.Column<string>(type: "text", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    previous_status = table.Column<short>(type: "smallint", nullable: false),
                    previous_attempts = table.Column<int>(type: "integer", nullable: false),
                    previous_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_consolidation_outbox_admin_log", x => x.id);
                    table.ForeignKey(
                        name: "fk_consolidation_outbox_admin_log_operation",
                        column: x => x.operation_id,
                        principalTable: "consolidation_outbox_admin_operation",
                        principalColumn: "operation_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_consolidation_outbox_lease_all_or_none",
                table: "consolidation_outbox",
                sql: "(lease_owner IS NULL AND lease_token IS NULL AND lease_expires_at IS NULL) OR (lease_owner IS NOT NULL AND lease_token IS NOT NULL AND lease_expires_at IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_consolidation_outbox_admin_log_event_id",
                table: "consolidation_outbox_admin_log",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "ux_consolidation_outbox_admin_log_operation_event",
                table: "consolidation_outbox_admin_log",
                columns: AdminLogOperationEventColumns,
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "consolidation_outbox_admin_log");

            migrationBuilder.DropTable(
                name: "consolidation_outbox_admin_operation");

            migrationBuilder.DropCheckConstraint(
                name: "ck_consolidation_outbox_lease_all_or_none",
                table: "consolidation_outbox");

            migrationBuilder.DropColumn(
                name: "lease_expires_at",
                table: "consolidation_outbox");

            migrationBuilder.DropColumn(
                name: "lease_owner",
                table: "consolidation_outbox");

            migrationBuilder.DropColumn(
                name: "lease_token",
                table: "consolidation_outbox");

            migrationBuilder.DropColumn(
                name: "next_eligible_at",
                table: "consolidation_outbox");
        }
    }
}
