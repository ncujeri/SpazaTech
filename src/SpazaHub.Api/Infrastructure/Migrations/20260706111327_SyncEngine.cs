using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpazaHub.Api.Migrations
{
    /// <inheritdoc />
    public partial class SyncEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAtUtc",
                table: "CashUps",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAtUtc",
                table: "Cashiers",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.CreateTable(
                name: "SyncConflictAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IncomingWon = table.Column<bool>(type: "bit", nullable: false),
                    LosingPayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IncomingUpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExistingUpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SourceDeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncConflictAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantChangeLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SourceDeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantChangeLog", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyncConflictAudits_TenantId",
                table: "SyncConflictAudits",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncConflictAudits_TenantId_EntityId",
                table: "SyncConflictAudits",
                columns: new[] { "TenantId", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantChangeLog_TenantId",
                table: "TenantChangeLog",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantChangeLog_TenantId_Id",
                table: "TenantChangeLog",
                columns: new[] { "TenantId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyncConflictAudits");

            migrationBuilder.DropTable(
                name: "TenantChangeLog");

            migrationBuilder.DropColumn(
                name: "UpdatedAtUtc",
                table: "CashUps");

            migrationBuilder.DropColumn(
                name: "UpdatedAtUtc",
                table: "Cashiers");
        }
    }
}
