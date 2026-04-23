using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GeoService.Data.Migrations
{
    /// <inheritdoc />
    public partial class CreateGeoCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "geo_cache",
                columns: table => new
                {
                    lat = table.Column<double>(type: "double precision", nullable: false),
                    lon = table.Column<double>(type: "double precision", nullable: false),
                    address = table.Column<string>(type: "text", nullable: false),
                    cached_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_geo_cache", x => new { x.lat, x.lon });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "geo_cache");
        }
    }
}
