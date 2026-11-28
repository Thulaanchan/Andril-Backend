using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andril.Persistence.Migrations.Economy
{
    /// <inheritdoc />
    public partial class InitialEconomy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "andril_economy");

            migrationBuilder.CreateTable(
                name: "coin_transactions",
                schema: "andril_economy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    balance_after = table.Column<long>(type: "bigint", nullable: false),
                    type = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    reference_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_coin_transactions", x => x.id);
                    table.CheckConstraint("ck_coin_transactions_account_id_nonempty", "account_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_coin_transactions_balance_after_nonnegative", "balance_after >= 0");
                    table.CheckConstraint("ck_coin_transactions_id_nonempty", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_coin_transactions_idempotency_key_nonempty", "length(trim(idempotency_key)) > 0");
                });

            migrationBuilder.CreateTable(
                name: "coin_wallets",
                schema: "andril_economy",
                columns: table => new
                {
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    balance = table.Column<long>(type: "bigint", nullable: false),
                    concurrency_version = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_coin_wallets", x => x.account_id);
                    table.CheckConstraint("ck_coin_wallets_account_id_nonempty", "account_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_coin_wallets_balance_nonnegative", "balance >= 0");
                    table.CheckConstraint("ck_coin_wallets_timestamps", "updated_at_utc >= created_at_utc");
                    table.ForeignKey(
                        name: "fk_coin_wallets_accounts",
                        column: x => x.account_id,
                        principalSchema: "andril_identity",
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "gift_transactions",
                schema: "andril_economy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sender_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    gift_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    coin_cost = table.Column<long>(type: "bigint", nullable: false),
                    module = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gift_transactions", x => x.id);
                    table.CheckConstraint("ck_gift_transactions_cost_positive", "coin_cost > 0");
                    table.CheckConstraint("ck_gift_transactions_id_nonempty", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_gift_transactions_no_self_gift", "sender_account_id <> recipient_account_id");
                    table.CheckConstraint("ck_gift_transactions_recipient_nonempty", "recipient_account_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_gift_transactions_sender_nonempty", "sender_account_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                });

            migrationBuilder.CreateIndex(
                name: "ix_coin_transactions_account_created",
                schema: "andril_economy",
                table: "coin_transactions",
                columns: new[] { "account_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "uq_coin_transactions_account_idempotency",
                schema: "andril_economy",
                table: "coin_transactions",
                columns: new[] { "account_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_gift_transactions_recipient_created",
                schema: "andril_economy",
                table: "gift_transactions",
                columns: new[] { "recipient_account_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "uq_gift_transactions_sender_idempotency",
                schema: "andril_economy",
                table: "gift_transactions",
                columns: new[] { "sender_account_id", "idempotency_key" },
                unique: true);

            migrationBuilder.Sql("ALTER TABLE andril_economy.coin_wallets ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE andril_economy.coin_transactions ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE andril_economy.gift_transactions ENABLE ROW LEVEL SECURITY;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "coin_transactions",
                schema: "andril_economy");

            migrationBuilder.DropTable(
                name: "coin_wallets",
                schema: "andril_economy");

            migrationBuilder.DropTable(
                name: "gift_transactions",
                schema: "andril_economy");
        }
    }
}
