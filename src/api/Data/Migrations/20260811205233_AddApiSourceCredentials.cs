using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddApiSourceCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "api_source_credential",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Prefix = table.Column<string>(type: "character varying(22)", maxLength: 22, nullable: false),
                    TokenHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_source_credential", x => x.Id);
                    table.CheckConstraint("CK_api_source_credential_PrefixLength", "char_length(\"Prefix\") = 22");
                    table.CheckConstraint("CK_api_source_credential_TokenHashLength", "octet_length(\"TokenHash\") = 32");
                    table.ForeignKey(
                        name: "FK_api_source_credential_report_source_ReportSourceId",
                        column: x => x.ReportSourceId,
                        principalTable: "report_source",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_api_source_credential_ReportSourceId_Prefix",
                table: "api_source_credential",
                columns: new[] { "ReportSourceId", "Prefix" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_source_credential_ReportSourceId_RevokedAtUtc",
                table: "api_source_credential",
                columns: new[] { "ReportSourceId", "RevokedAtUtc" });

            migrationBuilder.Sql(
                """
                CREATE FUNCTION dmarc_require_api_source_credential()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                DECLARE
                    source_protocol text;
                BEGIN
                    SELECT "Protocol"
                    INTO source_protocol
                    FROM report_source
                    WHERE "Id" = NEW."ReportSourceId"
                    FOR UPDATE;

                    IF source_protocol IS DISTINCT FROM 'api' THEN
                        RAISE EXCEPTION 'API credentials require an API report source'
                            USING ERRCODE = '23514',
                                  CONSTRAINT = 'CK_api_source_credential_SourceProtocol';
                    END IF;

                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER TR_api_source_credential_RequireApiSource
                BEFORE INSERT OR UPDATE OF "ReportSourceId"
                ON api_source_credential
                FOR EACH ROW
                EXECUTE FUNCTION dmarc_require_api_source_credential();

                CREATE FUNCTION dmarc_revoke_credentials_on_protocol_change()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    IF OLD."Protocol" = 'api' AND NEW."Protocol" <> 'api' THEN
                        UPDATE api_source_credential
                        SET "RevokedAtUtc" = CURRENT_TIMESTAMP
                        WHERE "ReportSourceId" = NEW."Id"
                          AND "RevokedAtUtc" IS NULL;
                    END IF;

                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER TR_report_source_RevokeApiCredentials
                BEFORE UPDATE OF "Protocol"
                ON report_source
                FOR EACH ROW
                EXECUTE FUNCTION dmarc_revoke_credentials_on_protocol_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM api_source_credential) THEN
                        RAISE EXCEPTION 'cannot remove API source credentials while credential rows exist';
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql(
                """
                DROP TRIGGER TR_report_source_RevokeApiCredentials ON report_source;
                DROP FUNCTION dmarc_revoke_credentials_on_protocol_change();
                DROP TRIGGER TR_api_source_credential_RequireApiSource ON api_source_credential;
                DROP FUNCTION dmarc_require_api_source_credential();
                """);

            migrationBuilder.DropTable(
                name: "api_source_credential");
        }
    }
}
