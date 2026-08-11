using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddApiMailboxSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Username",
                table: "mailbox_source",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(255)",
                oldMaxLength: 255);

            migrationBuilder.AlterColumn<bool>(
                name: "UseTls",
                table: "mailbox_source",
                type: "boolean",
                nullable: true,
                oldClrType: typeof(bool),
                oldType: "boolean");

            migrationBuilder.AlterColumn<int>(
                name: "Port",
                table: "mailbox_source",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<string>(
                name: "PasswordEncrypted",
                table: "mailbox_source",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(2048)",
                oldMaxLength: 2048);

            migrationBuilder.AlterColumn<string>(
                name: "Host",
                table: "mailbox_source",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(255)",
                oldMaxLength: 255);

            migrationBuilder.AddCheckConstraint(
                name: "CK_mailbox_source_ProtocolConfiguration",
                table: "mailbox_source",
                sql: "(\"Protocol\" = 'api' AND \"Host\" IS NULL AND \"Port\" IS NULL AND \"UseTls\" IS NULL AND \"Username\" IS NULL AND \"PasswordEncrypted\" IS NULL AND \"DeleteAfterRetention\" = FALSE AND \"OldestMessageAtUtc\" IS NULL AND \"LastSuccessSyncAtUtc\" IS NULL AND \"LastProcessedUid\" IS NULL AND \"LastProcessedUidValidity\" IS NULL) OR (\"Protocol\" IN ('imap', 'pop3') AND \"Host\" IS NOT NULL AND \"Port\" > 0 AND \"UseTls\" IS NOT NULL AND \"Username\" IS NOT NULL AND \"PasswordEncrypted\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM mailbox_source WHERE "Protocol" = 'api') THEN
                        RAISE EXCEPTION 'cannot remove API mailbox-source support while API sources exist';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_mailbox_source_ProtocolConfiguration",
                table: "mailbox_source");

            migrationBuilder.AlterColumn<string>(
                name: "Username",
                table: "mailbox_source",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(255)",
                oldMaxLength: 255,
                oldNullable: true);

            migrationBuilder.AlterColumn<bool>(
                name: "UseTls",
                table: "mailbox_source",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "Port",
                table: "mailbox_source",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "PasswordEncrypted",
                table: "mailbox_source",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(2048)",
                oldMaxLength: 2048,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Host",
                table: "mailbox_source",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(255)",
                oldMaxLength: 255,
                oldNullable: true);
        }
    }
}
