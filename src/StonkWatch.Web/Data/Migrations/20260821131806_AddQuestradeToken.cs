using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StonkWatch.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQuestradeToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "questrade_token",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    protected_refresh_token = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_questrade_token", x => x.id);
                    table.CheckConstraint("ck_questrade_token_singleton", "id = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "questrade_token");
        }
    }
}
