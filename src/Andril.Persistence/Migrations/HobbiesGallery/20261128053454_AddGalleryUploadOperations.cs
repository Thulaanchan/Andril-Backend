using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andril.Persistence.Migrations.HobbiesGallery
{
    /// <inheritdoc />
    public partial class AddGalleryUploadOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProviderGeneration",
                schema: "andril_hobbies",
                table: "GalleryMedia",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GalleryUploadOperations",
                schema: "andril_hobbies",
                columns: table => new
                {
                    owner_module_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    module = table.Column<string>(type: "text", nullable: false),
                    operation_kind = table.Column<short>(type: "smallint", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fingerprint_sha256 = table.Column<string>(type: "character(64)", fixedLength: true, nullable: false),
                    media_id = table.Column<Guid>(type: "uuid", nullable: false),
                    private_object_key = table.Column<string>(type: "text", nullable: false),
                    provider_generation = table.Column<string>(type: "text", nullable: true),
                    state = table.Column<short>(type: "smallint", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GalleryUploadOperations", x => new { x.owner_module_profile_id, x.module, x.operation_kind, x.operation_id });
                    table.CheckConstraint("CK_GalleryUploadOperations_evidence", "fingerprint_sha256 ~ '^[0-9A-F]{64}$' AND private_object_key ~ '[^[:space:]]' AND ((state = 0 AND provider_generation IS NULL) OR (state IN (1, 2) AND provider_generation IS NOT NULL AND provider_generation ~ '[^[:space:]]'))");
                    table.CheckConstraint("CK_GalleryUploadOperations_revision", "revision = state AND updated_at_utc >= created_at_utc");
                    table.CheckConstraint("CK_GalleryUploadOperations_scope", "module = 'Hobbies' AND operation_kind = 0 AND owner_module_profile_id <> '00000000-0000-0000-0000-000000000000' AND operation_id <> '00000000-0000-0000-0000-000000000000' AND media_id <> '00000000-0000-0000-0000-000000000000'");
                });

            migrationBuilder.CreateIndex(
                name: "IX_GalleryUploadOperations_media_id",
                schema: "andril_hobbies",
                table: "GalleryUploadOperations",
                column: "media_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GalleryUploadOperations_private_object_key",
                schema: "andril_hobbies",
                table: "GalleryUploadOperations",
                column: "private_object_key",
                unique: true);

            migrationBuilder.Sql("""
                CREATE FUNCTION andril_hobbies.guard_gallery_upload_operation() RETURNS trigger
                LANGUAGE plpgsql SET search_path = pg_catalog AS $$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        IF NEW.state <> 0 THEN
                            RAISE EXCEPTION 'Gallery upload must begin reserved' USING ERRCODE = '23514';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF ROW(NEW.owner_module_profile_id, NEW.module, NEW.operation_kind, NEW.operation_id,
                           NEW.fingerprint_sha256, NEW.media_id, NEW.private_object_key, NEW.created_at_utc)
                       IS DISTINCT FROM
                       ROW(OLD.owner_module_profile_id, OLD.module, OLD.operation_kind, OLD.operation_id,
                           OLD.fingerprint_sha256, OLD.media_id, OLD.private_object_key, OLD.created_at_utc)
                       OR NEW.state <> OLD.state + 1 OR NEW.state > 2
                       OR NEW.updated_at_utc < OLD.updated_at_utc
                       OR (OLD.provider_generation IS NOT NULL AND NEW.provider_generation IS DISTINCT FROM OLD.provider_generation) THEN
                        RAISE EXCEPTION 'Invalid Gallery upload transition' USING ERRCODE = '23514';
                    END IF;
                    IF NEW.state = 2 AND NOT EXISTS (
                        SELECT 1 FROM andril_hobbies."GalleryMedia" m
                        WHERE m."Id" = NEW.media_id AND m."OwnerModuleProfileId" = NEW.owner_module_profile_id
                          AND m."Module" = NEW.module AND m."StorageObjectKey" = NEW.private_object_key
                          AND m."ProviderGeneration" = NEW.provider_generation AND m."IsPrivate" AND NOT m."IsDeleted"
                    ) THEN
                        RAISE EXCEPTION 'Gallery upload requires matching media' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER guard_gallery_upload_operation BEFORE INSERT OR UPDATE
                    ON andril_hobbies."GalleryUploadOperations"
                    FOR EACH ROW EXECUTE FUNCTION andril_hobbies.guard_gallery_upload_operation();

                CREATE FUNCTION andril_hobbies.guard_gallery_generation() RETURNS trigger
                LANGUAGE plpgsql SET search_path = pg_catalog AS $$
                BEGIN
                    IF TG_OP = 'UPDATE' THEN
                        IF OLD."ProviderGeneration" IS NOT NULL AND
                            ROW(NEW."Id", NEW."OwnerModuleProfileId", NEW."Module", NEW."StorageObjectKey", NEW."ProviderGeneration", NEW."IsPrivate")
                            IS DISTINCT FROM
                            ROW(OLD."Id", OLD."OwnerModuleProfileId", OLD."Module", OLD."StorageObjectKey", OLD."ProviderGeneration", OLD."IsPrivate") THEN
                            RAISE EXCEPTION 'Gallery generation binding is immutable' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    IF NEW."ProviderGeneration" IS NOT NULL AND NOT EXISTS (
                        SELECT 1 FROM andril_hobbies."GalleryUploadOperations" op
                        WHERE op.media_id = NEW."Id" AND op.owner_module_profile_id = NEW."OwnerModuleProfileId"
                          AND op.module = NEW."Module" AND op.private_object_key = NEW."StorageObjectKey"
                          AND op.provider_generation = NEW."ProviderGeneration" AND op.state = 2
                    ) THEN
                        RAISE EXCEPTION 'Gallery generation requires a completed receipt' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE CONSTRAINT TRIGGER guard_gallery_generation AFTER INSERT OR UPDATE
                    ON andril_hobbies."GalleryMedia" DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION andril_hobbies.guard_gallery_generation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER guard_gallery_generation ON andril_hobbies."GalleryMedia";
                DROP FUNCTION andril_hobbies.guard_gallery_generation();
                DROP TRIGGER guard_gallery_upload_operation ON andril_hobbies."GalleryUploadOperations";
                DROP FUNCTION andril_hobbies.guard_gallery_upload_operation();
                """);
            migrationBuilder.DropTable(
                name: "GalleryUploadOperations",
                schema: "andril_hobbies");

            migrationBuilder.DropColumn(
                name: "ProviderGeneration",
                schema: "andril_hobbies",
                table: "GalleryMedia");
        }
    }
}
