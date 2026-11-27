using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andril.Persistence.Migrations.HobbiesMap
{
    /// <inheritdoc />
    public partial class AddMapAttractionLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "map_attraction_lifecycle",
                schema: "andril_hobbies",
                columns: table => new
                {
                    attraction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    operation_sequence = table.Column<long>(type: "bigint", nullable: false),
                    last_receipt_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_map_attraction_lifecycle", x => x.attraction_id);
                    table.CheckConstraint("ck_map_lifecycle_state", "version >= 0 AND operation_sequence >= 0 AND ((operation_sequence=0 AND version=0 AND last_receipt_id IS NULL) OR (operation_sequence>0 AND last_receipt_id IS NOT NULL))");
                    table.ForeignKey(
                        name: "fk_map_lifecycle_attraction",
                        column: x => x.attraction_id,
                        principalSchema: "andril_hobbies",
                        principalTable: "map_attractions",
                        principalColumn: "attraction_id");
                });

            migrationBuilder.CreateTable(
                name: "map_attraction_lifecycle_audit",
                schema: "andril_hobbies",
                columns: table => new
                {
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reviewer_subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attraction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_is_active = table.Column<bool>(type: "boolean", nullable: false),
                    expected_version = table.Column<long>(type: "bigint", nullable: false),
                    previous_is_active = table.Column<bool>(type: "boolean", nullable: false),
                    previous_version = table.Column<long>(type: "bigint", nullable: false),
                    result_is_active = table.Column<bool>(type: "boolean", nullable: false),
                    result_version = table.Column<long>(type: "bigint", nullable: false),
                    previous_operation_sequence = table.Column<long>(type: "bigint", nullable: false),
                    result_operation_sequence = table.Column<long>(type: "bigint", nullable: false),
                    outcome = table.Column<short>(type: "smallint", nullable: false),
                    accepted_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_map_lifecycle_audit", x => x.receipt_id);
                    table.UniqueConstraint("ak_map_lifecycle_audit_evidence", x => new { x.receipt_id, x.reviewer_subject_id, x.request_id, x.attraction_id, x.requested_is_active, x.expected_version, x.previous_is_active, x.previous_version, x.result_is_active, x.result_version, x.previous_operation_sequence, x.result_operation_sequence, x.outcome, x.accepted_at_utc });
                });

            migrationBuilder.CreateTable(
                name: "map_attraction_lifecycle_receipts",
                schema: "andril_hobbies",
                columns: table => new
                {
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reviewer_subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attraction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_is_active = table.Column<bool>(type: "boolean", nullable: false),
                    expected_version = table.Column<long>(type: "bigint", nullable: false),
                    previous_is_active = table.Column<bool>(type: "boolean", nullable: false),
                    previous_version = table.Column<long>(type: "bigint", nullable: false),
                    result_is_active = table.Column<bool>(type: "boolean", nullable: false),
                    result_version = table.Column<long>(type: "bigint", nullable: false),
                    previous_operation_sequence = table.Column<long>(type: "bigint", nullable: false),
                    result_operation_sequence = table.Column<long>(type: "bigint", nullable: false),
                    outcome = table.Column<short>(type: "smallint", nullable: false),
                    accepted_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_map_lifecycle_receipts", x => x.receipt_id);
                    table.UniqueConstraint("ak_map_lifecycle_receipt_result", x => new { x.receipt_id, x.attraction_id, x.result_is_active, x.result_version, x.result_operation_sequence });
                    table.CheckConstraint("ck_map_lifecycle_receipt_ids", "receipt_id<>'00000000-0000-0000-0000-000000000000'::uuid AND reviewer_subject_id<>'00000000-0000-0000-0000-000000000000'::uuid AND request_id<>'00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_map_lifecycle_receipt_time", "isfinite(accepted_at_utc)");
                    table.CheckConstraint("ck_map_lifecycle_receipt_transition", "expected_version=previous_version AND requested_is_active=result_is_active AND previous_version>=0 AND result_version>=0 AND previous_operation_sequence>=0 AND result_operation_sequence::numeric=previous_operation_sequence::numeric+1 AND ((outcome=1 AND previous_is_active<>result_is_active AND result_version::numeric=previous_version::numeric+1) OR (outcome=2 AND previous_is_active=result_is_active AND result_version=previous_version))");
                    table.ForeignKey(
                        name: "fk_map_lifecycle_receipt_attraction",
                        column: x => x.attraction_id,
                        principalSchema: "andril_hobbies",
                        principalTable: "map_attractions",
                        principalColumn: "attraction_id");
                });

            migrationBuilder.CreateIndex(
                name: "ux_map_lifecycle_operation",
                schema: "andril_hobbies",
                table: "map_attraction_lifecycle_receipts",
                columns: new[] { "attraction_id", "result_operation_sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_map_lifecycle_reviewer_request",
                schema: "andril_hobbies",
                table: "map_attraction_lifecycle_receipts",
                columns: new[] { "reviewer_subject_id", "request_id" },
                unique: true);

            migrationBuilder.Sql("""
                ALTER TABLE andril_hobbies.map_attraction_lifecycle
                    ADD CONSTRAINT fk_map_lifecycle_current_receipt
                    FOREIGN KEY(last_receipt_id,attraction_id,is_active,version,operation_sequence)
                    REFERENCES andril_hobbies.map_attraction_lifecycle_receipts
                        (receipt_id,attraction_id,result_is_active,result_version,result_operation_sequence)
                    ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;

                ALTER TABLE andril_hobbies.map_attraction_lifecycle_receipts
                    ADD CONSTRAINT fk_map_lifecycle_receipt_audit
                    FOREIGN KEY(receipt_id,reviewer_subject_id,request_id,attraction_id,requested_is_active,
                        expected_version,previous_is_active,previous_version,result_is_active,result_version,
                        previous_operation_sequence,result_operation_sequence,outcome,accepted_at_utc)
                    REFERENCES andril_hobbies.map_attraction_lifecycle_audit
                        (receipt_id,reviewer_subject_id,request_id,attraction_id,requested_is_active,
                         expected_version,previous_is_active,previous_version,result_is_active,result_version,
                         previous_operation_sequence,result_operation_sequence,outcome,accepted_at_utc)
                    ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;

                ALTER TABLE andril_hobbies.map_attraction_lifecycle_audit
                    ADD CONSTRAINT fk_map_lifecycle_audit_receipt
                    FOREIGN KEY(receipt_id)
                    REFERENCES andril_hobbies.map_attraction_lifecycle_receipts(receipt_id)
                    ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;

                CREATE FUNCTION andril_hobbies.map_lifecycle_check_update() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.attraction_id<>OLD.attraction_id OR NOT EXISTS (
                        SELECT 1 FROM andril_hobbies.map_attraction_lifecycle_receipts r
                        WHERE r.receipt_id=NEW.last_receipt_id AND r.attraction_id=OLD.attraction_id
                        AND ROW(r.previous_is_active,r.previous_version,r.previous_operation_sequence)=ROW(OLD.is_active,OLD.version,OLD.operation_sequence)
                        AND ROW(r.result_is_active,r.result_version,r.result_operation_sequence)=ROW(NEW.is_active,NEW.version,NEW.operation_sequence)) THEN
                        RAISE EXCEPTION 'Lifecycle update requires matching evidence' USING ERRCODE='23514',CONSTRAINT='ck_map_lifecycle_update_link';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER map_lifecycle_update_link BEFORE UPDATE ON andril_hobbies.map_attraction_lifecycle
                    FOR EACH ROW EXECUTE FUNCTION andril_hobbies.map_lifecycle_check_update();

                CREATE FUNCTION andril_hobbies.map_lifecycle_check_applied() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NOT EXISTS(
                        SELECT 1 FROM andril_hobbies.map_attraction_lifecycle s
                        WHERE s.attraction_id=NEW.attraction_id AND s.last_receipt_id=NEW.receipt_id
                        AND ROW(s.is_active,s.version,s.operation_sequence)=ROW(NEW.result_is_active,NEW.result_version,NEW.result_operation_sequence)) THEN
                        RAISE EXCEPTION 'Lifecycle receipt must be applied' USING ERRCODE='23514',CONSTRAINT='ck_map_lifecycle_receipt_applied';
                    END IF;
                    RETURN NULL;
                END $$;
                CREATE CONSTRAINT TRIGGER map_lifecycle_receipt_applied
                    AFTER INSERT ON andril_hobbies.map_attraction_lifecycle_receipts DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION andril_hobbies.map_lifecycle_check_applied();

                CREATE FUNCTION andril_hobbies.map_lifecycle_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'Lifecycle evidence is immutable' USING ERRCODE='23514',CONSTRAINT='ck_map_lifecycle_evidence_immutable';
                END $$;
                CREATE TRIGGER map_lifecycle_receipt_immutable
                    BEFORE UPDATE OR DELETE ON andril_hobbies.map_attraction_lifecycle_receipts
                    FOR EACH ROW EXECUTE FUNCTION andril_hobbies.map_lifecycle_immutable();
                CREATE TRIGGER map_lifecycle_audit_immutable
                    BEFORE UPDATE OR DELETE ON andril_hobbies.map_attraction_lifecycle_audit
                    FOR EACH ROW EXECUTE FUNCTION andril_hobbies.map_lifecycle_immutable();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException(
                "MAP lifecycle receipts and audit evidence are retained; this migration is forward-only.");
        }
    }
}
