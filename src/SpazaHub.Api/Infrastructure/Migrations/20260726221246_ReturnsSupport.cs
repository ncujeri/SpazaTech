using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpazaHub.Api.Migrations
{
    /// <inheritdoc />
    public partial class ReturnsSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CashiersMayDoReturns",
                table: "TenantConfigs",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ReversesSaleLineId",
                table: "SaleLines",
                type: "uniqueidentifier",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CashiersMayDoReturns",
                table: "TenantConfigs");

            migrationBuilder.DropColumn(
                name: "ReversesSaleLineId",
                table: "SaleLines");
        }
    }
}
