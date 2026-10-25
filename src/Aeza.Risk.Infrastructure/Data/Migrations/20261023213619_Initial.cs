using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Aeza.Risk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_state",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    active_generation_id = table.Column<long>(type: "bigint", nullable: true),
                    latest_source_snapshot_id = table.Column<long>(type: "bigint", nullable: true),
                    latest_model_revision_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_state", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    details = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "data_protection_keys",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    friendly_name = table.Column<string>(type: "text", nullable: true),
                    xml = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_data_protection_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "generations",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    source_snapshot_id = table.Column<long>(type: "bigint", nullable: false),
                    source_revision = table.Column<long>(type: "bigint", nullable: false),
                    model_revision_id = table.Column<long>(type: "bigint", nullable: false),
                    as_of = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    clock_mode = table.Column<string>(type: "text", nullable: false),
                    engine_version = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    job_id = table.Column<long>(type: "bigint", nullable: false),
                    summary_json = table.Column<string>(type: "jsonb", nullable: false),
                    forecast_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_generations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "model_revisions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false),
                    settings_json = table.Column<string>(type: "jsonb", nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    comment = table.Column<string>(type: "text", nullable: true),
                    based_on = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_revisions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "observed_service_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    source_snapshot_id = table.Column<long>(type: "bigint", nullable: false),
                    source_revision = table.Column<long>(type: "bigint", nullable: false),
                    service_id = table.Column<long>(type: "bigint", nullable: false),
                    owner_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    from_status = table.Column<string>(type: "text", nullable: true),
                    to_status = table.Column<string>(type: "text", nullable: true),
                    observed_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    observed_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_observed_service_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "recalc_jobs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    reason = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    available_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    source_snapshot_id = table.Column<long>(type: "bigint", nullable: true),
                    model_revision_id = table.Column<long>(type: "bigint", nullable: true),
                    generation_id = table.Column<long>(type: "bigint", nullable: true),
                    coalesced_into = table.Column<long>(type: "bigint", nullable: true),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    requested_by = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recalc_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                columns: table => new
                {
                    id_hash = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sessions", x => x.id_hash);
                });

            migrationBuilder.CreateTable(
                name: "source_snapshots",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    source_as_of = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source_as_of_assumed = table.Column<bool>(type: "boolean", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_hash = table.Column<string>(type: "text", nullable: false),
                    raw_sha256 = table.Column<string>(type: "text", nullable: false),
                    adapter = table.Column<string>(type: "text", nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    source_revision_label = table.Column<string>(type: "text", nullable: true),
                    synthetic = table.Column<bool>(type: "boolean", nullable: false),
                    provenance = table.Column<string>(type: "jsonb", nullable: false),
                    warnings = table.Column<string>(type: "jsonb", nullable: false),
                    accounts_count = table.Column<int>(type: "integer", nullable: false),
                    services_count = table.Column<int>(type: "integer", nullable: false),
                    transactions_count = table.Column<int>(type: "integer", nullable: false),
                    raw_bytes = table.Column<long>(type: "bigint", nullable: false),
                    raw_gzip = table.Column<byte[]>(type: "bytea", nullable: false),
                    actor = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_snapshots", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    login = table.Column<string>(type: "text", nullable: false),
                    login_normalized = table.Column<string>(type: "text", nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    failed_count = table.Column<int>(type: "integer", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    disabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "customer_results",
                columns: table => new
                {
                    generation_id = table.Column<long>(type: "bigint", nullable: false),
                    customer_id = table.Column<long>(type: "bigint", nullable: false),
                    email = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    email_sort = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    balance = table.Column<long>(type: "bigint", nullable: true),
                    total_replenished = table.Column<long>(type: "bigint", nullable: true),
                    withdraw_balance = table.Column<long>(type: "bigint", nullable: true),
                    bonus_balance = table.Column<long>(type: "bigint", nullable: true),
                    bonus_state = table.Column<string>(type: "text", nullable: true),
                    score = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: true),
                    level = table.Column<string>(type: "text", nullable: false),
                    score_state = table.Column<string>(type: "text", nullable: false),
                    primary_reason_code = table.Column<string>(type: "text", nullable: false),
                    primary_reason_text = table.Column<string>(type: "text", nullable: false),
                    primary_reason_contribution = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    primary_reason_service_id = table.Column<long>(type: "bigint", nullable: true),
                    reason_codes = table.Column<string[]>(type: "text[]", nullable: false),
                    flags = table.Column<string[]>(type: "text[]", nullable: false),
                    planned_minor = table.Column<long>(type: "bigint", nullable: false),
                    unfunded_minor = table.Column<long>(type: "bigint", nullable: false),
                    at_risk_minor = table.Column<long>(type: "bigint", nullable: false),
                    services_total = table.Column<int>(type: "integer", nullable: false),
                    services_renewable = table.Column<int>(type: "integer", nullable: false),
                    services_scored = table.Column<int>(type: "integer", nullable: false),
                    detail_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_results", x => new { x.generation_id, x.customer_id });
                    table.ForeignKey(
                        name: "FK_customer_results_generations_generation_id",
                        column: x => x.generation_id,
                        principalTable: "generations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "src_accounts",
                columns: table => new
                {
                    snapshot_id = table.Column<long>(type: "bigint", nullable: false),
                    id = table.Column<long>(type: "bigint", nullable: false),
                    email = table.Column<string>(type: "text", nullable: false),
                    balance = table.Column<long>(type: "bigint", nullable: true),
                    withdraw_balance = table.Column<long>(type: "bigint", nullable: true),
                    total_replenished = table.Column<long>(type: "bigint", nullable: true),
                    bonus_balance = table.Column<long>(type: "bigint", nullable: true),
                    bonus_state = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_src_accounts", x => new { x.snapshot_id, x.id });
                    table.ForeignKey(
                        name: "FK_src_accounts_source_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalTable: "source_snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "src_services",
                columns: table => new
                {
                    snapshot_id = table.Column<long>(type: "bigint", nullable: false),
                    id = table.Column<long>(type: "bigint", nullable: false),
                    owner_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    payment_term = table.Column<string>(type: "text", nullable: true),
                    auto_prolong = table.Column<bool>(type: "boolean", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: true),
                    price = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_src_services", x => new { x.snapshot_id, x.id });
                    table.ForeignKey(
                        name: "FK_src_services_source_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalTable: "source_snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "src_transactions",
                columns: table => new
                {
                    snapshot_id = table.Column<long>(type: "bigint", nullable: false),
                    id = table.Column<long>(type: "bigint", nullable: false),
                    service_id = table.Column<long>(type: "bigint", nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<string>(type: "text", nullable: true),
                    performed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    type = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_src_transactions", x => new { x.snapshot_id, x.id });
                    table.ForeignKey(
                        name: "FK_src_transactions_source_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalTable: "source_snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_customer_results_generation_id_at_risk_minor_customer_id",
                table: "customer_results",
                columns: new[] { "generation_id", "at_risk_minor", "customer_id" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_results_generation_id_balance_customer_id",
                table: "customer_results",
                columns: new[] { "generation_id", "balance", "customer_id" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_results_generation_id_email_sort_email_customer_id",
                table: "customer_results",
                columns: new[] { "generation_id", "email_sort", "email", "customer_id" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_results_generation_id_level",
                table: "customer_results",
                columns: new[] { "generation_id", "level" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_results_generation_id_score_customer_id",
                table: "customer_results",
                columns: new[] { "generation_id", "score", "customer_id" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_results_generation_id_total_replenished_customer_id",
                table: "customer_results",
                columns: new[] { "generation_id", "total_replenished", "customer_id" });

            migrationBuilder.CreateIndex(
                name: "IX_observed_service_events_observed_to",
                table: "observed_service_events",
                column: "observed_to");

            migrationBuilder.CreateIndex(
                name: "IX_recalc_jobs_status_available_at",
                table: "recalc_jobs",
                columns: new[] { "status", "available_at" });

            migrationBuilder.CreateIndex(
                name: "IX_sessions_user_id",
                table: "sessions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_source_snapshots_content_hash",
                table: "source_snapshots",
                column: "content_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_source_snapshots_revision",
                table: "source_snapshots",
                column: "revision",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_src_services_snapshot_id_owner_id",
                table: "src_services",
                columns: new[] { "snapshot_id", "owner_id" });

            migrationBuilder.CreateIndex(
                name: "IX_src_transactions_snapshot_id_service_id",
                table: "src_transactions",
                columns: new[] { "snapshot_id", "service_id" });

            migrationBuilder.CreateIndex(
                name: "IX_users_login_normalized",
                table: "users",
                column: "login_normalized",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_state");

            migrationBuilder.DropTable(
                name: "audit_log");

            migrationBuilder.DropTable(
                name: "customer_results");

            migrationBuilder.DropTable(
                name: "data_protection_keys");

            migrationBuilder.DropTable(
                name: "model_revisions");

            migrationBuilder.DropTable(
                name: "observed_service_events");

            migrationBuilder.DropTable(
                name: "recalc_jobs");

            migrationBuilder.DropTable(
                name: "sessions");

            migrationBuilder.DropTable(
                name: "src_accounts");

            migrationBuilder.DropTable(
                name: "src_services");

            migrationBuilder.DropTable(
                name: "src_transactions");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "generations");

            migrationBuilder.DropTable(
                name: "source_snapshots");
        }
    }
}
