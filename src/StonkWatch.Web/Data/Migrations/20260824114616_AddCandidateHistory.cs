using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StonkWatch.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCandidateHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "candidate_history_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    previous_state = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_candidate_history_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_candidate_history_entries_candidates_candidate_id",
                        column: x => x.candidate_id,
                        principalTable: "candidates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_candidate_history_entries_candidate_id_snapshot_at",
                table: "candidate_history_entries",
                columns: new[] { "candidate_id", "snapshot_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "candidate_history_entries");
        }
    }
}
