using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceApiCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "service_api_credential",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Prefix = table.Column<string>(type: "character varying(22)", maxLength: 22, nullable: false),
                    TokenHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_api_credential", x => x.Id);
                    table.CheckConstraint("CK_service_api_credential_Expiry", "\"ExpiresAtUtc\" > \"CreatedAtUtc\"");
                    table.CheckConstraint("CK_service_api_credential_PrefixLength", "char_length(\"Prefix\") = 22");
                    table.CheckConstraint("CK_service_api_credential_TokenHashLength", "octet_length(\"TokenHash\") = 32");
                });

            migrationBuilder.CreateIndex(
                name: "IX_service_api_credential_ExpiresAtUtc",
                table: "service_api_credential",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_service_api_credential_Prefix",
                table: "service_api_credential",
                column: "Prefix",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_service_api_credential_RevokedAtUtc",
                table: "service_api_credential",
                column: "RevokedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "service_api_credential");
        }
    }
}
