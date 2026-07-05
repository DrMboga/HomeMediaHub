using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaWorker.Sagas.Migrations
{
    /// <inheritdoc />
    public partial class AddEnrichmentSaga : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "public");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "enrichment_sagas",
                schema: "public",
                columns: table => new
                {
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    current_state = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    analyze_file_path = table.Column<string>(type: "text", nullable: false),
                    work_file_path = table.Column<string>(type: "text", nullable: true),
                    has_gps = table.Column<bool>(type: "boolean", nullable: false),
                    lat = table.Column<double>(type: "double precision", nullable: true),
                    lon = table.Column<double>(type: "double precision", nullable: true),
                    ai_done = table.Column<bool>(type: "boolean", nullable: false),
                    ai_success = table.Column<bool>(type: "boolean", nullable: false),
                    tags_json = table.Column<string>(type: "text", nullable: true),
                    embedding_json = table.Column<string>(type: "text", nullable: true),
                    origin_class = table.Column<string>(type: "text", nullable: true),
                    origin_confidence = table.Column<double>(type: "double precision", nullable: true),
                    ai_error = table.Column<string>(type: "text", nullable: true),
                    geo_done = table.Column<bool>(type: "boolean", nullable: false),
                    geo_success = table.Column<bool>(type: "boolean", nullable: false),
                    address = table.Column<string>(type: "text", nullable: true),
                    geo_error = table.Column<string>(type: "text", nullable: true),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_enrichment_sagas", x => x.correlation_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "enrichment_sagas",
                schema: "public");
        }
    }
}
