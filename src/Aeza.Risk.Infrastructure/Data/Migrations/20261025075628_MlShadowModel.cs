using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Aeza.Risk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MlShadowModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ml_model_id",
                table: "generations",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ml_revision",
                table: "generations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "bonus_expired",
                table: "customer_activity_daily",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "deletions",
                table: "customer_activity_daily",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "renewals",
                table: "customer_activity_daily",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "active_ml_model_id",
                table: "app_state",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ml_revision",
                table: "app_state",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "ml_models",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    feature_schema = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    synthetic = table.Column<bool>(type: "boolean", nullable: false),
                    card_json = table.Column<string>(type: "jsonb", nullable: false),
                    report_markdown = table.Column<string>(type: "text", nullable: true),
                    onnx = table.Column<byte[]>(type: "bytea", nullable: false),
                    onnx_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    uploaded_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ml_models", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ml_shadow_predictions",
                columns: table => new
                {
                    model_id = table.Column<long>(type: "bigint", nullable: false),
                    service_id = table.Column<long>(type: "bigint", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    offset_days = table.Column<int>(type: "integer", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    predicted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    generation_id = table.Column<long>(type: "bigint", nullable: false),
                    probability = table.Column<double>(type: "double precision", nullable: false),
                    rules_score = table.Column<decimal>(type: "numeric", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ml_shadow_predictions", x => new { x.model_id, x.service_id, x.expires_at, x.offset_days });
                    table.ForeignKey(
                        name: "FK_ml_shadow_predictions_ml_models_model_id",
                        column: x => x.model_id,
                        principalTable: "ml_models",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ml_shadow_predictions_service_id_expires_at",
                table: "ml_shadow_predictions",
                columns: new[] { "service_id", "expires_at" });

            // Новые счётчики активности из уже принятого журнала — та же свёртка, что ActivityProjection.Fold
            migrationBuilder.Sql(@"
UPDATE customer_activity_daily a SET renewals = x.renewals, deletions = x.deletions, bonus_expired = x.bonus_expired
FROM (SELECT account_id, (occurred_at AT TIME ZONE 'UTC')::date AS day,
             count(*) FILTER (WHERE type = 'service.renewed') AS renewals,
             count(*) FILTER (WHERE type = 'service.deleted') AS deletions,
             count(*) FILTER (WHERE type = 'bonus.expired') AS bonus_expired
      FROM source_domain_events
      WHERE account_id IS NOT NULL AND type IN ('service.renewed', 'service.deleted', 'bonus.expired')
      GROUP BY 1, 2) x
WHERE a.account_id = x.account_id AND a.day = x.day;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ml_shadow_predictions");

            migrationBuilder.DropTable(
                name: "ml_models");

            migrationBuilder.DropColumn(
                name: "ml_model_id",
                table: "generations");

            migrationBuilder.DropColumn(
                name: "ml_revision",
                table: "generations");

            migrationBuilder.DropColumn(
                name: "bonus_expired",
                table: "customer_activity_daily");

            migrationBuilder.DropColumn(
                name: "deletions",
                table: "customer_activity_daily");

            migrationBuilder.DropColumn(
                name: "renewals",
                table: "customer_activity_daily");

            migrationBuilder.DropColumn(
                name: "active_ml_model_id",
                table: "app_state");

            migrationBuilder.DropColumn(
                name: "ml_revision",
                table: "app_state");
        }
    }
}
