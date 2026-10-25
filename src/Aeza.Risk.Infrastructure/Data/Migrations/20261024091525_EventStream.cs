using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Aeza.Risk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class EventStream : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "source_seq",
                table: "generations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<bool>(
                name: "source_synthetic",
                table: "generations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "source_updated_at",
                table: "generations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "full_churn_level",
                table: "customer_results",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "full_churn_score",
                table: "customer_results",
                type: "numeric(12,6)",
                precision: 12,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "full_churn_state",
                table: "customer_results",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "paid_current_minor",
                table: "customer_results",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "paid_previous_minor",
                table: "customer_results",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<decimal>(
                name: "payment_change_pct",
                table: "customer_results",
                type: "numeric(9,1)",
                precision: 9,
                scale: 1,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payment_trend",
                table: "customer_results",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "source_seq",
                table: "app_state",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "source_updated_at",
                table: "app_state",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "cur_accounts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false),
                    email = table.Column<string>(type: "text", nullable: false),
                    balance = table.Column<long>(type: "bigint", nullable: true),
                    withdraw_balance = table.Column<long>(type: "bigint", nullable: true),
                    total_replenished = table.Column<long>(type: "bigint", nullable: true),
                    bonus_balance = table.Column<long>(type: "bigint", nullable: true),
                    bonus_state = table.Column<string>(type: "text", nullable: true),
                    updated_seq = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cur_accounts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "cur_services",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false),
                    owner_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    payment_term = table.Column<string>(type: "text", nullable: true),
                    auto_prolong = table.Column<bool>(type: "boolean", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: true),
                    price = table.Column<long>(type: "bigint", nullable: true),
                    updated_seq = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cur_services", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "cur_transactions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false),
                    service_id = table.Column<long>(type: "bigint", nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<string>(type: "text", nullable: true),
                    performed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    type = table.Column<string>(type: "text", nullable: true),
                    updated_seq = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cur_transactions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "expiry_outcomes",
                columns: table => new
                {
                    service_id = table.Column<long>(type: "bigint", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    owner_id = table.Column<long>(type: "bigint", nullable: false),
                    payment_term = table.Column<string>(type: "text", nullable: true),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: true),
                    resolution = table.Column<string>(type: "text", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    synthetic = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_expiry_outcomes", x => new { x.service_id, x.expires_at });
                });

            migrationBuilder.CreateTable(
                name: "source_events",
                columns: table => new
                {
                    seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    event_id = table.Column<string>(type: "text", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<long>(type: "bigint", nullable: false),
                    owner_id = table.Column<long>(type: "bigint", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    stale_reason = table.Column<string>(type: "text", nullable: true),
                    changes = table.Column<string[]>(type: "text[]", nullable: false),
                    effect = table.Column<string>(type: "jsonb", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    origin = table.Column<string>(type: "text", nullable: false),
                    actor = table.Column<string>(type: "text", nullable: false),
                    synthetic = table.Column<bool>(type: "boolean", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_events", x => x.seq);
                });

            migrationBuilder.CreateTable(
                name: "source_pull_requests",
                columns: table => new
                {
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_pull_requests", x => x.account_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_customer_results_generation_id_full_churn_score_customer_id",
                table: "customer_results",
                columns: new[] { "generation_id", "full_churn_score", "customer_id" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_results_generation_id_payment_change_pct_customer_~",
                table: "customer_results",
                columns: new[] { "generation_id", "payment_change_pct", "customer_id" });

            migrationBuilder.CreateIndex(
                name: "IX_cur_services_owner_id",
                table: "cur_services",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "IX_cur_transactions_service_id",
                table: "cur_transactions",
                column: "service_id");

            migrationBuilder.CreateIndex(
                name: "IX_expiry_outcomes_outcome_expires_at",
                table: "expiry_outcomes",
                columns: new[] { "outcome", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_expiry_outcomes_resolved_at",
                table: "expiry_outcomes",
                column: "resolved_at");

            migrationBuilder.CreateIndex(
                name: "IX_source_events_event_id",
                table: "source_events",
                column: "event_id",
                unique: true,
                filter: "event_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_source_events_kind_entity_id_seq",
                table: "source_events",
                columns: new[] { "kind", "entity_id", "seq" });

            migrationBuilder.CreateIndex(
                name: "IX_source_events_owner_id_seq",
                table: "source_events",
                columns: new[] { "owner_id", "seq" });

            migrationBuilder.CreateIndex(
                name: "IX_source_events_received_at",
                table: "source_events",
                column: "received_at");

            migrationBuilder.CreateIndex(
                name: "IX_source_pull_requests_status_next_attempt_at",
                table: "source_pull_requests",
                columns: new[] { "status", "next_attempt_at" });

            // Текущее состояние существующей БД = последний принятый снимок: без потока поведение не меняется
            migrationBuilder.Sql(@"
INSERT INTO cur_accounts (id, email, balance, withdraw_balance, total_replenished, bonus_balance, bonus_state, updated_seq, updated_at)
SELECT a.id, a.email, a.balance, a.withdraw_balance, a.total_replenished, a.bonus_balance, a.bonus_state, 0, s.source_as_of
FROM src_accounts a JOIN source_snapshots s ON s.id = a.snapshot_id
WHERE a.snapshot_id = (SELECT latest_source_snapshot_id FROM app_state WHERE id = 1);

INSERT INTO cur_services (id, owner_id, name, payment_term, auto_prolong, created_at, expires_at, status, price, updated_seq, updated_at)
SELECT v.id, v.owner_id, v.name, v.payment_term, v.auto_prolong, v.created_at, v.expires_at, v.status, v.price, 0, s.source_as_of
FROM src_services v JOIN source_snapshots s ON s.id = v.snapshot_id
WHERE v.snapshot_id = (SELECT latest_source_snapshot_id FROM app_state WHERE id = 1);

INSERT INTO cur_transactions (id, service_id, amount, status, performed_at, created_at, type, updated_seq, updated_at)
SELECT t.id, t.service_id, t.amount, t.status, t.performed_at, t.created_at, t.type, 0, s.source_as_of
FROM src_transactions t JOIN source_snapshots s ON s.id = t.snapshot_id
WHERE t.snapshot_id = (SELECT latest_source_snapshot_id FROM app_state WHERE id = 1);

INSERT INTO expiry_outcomes (service_id, expires_at, owner_id, payment_term, registered_at, synthetic)
SELECT v.id, v.expires_at, v.owner_id, v.payment_term, s.source_as_of, s.synthetic
FROM src_services v JOIN source_snapshots s ON s.id = v.snapshot_id
WHERE v.snapshot_id = (SELECT latest_source_snapshot_id FROM app_state WHERE id = 1)
  AND v.status IN ('active', 'suspended', 'prolong_wait') AND v.expires_at IS NOT NULL
  AND s.source_as_of <= v.expires_at + interval '5 days';

UPDATE app_state SET source_updated_at = (SELECT source_as_of FROM source_snapshots WHERE id = app_state.latest_source_snapshot_id) WHERE id = 1;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cur_accounts");

            migrationBuilder.DropTable(
                name: "cur_services");

            migrationBuilder.DropTable(
                name: "cur_transactions");

            migrationBuilder.DropTable(
                name: "expiry_outcomes");

            migrationBuilder.DropTable(
                name: "source_events");

            migrationBuilder.DropTable(
                name: "source_pull_requests");

            migrationBuilder.DropIndex(
                name: "IX_customer_results_generation_id_full_churn_score_customer_id",
                table: "customer_results");

            migrationBuilder.DropIndex(
                name: "IX_customer_results_generation_id_payment_change_pct_customer_~",
                table: "customer_results");

            migrationBuilder.DropColumn(
                name: "source_seq",
                table: "generations");

            migrationBuilder.DropColumn(
                name: "source_synthetic",
                table: "generations");

            migrationBuilder.DropColumn(
                name: "source_updated_at",
                table: "generations");

            migrationBuilder.DropColumn(
                name: "full_churn_level",
                table: "customer_results");

            migrationBuilder.DropColumn(
                name: "full_churn_score",
                table: "customer_results");

            migrationBuilder.DropColumn(
                name: "full_churn_state",
                table: "customer_results");

            migrationBuilder.DropColumn(
                name: "paid_current_minor",
                table: "customer_results");

            migrationBuilder.DropColumn(
                name: "paid_previous_minor",
                table: "customer_results");

            migrationBuilder.DropColumn(
                name: "payment_change_pct",
                table: "customer_results");

            migrationBuilder.DropColumn(
                name: "payment_trend",
                table: "customer_results");

            migrationBuilder.DropColumn(
                name: "source_seq",
                table: "app_state");

            migrationBuilder.DropColumn(
                name: "source_updated_at",
                table: "app_state");
        }
    }
}
