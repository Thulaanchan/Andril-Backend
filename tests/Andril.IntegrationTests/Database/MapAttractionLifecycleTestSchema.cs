namespace Andril.IntegrationTests.Database;

// Test-owned accepted schema only; not a production migration or provisioning entry point.
internal static class MapAttractionLifecycleTestSchema
{
    internal const string Sql = """
        CREATE TABLE andril_hobbies.map_attraction_lifecycle (
         attraction_id uuid CONSTRAINT pk_map_attraction_lifecycle PRIMARY KEY CONSTRAINT fk_map_lifecycle_attraction REFERENCES andril_hobbies.map_attractions(attraction_id) ON DELETE NO ACTION,
         is_active boolean NOT NULL,version bigint NOT NULL,operation_sequence bigint NOT NULL,last_receipt_id uuid,
         CONSTRAINT ck_map_lifecycle_state CHECK (version>=0 AND operation_sequence>=0 AND
         ((operation_sequence=0 AND version=0 AND last_receipt_id IS NULL) OR (operation_sequence>0 AND last_receipt_id IS NOT NULL))));
        CREATE TABLE andril_hobbies.map_attraction_lifecycle_receipts (
         receipt_id uuid CONSTRAINT pk_map_lifecycle_receipts PRIMARY KEY,
         reviewer_subject_id uuid NOT NULL,request_id uuid NOT NULL,attraction_id uuid NOT NULL CONSTRAINT fk_map_lifecycle_receipt_attraction REFERENCES andril_hobbies.map_attractions(attraction_id) ON DELETE NO ACTION,
         requested_is_active boolean NOT NULL,expected_version bigint NOT NULL,previous_is_active boolean NOT NULL,previous_version bigint NOT NULL,
         result_is_active boolean NOT NULL,result_version bigint NOT NULL,previous_operation_sequence bigint NOT NULL,result_operation_sequence bigint NOT NULL,
         outcome smallint NOT NULL,accepted_at_utc timestamp(6) with time zone NOT NULL,
         CONSTRAINT ck_map_lifecycle_receipt_ids CHECK(receipt_id<>'00000000-0000-0000-0000-000000000000'::uuid AND reviewer_subject_id<>'00000000-0000-0000-0000-000000000000'::uuid AND request_id<>'00000000-0000-0000-0000-000000000000'::uuid),
         CONSTRAINT ck_map_lifecycle_receipt_time CHECK(isfinite(accepted_at_utc)),
         CONSTRAINT ck_map_lifecycle_receipt_transition CHECK(expected_version=previous_version AND requested_is_active=result_is_active AND previous_version>=0 AND result_version>=0 AND previous_operation_sequence>=0 AND result_operation_sequence::numeric=previous_operation_sequence::numeric+1 AND
         ((outcome=1 AND previous_is_active<>result_is_active AND result_version::numeric=previous_version::numeric+1) OR (outcome=2 AND previous_is_active=result_is_active AND result_version=previous_version))),
         CONSTRAINT ux_map_lifecycle_reviewer_request UNIQUE(reviewer_subject_id,request_id),
         CONSTRAINT ux_map_lifecycle_operation UNIQUE(attraction_id,result_operation_sequence),
         CONSTRAINT ak_map_lifecycle_receipt_result UNIQUE(receipt_id,attraction_id,result_is_active,result_version,result_operation_sequence));
        CREATE TABLE andril_hobbies.map_attraction_lifecycle_audit (
         receipt_id uuid CONSTRAINT pk_map_lifecycle_audit PRIMARY KEY,reviewer_subject_id uuid NOT NULL,request_id uuid NOT NULL,attraction_id uuid NOT NULL,
         requested_is_active boolean NOT NULL,expected_version bigint NOT NULL,previous_is_active boolean NOT NULL,previous_version bigint NOT NULL,
         result_is_active boolean NOT NULL,result_version bigint NOT NULL,previous_operation_sequence bigint NOT NULL,result_operation_sequence bigint NOT NULL,
         outcome smallint NOT NULL,accepted_at_utc timestamp(6) with time zone NOT NULL,
         CONSTRAINT ak_map_lifecycle_audit_evidence UNIQUE(receipt_id,reviewer_subject_id,request_id,attraction_id,requested_is_active,expected_version,previous_is_active,previous_version,result_is_active,result_version,previous_operation_sequence,result_operation_sequence,outcome,accepted_at_utc),
         CONSTRAINT fk_map_lifecycle_audit_receipt FOREIGN KEY(receipt_id) REFERENCES andril_hobbies.map_attraction_lifecycle_receipts(receipt_id) ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED);
        ALTER TABLE andril_hobbies.map_attraction_lifecycle_receipts ADD CONSTRAINT fk_map_lifecycle_receipt_audit FOREIGN KEY(receipt_id,reviewer_subject_id,request_id,attraction_id,requested_is_active,expected_version,previous_is_active,previous_version,result_is_active,result_version,previous_operation_sequence,result_operation_sequence,outcome,accepted_at_utc)
         REFERENCES andril_hobbies.map_attraction_lifecycle_audit(receipt_id,reviewer_subject_id,request_id,attraction_id,requested_is_active,expected_version,previous_is_active,previous_version,result_is_active,result_version,previous_operation_sequence,result_operation_sequence,outcome,accepted_at_utc) ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE andril_hobbies.map_attraction_lifecycle ADD CONSTRAINT fk_map_lifecycle_current_receipt
         FOREIGN KEY(last_receipt_id,attraction_id,is_active,version,operation_sequence)
         REFERENCES andril_hobbies.map_attraction_lifecycle_receipts(receipt_id,attraction_id,result_is_active,result_version,result_operation_sequence)
         ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        CREATE FUNCTION andril_hobbies.map_lifecycle_check_update() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
         IF NEW.attraction_id<>OLD.attraction_id OR NOT EXISTS (
         SELECT 1 FROM andril_hobbies.map_attraction_lifecycle_receipts r
         WHERE r.receipt_id=NEW.last_receipt_id AND r.attraction_id=OLD.attraction_id
         AND ROW(r.previous_is_active,r.previous_version,r.previous_operation_sequence)=ROW(OLD.is_active,OLD.version,OLD.operation_sequence)
         AND ROW(r.result_is_active,r.result_version,r.result_operation_sequence)=ROW(NEW.is_active,NEW.version,NEW.operation_sequence)) THEN
         RAISE EXCEPTION 'Lifecycle update requires matching evidence' USING ERRCODE='23514',CONSTRAINT='ck_map_lifecycle_update_link'; END IF;
         RETURN NEW;
        END $$;
        CREATE TRIGGER map_lifecycle_update_link BEFORE UPDATE ON andril_hobbies.map_attraction_lifecycle FOR EACH ROW EXECUTE FUNCTION andril_hobbies.map_lifecycle_check_update();
        CREATE FUNCTION andril_hobbies.map_lifecycle_check_applied() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
         IF NOT EXISTS(SELECT 1 FROM andril_hobbies.map_attraction_lifecycle s WHERE s.attraction_id=NEW.attraction_id AND s.last_receipt_id=NEW.receipt_id
         AND ROW(s.is_active,s.version,s.operation_sequence)=ROW(NEW.result_is_active,NEW.result_version,NEW.result_operation_sequence)) THEN
         RAISE EXCEPTION 'Lifecycle receipt must be applied' USING ERRCODE='23514',CONSTRAINT='ck_map_lifecycle_receipt_applied'; END IF;
         RETURN NULL;
        END $$;
        CREATE CONSTRAINT TRIGGER map_lifecycle_receipt_applied AFTER INSERT ON andril_hobbies.map_attraction_lifecycle_receipts DEFERRABLE INITIALLY DEFERRED
         FOR EACH ROW EXECUTE FUNCTION andril_hobbies.map_lifecycle_check_applied();
        CREATE FUNCTION andril_hobbies.map_lifecycle_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN RAISE EXCEPTION 'Lifecycle evidence is immutable' USING ERRCODE='23514',CONSTRAINT='ck_map_lifecycle_evidence_immutable'; END $$;
        CREATE TRIGGER map_lifecycle_receipt_immutable BEFORE UPDATE OR DELETE ON andril_hobbies.map_attraction_lifecycle_receipts FOR EACH ROW EXECUTE FUNCTION andril_hobbies.map_lifecycle_immutable();
        CREATE TRIGGER map_lifecycle_audit_immutable BEFORE UPDATE OR DELETE ON andril_hobbies.map_attraction_lifecycle_audit FOR EACH ROW EXECUTE FUNCTION andril_hobbies.map_lifecycle_immutable();
        """;
}
