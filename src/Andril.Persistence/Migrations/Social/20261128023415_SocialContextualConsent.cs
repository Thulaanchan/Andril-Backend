using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andril.Persistence.Migrations.Social
{
    /// <inheritdoc />
    public partial class SocialContextualConsent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE SCHEMA andril_social_ai;
                REVOKE ALL ON SCHEMA andril_social_ai FROM PUBLIC;

                CREATE TABLE andril_social_ai.consents (
                    operation_id uuid PRIMARY KEY CHECK (operation_id <> '00000000-0000-0000-0000-000000000000'::uuid),
                    account_id uuid NOT NULL REFERENCES andril_identity.accounts(id),
                    profile_id uuid NOT NULL,
                    module varchar(16) NOT NULL DEFAULT 'social' CHECK (module = 'social'),
                    conversation_id uuid NOT NULL CHECK (conversation_id <> '00000000-0000-0000-0000-000000000000'::uuid),
                    session_epoch uuid NOT NULL CHECK (session_epoch <> '00000000-0000-0000-0000-000000000000'::uuid),
                    module_epoch uuid NOT NULL CHECK (module_epoch <> '00000000-0000-0000-0000-000000000000'::uuid),
                    purpose_id text NOT NULL CHECK (length(btrim(purpose_id)) > 0),
                    purpose_version text NOT NULL CHECK (length(btrim(purpose_version)) > 0),
                    scope_id text NOT NULL CHECK (length(btrim(scope_id)) > 0),
                    notice_revision text NOT NULL CHECK (length(btrim(notice_revision)) > 0),
                    state integer NOT NULL CHECK (state BETWEEN 1 AND 8),
                    revision uuid NOT NULL CHECK (revision <> '00000000-0000-0000-0000-000000000000'::uuid),
                    offered_at_utc timestamptz NOT NULL CHECK (isfinite(offered_at_utc)),
                    granted_at_utc timestamptz CHECK (isfinite(granted_at_utc) AND granted_at_utc >= offered_at_utc),
                    started_at_utc timestamptz CHECK (isfinite(started_at_utc) AND started_at_utc >= granted_at_utc
                        AND started_at_utc < granted_at_utc + interval '5 minutes'),
                    attempt_id uuid UNIQUE CHECK (attempt_id <> '00000000-0000-0000-0000-000000000000'::uuid),
                    CHECK ((started_at_utc IS NULL) = (attempt_id IS NULL)),
                    CHECK (started_at_utc IS NULL OR granted_at_utc IS NOT NULL),
                    CHECK (CASE
                        WHEN state IN (1,4) THEN granted_at_utc IS NULL AND started_at_utc IS NULL
                        WHEN state IN (2,5) THEN granted_at_utc IS NOT NULL AND started_at_utc IS NULL
                        WHEN state IN (3,7,8) THEN started_at_utc IS NOT NULL
                        ELSE state = 6 END),
                    FOREIGN KEY (profile_id, module) REFERENCES andril_identity.private_module_profiles(id, module)
                );
                CREATE INDEX ix_social_consent_conversation_state ON andril_social_ai.consents(account_id, profile_id, conversation_id, state);

                CREATE TABLE andril_social_ai.consent_requests (
                    operation_id uuid NOT NULL REFERENCES andril_social_ai.consents(operation_id),
                    request_id uuid NOT NULL CHECK (request_id <> '00000000-0000-0000-0000-000000000000'::uuid),
                    request_digest bytea NOT NULL CHECK (octet_length(request_digest) = 32),
                    interaction_id uuid UNIQUE CHECK (interaction_id <> '00000000-0000-0000-0000-000000000000'::uuid),
                    PRIMARY KEY (operation_id, request_id)
                );

                CREATE TABLE andril_social_ai.consent_invalidations (
                    event_id uuid PRIMARY KEY CHECK (event_id <> '00000000-0000-0000-0000-000000000000'::uuid),
                    operation_id uuid NOT NULL REFERENCES andril_social_ai.consents(operation_id),
                    reason integer NOT NULL CHECK (reason BETWEEN 1 AND 8)
                );

                CREATE TABLE andril_social_ai.consent_decision_audit (
                    event_id uuid PRIMARY KEY CHECK (event_id <> '00000000-0000-0000-0000-000000000000'::uuid),
                    operation_id uuid NOT NULL REFERENCES andril_social_ai.consents(operation_id),
                    decision integer NOT NULL CHECK (decision IN (1,2)),
                    purpose_version text NOT NULL CHECK (length(btrim(purpose_version)) > 0),
                    occurred_at_utc timestamptz NOT NULL CHECK (isfinite(occurred_at_utc))
                );
                CREATE INDEX ix_social_consent_audit_occurred ON andril_social_ai.consent_decision_audit(occurred_at_utc);

                CREATE TABLE andril_social_ai.consent_audit_holds (
                    event_id uuid PRIMARY KEY REFERENCES andril_social_ai.consent_decision_audit(event_id)
                );

                DO $$
                DECLARE relation text;
                BEGIN
                    FOREACH relation IN ARRAY ARRAY['consents','consent_requests','consent_invalidations','consent_decision_audit','consent_audit_holds'] LOOP
                        EXECUTE format('REVOKE ALL ON andril_social_ai.%I FROM PUBLIC', relation);
                        EXECUTE format('ALTER TABLE andril_social_ai.%I ENABLE ROW LEVEL SECURITY', relation);
                        EXECUTE format('ALTER TABLE andril_social_ai.%I FORCE ROW LEVEL SECURITY', relation);
                        EXECUTE format('CREATE POLICY social_ai_consent_authority ON andril_social_ai.%I TO CURRENT_USER USING (true) WITH CHECK (true)', relation);
                    END LOOP;
                END;
                $$;

                CREATE FUNCTION andril_social_ai.read_current_consent_audit()
                RETURNS SETOF andril_social_ai.consent_decision_audit LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
                    SELECT event.* FROM andril_social_ai.consent_decision_audit event
                    WHERE event.occurred_at_utc <= statement_timestamp() AND event.occurred_at_utc > statement_timestamp() - interval '336 hours';
                $$;

                CREATE FUNCTION andril_social_ai.purge_expired_consent_audit() RETURNS integer
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                DECLARE removed integer;
                BEGIN
                    LOCK TABLE andril_social_ai.consent_audit_holds IN SHARE MODE;
                    DELETE FROM andril_social_ai.consent_decision_audit event
                      WHERE event.occurred_at_utc <= statement_timestamp() - interval '336 hours'
                      AND NOT EXISTS (SELECT FROM andril_social_ai.consent_audit_holds hold WHERE hold.event_id = event.event_id);
                    GET DIAGNOSTICS removed = ROW_COUNT;
                    RETURN removed;
                END;
                $$;
                REVOKE ALL ON FUNCTION andril_social_ai.read_current_consent_audit(),
                    andril_social_ai.purge_expired_consent_audit() FROM PUBLIC;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION andril_social_ai.purge_expired_consent_audit();
                DROP FUNCTION andril_social_ai.read_current_consent_audit();
                DROP TABLE andril_social_ai.consent_audit_holds;
                DROP TABLE andril_social_ai.consent_decision_audit;
                DROP TABLE andril_social_ai.consent_invalidations;
                DROP TABLE andril_social_ai.consent_requests;
                DROP TABLE andril_social_ai.consents;
                DROP SCHEMA andril_social_ai;
                """);
        }
    }
}
