using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aeza.Risk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScoringMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "scoring_primary",
                table: "app_state",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "rules");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "scoring_primary",
                table: "app_state");
        }
    }
}
