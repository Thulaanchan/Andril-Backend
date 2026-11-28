using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andril.Persistence.Migrations.Groups
{
    /// <inheritdoc />
    public partial class CanonicalGroupEntitlement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION andril_groups.creator_entitlement(creator uuid, source_module text) RETURNS integer
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                DECLARE creator_account uuid;
                DECLARE subscription record;
                BEGIN
                    SELECT p.account_id INTO creator_account FROM andril_identity.private_module_profiles p WHERE p.id = creator AND p.module = source_module AND source_module IN ('learning', 'hobbies', 'social');
                    IF creator_account IS NULL OR to_regclass('andril_economy.premium_entitlements') IS NULL THEN
                        RAISE EXCEPTION 'Group authority unavailable' USING ERRCODE = 'AG503';
                    END IF;
                    -- Hold the table fence even when the canonical record has not been created. Observe evidence after acquiring it.
                    LOCK TABLE andril_economy.premium_entitlements IN SHARE MODE;
                    IF row_security_active('andril_economy.premium_entitlements'::regclass) THEN
                        RAISE EXCEPTION 'Entitlement visibility is indeterminate' USING ERRCODE = 'AG503';
                    END IF;
                    SELECT entitlement.tier, entitlement.is_active, entitlement.expires_at_utc INTO subscription
                      FROM andril_economy.premium_entitlements entitlement WHERE entitlement.account_id = creator_account;
                    IF NOT FOUND OR (subscription.tier = 0 AND NOT subscription.is_active AND subscription.expires_at_utc IS NULL) THEN RETURN 1; END IF;
                    IF subscription.tier IN (1, 2) AND subscription.is_active AND subscription.expires_at_utc IS NOT NULL
                        AND isfinite(subscription.expires_at_utc) THEN
                        RETURN CASE WHEN subscription.expires_at_utc > clock_timestamp() THEN 2 ELSE 3 END;
                    END IF;
                    RAISE EXCEPTION 'Entitlement status is indeterminate' USING ERRCODE = 'AG503';
                EXCEPTION WHEN insufficient_privilege OR undefined_table OR undefined_column THEN
                    RAISE EXCEPTION 'Group entitlement authority unavailable' USING ERRCODE = 'AG503';
                END;
                $$;

                REVOKE ALL ON FUNCTION andril_groups.creator_entitlement(uuid, text) FROM PUBLIC;

                CREATE OR REPLACE FUNCTION andril_groups.club_limit(creator uuid) RETURNS integer
                LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                    SELECT CASE andril_groups.creator_entitlement(creator, 'hobbies') WHEN 2 THEN 100 ELSE 50 END;
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION andril_groups.club_limit(creator uuid) RETURNS integer
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                DECLARE creator_account uuid;
                DECLARE subscription record;
                BEGIN
                    SELECT p.account_id INTO creator_account FROM andril_identity.private_module_profiles p WHERE p.id = creator AND p.module = 'hobbies';
                    IF creator_account IS NULL OR to_regclass('andril_economy.premium_entitlements') IS NULL THEN
                        RAISE EXCEPTION 'Group authority unavailable' USING ERRCODE = 'AG503';
                    END IF;
                    -- Share conflicts with the canonical writer's insert/update lock, including when there is no subscription row yet.
                    LOCK TABLE andril_economy.premium_entitlements IN SHARE MODE;
                    IF row_security_active('andril_economy.premium_entitlements'::regclass) THEN
                        RAISE EXCEPTION 'Entitlement visibility is indeterminate' USING ERRCODE = 'AG503';
                    END IF;
                    SELECT entitlement.tier, entitlement.is_active, entitlement.expires_at_utc INTO subscription
                      FROM andril_economy.premium_entitlements entitlement WHERE entitlement.account_id = creator_account;
                    IF NOT FOUND OR (subscription.tier = 0 AND NOT subscription.is_active AND subscription.expires_at_utc IS NULL) THEN RETURN 50; END IF;
                    IF subscription.tier IN (1, 2) AND subscription.is_active AND subscription.expires_at_utc IS NOT NULL
                        AND isfinite(subscription.expires_at_utc) THEN
                        RETURN CASE WHEN subscription.expires_at_utc > clock_timestamp() THEN 100 ELSE 50 END;
                    END IF;
                    RAISE EXCEPTION 'Entitlement status is indeterminate' USING ERRCODE = 'AG503';
                EXCEPTION WHEN insufficient_privilege OR undefined_table OR undefined_column THEN
                    RAISE EXCEPTION 'Group entitlement authority unavailable' USING ERRCODE = 'AG503';
                END;
                $$;
                DROP FUNCTION andril_groups.creator_entitlement(uuid, text);
                """);
        }
    }
}
