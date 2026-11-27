using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andril.Persistence.Migrations.Social
{
    /// <inheritdoc />
    public partial class SocialRelationshipPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "social_directional_interests",
                schema: "andril_social",
                columns: table => new
                {
                    actor_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    module = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    is_positive = table.Column<bool>(type: "boolean", nullable: false),
                    recorded_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_social_directional_interests", x => new { x.actor_profile_id, x.target_profile_id });
                    table.CheckConstraint("ck_social_interest_distinct", "actor_profile_id <> target_profile_id");
                    table.CheckConstraint("ck_social_interest_module", "module = 'social'");
                    table.CheckConstraint("ck_social_interest_timestamp", "isfinite(recorded_at_utc)");
                });

            migrationBuilder.CreateTable(
                name: "social_matches",
                schema: "andril_social",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    second_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    module = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revoked_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_social_matches", x => x.id);
                    table.CheckConstraint("ck_social_match_canonical_pair", "first_profile_id < second_profile_id");
                    table.CheckConstraint("ck_social_match_id", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_social_match_module", "module = 'social'");
                    table.CheckConstraint("ck_social_match_state", "isfinite(created_at_utc) AND (\n    (state = 'active' AND revoked_at_utc IS NULL) OR\n    (state = 'revoked' AND revoked_at_utc IS NOT NULL AND isfinite(revoked_at_utc) AND revoked_at_utc >= created_at_utc)\n)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_social_interests_target",
                schema: "andril_social",
                table: "social_directional_interests",
                column: "target_profile_id");

            migrationBuilder.CreateIndex(
                name: "ix_social_matches_pair",
                schema: "andril_social",
                table: "social_matches",
                columns: new[] { "first_profile_id", "second_profile_id" });

            migrationBuilder.CreateIndex(
                name: "ix_social_matches_second",
                schema: "andril_social",
                table: "social_matches",
                column: "second_profile_id");

            migrationBuilder.CreateIndex(
                name: "ux_social_matches_active_pair",
                schema: "andril_social",
                table: "social_matches",
                columns: new[] { "first_profile_id", "second_profile_id" },
                unique: true,
                filter: "state = 'active'");

            migrationBuilder.Sql("""
                ALTER TABLE andril_social.social_directional_interests
                    ADD CONSTRAINT fk_social_interest_actor
                        FOREIGN KEY (actor_profile_id, module) REFERENCES andril_identity.private_module_profiles (id, module)
                        ON UPDATE RESTRICT ON DELETE RESTRICT,
                    ADD CONSTRAINT fk_social_interest_target
                        FOREIGN KEY (target_profile_id, module) REFERENCES andril_identity.private_module_profiles (id, module)
                        ON UPDATE RESTRICT ON DELETE RESTRICT;
                ALTER TABLE andril_social.social_matches
                    ADD CONSTRAINT fk_social_match_first
                        FOREIGN KEY (first_profile_id, module) REFERENCES andril_identity.private_module_profiles (id, module)
                        ON UPDATE RESTRICT ON DELETE RESTRICT,
                    ADD CONSTRAINT fk_social_match_second
                        FOREIGN KEY (second_profile_id, module) REFERENCES andril_identity.private_module_profiles (id, module)
                        ON UPDATE RESTRICT ON DELETE RESTRICT;

                REVOKE ALL ON andril_social.social_directional_interests, andril_social.social_matches FROM PUBLIC;
                ALTER TABLE andril_social.social_directional_interests ENABLE ROW LEVEL SECURITY;
                ALTER TABLE andril_social.social_directional_interests FORCE ROW LEVEL SECURITY;
                ALTER TABLE andril_social.social_matches ENABLE ROW LEVEL SECURITY;
                ALTER TABLE andril_social.social_matches FORCE ROW LEVEL SECURITY;

                CREATE POLICY social_interest_owner_select ON andril_social.social_directional_interests
                    FOR SELECT USING (andril_social.questionnaire_owner_visible(actor_profile_id));
                CREATE POLICY social_interest_owner_insert ON andril_social.social_directional_interests
                    FOR INSERT WITH CHECK (andril_social.questionnaire_owner_visible(actor_profile_id));
                CREATE POLICY social_interest_owner_update ON andril_social.social_directional_interests
                    FOR UPDATE USING (andril_social.questionnaire_owner_visible(actor_profile_id))
                    WITH CHECK (andril_social.questionnaire_owner_visible(actor_profile_id));
                CREATE POLICY social_match_participant_select ON andril_social.social_matches
                    FOR SELECT USING (andril_social.questionnaire_owner_visible(first_profile_id)
                        OR andril_social.questionnaire_owner_visible(second_profile_id));
                CREATE POLICY social_match_participant_revoke ON andril_social.social_matches
                    FOR UPDATE USING (state = 'active' AND (andril_social.questionnaire_owner_visible(first_profile_id)
                        OR andril_social.questionnaire_owner_visible(second_profile_id)))
                    WITH CHECK (state = 'revoked' AND (andril_social.questionnaire_owner_visible(first_profile_id)
                        OR andril_social.questionnaire_owner_visible(second_profile_id)));

                -- The migration owner executes the narrowly scoped functions below even when FORCE RLS is active.
                CREATE POLICY social_interest_authority_select ON andril_social.social_directional_interests
                    FOR SELECT TO CURRENT_USER USING (true);
                CREATE POLICY social_match_authority_insert ON andril_social.social_matches
                    FOR INSERT TO CURRENT_USER WITH CHECK (state = 'active');

                CREATE FUNCTION andril_social.social_reciprocal_interest(actor uuid, target uuid)
                    RETURNS TABLE(is_positive boolean, recorded_at_utc timestamp with time zone)
                    LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
                        SELECT interest.is_positive, interest.recorded_at_utc
                        FROM andril_social.social_directional_interests interest
                        WHERE andril_social.questionnaire_owner_visible(actor)
                            AND interest.actor_profile_id = target AND interest.target_profile_id = actor;
                    $$;

                CREATE FUNCTION andril_social.try_form_social_match(actor uuid, target uuid, match_id uuid, match_time timestamp with time zone)
                    RETURNS uuid LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                        INSERT INTO andril_social.social_matches(id, first_profile_id, second_profile_id, module, state, created_at_utc)
                        SELECT match_id, LEAST(actor, target), GREATEST(actor, target), 'social', 'active', match_time
                        WHERE andril_social.questionnaire_owner_visible(actor)
                            AND EXISTS (SELECT 1 FROM andril_social.social_directional_interests interest
                                WHERE interest.actor_profile_id = actor AND interest.target_profile_id = target AND interest.is_positive)
                            AND EXISTS (SELECT 1 FROM andril_social.social_directional_interests interest
                                WHERE interest.actor_profile_id = target AND interest.target_profile_id = actor AND interest.is_positive)
                            AND NOT EXISTS (SELECT 1 FROM andril_social.social_matches previous
                                WHERE previous.first_profile_id = LEAST(actor, target)
                                    AND previous.second_profile_id = GREATEST(actor, target) AND previous.state = 'revoked')
                        ON CONFLICT (first_profile_id, second_profile_id) WHERE state = 'active' DO NOTHING
                        RETURNING id;
                    $$;

                REVOKE ALL ON FUNCTION andril_social.social_reciprocal_interest(uuid, uuid),
                    andril_social.try_form_social_match(uuid, uuid, uuid, timestamp with time zone) FROM PUBLIC;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION andril_social.try_form_social_match(uuid, uuid, uuid, timestamp with time zone);
                DROP FUNCTION andril_social.social_reciprocal_interest(uuid, uuid);
                """);

            migrationBuilder.DropTable(
                name: "social_directional_interests",
                schema: "andril_social");

            migrationBuilder.DropTable(
                name: "social_matches",
                schema: "andril_social");
        }
    }
}
