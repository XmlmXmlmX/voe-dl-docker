using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VoeDl.Web.Migrations
{
    /// <inheritdoc />
    public partial class DownloadHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Success",
                table: "DownloadHistory",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Success",
                table: "DownloadHistory");
        }
    }
}
