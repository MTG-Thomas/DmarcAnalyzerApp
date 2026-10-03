using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSpfDriftState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "spf_drift_state",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DomainId = table.Column<Guid>(type: "uuid", nullable: false),
                    SpfRecordStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RawRecord = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    DependencySnapshotJson = table.Column<string>(type: "text", nullable: true),
                    DependencyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PreviousDependencyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PreviousDependencySnapshotJson = table.Column<string>(type: "text", nullable: true),
                    DependencyChangedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CandidateStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    CandidateText = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    CandidateHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PreviousCandidateStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    CandidateChangedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PublishedLookups = table.Column<int>(type: "integer", nullable: true),
                    CandidateLookups = table.Column<int>(type: "integer", nullable: true),
                    CandidateLength = table.Column<int>(type: "integer", nullable: true),
                    PublishedOverBudget = table.Column<bool>(type: "boolean", nullable: true),
                    IssuesJson = table.Column<string>(type: "text", nullable: true),
                    PreviousPublishedLookups = table.Column<int>(type: "integer", nullable: true),
                    PreviousCandidateLookups = table.Column<int>(type: "integer", nullable: true),
                    PreviousCandidateLength = table.Column<int>(type: "integer", nullable: true),
                    PreviousPublishedOverBudget = table.Column<bool>(type: "boolean", nullable: true),
                    PreviousCandidateText = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    PreviousCandidateHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastSuccessAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    LastCheckedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastChangedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_spf_drift_state", x => x.Id);
                    table.ForeignKey(
                        name: "FK_spf_drift_state_domain_DomainId",
                        column: x => x.DomainId,
                        principalTable: "domain",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_spf_drift_state_DomainId",
                table: "spf_drift_state",
                column: "DomainId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_spf_drift_state_LastCheckedAtUtc",
                table: "spf_drift_state",
                column: "LastCheckedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "spf_drift_state");
        }
    }
}
