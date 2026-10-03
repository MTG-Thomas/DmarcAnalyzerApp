using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class WidenProtocolConfigurationForS3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_report_source_ProtocolConfiguration",
                table: "report_source");

            migrationBuilder.AddCheckConstraint(
                name: "CK_report_source_ProtocolConfiguration",
                table: "report_source",
                sql: "(\"Protocol\" = 'api' AND \"Host\" IS NULL AND \"Port\" IS NULL AND \"UseTls\" IS NULL AND \"Username\" IS NULL AND \"PasswordEncrypted\" IS NULL AND \"DeleteAfterRetention\" = FALSE AND \"OldestMessageAtUtc\" IS NULL AND \"LastSuccessSyncAtUtc\" IS NULL AND \"LastProcessedUid\" IS NULL AND \"LastProcessedUidValidity\" IS NULL) OR (\"Protocol\" IN ('imap', 'pop3') AND \"Host\" IS NOT NULL AND \"Port\" > 0 AND \"UseTls\" IS NOT NULL AND \"Username\" IS NOT NULL AND \"PasswordEncrypted\" IS NOT NULL) OR (\"Protocol\" = 's3' AND \"Host\" IS NULL AND \"Port\" IS NULL AND \"UseTls\" IS NOT NULL AND \"S3Bucket\" IS NOT NULL AND \"S3Bucket\" <> '' AND ((\"Username\" IS NULL AND \"PasswordEncrypted\" IS NULL) OR (\"Username\" IS NOT NULL AND \"PasswordEncrypted\" IS NOT NULL)) AND \"LastProcessedUid\" IS NULL AND \"LastProcessedUidValidity\" IS NULL AND \"LastProcessedUidl\" IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_report_source_ProtocolConfiguration",
                table: "report_source");

            migrationBuilder.AddCheckConstraint(
                name: "CK_report_source_ProtocolConfiguration",
                table: "report_source",
                sql: "(\"Protocol\" = 'api' AND \"Host\" IS NULL AND \"Port\" IS NULL AND \"UseTls\" IS NULL AND \"Username\" IS NULL AND \"PasswordEncrypted\" IS NULL AND \"DeleteAfterRetention\" = FALSE AND \"OldestMessageAtUtc\" IS NULL AND \"LastSuccessSyncAtUtc\" IS NULL AND \"LastProcessedUid\" IS NULL AND \"LastProcessedUidValidity\" IS NULL) OR (\"Protocol\" IN ('imap', 'pop3') AND \"Host\" IS NOT NULL AND \"Port\" > 0 AND \"UseTls\" IS NOT NULL AND \"Username\" IS NOT NULL AND \"PasswordEncrypted\" IS NOT NULL)");
        }
    }
}
