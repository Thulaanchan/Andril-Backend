BEGIN;

-- Compatible with the entitlement relation in M2's 20261126160000_InitialEconomy candidate.
CREATE SCHEMA IF NOT EXISTS andril_economy;
CREATE TABLE IF NOT EXISTS andril_economy.premium_entitlements (
    account_id uuid CONSTRAINT pk_premium_entitlements PRIMARY KEY,
    tier integer NOT NULL,
    is_active boolean NOT NULL,
    expires_at_utc timestamptz,
    provider varchar(32) NOT NULL,
    external_subscription_id varchar(128),
    created_at_utc timestamptz NOT NULL,
    updated_at_utc timestamptz NOT NULL,
    CONSTRAINT ck_premium_entitlements_account_id_nonempty CHECK (account_id <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT ck_premium_entitlements_timestamps CHECK (updated_at_utc >= created_at_utc),
    CONSTRAINT fk_premium_entitlements_accounts FOREIGN KEY (account_id) REFERENCES andril_identity.accounts(id) ON DELETE CASCADE
);
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_catalog.pg_class relation
        JOIN pg_catalog.pg_namespace schema ON schema.oid = relation.relnamespace
        WHERE schema.nspname = 'andril_economy' AND relation.relname = 'premium_entitlements'
            AND relation.relkind = 'r' AND relation.relowner = CURRENT_USER::regrole
            AND schema.nspowner = CURRENT_USER::regrole AND NOT relation.relforcerowsecurity
            AND EXISTS (SELECT FROM pg_catalog.pg_constraint c WHERE c.conrelid = relation.oid
                AND c.contype = 'p' AND pg_catalog.pg_get_constraintdef(c.oid) = 'PRIMARY KEY (account_id)')
            AND EXISTS (SELECT FROM pg_catalog.pg_constraint c WHERE c.conrelid = relation.oid
                AND c.contype = 'f' AND c.convalidated AND c.confdeltype = 'c'
                AND c.confrelid = 'andril_identity.accounts'::regclass
                AND c.conname = 'fk_premium_entitlements_accounts')) THEN
        RAISE EXCEPTION 'Canonical entitlement ownership or schema is incompatible';
    END IF;
END;
$$;
ALTER TABLE andril_economy.premium_entitlements ADD CONSTRAINT ck_premium_entitlements_supported_state CHECK (
    (tier = 0 AND NOT is_active AND expires_at_utc IS NULL)
    OR (tier IN (1, 2) AND is_active AND expires_at_utc IS NOT NULL AND isfinite(expires_at_utc))
);
ALTER TABLE andril_economy.premium_entitlements ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON andril_economy.premium_entitlements FROM PUBLIC;

CREATE TABLE andril_economy.premium_entitlement_transitions (
    event_id uuid PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES andril_identity.accounts(id) ON DELETE CASCADE,
    expected_revision timestamptz,
    committed_revision timestamptz NOT NULL,
    operation text NOT NULL CHECK (operation IN ('grant', 'revoke')),
    invoked_by name NOT NULL,
    intent_digest bytea NOT NULL
);
ALTER TABLE andril_economy.premium_entitlement_transitions ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON andril_economy.premium_entitlement_transitions FROM PUBLIC;

CREATE FUNCTION andril_economy.subscription_transition_revision(verified_subject uuid) RETURNS timestamptz
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE revision timestamptz;
BEGIN
    SELECT e.updated_at_utc INTO revision FROM andril_identity.accounts a
      LEFT JOIN andril_economy.premium_entitlements e ON e.account_id = a.id
      WHERE a.supabase_user_id = verified_subject;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Entitlement account is unavailable' USING ERRCODE = '42501';
    END IF;
    RETURN revision;
END;
$$;

CREATE FUNCTION andril_economy.apply_subscription_transition(
    verified_subject uuid, event_key uuid, expected_revision timestamptz,
    requested_tier integer, expiry timestamptz, source_provider text, subscription_reference text
) RETURNS timestamptz
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog SET TimeZone = 'UTC' AS $$
DECLARE
    target_account uuid;
    saved andril_economy.premium_entitlement_transitions%ROWTYPE;
    current_revision timestamptz;
    changed_at timestamptz;
    intent bytea;
    changed_count integer;
BEGIN
    IF verified_subject IS NULL OR verified_subject = '00000000-0000-0000-0000-000000000000'
        OR event_key IS NULL OR event_key = '00000000-0000-0000-0000-000000000000'
        OR requested_tier IS NULL OR requested_tier NOT IN (0, 1, 2)
        OR (expected_revision IS NOT NULL AND NOT isfinite(expected_revision))
        OR source_provider IS NULL OR length(source_provider) NOT BETWEEN 1 AND 32
        OR source_provider <> lower(btrim(source_provider)) OR source_provider IN ('none', 'controlled_test')
        OR subscription_reference IS NULL OR length(subscription_reference) NOT BETWEEN 1 AND 128
        OR subscription_reference <> btrim(subscription_reference)
        OR (requested_tier = 0 AND expiry IS NOT NULL)
        OR (requested_tier IN (1, 2) AND (expiry IS NULL OR NOT isfinite(expiry))) THEN
        RAISE EXCEPTION 'Invalid entitlement transition' USING ERRCODE = '22023';
    END IF;

    SELECT a.id INTO target_account FROM andril_identity.accounts a
      WHERE a.supabase_user_id = verified_subject FOR NO KEY UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Entitlement account is unavailable' USING ERRCODE = '42501';
    END IF;

    -- Reflect the exact command without persisting the external subscription reference in the journal.
    intent := sha256(convert_to(jsonb_build_array($3, requested_tier, expiry, source_provider, subscription_reference)::text, 'UTF8'));
    SELECT t.* INTO saved FROM andril_economy.premium_entitlement_transitions t WHERE t.event_id = event_key;
    IF FOUND THEN
        IF saved.account_id <> target_account OR saved.intent_digest <> intent OR saved.invoked_by <> session_user THEN
            RAISE EXCEPTION 'Conflicting entitlement event' USING ERRCODE = '23505';
        END IF;
        RETURN saved.committed_revision;
    END IF;

    -- The lock also orders first inserts against Group quota and admission decisions.
    LOCK TABLE andril_economy.premium_entitlements IN ROW EXCLUSIVE MODE;
    SELECT e.updated_at_utc INTO current_revision FROM andril_economy.premium_entitlements e
        WHERE e.account_id = target_account FOR UPDATE;
    IF current_revision IS DISTINCT FROM $3 THEN
        RAISE EXCEPTION 'Entitlement revision changed' USING ERRCODE = '40001';
    END IF;

    changed_at := greatest(clock_timestamp(), current_revision + interval '1 microsecond');
    IF expiry IS NOT NULL AND expiry <= changed_at THEN
        RAISE EXCEPTION 'Entitlement expiry must be in the future' USING ERRCODE = '22023';
    END IF;
    IF current_revision IS NULL THEN
        INSERT INTO andril_economy.premium_entitlements (
            account_id, tier, is_active, expires_at_utc, provider, external_subscription_id, created_at_utc, updated_at_utc
        ) VALUES (target_account, requested_tier, requested_tier <> 0, expiry, source_provider,
                  subscription_reference, changed_at, changed_at)
        ON CONFLICT (account_id) DO NOTHING;
    ELSE
        UPDATE andril_economy.premium_entitlements e SET
            tier = requested_tier, is_active = requested_tier <> 0, expires_at_utc = expiry,
            provider = source_provider, external_subscription_id = subscription_reference, updated_at_utc = changed_at
        WHERE e.account_id = target_account AND e.updated_at_utc = $3;
    END IF;
    GET DIAGNOSTICS changed_count = ROW_COUNT;
    IF changed_count <> 1 THEN
        RAISE EXCEPTION 'Entitlement revision changed' USING ERRCODE = '40001';
    END IF;

    INSERT INTO andril_economy.premium_entitlement_transitions (
        event_id, account_id, expected_revision, committed_revision, operation, invoked_by, intent_digest
    ) VALUES (event_key, target_account, $3, changed_at,
              CASE WHEN requested_tier = 0 THEN 'revoke' ELSE 'grant' END, session_user, intent);
    RETURN changed_at;
END;
$$;

REVOKE ALL ON FUNCTION andril_economy.apply_subscription_transition(uuid, uuid, timestamptz, integer, timestamptz, text, text) FROM PUBLIC;
REVOKE ALL ON FUNCTION andril_economy.subscription_transition_revision(uuid) FROM PUBLIC;

COMMIT;
