using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aeza.Risk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MlTreesAndSignals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "trees_json",
                table: "ml_models",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "backup_downloads",
                table: "customer_activity_daily",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "dns_external",
                table: "customer_activity_daily",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "retention_holdouts",
                table: "customer_activity_daily",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "retention_offers",
                table: "customer_activity_daily",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "trees_json",
                table: "ml_models");

            migrationBuilder.DropColumn(
                name: "backup_downloads",
                table: "customer_activity_daily");

            migrationBuilder.DropColumn(
                name: "dns_external",
                table: "customer_activity_daily");

            migrationBuilder.DropColumn(
                name: "retention_holdouts",
                table: "customer_activity_daily");

            migrationBuilder.DropColumn(
                name: "retention_offers",
                table: "customer_activity_daily");
        }
    }
}
