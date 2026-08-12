using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceApiCredentialPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "Permissions",
                table: "service_api_credential",
                type: "text[]",
                nullable: true);

            // Existing global analyst credentials retain read access but gain no
            // newly service-enabled administrative capability.
            migrationBuilder.Sql(
                "UPDATE service_api_credential SET \"Permissions\" = ARRAY['portfolio.read']::text[]");

            migrationBuilder.AlterColumn<string[]>(
                name: "Permissions",
                table: "service_api_credential",
                type: "text[]",
                nullable: false,
                oldClrType: typeof(string[]),
                oldType: "text[]",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_service_api_credential_Permissions",
                table: "service_api_credential",
                sql: "cardinality(\"Permissions\") BETWEEN 1 AND 8 AND \"Permissions\" <@ ARRAY['portfolio.read','alerts.manage','clients.manage','domains.manage','sources.manage','sources.sync','notifications.manage','audit.read']::text[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_service_api_credential_Permissions",
                table: "service_api_credential");

            migrationBuilder.DropColumn(
                name: "Permissions",
                table: "service_api_credential");
        }
    }
}
