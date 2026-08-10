using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StonkWatch.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "candidates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticker = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    company = table.Column<string>(type: "text", nullable: true),
                    exchange = table.Column<string>(type: "text", nullable: true),
                    currency = table.Column<string>(type: "text", nullable: true),
                    priority = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    conviction = table.Column<string>(type: "character varying(1)", maxLength: 1, nullable: true),
                    preferred_setup = table.Column<string>(type: "text", nullable: true),
                    thesis = table.Column<string>(type: "text", nullable: true),
                    current_price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    reviewed_price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    last_reviewed = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    support_low = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    support_high = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    secondary_support_low = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    secondary_support_high = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    reclaim_trigger1 = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    reclaim_trigger2 = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    invalidation = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    t1 = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    t2 = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    next_event = table.Column<string>(type: "text", nullable: true),
                    event_date = table.Column<DateOnly>(type: "date", nullable: true),
                    data_quality = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    main_risk = table.Column<string>(type: "text", nullable: true),
                    source_notes = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_candidates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "alerts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    level_low = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    level_high = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    condition_signal = table.Column<string>(type: "text", nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    triggered = table.Column<bool>(type: "boolean", nullable: false),
                    last_checked = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alerts", x => x.id);
                    table.ForeignKey(
                        name: "fk_alerts_candidates_candidate_id",
                        column: x => x.candidate_id,
                        principalTable: "candidates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "review_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    review_date = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    status_at_review = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    thesis_impact = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    what_changed = table.Column<string>(type: "text", nullable: true),
                    levels_changed = table.Column<bool>(type: "boolean", nullable: false),
                    next_action = table.Column<string>(type: "text", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_review_log", x => x.id);
                    table.ForeignKey(
                        name: "fk_review_log_candidates_candidate_id",
                        column: x => x.candidate_id,
                        principalTable: "candidates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_alerts_candidate_id",
                table: "alerts",
                column: "candidate_id");

            migrationBuilder.CreateIndex(
                name: "ix_candidates_ticker",
                table: "candidates",
                column: "ticker",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_review_log_candidate_id",
                table: "review_log",
                column: "candidate_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alerts");

            migrationBuilder.DropTable(
                name: "review_log");

            migrationBuilder.DropTable(
                name: "candidates");
        }
    }
}
