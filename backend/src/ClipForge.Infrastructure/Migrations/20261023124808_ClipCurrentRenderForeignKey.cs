using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClipForge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ClipCurrentRenderForeignKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rows written before the constraint existed: a current render that is gone or belongs to another clip
            // is cleared (the clip then shows no current render, like after a purge).
            migrationBuilder.Sql("""
                UPDATE clips c
                SET current_render_id = NULL
                WHERE c.current_render_id IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM clip_renders r WHERE r.id = c.current_render_id AND r.clip_id = c.id);
                """);

            migrationBuilder.AddForeignKey(
                name: "fk_clips_clip_renders_current_render_id",
                table: "clips",
                column: "current_render_id",
                principalTable: "clip_renders",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_clips_clip_renders_current_render_id",
                table: "clips");
        }
    }
}
