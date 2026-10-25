using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aeza.Risk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class LoginThrottle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "login_throttle",
                columns: table => new
                {
                    key_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    failures = table.Column<int>(type: "integer", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_login_throttle", x => x.key_hash);
                });

            migrationBuilder.CreateIndex(
                name: "IX_login_throttle_updated_at",
                table: "login_throttle",
                column: "updated_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "login_throttle");
        }
    }
}
