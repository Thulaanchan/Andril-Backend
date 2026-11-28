using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Andril.Persistence.Migrations.Groups
{
    /// <inheritdoc />
    public partial class InitialSharedGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "andril_groups");

            migrationBuilder.CreateTable(
                name: "groups",
                schema: "andril_groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    module = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    creator_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    visibility = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    is_discoverable = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    is_open = table.Column<bool>(type: "boolean", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_groups", x => new { x.id, x.module });
                    table.CheckConstraint("ck_groups_identity", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_groups_module_kind", "(module, kind) IN (('learning', 1), ('hobbies', 2), ('social', 3))");
                    table.CheckConstraint("ck_groups_name", "length(btrim(name)) > 0");
                    table.CheckConstraint("ck_groups_revision", "revision > 0");
                    table.CheckConstraint("ck_groups_visibility", "visibility IN ('private', 'public')");
                });

            migrationBuilder.CreateTable(
                name: "audit",
                schema: "andril_groups",
                columns: table => new
                {
                    sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    module = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    actor_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit", x => x.sequence);
                    table.CheckConstraint("ck_groups_audit_dates", "isfinite(recorded_at_utc)");
                    table.CheckConstraint("ck_groups_audit_revision", "revision > 0");
                    table.ForeignKey(
                        name: "FK_audit_groups_group_id_module",
                        columns: x => new { x.group_id, x.module },
                        principalSchema: "andril_groups",
                        principalTable: "groups",
                        principalColumns: new[] { "id", "module" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "join_requests",
                schema: "andril_groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    module = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    requested_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_join_requests", x => x.id);
                    table.CheckConstraint("ck_groups_request_dates", "isfinite(requested_at_utc) AND (resolved_at_utc IS NULL OR (isfinite(resolved_at_utc) AND resolved_at_utc >= requested_at_utc))");
                    table.CheckConstraint("ck_groups_request_state", "(state = 'pending' AND resolved_at_utc IS NULL) OR (state IN ('cancelled', 'approved', 'rejected') AND resolved_at_utc IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_join_requests_groups_group_id_module",
                        columns: x => new { x.group_id, x.module },
                        principalSchema: "andril_groups",
                        principalTable: "groups",
                        principalColumns: new[] { "id", "module" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "memberships",
                schema: "andril_groups",
                columns: table => new
                {
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    module = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    joined_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    departed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memberships", x => new { x.group_id, x.module, x.profile_id });
                    table.CheckConstraint("ck_groups_membership_dates", "isfinite(joined_at_utc) AND (departed_at_utc IS NULL OR (isfinite(departed_at_utc) AND departed_at_utc >= joined_at_utc))");
                    table.ForeignKey(
                        name: "FK_memberships_groups_group_id_module",
                        columns: x => new { x.group_id, x.module },
                        principalSchema: "andril_groups",
                        principalTable: "groups",
                        principalColumns: new[] { "id", "module" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "capabilities",
                schema: "andril_groups",
                columns: table => new
                {
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    module = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    capability = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_capabilities", x => new { x.group_id, x.module, x.profile_id, x.capability });
                    table.CheckConstraint("ck_groups_capability", "capability IN (1, 2, 4)");
                    table.ForeignKey(
                        name: "FK_capabilities_memberships_group_id_module_profile_id",
                        columns: x => new { x.group_id, x.module, x.profile_id },
                        principalSchema: "andril_groups",
                        principalTable: "memberships",
                        principalColumns: new[] { "group_id", "module", "profile_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_group_id_module_revision",
                schema: "andril_groups",
                table: "audit",
                columns: new[] { "group_id", "module", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_groups_module_creator_profile_id",
                schema: "andril_groups",
                table: "groups",
                columns: new[] { "module", "creator_profile_id" });

            migrationBuilder.CreateIndex(
                name: "IX_groups_module_id",
                schema: "andril_groups",
                table: "groups",
                columns: new[] { "module", "id" },
                filter: "is_discoverable AND is_active");

            migrationBuilder.CreateIndex(
                name: "IX_join_requests_group_id_module_profile_id",
                schema: "andril_groups",
                table: "join_requests",
                columns: new[] { "group_id", "module", "profile_id" },
                unique: true,
                filter: "state = 'pending'");

            migrationBuilder.CreateIndex(
                name: "IX_join_requests_profile_id_module",
                schema: "andril_groups",
                table: "join_requests",
                columns: new[] { "profile_id", "module" });

            migrationBuilder.CreateIndex(
                name: "IX_memberships_profile_id_module",
                schema: "andril_groups",
                table: "memberships",
                columns: new[] { "profile_id", "module" });

            migrationBuilder.Sql("""
                ALTER TABLE andril_groups.groups ADD CONSTRAINT fk_groups_creator_profile
                    FOREIGN KEY (creator_profile_id, module) REFERENCES andril_identity.private_module_profiles(id, module)
                    ON UPDATE RESTRICT ON DELETE RESTRICT;
                ALTER TABLE andril_groups.memberships ADD CONSTRAINT fk_groups_member_profile
                    FOREIGN KEY (profile_id, module) REFERENCES andril_identity.private_module_profiles(id, module)
                    ON UPDATE RESTRICT ON DELETE RESTRICT;
                ALTER TABLE andril_groups.join_requests ADD CONSTRAINT fk_groups_request_profile
                    FOREIGN KEY (profile_id, module) REFERENCES andril_identity.private_module_profiles(id, module)
                    ON UPDATE RESTRICT ON DELETE RESTRICT;
                ALTER TABLE andril_groups.audit ADD CONSTRAINT fk_groups_audit_actor
                    FOREIGN KEY (actor_profile_id, module) REFERENCES andril_identity.private_module_profiles(id, module)
                    ON UPDATE RESTRICT ON DELETE RESTRICT;

                REVOKE ALL ON SCHEMA andril_groups FROM PUBLIC;
                DO $$
                DECLARE table_name text;
                BEGIN
                    FOREACH table_name IN ARRAY ARRAY['groups', 'memberships', 'join_requests', 'capabilities', 'audit']
                    LOOP
                        EXECUTE format('REVOKE ALL ON andril_groups.%I FROM PUBLIC', table_name);
                        EXECUTE format('ALTER TABLE andril_groups.%I ENABLE ROW LEVEL SECURITY', table_name);
                        EXECUTE format('ALTER TABLE andril_groups.%I FORCE ROW LEVEL SECURITY', table_name);
                        EXECUTE format('CREATE POLICY groups_authority ON andril_groups.%I TO CURRENT_USER USING (true) WITH CHECK (true)', table_name);
                    END LOOP;
                END;
                $$;

                CREATE FUNCTION andril_groups.owns_current_profile(profile_id uuid, module_name text) RETURNS boolean
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
                    SELECT module_name = current_setting('andril.module', true) AND EXISTS (
                        SELECT 1
                        FROM andril_identity.private_module_profiles profile
                        JOIN andril_identity.accounts account ON account.id = profile.account_id
                        JOIN andril_identity.account_starter_memberships membership ON membership.account_id = account.id AND membership.module = profile.module
                        JOIN andril_identity.account_module_lifecycles lifecycle ON lifecycle.account_id = account.id AND lifecycle.module = profile.module
                        JOIN andril_identity.account_demographics demographics ON demographics.account_id = account.id
                        WHERE profile.id = $1 AND profile.module = module_name AND lifecycle.state = 'active'
                            AND account.id::text = current_setting('andril.account', true)
                            AND account.supabase_user_id::text = current_setting('andril.subject', true)
                            AND demographics.date_of_birth <= (timezone('UTC', statement_timestamp())::date -
                                CASE WHEN module_name = 'social' THEN interval '18 years' ELSE interval '16 years' END)
                            AND (module_name <> 'social' OR membership.social_opt_in_at_utc IS NOT NULL));
                $$;
                REVOKE ALL ON FUNCTION andril_groups.owns_current_profile(uuid, text) FROM PUBLIC;

                CREATE POLICY groups_creator_read ON andril_groups.groups
                    FOR SELECT USING (andril_groups.owns_current_profile(creator_profile_id, module));
                CREATE POLICY groups_member_self_read ON andril_groups.memberships
                    FOR SELECT USING (andril_groups.owns_current_profile(profile_id, module));
                CREATE POLICY groups_request_self_read ON andril_groups.join_requests
                    FOR SELECT USING (andril_groups.owns_current_profile(profile_id, module));
                CREATE POLICY groups_capability_self_read ON andril_groups.capabilities
                    FOR SELECT USING (andril_groups.owns_current_profile(profile_id, module));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY groups_creator_read ON andril_groups.groups;
                DROP POLICY groups_member_self_read ON andril_groups.memberships;
                DROP POLICY groups_request_self_read ON andril_groups.join_requests;
                DROP POLICY groups_capability_self_read ON andril_groups.capabilities;
                DROP FUNCTION andril_groups.owns_current_profile(uuid, text);
                """);

            migrationBuilder.DropTable(
                name: "audit",
                schema: "andril_groups");

            migrationBuilder.DropTable(
                name: "capabilities",
                schema: "andril_groups");

            migrationBuilder.DropTable(
                name: "join_requests",
                schema: "andril_groups");

            migrationBuilder.DropTable(
                name: "memberships",
                schema: "andril_groups");

            migrationBuilder.DropTable(
                name: "groups",
                schema: "andril_groups");
        }
    }
}
