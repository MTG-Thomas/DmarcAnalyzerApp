using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddServerlessDurabilityState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "passkey_ceremony",
                columns: table => new
                {
                    Handle = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Challenge = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsumedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_passkey_ceremony", x => x.Handle);
                });

            migrationBuilder.CreateTable(
                name: "scheduled_task_state",
                columns: table => new
                {
                    TaskKey = table.Column<string>(type: "text", nullable: false),
                    LastRunAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSuccessAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scheduled_task_state", x => x.TaskKey);
                });

            migrationBuilder.CreateTable(
                name: "sync_request",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ResultJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_request", x => x.Id);
                    table.CheckConstraint("CK_sync_request_Status", "\"Status\" IN ('queued','running','completed','partial','failed','cancelled')");
                    table.ForeignKey(
                        name: "FK_sync_request_report_source_ReportSourceId",
                        column: x => x.ReportSourceId,
                        principalTable: "report_source",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_passkey_ceremony_ExpiresAtUtc",
                table: "passkey_ceremony",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_sync_request_ReportSourceId",
                table: "sync_request",
                column: "ReportSourceId",
                unique: true,
                filter: "\"Status\" IN ('queued','running')");

            migrationBuilder.CreateIndex(
                name: "IX_sync_request_Status_CreatedAtUtc",
                table: "sync_request",
                columns: new[] { "Status", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "passkey_ceremony");

            migrationBuilder.DropTable(
                name: "scheduled_task_state");

            migrationBuilder.DropTable(
                name: "sync_request");
        }
    }
}
