using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancialTransactions.Infrastructure.Migrations
{
    /// <summary>
    /// Esquema inicial e massa sintética (H2, 4.2). Gerada por dotnet ef e ajustada à mão apenas na forma:
    /// colunas em campos static readonly e InsertData por linha, para passar nos analisadores (CA1861/CA1814)
    /// sem nenhuma supressão. O efeito no banco é o mesmo do scaffold original.
    /// </summary>
    public partial class InitialSchema : Migration
    {
        private static readonly string[] CustomerColumns = ["id", "created_at", "name"];
        private static readonly string[] AccountColumns = ["id", "currency", "customer_id", "opened_at"];
        private static readonly string[] MovementsAccountEffectiveAtColumns = ["account_id", "effective_at"];
        private static readonly string[] MovementsAccountIdempotencyKeyColumns = ["account_id", "idempotency_key"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounts", x => x.id);
                    table.ForeignKey(
                        name: "fk_accounts_customers",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "movements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<short>(type: "smallint", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    effective_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    idempotency_key = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    request_fingerprint = table.Column<string>(type: "char(64)", nullable: false),
                    description = table.Column<string>(type: "varchar(140)", maxLength: 140, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_movements", x => x.id);
                    table.CheckConstraint("ck_movements_amount_positive", "amount > 0");
                    table.ForeignKey(
                        name: "fk_movements_accounts",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "customers",
                columns: CustomerColumns,
                values: new object[] { new Guid("11111111-1111-1111-1111-111111111111"), new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Cliente A" });
            migrationBuilder.InsertData(
                table: "customers",
                columns: CustomerColumns,
                values: new object[] { new Guid("22222222-2222-2222-2222-222222222222"), new DateTimeOffset(new DateTime(2026, 2, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Cliente B" });

            migrationBuilder.InsertData(
                table: "accounts",
                columns: AccountColumns,
                values: new object[] { new Guid("aaaaaaa1-0000-0000-0000-000000000001"), "BRL", new Guid("11111111-1111-1111-1111-111111111111"), new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });
            migrationBuilder.InsertData(
                table: "accounts",
                columns: AccountColumns,
                values: new object[] { new Guid("aaaaaaa2-0000-0000-0000-000000000002"), "BRL", new Guid("11111111-1111-1111-1111-111111111111"), new DateTimeOffset(new DateTime(2026, 3, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });
            migrationBuilder.InsertData(
                table: "accounts",
                columns: AccountColumns,
                values: new object[] { new Guid("bbbbbbb1-0000-0000-0000-000000000001"), "BRL", new Guid("22222222-2222-2222-2222-222222222222"), new DateTimeOffset(new DateTime(2026, 2, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });

            migrationBuilder.CreateIndex(
                name: "ix_accounts_customer_id",
                table: "accounts",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_movements_account_id_effective_at",
                table: "movements",
                columns: MovementsAccountEffectiveAtColumns);

            migrationBuilder.CreateIndex(
                name: "ux_movements_account_id_idempotency_key",
                table: "movements",
                columns: MovementsAccountIdempotencyKeyColumns,
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "movements");

            migrationBuilder.DropTable(
                name: "accounts");

            migrationBuilder.DropTable(
                name: "customers");
        }
    }
}
