using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "public");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "albums",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_albums", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "media_files",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_path = table.Column<string>(type: "text", nullable: false),
                    file_hash = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    immich_asset_id = table.Column<string>(type: "text", nullable: true),
                    mime_type = table.Column<string>(type: "text", nullable: false),
                    file_size = table.Column<long>(type: "bigint", nullable: true),
                    width = table.Column<int>(type: "integer", nullable: true),
                    height = table.Column<int>(type: "integer", nullable: true),
                    duration_sec = table.Column<double>(type: "double precision", nullable: true),
                    taken_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    is_favorite = table.Column<bool>(type: "boolean", nullable: false),
                    origin_class = table.Column<string>(type: "text", nullable: true),
                    origin_confidence = table.Column<double>(type: "double precision", nullable: true),
                    origin_reviewed = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_files", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "photo_series",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    series_type = table.Column<string>(type: "text", nullable: false),
                    taken_at_start = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    taken_at_end = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    latitude = table.Column<double>(type: "double precision", nullable: true),
                    longitude = table.Column<double>(type: "double precision", nullable: true),
                    cover_media_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_photo_series", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "processing_jobs",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_path = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    scan_id = table.Column<Guid>(type: "uuid", nullable: true),
                    media_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    failed_step = table.Column<string>(type: "text", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    retry_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_processing_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "video_transcodes",
                schema: "public",
                columns: table => new
                {
                    media_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transcode_path = table.Column<string>(type: "text", nullable: false),
                    codec = table.Column<string>(type: "text", nullable: false),
                    container = table.Column<string>(type: "text", nullable: false),
                    file_size = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_video_transcodes", x => x.media_file_id);
                });

            migrationBuilder.CreateTable(
                name: "album_media",
                schema: "public",
                columns: table => new
                {
                    album_id = table.Column<Guid>(type: "uuid", nullable: false),
                    media_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    added_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_album_media", x => new { x.album_id, x.media_file_id });
                    table.ForeignKey(
                        name: "fk_album_media_albums_album_id",
                        column: x => x.album_id,
                        principalSchema: "public",
                        principalTable: "albums",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_album_media_media_files_media_file_id",
                        column: x => x.media_file_id,
                        principalSchema: "public",
                        principalTable: "media_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "media_embeddings",
                schema: "public",
                columns: table => new
                {
                    media_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    embedding = table.Column<Vector>(type: "vector(512)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_embeddings", x => x.media_file_id);
                    table.ForeignKey(
                        name: "fk_media_embeddings_media_files_media_file_id",
                        column: x => x.media_file_id,
                        principalSchema: "public",
                        principalTable: "media_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "media_metadata",
                schema: "public",
                columns: table => new
                {
                    media_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    latitude = table.Column<double>(type: "double precision", nullable: true),
                    longitude = table.Column<double>(type: "double precision", nullable: true),
                    address = table.Column<string>(type: "text", nullable: true),
                    camera_make = table.Column<string>(type: "text", nullable: true),
                    camera_model = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_metadata", x => x.media_file_id);
                    table.ForeignKey(
                        name: "fk_media_metadata_media_files_media_file_id",
                        column: x => x.media_file_id,
                        principalSchema: "public",
                        principalTable: "media_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "media_tags",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    media_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag = table.Column<string>(type: "text", nullable: false),
                    confidence = table.Column<float>(type: "real", nullable: false),
                    model = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_tags", x => x.id);
                    table.ForeignKey(
                        name: "fk_media_tags_media_files_media_file_id",
                        column: x => x.media_file_id,
                        principalSchema: "public",
                        principalTable: "media_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "media_thumbnails",
                schema: "public",
                columns: table => new
                {
                    media_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    thumbnail_path = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_thumbnails", x => x.media_file_id);
                    table.ForeignKey(
                        name: "fk_media_thumbnails_media_files_media_file_id",
                        column: x => x.media_file_id,
                        principalSchema: "public",
                        principalTable: "media_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "series_media",
                schema: "public",
                columns: table => new
                {
                    series_id = table.Column<Guid>(type: "uuid", nullable: false),
                    media_file_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_series_media", x => new { x.series_id, x.media_file_id });
                    table.ForeignKey(
                        name: "fk_series_media_media_files_media_file_id",
                        column: x => x.media_file_id,
                        principalSchema: "public",
                        principalTable: "media_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_series_media_photo_series_series_id",
                        column: x => x.series_id,
                        principalSchema: "public",
                        principalTable: "photo_series",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_album_media_media_file_id",
                schema: "public",
                table: "album_media",
                column: "media_file_id");

            migrationBuilder.CreateIndex(
                name: "ix_media_files_file_hash",
                schema: "public",
                table: "media_files",
                column: "file_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_media_files_file_path",
                schema: "public",
                table: "media_files",
                column: "file_path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_media_files_is_deleted_taken_at",
                schema: "public",
                table: "media_files",
                columns: new[] { "is_deleted", "taken_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_media_files_taken_at",
                schema: "public",
                table: "media_files",
                column: "taken_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_media_metadata_latitude_longitude",
                schema: "public",
                table: "media_metadata",
                columns: new[] { "latitude", "longitude" });

            migrationBuilder.CreateIndex(
                name: "ix_media_tags_media_file_id",
                schema: "public",
                table: "media_tags",
                column: "media_file_id");

            migrationBuilder.CreateIndex(
                name: "ix_media_tags_tag",
                schema: "public",
                table: "media_tags",
                column: "tag");

            migrationBuilder.CreateIndex(
                name: "ix_processing_jobs_scan_id_status",
                schema: "public",
                table: "processing_jobs",
                columns: new[] { "scan_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_processing_jobs_started_at",
                schema: "public",
                table: "processing_jobs",
                column: "started_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_processing_jobs_status",
                schema: "public",
                table: "processing_jobs",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_series_media_media_file_id",
                schema: "public",
                table: "series_media",
                column: "media_file_id");

            // ── Partial / ivfflat indexes (not expressible via EF fluent mapping) ──────────

            // Only classified rows are queried on the Review page / origin filter.
            migrationBuilder.Sql(
                "CREATE INDEX ix_media_files_origin_class " +
                "ON public.media_files (origin_class) " +
                "WHERE origin_class IS NOT NULL;");

            // At most one active job per file — backstop against rescans while a file is in flight.
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ix_processing_jobs_file_path_active " +
                "ON public.processing_jobs (file_path) " +
                "WHERE status IN ('queued', 'processing', 'enriching');");

            // ivfflat cosine-similarity index for CLIP embeddings. Created on an empty table —
            // recreate once after the Phase 9 archive load for correct recall.
            migrationBuilder.Sql(
                "CREATE INDEX ix_media_embeddings_embedding " +
                "ON public.media_embeddings " +
                "USING ivfflat (embedding vector_cosine_ops) WITH (lists = 100);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "album_media",
                schema: "public");

            migrationBuilder.DropTable(
                name: "media_embeddings",
                schema: "public");

            migrationBuilder.DropTable(
                name: "media_metadata",
                schema: "public");

            migrationBuilder.DropTable(
                name: "media_tags",
                schema: "public");

            migrationBuilder.DropTable(
                name: "media_thumbnails",
                schema: "public");

            migrationBuilder.DropTable(
                name: "processing_jobs",
                schema: "public");

            migrationBuilder.DropTable(
                name: "series_media",
                schema: "public");

            migrationBuilder.DropTable(
                name: "video_transcodes",
                schema: "public");

            migrationBuilder.DropTable(
                name: "albums",
                schema: "public");

            migrationBuilder.DropTable(
                name: "media_files",
                schema: "public");

            migrationBuilder.DropTable(
                name: "photo_series",
                schema: "public");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");
        }
    }
}
