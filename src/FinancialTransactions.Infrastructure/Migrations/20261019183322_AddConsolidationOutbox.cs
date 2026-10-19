using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FinancialTransactions.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddConsolidationOutbox : Migration
    {
        private static readonly string[] OutboxAccountStatusColumns = ["account_id", "status"];
        private static readonly string[] OutboxBootstrapStatusColumns = ["bootstrap_id", "status"];
        private static readonly string[] OutboxStatusSequenceColumns = ["status", "sequence"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "consolidation_bootstrap_capture",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    bootstrap_id = table.Column<Guid>(type: "uuid", nullable: false),
                    member_count = table.Column<long>(type: "bigint", nullable: false),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    snapshot = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_consolidation_bootstrap_capture", x => x.id);
                    table.CheckConstraint("ck_consolidation_bootstrap_capture_single_row", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "consolidation_outbox",
                columns: table => new
                {
                    sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<short>(type: "smallint", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    effective_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    origin = table.Column<short>(type: "smallint", nullable: false),
                    bootstrap_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_consolidation_outbox", x => x.sequence);
                    table.CheckConstraint("ck_consolidation_outbox_attempts", "attempts >= 0");
                    table.CheckConstraint("ck_consolidation_outbox_origin", "origin IN (0, 1)");
                    table.CheckConstraint("ck_consolidation_outbox_status", "status IN (0, 1, 2)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_consolidation_outbox_account_id_status",
                table: "consolidation_outbox",
                columns: OutboxAccountStatusColumns);

            migrationBuilder.CreateIndex(
                name: "ix_consolidation_outbox_bootstrap_id_status",
                table: "consolidation_outbox",
                columns: OutboxBootstrapStatusColumns);

            migrationBuilder.CreateIndex(
                name: "ix_consolidation_outbox_status_sequence",
                table: "consolidation_outbox",
                columns: OutboxStatusSequenceColumns);

            migrationBuilder.CreateIndex(
                name: "ux_consolidation_outbox_event_id",
                table: "consolidation_outbox",
                column: "event_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "consolidation_bootstrap_capture");

            migrationBuilder.DropTable(
                name: "consolidation_outbox");
        }
    }
}
