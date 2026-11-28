using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Andril.Persistence.Migrations.LearningPrivateActivities;

[DbContext(typeof(LearningPrivateActivitiesDbContext))]
[Migration("20261128123000_PrivateOwnerActivities")]
public sealed class PrivateOwnerActivities:Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)=>migrationBuilder.Sql("""
        CREATE TABLE andril_learning.private_owner_activities (
            id uuid PRIMARY KEY,module varchar(16) NOT NULL,kind integer NOT NULL,generation uuid NOT NULL,version bigint NOT NULL,
            status varchar(32),pair_low uuid,pair_high uuid,members jsonb NOT NULL,content jsonb NOT NULL,
            CONSTRAINT ck_learning_activity_scope CHECK(module='learning' AND id<>'00000000-0000-0000-0000-000000000000'::uuid AND generation<>'00000000-0000-0000-0000-000000000000'::uuid AND version>0),
            CONSTRAINT ck_learning_activity_kind CHECK(kind BETWEEN 1 AND 7),
            CONSTRAINT ck_learning_activity_json CHECK(jsonb_typeof(members)='array' AND jsonb_array_length(members)>0 AND jsonb_typeof(content)='object'),
            CONSTRAINT ck_learning_connection_pair CHECK(kind<>7 OR (jsonb_array_length(members)=2 AND members->0->>'ProfileId' IS NOT NULL AND members->1->>'ProfileId' IS NOT NULL AND pair_low IS NOT NULL AND pair_high IS NOT NULL AND pair_low<pair_high AND pair_low=LEAST((members->0->>'ProfileId')::uuid,(members->1->>'ProfileId')::uuid) AND pair_high=GREATEST((members->0->>'ProfileId')::uuid,(members->1->>'ProfileId')::uuid) AND status IN ('Pending','Accepted','Declined','Cancelled','Disconnected'))),
            CONSTRAINT ck_learning_exchange_pair CHECK(kind<>1 OR (jsonb_array_length(members)=2 AND members->0->>'ProfileId' IS NOT NULL AND members->1->>'ProfileId' IS NOT NULL AND pair_low IS NOT NULL AND pair_high IS NOT NULL AND pair_low<pair_high AND pair_low=LEAST((members->0->>'ProfileId')::uuid,(members->1->>'ProfileId')::uuid) AND pair_high=GREATEST((members->0->>'ProfileId')::uuid,(members->1->>'ProfileId')::uuid) AND status IN ('Pending','Accepted','Declined','Cancelled'))),
            CONSTRAINT ck_private_owner_activity CHECK(COALESCE((kind IN (3,4) AND jsonb_array_length(members)=1 AND pair_low IS NULL AND pair_high IS NULL AND content->'Members'=members AND content->'Creator'=members->0 AND content->>'Id'=id::text AND content->>'Kind'=kind::text),false)));
        REVOKE ALL ON andril_learning.private_owner_activities FROM PUBLIC;
        ALTER TABLE andril_learning.private_owner_activities ENABLE ROW LEVEL SECURITY;
        ALTER TABLE andril_learning.private_owner_activities FORCE ROW LEVEL SECURITY;
        CREATE POLICY private_owner_activity_scope ON andril_learning.private_owner_activities
            USING(kind IN (3,4) AND jsonb_array_length(members)=1
                AND members->0->>'AccountId'=current_setting('andril.account',true)
                AND andril_learning.scope_visible((members->0->>'ProfileId')::uuid,module))
            WITH CHECK(kind IN (3,4) AND jsonb_array_length(members)=1
                AND members->0->>'AccountId'=current_setting('andril.account',true)
                AND andril_learning.scope_visible((members->0->>'ProfileId')::uuid,module));
        """);
    protected override void Down(MigrationBuilder migrationBuilder)=>migrationBuilder.DropTable("private_owner_activities","andril_learning");
    protected override void BuildTargetModel(ModelBuilder modelBuilder)=>LearningPrivateActivitiesDbContext.Configure(modelBuilder);
}

[DbContext(typeof(LearningPrivateActivitiesDbContext))]
public sealed class LearningPrivateActivitiesDbContextModelSnapshot:ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)=>LearningPrivateActivitiesDbContext.Configure(modelBuilder);
}
