using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpazaHub.Api.Migrations
{
    /// <inheritdoc />
    public partial class OwnerPin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OwnerPinHash",
                table: "TenantConfigs",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OwnerPinHash",
                table: "TenantConfigs");
        }
    }
}
