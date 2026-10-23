using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClipForge.Infrastructure.Migrations
{
    /// <summary>
    /// Live capture (docs/LIVE_CAPTURE.md): companion_devices (device tokens stored as SHA-256 hex), live_sessions (one
    /// project each; at most one waiting/live/ending session per device) and live_segments (one media asset and one
    /// live_segment job per uploaded segment, unique per (session, sequence)). New tables only: existing rows are not
    /// touched, so it applies to a database that already has data. projects.source_platform gains the value "obs"
    /// (text column, no schema change).
    /// </summary>
    public partial class LiveCapture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "companion_devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    token_prefix = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_companion_devices", x => x.id);
                    table.ForeignKey(
                        name: "fk_companion_devices_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "live_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    segment_seconds = table.Column<int>(type: "integer", nullable: false),
                    settings = table.Column<string>(type: "jsonb", nullable: false),
                    companion_state = table.Column<string>(type: "jsonb", nullable: true),
                    stop_requested = table.Column<bool>(type: "boolean", nullable: false),
                    end_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    obs = table.Column<string>(type: "jsonb", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_segment_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_heartbeat_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_live_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_live_sessions_companion_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "companion_devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_live_sessions_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_live_sessions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "live_segments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    media_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    start_offset_seconds = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: false),
                    duration_seconds = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: false),
                    is_final = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    live_context = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_live_segments", x => x.id);
                    table.ForeignKey(
                        name: "fk_live_segments_live_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "live_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_live_segments_media_assets_media_asset_id",
                        column: x => x.media_asset_id,
                        principalTable: "media_assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_live_segments_processing_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "processing_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_live_segments_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_companion_devices_token_hash",
                table: "companion_devices",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_companion_devices_user_id_created_at",
                table: "companion_devices",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_live_segments_job_id",
                table: "live_segments",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "ix_live_segments_media_asset_id",
                table: "live_segments",
                column: "media_asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_live_segments_session_id_sequence",
                table: "live_segments",
                columns: new[] { "session_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_live_segments_user_id",
                table: "live_segments",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_live_sessions_device_active",
                table: "live_sessions",
                column: "device_id",
                unique: true,
                filter: "status IN ('waiting', 'live', 'ending')");

            migrationBuilder.CreateIndex(
                name: "ix_live_sessions_open",
                table: "live_sessions",
                columns: new[] { "status", "last_heartbeat_at" },
                filter: "status IN ('waiting', 'live', 'ending')");

            migrationBuilder.CreateIndex(
                name: "ix_live_sessions_project_id",
                table: "live_sessions",
                column: "project_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_live_sessions_user_id_started_at",
                table: "live_sessions",
                columns: new[] { "user_id", "started_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "live_segments");

            migrationBuilder.DropTable(
                name: "live_sessions");

            migrationBuilder.DropTable(
                name: "companion_devices");
        }
    }
}
