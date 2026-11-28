using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andril.Persistence.Migrations.Social
{
    /// <inheritdoc />
    public partial class SocialContextualConsentModuleEpochs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE andril_social_ai.module_epochs (
                    account_id uuid PRIMARY KEY REFERENCES andril_identity.accounts(id) ON DELETE CASCADE,
                    epoch uuid NOT NULL UNIQUE,
                    lifecycle_revision bigint NOT NULL CHECK (lifecycle_revision > 0)
                );
                REVOKE ALL ON andril_social_ai.module_epochs FROM PUBLIC;
                ALTER TABLE andril_social_ai.module_epochs ENABLE ROW LEVEL SECURITY;
                ALTER TABLE andril_social_ai.module_epochs FORCE ROW LEVEL SECURITY;
                CREATE POLICY social_ai_epoch_authority ON andril_social_ai.module_epochs TO CURRENT_USER USING (true) WITH CHECK (true);

                INSERT INTO andril_social_ai.module_epochs(account_id, epoch, lifecycle_revision)
                SELECT account_id, gen_random_uuid(), revision FROM andril_identity.account_module_lifecycles WHERE module = 'social';

                CREATE FUNCTION andril_social_ai.rotate_social_consent_epoch() RETURNS trigger
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
                DECLARE account uuid;
                DECLARE reason integer := 3;
                BEGIN
                    IF TG_OP = 'DELETE' THEN account := OLD.account_id;
                    ELSE account := NEW.account_id; END IF;
                    PERFORM andril_social.social_safety_lock();
                    IF TG_TABLE_NAME = 'account_module_lifecycles' THEN
                        IF TG_OP = 'DELETE' THEN
                            DELETE FROM andril_social_ai.module_epochs WHERE account_id = account;
                        ELSE
                            INSERT INTO andril_social_ai.module_epochs(account_id, epoch, lifecycle_revision)
                            VALUES (account, gen_random_uuid(), NEW.revision)
                            ON CONFLICT (account_id) DO UPDATE SET epoch = EXCLUDED.epoch, lifecycle_revision = EXCLUDED.lifecycle_revision;
                        END IF;
                    ELSE
                        UPDATE andril_social_ai.module_epochs SET epoch = gen_random_uuid() WHERE account_id = account;
                    END IF;
                    IF TG_TABLE_NAME = 'account_module_lifecycles' THEN
                        IF TG_OP = 'DELETE' THEN reason := 5;
                        ELSIF NEW.state = 'inactive' THEN reason := 5; END IF;
                    END IF;
                    WITH revoked AS (
                        UPDATE andril_social_ai.consents SET state = 6, revision = gen_random_uuid()
                        WHERE account_id = account AND state IN (1,2,3) RETURNING operation_id
                    )
                    INSERT INTO andril_social_ai.consent_invalidations(event_id, operation_id, reason)
                    SELECT gen_random_uuid(), revoked.operation_id, reason FROM revoked;
                    RETURN NULL;
                END;
                $$;

                CREATE TRIGGER social_ai_lifecycle_epoch_insert AFTER INSERT ON andril_identity.account_module_lifecycles
                    FOR EACH ROW WHEN (NEW.module = 'social') EXECUTE FUNCTION andril_social_ai.rotate_social_consent_epoch();
                CREATE TRIGGER social_ai_lifecycle_epoch_update AFTER UPDATE OF state, revision ON andril_identity.account_module_lifecycles
                    FOR EACH ROW WHEN (NEW.module = 'social' AND (OLD.state,OLD.revision) IS DISTINCT FROM (NEW.state,NEW.revision))
                    EXECUTE FUNCTION andril_social_ai.rotate_social_consent_epoch();
                CREATE TRIGGER social_ai_lifecycle_epoch_delete AFTER DELETE ON andril_identity.account_module_lifecycles
                    FOR EACH ROW WHEN (OLD.module = 'social') EXECUTE FUNCTION andril_social_ai.rotate_social_consent_epoch();

                CREATE TRIGGER social_ai_starter_epoch_update AFTER UPDATE OF module, social_opt_in_at_utc ON andril_identity.account_starter_memberships
                    FOR EACH ROW WHEN ((OLD.module = 'social' OR NEW.module = 'social')
                        AND (OLD.module,OLD.social_opt_in_at_utc) IS DISTINCT FROM (NEW.module,NEW.social_opt_in_at_utc))
                    EXECUTE FUNCTION andril_social_ai.rotate_social_consent_epoch();
                CREATE TRIGGER social_ai_starter_epoch_delete AFTER DELETE ON andril_identity.account_starter_memberships
                    FOR EACH ROW WHEN (OLD.module = 'social') EXECUTE FUNCTION andril_social_ai.rotate_social_consent_epoch();

                CREATE FUNCTION andril_social_ai.is_current_module_epoch(account_key uuid, profile_key uuid, expected_epoch uuid) RETURNS boolean
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
                    SELECT EXISTS (
                        SELECT FROM andril_social_ai.module_epochs epoch
                        JOIN andril_identity.private_module_profiles profile ON profile.account_id = epoch.account_id AND profile.module = 'social'
                        JOIN andril_identity.account_module_lifecycles l ON l.account_id = epoch.account_id AND l.module = 'social'
                        JOIN andril_identity.account_starter_memberships s ON s.account_id = epoch.account_id AND s.module = 'social'
                        JOIN andril_identity.account_demographics d ON d.account_id = epoch.account_id
                        WHERE epoch.account_id = account_key AND profile.id = profile_key AND epoch.epoch = expected_epoch
                            AND epoch.lifecycle_revision = l.revision AND l.state = 'active' AND s.social_opt_in_at_utc IS NOT NULL
                            AND d.date_of_birth <= (timezone('UTC', statement_timestamp())::date - interval '18 years')
                            AND NOT EXISTS (SELECT FROM andril_social.social_profile_restrictions r WHERE r.profile_id = profile_key)
                    );
                $$;

                REVOKE ALL ON FUNCTION andril_social_ai.rotate_social_consent_epoch(),
                    andril_social_ai.is_current_module_epoch(uuid,uuid,uuid) FROM PUBLIC;

                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER social_ai_starter_epoch_delete ON andril_identity.account_starter_memberships;
                DROP TRIGGER social_ai_starter_epoch_update ON andril_identity.account_starter_memberships;
                DROP TRIGGER social_ai_lifecycle_epoch_delete ON andril_identity.account_module_lifecycles;
                DROP TRIGGER social_ai_lifecycle_epoch_update ON andril_identity.account_module_lifecycles;
                DROP TRIGGER social_ai_lifecycle_epoch_insert ON andril_identity.account_module_lifecycles;
                DROP FUNCTION andril_social_ai.is_current_module_epoch(uuid,uuid,uuid);
                DROP FUNCTION andril_social_ai.rotate_social_consent_epoch();
                DROP TABLE andril_social_ai.module_epochs;
                """);
        }
    }
}
