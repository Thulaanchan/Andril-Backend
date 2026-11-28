using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Andril.Persistence.Migrations.LearningXpRead;

[DbContext(typeof(LearningXpReadDbContext))]
[Migration("20261128140000_LearningXpReadLedger")]
public sealed class LearningXpReadLedger:Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($"""
            CREATE TABLE andril_learning.xp_credits (
                id uuid NOT NULL,account_id uuid NOT NULL,profile_id uuid NOT NULL,module varchar(16) NOT NULL,
                source_kind varchar(128) NOT NULL,source_event_id uuid NOT NULL,amount bigint NOT NULL,entry_kind text NOT NULL,
                original_credit_id uuid,audit_reason text,policy_revision text NOT NULL,evidence_revision text NOT NULL,credited_at_utc timestamptz NOT NULL,
                CONSTRAINT pk_learning_xp_credits PRIMARY KEY(id),
                CONSTRAINT ak_learning_xp_credit_scope UNIQUE(id,account_id,profile_id,module),
                CONSTRAINT fk_learning_xp_adjustment_credit_scope FOREIGN KEY(original_credit_id,account_id,profile_id,module)
                    REFERENCES andril_learning.xp_credits(id,account_id,profile_id,module) ON DELETE RESTRICT,
                CONSTRAINT ck_learning_xp_scope CHECK({LearningXpReadModelV1.ScopeCheck}),
                CONSTRAINT ck_learning_xp_credit CHECK({LearningXpReadModelV1.CreditCheck}));
            CREATE UNIQUE INDEX ux_learning_xp_source_event ON andril_learning.xp_credits(profile_id,source_kind,source_event_id);
            CREATE INDEX ix_learning_xp_owner_history ON andril_learning.xp_credits(account_id,profile_id,id);
            CREATE INDEX ix_learning_xp_adjustment_origin ON andril_learning.xp_credits(original_credit_id,account_id,profile_id,module);
            CREATE UNIQUE INDEX ux_learning_xp_reversal ON andril_learning.xp_credits(profile_id,original_credit_id) WHERE entry_kind='Reversal';
            REVOKE ALL ON andril_learning.xp_credits FROM PUBLIC;
            ALTER TABLE andril_learning.xp_credits ENABLE ROW LEVEL SECURITY;
            ALTER TABLE andril_learning.xp_credits FORCE ROW LEVEL SECURITY;
            CREATE POLICY learning_xp_owner_scope ON andril_learning.xp_credits
                USING(module='learning' AND account_id::text=current_setting('andril.account',true) AND andril_learning.scope_visible(profile_id,module))
                WITH CHECK(module='learning' AND account_id::text=current_setting('andril.account',true) AND andril_learning.scope_visible(profile_id,module));
            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder)=>migrationBuilder.DropTable("xp_credits","andril_learning");
    protected override void BuildTargetModel(ModelBuilder modelBuilder)=>LearningXpReadModelV1.Configure(modelBuilder);
}

[DbContext(typeof(LearningXpReadDbContext))]
public sealed class LearningXpReadDbContextModelSnapshot:ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)=>LearningXpReadModelV1.Configure(modelBuilder);
}
