using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Andril.Persistence.Migrations.Learning;

[DbContext(typeof(LearningDbContext))]
[Migration("20261126140000_LearningProjectImport")]
public sealed class LearningProjectImport : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        ALTER TABLE andril_learning.learning_portfolios ADD COLUMN content_version uuid NOT NULL DEFAULT gen_random_uuid();
        ALTER TABLE andril_learning.learning_portfolios ALTER COLUMN content_version DROP DEFAULT;
        ALTER TABLE andril_learning.learning_portfolios ADD CONSTRAINT ck_learning_portfolio_version CHECK(content_version <> '00000000-0000-0000-0000-000000000000'::uuid);
        CREATE TABLE andril_learning.learning_project_import_reviews (
          owner_learning_profile_id uuid NOT NULL,module varchar(16) NOT NULL,review_id uuid NOT NULL,
          portfolio_id uuid NOT NULL,cv_id uuid NOT NULL,source_version uuid NOT NULL,
          source_fingerprint text NOT NULL,projects jsonb NOT NULL,confirmed boolean NOT NULL,
          CONSTRAINT pk_learning_project_import_reviews PRIMARY KEY(owner_learning_profile_id,module,review_id),
          CONSTRAINT ck_learning_import_review_ids CHECK(owner_learning_profile_id <> '00000000-0000-0000-0000-000000000000'::uuid AND review_id <> '00000000-0000-0000-0000-000000000000'::uuid AND portfolio_id <> '00000000-0000-0000-0000-000000000000'::uuid AND cv_id <> '00000000-0000-0000-0000-000000000000'::uuid AND source_version <> '00000000-0000-0000-0000-000000000000'::uuid),
          CONSTRAINT ck_learning_import_review_module CHECK(module='learning'),
          CONSTRAINT ck_learning_import_review_projects CHECK(jsonb_typeof(projects)='array' AND jsonb_array_length(projects)>0));
        CREATE TABLE andril_learning.learning_project_import_origins (
          owner_learning_profile_id uuid NOT NULL,module varchar(16) NOT NULL,portfolio_id uuid NOT NULL,
          project_id uuid NOT NULL,cv_id uuid NOT NULL,entry_id uuid NOT NULL,review_id uuid NOT NULL,
          CONSTRAINT pk_learning_project_import_origins PRIMARY KEY(owner_learning_profile_id,module,portfolio_id,project_id,cv_id),
          CONSTRAINT ck_learning_import_origin_ids CHECK(owner_learning_profile_id <> '00000000-0000-0000-0000-000000000000'::uuid AND portfolio_id <> '00000000-0000-0000-0000-000000000000'::uuid AND project_id <> '00000000-0000-0000-0000-000000000000'::uuid AND cv_id <> '00000000-0000-0000-0000-000000000000'::uuid AND entry_id <> '00000000-0000-0000-0000-000000000000'::uuid AND review_id <> '00000000-0000-0000-0000-000000000000'::uuid),
          CONSTRAINT ck_learning_import_origin_module CHECK(module='learning'));
        REVOKE ALL ON andril_learning.learning_project_import_reviews,andril_learning.learning_project_import_origins FROM PUBLIC;
        ALTER TABLE andril_learning.learning_project_import_reviews ENABLE ROW LEVEL SECURITY;
        ALTER TABLE andril_learning.learning_project_import_reviews FORCE ROW LEVEL SECURITY;
        ALTER TABLE andril_learning.learning_project_import_origins ENABLE ROW LEVEL SECURITY;
        ALTER TABLE andril_learning.learning_project_import_origins FORCE ROW LEVEL SECURITY;
        CREATE POLICY import_review_owner_scope ON andril_learning.learning_project_import_reviews
          USING(andril_learning.scope_visible(owner_learning_profile_id,module)) WITH CHECK(andril_learning.scope_visible(owner_learning_profile_id,module));
        CREATE POLICY import_origin_owner_scope ON andril_learning.learning_project_import_origins
          USING(andril_learning.scope_visible(owner_learning_profile_id,module)) WITH CHECK(andril_learning.scope_visible(owner_learning_profile_id,module));
        """);
    protected override void Down(MigrationBuilder m) => m.Sql("""
        DROP TABLE andril_learning.learning_project_import_origins;
        DROP TABLE andril_learning.learning_project_import_reviews;
        ALTER TABLE andril_learning.learning_portfolios DROP COLUMN content_version;
        """);
    protected override void BuildTargetModel(ModelBuilder b) => LearningSnapshotV4.Configure(b);
}
