using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Andril.Persistence.Migrations.Learning;

[DbContext(typeof(LearningDbContext))]
[Migration("20261126110000_LearningPortfolioPersistence")]
public sealed class LearningPortfolioPersistence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE andril_learning.learning_portfolios (
          owner_learning_profile_id uuid NOT NULL,module varchar(16) NOT NULL,portfolio_id uuid NOT NULL,
          template integer NOT NULL,projects jsonb NOT NULL,
          CONSTRAINT pk_learning_portfolios PRIMARY KEY(owner_learning_profile_id,module,portfolio_id),
          CONSTRAINT ck_learning_portfolio_owner CHECK(owner_learning_profile_id <> '00000000-0000-0000-0000-000000000000'::uuid),
          CONSTRAINT ck_learning_portfolio_id CHECK(portfolio_id <> '00000000-0000-0000-0000-000000000000'::uuid),
          CONSTRAINT ck_learning_portfolio_module CHECK(module='learning'),
          CONSTRAINT ck_learning_portfolio_template CHECK(template IN (1,2,3)),
          CONSTRAINT ck_learning_portfolio_projects CHECK(jsonb_typeof(projects)='array'));
        REVOKE ALL ON andril_learning.learning_portfolios FROM PUBLIC;
        ALTER TABLE andril_learning.learning_portfolios ENABLE ROW LEVEL SECURITY;
        ALTER TABLE andril_learning.learning_portfolios FORCE ROW LEVEL SECURITY;
        CREATE POLICY portfolio_owner_scope ON andril_learning.learning_portfolios
          USING(andril_learning.scope_visible(owner_learning_profile_id,module))
          WITH CHECK(andril_learning.scope_visible(owner_learning_profile_id,module));
        -- Existing Learning history and M2 physical ownership functions; no shared FK,
        -- cascade, runtime grants or lifecycle-deletion policy are introduced.
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("learning_portfolios", "andril_learning");
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => LearningSnapshotV3.Configure(modelBuilder);
}
