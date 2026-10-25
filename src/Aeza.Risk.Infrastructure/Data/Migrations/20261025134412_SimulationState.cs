using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aeza.Risk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class SimulationState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "simulation_state",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    settings_json = table.Column<string>(type: "jsonb", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    world_revision = table.Column<long>(type: "bigint", nullable: false),
                    world_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    world_settings_json = table.Column<string>(type: "jsonb", nullable: false),
                    world_reset_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    world_reset_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    scenario_request = table.Column<long>(type: "bigint", nullable: false),
                    agent_status_json = table.Column<string>(type: "jsonb", nullable: true),
                    agent_reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_simulation_state", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "simulation_state");
        }
    }
}
