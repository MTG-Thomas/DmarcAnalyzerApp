using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RepairApiSourceCredentialUpgrade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    has_legacy_column boolean;
                    has_canonical_column boolean;
                BEGIN
                    SELECT EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'api_source_credential'
                          AND column_name = 'MailboxSourceId'
                    ) INTO has_legacy_column;

                    SELECT EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'api_source_credential'
                          AND column_name = 'ReportSourceId'
                    ) INTO has_canonical_column;

                    IF has_legacy_column AND has_canonical_column THEN
                        RAISE EXCEPTION 'api_source_credential contains both legacy and canonical source columns';
                    ELSIF has_legacy_column THEN
                        ALTER TABLE api_source_credential
                            RENAME COLUMN "MailboxSourceId" TO "ReportSourceId";
                    ELSIF NOT has_canonical_column THEN
                        RAISE EXCEPTION 'api_source_credential source column is missing';
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    has_legacy_index boolean;
                    has_canonical_index boolean;
                BEGIN
                    SELECT to_regclass('public."IX_api_source_credential_MailboxSourceId_Prefix"') IS NOT NULL
                        INTO has_legacy_index;
                    SELECT to_regclass('public."IX_api_source_credential_ReportSourceId_Prefix"') IS NOT NULL
                        INTO has_canonical_index;

                    IF has_legacy_index AND has_canonical_index THEN
                        RAISE EXCEPTION 'api_source_credential contains both legacy and canonical prefix indexes';
                    ELSIF has_legacy_index THEN
                        ALTER INDEX "IX_api_source_credential_MailboxSourceId_Prefix"
                            RENAME TO "IX_api_source_credential_ReportSourceId_Prefix";
                    ELSIF NOT has_canonical_index THEN
                        RAISE EXCEPTION 'api_source_credential prefix index is missing';
                    END IF;

                    SELECT to_regclass('public."IX_api_source_credential_MailboxSourceId_RevokedAtUtc"') IS NOT NULL
                        INTO has_legacy_index;
                    SELECT to_regclass('public."IX_api_source_credential_ReportSourceId_RevokedAtUtc"') IS NOT NULL
                        INTO has_canonical_index;

                    IF has_legacy_index AND has_canonical_index THEN
                        RAISE EXCEPTION 'api_source_credential contains both legacy and canonical revocation indexes';
                    ELSIF has_legacy_index THEN
                        ALTER INDEX "IX_api_source_credential_MailboxSourceId_RevokedAtUtc"
                            RENAME TO "IX_api_source_credential_ReportSourceId_RevokedAtUtc";
                    ELSIF NOT has_canonical_index THEN
                        RAISE EXCEPTION 'api_source_credential revocation index is missing';
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    has_legacy_constraint boolean;
                    has_canonical_constraint boolean;
                BEGIN
                    SELECT EXISTS (
                        SELECT 1 FROM pg_constraint
                        WHERE conrelid = 'api_source_credential'::regclass
                          AND conname = 'FK_api_source_credential_mailbox_source_MailboxSourceId'
                    ) INTO has_legacy_constraint;

                    SELECT EXISTS (
                        SELECT 1 FROM pg_constraint
                        WHERE conrelid = 'api_source_credential'::regclass
                          AND conname = 'FK_api_source_credential_report_source_ReportSourceId'
                    ) INTO has_canonical_constraint;

                    IF has_legacy_constraint AND has_canonical_constraint THEN
                        RAISE EXCEPTION 'api_source_credential contains both legacy and canonical source constraints';
                    ELSIF has_legacy_constraint THEN
                        ALTER TABLE api_source_credential
                            RENAME CONSTRAINT "FK_api_source_credential_mailbox_source_MailboxSourceId"
                            TO "FK_api_source_credential_report_source_ReportSourceId";
                    ELSIF NOT has_canonical_constraint THEN
                        RAISE EXCEPTION 'api_source_credential source constraint is missing';
                    END IF;

                    IF NOT EXISTS (
                        SELECT 1
                        FROM pg_constraint c
                        JOIN pg_attribute source_column
                          ON source_column.attrelid = c.conrelid
                         AND source_column.attnum = c.conkey[1]
                        JOIN pg_attribute target_column
                          ON target_column.attrelid = c.confrelid
                         AND target_column.attnum = c.confkey[1]
                        WHERE c.conrelid = 'api_source_credential'::regclass
                          AND c.confrelid = 'report_source'::regclass
                          AND c.conname = 'FK_api_source_credential_report_source_ReportSourceId'
                          AND c.contype = 'f'
                          AND c.confdeltype = 'c'
                          AND source_column.attname = 'ReportSourceId'
                          AND target_column.attname = 'Id'
                    ) THEN
                        RAISE EXCEPTION 'api_source_credential source constraint has an unexpected definition';
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS TR_api_source_credential_RequireApiSource
                    ON api_source_credential;
                DROP TRIGGER IF EXISTS TR_mailbox_source_RevokeApiCredentials
                    ON report_source;
                DROP TRIGGER IF EXISTS TR_report_source_RevokeApiCredentials
                    ON report_source;

                CREATE OR REPLACE FUNCTION dmarc_require_api_source_credential()
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

                CREATE OR REPLACE FUNCTION dmarc_revoke_credentials_on_protocol_change()
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
            // The migration repairs drift from an older, already-applied version of
            // AddApiSourceCredentials. Its result is the schema expected by that earlier
            // migration, so no catalog change is required when removing this history row.
        }
    }
}
