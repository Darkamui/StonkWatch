using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StonkWatch.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPriceMonitoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "last_quote",
                table: "candidates",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "quote_at",
                table: "candidates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "acknowledged_at",
                table: "alerts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "auto_generated",
                table: "alerts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_notified_at",
                table: "alerts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "level_key",
                table: "alerts",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "trigger_price",
                table: "alerts",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "triggered_at",
                table: "alerts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "job_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    candidates_checked = table.Column<int>(type: "integer", nullable: false),
                    alerts_fired = table.Column<int>(type: "integer", nullable: false),
                    notifications_sent = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    skip_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_job_runs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_alerts_candidate_id_level_key",
                table: "alerts",
                columns: new[] { "candidate_id", "level_key" },
                unique: true,
                filter: "level_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_job_runs_job_started_at",
                table: "job_runs",
                columns: new[] { "job", "started_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "job_runs");

            migrationBuilder.DropIndex(
                name: "ix_alerts_candidate_id_level_key",
                table: "alerts");

            migrationBuilder.DropColumn(
                name: "last_quote",
                table: "candidates");

            migrationBuilder.DropColumn(
                name: "quote_at",
                table: "candidates");

            migrationBuilder.DropColumn(
                name: "acknowledged_at",
                table: "alerts");

            migrationBuilder.DropColumn(
                name: "auto_generated",
                table: "alerts");

            migrationBuilder.DropColumn(
                name: "last_notified_at",
                table: "alerts");

            migrationBuilder.DropColumn(
                name: "level_key",
                table: "alerts");

            migrationBuilder.DropColumn(
                name: "trigger_price",
                table: "alerts");

            migrationBuilder.DropColumn(
                name: "triggered_at",
                table: "alerts");
        }
    }
}
