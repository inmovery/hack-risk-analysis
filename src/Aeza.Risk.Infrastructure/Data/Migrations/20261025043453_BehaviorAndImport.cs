using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Aeza.Risk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class BehaviorAndImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "duplicates",
                table: "source_connectors",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "last_error",
                table: "source_connectors",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_error_at",
                table: "source_connectors",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "rejected",
                table: "source_connectors",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "activity_seq",
                table: "generations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "customer_activity_daily",
                columns: table => new
                {
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    logins = table.Column<int>(type: "integer", nullable: false),
                    logins_failed = table.Column<int>(type: "integer", nullable: false),
                    usage_samples = table.Column<int>(type: "integer", nullable: false),
                    cpu_sum = table.Column<double>(type: "double precision", nullable: false),
                    traffic_out_gb = table.Column<double>(type: "double precision", nullable: false),
                    incident_minutes = table.Column<int>(type: "integer", nullable: false),
                    tickets_opened = table.Column<int>(type: "integer", nullable: false),
                    tickets_unsolved = table.Column<int>(type: "integer", nullable: false),
                    csat_min = table.Column<int>(type: "integer", nullable: true),
                    nps_min = table.Column<int>(type: "integer", nullable: true),
                    payments_count = table.Column<int>(type: "integer", nullable: false),
                    payments_minor = table.Column<long>(type: "bigint", nullable: false),
                    payments_failed = table.Column<int>(type: "integer", nullable: false),
                    top_ups_count = table.Column<int>(type: "integer", nullable: false),
                    top_ups_minor = table.Column<long>(type: "bigint", nullable: false),
                    downgrades = table.Column<int>(type: "integer", nullable: false),
                    upgrades = table.Column<int>(type: "integer", nullable: false),
                    auto_prolong_off = table.Column<int>(type: "integer", nullable: false),
                    suspends = table.Column<int>(type: "integer", nullable: false),
                    withdrawals_minor = table.Column<long>(type: "bigint", nullable: false),
                    backups = table.Column<int>(type: "integer", nullable: false),
                    events = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_activity_daily", x => new { x.account_id, x.day });
                });

            migrationBuilder.CreateTable(
                name: "source_imports",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    connector = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    synthetic = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    bytes = table.Column<long>(type: "bigint", nullable: false),
                    chunks_total = table.Column<int>(type: "integer", nullable: false),
                    chunks_done = table.Column<int>(type: "integer", nullable: false),
                    events_total = table.Column<long>(type: "bigint", nullable: false),
                    applied = table.Column<long>(type: "bigint", nullable: false),
                    recorded = table.Column<long>(type: "bigint", nullable: false),
                    no_change = table.Column<long>(type: "bigint", nullable: false),
                    stale = table.Column<long>(type: "bigint", nullable: false),
                    duplicates = table.Column<long>(type: "bigint", nullable: false),
                    rejected = table.Column<long>(type: "bigint", nullable: false),
                    errors = table.Column<string>(type: "jsonb", nullable: true),
                    error_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    job_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_imports", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "source_import_chunks",
                columns: table => new
                {
                    import_id = table.Column<long>(type: "bigint", nullable: false),
                    no = table.Column<int>(type: "integer", nullable: false),
                    lines = table.Column<int>(type: "integer", nullable: false),
                    first_line = table.Column<long>(type: "bigint", nullable: false),
                    data = table.Column<byte[]>(type: "bytea", nullable: false),
                    done = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_import_chunks", x => new { x.import_id, x.no });
                    table.ForeignKey(
                        name: "FK_source_import_chunks_source_imports_import_id",
                        column: x => x.import_id,
                        principalTable: "source_imports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_customer_activity_daily_day",
                table: "customer_activity_daily",
                column: "day");

            migrationBuilder.CreateIndex(
                name: "IX_source_imports_status",
                table: "source_imports",
                column: "status");

            // Активность из уже принятых доменных событий: та же свёртка, что ActivityProjection.Fold
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION pg_temp.aeza_date(t text) RETURNS date AS $f$ BEGIN RETURN to_date(t, 'YYYY-MM-DD'); EXCEPTION WHEN others THEN RETURN NULL; END; $f$ LANGUAGE plpgsql;
CREATE OR REPLACE FUNCTION pg_temp.aeza_long(v jsonb) RETURNS bigint AS $f$ BEGIN RETURN CASE WHEN jsonb_typeof(v) = 'number' THEN (v #>> '{}')::bigint END; EXCEPTION WHEN others THEN RETURN NULL; END; $f$ LANGUAGE plpgsql;
CREATE OR REPLACE FUNCTION pg_temp.aeza_num(v jsonb) RETURNS double precision AS $f$ BEGIN RETURN CASE WHEN jsonb_typeof(v) = 'number' THEN (v #>> '{}')::double precision END; EXCEPTION WHEN others THEN RETURN NULL; END; $f$ LANGUAGE plpgsql;
INSERT INTO customer_activity_daily (account_id, day, logins, logins_failed, usage_samples, cpu_sum, traffic_out_gb, incident_minutes,
  tickets_opened, tickets_unsolved, csat_min, nps_min, payments_count, payments_minor, payments_failed, top_ups_count, top_ups_minor,
  downgrades, upgrades, auto_prolong_off, suspends, withdrawals_minor, backups, events, updated_at)
SELECT account_id, day,
  count(*) FILTER (WHERE type = 'account.login' AND (data->'success') IS DISTINCT FROM 'false'::jsonb),
  count(*) FILTER (WHERE type = 'account.login' AND data->'success' = 'false'::jsonb),
  count(*) FILTER (WHERE type = 'usage.daily' AND pg_temp.aeza_num(data->'cpuAvgPct') IS NOT NULL),
  coalesce(sum(LEAST(GREATEST(pg_temp.aeza_num(data->'cpuAvgPct'), 0), 100)) FILTER (WHERE type = 'usage.daily'), 0),
  coalesce(sum(GREATEST(pg_temp.aeza_num(data->'trafficOutGb'), 0)) FILTER (WHERE type = 'usage.daily'), 0),
  coalesce(sum(trunc(LEAST(GREATEST(pg_temp.aeza_num(data->'durationMin'), 0), 1440))::int) FILTER (WHERE type = 'service.incident'), 0),
  count(*) FILTER (WHERE type = 'ticket.opened'),
  count(*) FILTER (WHERE type = 'ticket.closed' AND data->>'resolution' = 'unsolved'),
  min(trunc(LEAST(GREATEST(pg_temp.aeza_num(data->'csat'), 1), 5))::int) FILTER (WHERE type = 'ticket.closed'),
  min(trunc(LEAST(GREATEST(pg_temp.aeza_num(data->'score'), 0), 10))::int) FILTER (WHERE type = 'nps.responded'),
  count(*) FILTER (WHERE type = 'payment.charged'),
  coalesce(sum(GREATEST(pg_temp.aeza_long(data->'amount'), 0)) FILTER (WHERE type = 'payment.charged'), 0),
  count(*) FILTER (WHERE type = 'payment.failed'),
  count(*) FILTER (WHERE type = 'balance.topped_up'),
  coalesce(sum(GREATEST(pg_temp.aeza_long(data->'amount'), 0)) FILTER (WHERE type = 'balance.topped_up'), 0),
  count(*) FILTER (WHERE type = 'service.plan_changed' AND data->>'direction' = 'downgrade'),
  count(*) FILTER (WHERE type = 'service.plan_changed' AND data->>'direction' = 'upgrade'),
  count(*) FILTER (WHERE type = 'service.autoprolong_changed' AND data->'enabled' = 'false'::jsonb),
  count(*) FILTER (WHERE type = 'service.suspended'),
  coalesce(sum(GREATEST(pg_temp.aeza_long(data->'amount'), 0)) FILTER (WHERE type = 'withdrawal.requested'), 0),
  count(*) FILTER (WHERE type = 'service.backup_created'),
  count(*),
  now()
FROM (SELECT account_id, type, data,
             COALESCE(CASE WHEN type = 'usage.daily' THEN pg_temp.aeza_date(data->>'date') END, (occurred_at AT TIME ZONE 'UTC')::date) AS day
      FROM source_domain_events WHERE account_id IS NOT NULL) e
GROUP BY account_id, day;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "customer_activity_daily");

            migrationBuilder.DropTable(
                name: "source_import_chunks");

            migrationBuilder.DropTable(
                name: "source_imports");

            migrationBuilder.DropColumn(
                name: "duplicates",
                table: "source_connectors");

            migrationBuilder.DropColumn(
                name: "last_error",
                table: "source_connectors");

            migrationBuilder.DropColumn(
                name: "last_error_at",
                table: "source_connectors");

            migrationBuilder.DropColumn(
                name: "rejected",
                table: "source_connectors");

            migrationBuilder.DropColumn(
                name: "activity_seq",
                table: "generations");
        }
    }
}
