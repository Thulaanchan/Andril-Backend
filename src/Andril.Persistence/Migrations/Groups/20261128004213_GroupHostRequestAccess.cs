using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andril.Persistence.Migrations.Groups
{
    /// <inheritdoc />
    public partial class GroupHostRequestAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION andril_groups.read_host_requests(actor uuid, source_module text, target_id uuid, target_request uuid, maximum integer)
                RETURNS TABLE(id uuid, applicant_id uuid, requested_at timestamptz, revision bigint)
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                BEGIN
                    IF maximum IS NULL OR maximum < 1 OR maximum > 100 OR andril_groups.actor_standing(actor, source_module) IS NOT TRUE
                        OR NOT andril_groups.has_capability(actor, source_module, target_id, 1) THEN
                        RAISE EXCEPTION 'Group admission denied' USING ERRCODE = 'AG403';
                    END IF;
                    RETURN QUERY SELECT r.id, r.profile_id, r.requested_at_utc, g.revision
                        FROM andril_groups.groups g JOIN andril_groups.join_requests r ON r.group_id = g.id AND r.module = g.module
                        WHERE g.id = target_id AND g.module = source_module AND g.is_active AND g.is_open AND r.state = 'pending'
                            AND (target_request IS NULL OR r.id = target_request)
                            AND andril_groups.can_interact_with_hosts(r.profile_id, source_module, target_id)
                        ORDER BY r.requested_at_utc, r.id LIMIT maximum;
                END;
                $$;
                REVOKE ALL ON FUNCTION andril_groups.read_host_requests(uuid, text, uuid, uuid, integer) FROM PUBLIC;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION andril_groups.read_host_requests(uuid, text, uuid, uuid, integer);
                """);
        }
    }
}
