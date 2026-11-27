using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Andril.Persistence.Migrations.AI;

/// <summary>Frozen M1 AI core schema preparation. No runtime role or live activation implied.</summary>
[DbContext(typeof(LearningAiDbContext))]
[Migration("20261127090000_LearningAiCore")]
public sealed class LearningAiCore : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DO $authority$ BEGIN
    IF to_regprocedure('andril_learning.scope_visible(uuid,text)') IS NULL THEN
        RAISE EXCEPTION 'Learning authority migrations must precede AI core schema';
    END IF;
END $authority$;
""");
        migrationBuilder.Sql("""
DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'andril_ai') THEN
        CREATE SCHEMA andril_ai;
    END IF;
END $EF$;


CREATE TABLE andril_ai.conversations (
    id uuid NOT NULL,
    account_id uuid NOT NULL,
    module_profile_id uuid NOT NULL,
    module character varying(16) NOT NULL,
    create_operation_id uuid NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    last_saved_user_message_at_utc timestamp with time zone NOT NULL,
    expires_at_utc timestamp with time zone NOT NULL,
    deleted_at_utc timestamp with time zone,
    CONSTRAINT "PK_conversations" PRIMARY KEY (id),
    CONSTRAINT "AK_conversations_id_account_id_module_profile_id_module" UNIQUE (id, account_id, module_profile_id, module),
    CONSTRAINT ck_ai_conversation_expiry CHECK (expires_at_utc = last_saved_user_message_at_utc + interval '336 hours'),
    CONSTRAINT ck_ai_conversation_scope CHECK (account_id <> '00000000-0000-0000-0000-000000000000'::uuid AND module_profile_id <> '00000000-0000-0000-0000-000000000000'::uuid AND module = 'Learning')
);


CREATE TABLE andril_ai.privacy_acknowledgements (
    account_id uuid NOT NULL,
    module_profile_id uuid NOT NULL,
    module text NOT NULL,
    generation uuid NOT NULL,
    version bigint NOT NULL,
    policy_version text NOT NULL,
    acknowledged boolean NOT NULL,
    last_operation_id uuid NOT NULL,
    updated_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT "PK_privacy_acknowledgements" PRIMARY KEY (account_id, module_profile_id, module),
    CONSTRAINT ck_ai_ack_policy CHECK (NOT acknowledged OR length(trim(policy_version)) > 0),
    CONSTRAINT ck_ai_ack_revision CHECK (generation <> '00000000-0000-0000-0000-000000000000'::uuid AND version > 0 AND last_operation_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_ai_ack_scope CHECK (account_id <> '00000000-0000-0000-0000-000000000000'::uuid AND module_profile_id <> '00000000-0000-0000-0000-000000000000'::uuid AND module = 'Learning')
);


CREATE TABLE andril_ai.messages (
    id uuid NOT NULL,
    conversation_id uuid NOT NULL,
    account_id uuid NOT NULL,
    module_profile_id uuid NOT NULL,
    module character varying(16) NOT NULL,
    operation_id uuid NOT NULL,
    role character varying(16) NOT NULL,
    text text NOT NULL,
    saved_at_utc timestamp with time zone NOT NULL,
    store_sequence bigint GENERATED ALWAYS AS IDENTITY,
    CONSTRAINT "PK_messages" PRIMARY KEY (id),
    CONSTRAINT ck_ai_message_role CHECK (role IN ('User','Assistant')),
    CONSTRAINT ck_ai_message_scope CHECK (account_id <> '00000000-0000-0000-0000-000000000000'::uuid AND module_profile_id <> '00000000-0000-0000-0000-000000000000'::uuid AND module = 'Learning'),
    CONSTRAINT ck_ai_message_text CHECK (length(text) > 0),
    CONSTRAINT "FK_messages_conversations_conversation_id_account_id_module_pr~" FOREIGN KEY (conversation_id, account_id, module_profile_id, module) REFERENCES andril_ai.conversations (id, account_id, module_profile_id, module) ON DELETE RESTRICT
);


CREATE TABLE andril_ai.purge_work (
    conversation_id uuid NOT NULL,
    account_id uuid NOT NULL,
    module_profile_id uuid NOT NULL,
    module character varying(16) NOT NULL,
    requested_at_utc timestamp with time zone NOT NULL,
    purge_by_utc timestamp with time zone NOT NULL,
    CONSTRAINT "PK_purge_work" PRIMARY KEY (conversation_id),
    CONSTRAINT ck_ai_purge_target CHECK (purge_by_utc = requested_at_utc + interval '24 hours' AND module = 'Learning'),
    CONSTRAINT "FK_purge_work_conversations_conversation_id_account_id_module_~" FOREIGN KEY (conversation_id, account_id, module_profile_id, module) REFERENCES andril_ai.conversations (id, account_id, module_profile_id, module) ON DELETE RESTRICT
);


CREATE TABLE andril_ai.dispatch_operations (
    account_id uuid NOT NULL,
    module_profile_id uuid NOT NULL,
    module text NOT NULL,
    operation_id uuid NOT NULL,
    acknowledgement_generation uuid NOT NULL,
    acknowledgement_version bigint NOT NULL,
    policy_version text NOT NULL,
    state text NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    updated_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT "PK_dispatch_operations" PRIMARY KEY (account_id, module_profile_id, module, operation_id),
    CONSTRAINT ck_ai_dispatch_ack CHECK (acknowledgement_generation <> '00000000-0000-0000-0000-000000000000'::uuid AND acknowledgement_version > 0 AND length(trim(policy_version)) > 0),
    CONSTRAINT ck_ai_dispatch_scope CHECK (module = 'Learning' AND operation_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_ai_dispatch_state CHECK (state IN ('Prepared','Claimed','Cancelled','Indeterminate','Completed')),
    CONSTRAINT "FK_dispatch_operations_privacy_acknowledgements_account_id_mod~" FOREIGN KEY (account_id, module_profile_id, module) REFERENCES andril_ai.privacy_acknowledgements (account_id, module_profile_id, module) ON DELETE RESTRICT
);


CREATE UNIQUE INDEX uq_ai_conversation_operation ON andril_ai.conversations (account_id, module_profile_id, module, create_operation_id);


CREATE INDEX "IX_messages_conversation_id_account_id_module_profile_id_module" ON andril_ai.messages (conversation_id, account_id, module_profile_id, module);


CREATE UNIQUE INDEX uq_ai_message_operation_role ON andril_ai.messages (account_id, module_profile_id, module, operation_id, role);


CREATE INDEX "IX_purge_work_conversation_id_account_id_module_profile_id_mod~" ON andril_ai.purge_work (conversation_id, account_id, module_profile_id, module);



""");
        migrationBuilder.Sql("REVOKE ALL ON SCHEMA andril_ai FROM PUBLIC; REVOKE ALL ON ALL TABLES IN SCHEMA andril_ai FROM PUBLIC; REVOKE ALL ON ALL SEQUENCES IN SCHEMA andril_ai FROM PUBLIC;");
        foreach (var table in new[] { "conversations", "messages", "purge_work", "privacy_acknowledgements", "dispatch_operations" })
        {
            // Fixed migration-owned identifiers only. No runtime input reaches migration SQL.
            migrationBuilder.Sql($"ALTER TABLE andril_ai.{table} ENABLE ROW LEVEL SECURITY; ALTER TABLE andril_ai.{table} FORCE ROW LEVEL SECURITY; CREATE POLICY owner_scope ON andril_ai.{table} USING(module='Learning' AND account_id::text=current_setting('andril.account',true) AND andril_learning.scope_visible(module_profile_id,'learning')) WITH CHECK(module='Learning' AND account_id::text=current_setting('andril.account',true) AND andril_learning.scope_visible(module_profile_id,'learning'));");
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("dispatch_operations", "andril_ai");
        migrationBuilder.DropTable("purge_work", "andril_ai");
        migrationBuilder.DropTable("messages", "andril_ai");
        migrationBuilder.DropTable("privacy_acknowledgements", "andril_ai");
        migrationBuilder.DropTable("conversations", "andril_ai");
        // Do not drop schema, other contexts' objects or the dedicated EF history table.
    }
}