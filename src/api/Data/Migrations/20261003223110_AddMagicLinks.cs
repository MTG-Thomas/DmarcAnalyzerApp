using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMagicLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "magic_link",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    Label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Prefix = table.Column<string>(type: "character varying(22)", maxLength: 22, nullable: false),
                    TokenHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUsedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_magic_link", x => x.Id);
                    table.CheckConstraint("CK_magic_link_Expiry", "\"ExpiresAtUtc\" > \"CreatedAtUtc\"");
                    table.CheckConstraint("CK_magic_link_PrefixLength", "char_length(\"Prefix\") = 22");
                    table.CheckConstraint("CK_magic_link_TokenHashLength", "octet_length(\"TokenHash\") = 32");
                    table.ForeignKey(
                        name: "FK_magic_link_client_ClientId",
                        column: x => x.ClientId,
                        principalTable: "client",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_magic_link_ClientId",
                table: "magic_link",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_magic_link_ExpiresAtUtc",
                table: "magic_link",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_magic_link_Prefix",
                table: "magic_link",
                column: "Prefix",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_magic_link_RevokedAtUtc",
                table: "magic_link",
                column: "RevokedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "magic_link");
        }
    }
}
