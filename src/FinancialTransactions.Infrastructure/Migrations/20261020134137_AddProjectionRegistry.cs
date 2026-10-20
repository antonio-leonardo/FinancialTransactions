using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialTransactions.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectionRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "consolidation_projection_registry",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    active_projection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    promoted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    operation_label = table.Column<string>(type: "text", nullable: false),
                    previous_database = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_consolidation_projection_registry", x => x.id);
                    table.CheckConstraint("ck_consolidation_projection_registry_single_row", "id = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "consolidation_projection_registry");
        }
    }
}
