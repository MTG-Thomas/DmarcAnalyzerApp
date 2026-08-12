using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserPasskeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_passkey",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CredentialId = table.Column<byte[]>(type: "bytea", nullable: false),
                    PublicKey = table.Column<byte[]>(type: "bytea", nullable: false),
                    UserHandle = table.Column<byte[]>(type: "bytea", nullable: false),
                    SignCount = table.Column<long>(type: "bigint", nullable: false),
                    Transports = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AaGuid = table.Column<Guid>(type: "uuid", nullable: false),
                    IsBackupEligible = table.Column<bool>(type: "boolean", nullable: false),
                    IsBackedUp = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUsedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_passkey", x => x.Id);
                    table.CheckConstraint("CK_user_passkey_CredentialIdLength", "octet_length(\"CredentialId\") BETWEEN 16 AND 1023");
                    table.CheckConstraint("CK_user_passkey_PublicKeyLength", "octet_length(\"PublicKey\") BETWEEN 32 AND 4096");
                    table.CheckConstraint("CK_user_passkey_SignCount", "\"SignCount\" >= 0 AND \"SignCount\" <= 4294967295");
                    table.CheckConstraint("CK_user_passkey_UserHandleLength", "octet_length(\"UserHandle\") = 16");
                    table.ForeignKey(
                        name: "FK_user_passkey_agency_user_UserId",
                        column: x => x.UserId,
                        principalTable: "agency_user",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_passkey_UserId",
                table: "user_passkey",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "UX_user_passkey_CredentialId",
                table: "user_passkey",
                column: "CredentialId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM user_passkey) THEN
                        RAISE EXCEPTION 'Cannot remove passkey schema while passkey rows exist.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropTable(
                name: "user_passkey");
        }
    }
}
