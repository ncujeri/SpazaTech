using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpazaHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ExpiryTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ExpiryWarningDays",
                table: "TenantConfigs",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ExpiryDate",
                table: "StockMovements",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExpiryWarningDays",
                table: "TenantConfigs");

            migrationBuilder.DropColumn(
                name: "ExpiryDate",
                table: "StockMovements");
        }
    }
}
