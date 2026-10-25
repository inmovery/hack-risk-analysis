using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Aeza.Risk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class EventEnvelopeV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "source_pull_requests");

            migrationBuilder.AddColumn<long>(
                name: "domain_seq",
                table: "source_events",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "event_type",
                table: "source_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "occurred_at",
                table: "source_events",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // Записи v1 (до): время события = время получения
            migrationBuilder.Sql("UPDATE source_events SET occurred_at = received_at;");

            migrationBuilder.CreateTable(
                name: "source_account_streams",
                columns: table => new
                {
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    max_seq = table.Column<long>(type: "bigint", nullable: false),
                    missing = table.Column<long[]>(type: "bigint[]", nullable: false),
                    gap_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_account_streams", x => x.account_id);
                });

            migrationBuilder.CreateTable(
                name: "source_connectors",
                columns: table => new
                {
                    name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    watermark = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    events = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_connectors", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "source_domain_events",
                columns: table => new
                {
                    seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    event_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    known_type = table.Column<bool>(type: "boolean", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: true),
                    account_seq = table.Column<long>(type: "bigint", nullable: true),
                    service_id = table.Column<long>(type: "bigint", nullable: true),
                    connector = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    state_applied = table.Column<int>(type: "integer", nullable: false),
                    synthetic = table.Column<bool>(type: "boolean", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_domain_events", x => x.seq);
                });

            migrationBuilder.CreateIndex(
                name: "IX_source_events_domain_seq",
                table: "source_events",
                column: "domain_seq");

            migrationBuilder.CreateIndex(
                name: "IX_source_events_occurred_at",
                table: "source_events",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "IX_source_account_streams_gap_since",
                table: "source_account_streams",
                column: "gap_since",
                filter: "gap_since IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_source_domain_events_account_id_occurred_at",
                table: "source_domain_events",
                columns: new[] { "account_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_source_domain_events_event_id",
                table: "source_domain_events",
                column: "event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_source_domain_events_occurred_at",
                table: "source_domain_events",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "IX_source_domain_events_received_at",
                table: "source_domain_events",
                column: "received_at");

            migrationBuilder.CreateIndex(
                name: "IX_source_domain_events_type_occurred_at",
                table: "source_domain_events",
                columns: new[] { "type", "occurred_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "source_account_streams");

            migrationBuilder.DropTable(
                name: "source_connectors");

            migrationBuilder.DropTable(
                name: "source_domain_events");

            migrationBuilder.DropIndex(
                name: "IX_source_events_domain_seq",
                table: "source_events");

            migrationBuilder.DropIndex(
                name: "IX_source_events_occurred_at",
                table: "source_events");

            migrationBuilder.DropColumn(
                name: "domain_seq",
                table: "source_events");

            migrationBuilder.DropColumn(
                name: "event_type",
                table: "source_events");

            migrationBuilder.DropColumn(
                name: "occurred_at",
                table: "source_events");

            migrationBuilder.CreateTable(
                name: "source_pull_requests",
                columns: table => new
                {
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_pull_requests", x => x.account_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_source_pull_requests_status_next_attempt_at",
                table: "source_pull_requests",
                columns: new[] { "status", "next_attempt_at" });
        }
    }
}
