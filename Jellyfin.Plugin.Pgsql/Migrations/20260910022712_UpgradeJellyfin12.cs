using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // Generated migration column arrays are created only once.

namespace Jellyfin.Plugin.Pgsql.Migrations
{
    /// <inheritdoc />
    public partial class UpgradeJellyfin12 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Match upstream cleanup before making the owning user mandatory.
            migrationBuilder.Sql("""
                DELETE FROM "Permissions" WHERE "UserId" IS NULL;
                DELETE FROM "Preferences" WHERE "UserId" IS NULL;
                """);

            migrationBuilder.DropIndex(
                name: "IX_Preferences_UserId_Kind",
                table: "Preferences");

            migrationBuilder.DropIndex(
                name: "IX_Permissions_UserId_Kind",
                table: "Permissions");

            migrationBuilder.DropIndex(
                name: "IX_PeopleBaseItemMap_PeopleId",
                table: "PeopleBaseItemMap");

            migrationBuilder.DropIndex(
                name: "IX_MediaStreamInfos_StreamIndex",
                table: "MediaStreamInfos");

            migrationBuilder.DropIndex(
                name: "IX_MediaStreamInfos_StreamIndex_StreamType",
                table: "MediaStreamInfos");

            migrationBuilder.DropIndex(
                name: "IX_MediaStreamInfos_StreamIndex_StreamType_Language",
                table: "MediaStreamInfos");

            migrationBuilder.DropIndex(
                name: "IX_MediaStreamInfos_StreamType",
                table: "MediaStreamInfos");

            migrationBuilder.DropIndex(
                name: "IX_Devices_DeviceId",
                table: "Devices");

            migrationBuilder.DropIndex(
                name: "IX_BaseItems_Id_Type_IsFolder_IsVirtualItem",
                table: "BaseItems");

            migrationBuilder.DropIndex(
                name: "IX_BaseItemProviders_ProviderId_ProviderValue_ItemId",
                table: "BaseItemProviders");

            migrationBuilder.DropIndex(
                name: "IX_BaseItemImageInfos_ItemId",
                table: "BaseItemImageInfos");

            migrationBuilder.DropColumn(
                name: "Preference_Preferences_Guid",
                table: "Preferences");

            migrationBuilder.DropColumn(
                name: "Permission_Permissions_Guid",
                table: "Permissions");

            // These columns contain unrelated values; do not carry legacy extra IDs into language metadata.
            migrationBuilder.DropColumn(name: "ExtraIds", table: "BaseItems");
            migrationBuilder.AddColumn<string>(name: "OriginalLanguage", table: "BaseItems", type: "text", nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "UserId",
                table: "Preferences",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "UserId",
                table: "Permissions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsOriginal",
                table: "MediaStreamInfos",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // PostgreSQL needs an explicit cast, including legacy compact GUIDs and invalid IDs.
            migrationBuilder.Sql("""
                ALTER TABLE "BaseItems" ALTER COLUMN "PrimaryVersionId" TYPE uuid USING
                    CASE WHEN replace("PrimaryVersionId", '-', '') ~ '^[0-9a-fA-F]{32}$'
                         THEN NULLIF(replace("PrimaryVersionId", '-', '')::uuid, '00000000-0000-0000-0000-000000000000'::uuid)
                         ELSE NULL END;
                """);

            // PostgreSQL needs an explicit cast, including legacy compact GUIDs and invalid IDs.
            migrationBuilder.Sql("""
                ALTER TABLE "BaseItems" ALTER COLUMN "OwnerId" TYPE uuid USING
                    CASE WHEN replace("OwnerId", '-', '') ~ '^[0-9a-fA-F]{32}$'
                         THEN NULLIF(replace("OwnerId", '-', '')::uuid, '00000000-0000-0000-0000-000000000000'::uuid)
                         ELSE NULL END;
                """);

            migrationBuilder.CreateTable(
                name: "LinkedChildren",
                columns: table => new
                {
                    ParentId = table.Column<Guid>(type: "uuid", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    ChildId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChildType = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkedChildren", x => new { x.ParentId, x.SortOrder });
                    table.ForeignKey(
                        name: "FK_LinkedChildren_BaseItems_ChildId",
                        column: x => x.ChildId,
                        principalTable: "BaseItems",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_LinkedChildren_BaseItems_ParentId",
                        column: x => x.ParentId,
                        principalTable: "BaseItems",
                        principalColumn: "Id");
                });

            migrationBuilder.UpdateData(
                table: "BaseItems",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-000000000001"),
                columns: new[] { "Name", "OwnerId", "PrimaryVersionId" },
                values: new object[] { "This is a placeholder item for UserData that has been detached from its original item", null, null });

            migrationBuilder.CreateIndex(
                name: "IX_UserData_UserId_IsFavorite_ItemId",
                table: "UserData",
                columns: new[] { "UserId", "IsFavorite", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_UserData_UserId_Played_ItemId",
                table: "UserData",
                columns: new[] { "UserId", "Played", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_Preferences_UserId_Kind",
                table: "Preferences",
                columns: new[] { "UserId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Permissions_UserId_Kind",
                table: "Permissions",
                columns: new[] { "UserId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PeopleBaseItemMap_PeopleId_ItemId",
                table: "PeopleBaseItemMap",
                columns: new[] { "PeopleId", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_MediaStreamInfos_StreamType_ItemId_Language_IsExternal",
                table: "MediaStreamInfos",
                columns: new[] { "StreamType", "ItemId", "Language", "IsExternal" });

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_ExtraType_OwnerId",
                table: "BaseItems",
                columns: new[] { "ExtraType", "OwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_Name",
                table: "BaseItems",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_OwnerId",
                table: "BaseItems",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_PrimaryVersionId",
                table: "BaseItems",
                column: "PrimaryVersionId",
                filter: "\"PrimaryVersionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_SeasonId",
                table: "BaseItems",
                column: "SeasonId");

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_SeriesId",
                table: "BaseItems",
                column: "SeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_SeriesName",
                table: "BaseItems",
                column: "SeriesName");

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_TopParentId_IsFolder_IsVirtualItem_DateCreated",
                table: "BaseItems",
                columns: new[] { "TopParentId", "IsFolder", "IsVirtualItem", "DateCreated" });

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_TopParentId_MediaType_IsVirtualItem_DateCreated",
                table: "BaseItems",
                columns: new[] { "TopParentId", "MediaType", "IsVirtualItem", "DateCreated" });

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_TopParentId_Type_IsVirtualItem",
                table: "BaseItems",
                columns: new[] { "TopParentId", "Type", "IsVirtualItem" },
                filter: "\"PrimaryVersionId\" IS NULL AND (\"OwnerId\" IS NULL OR \"ExtraType\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_TopParentId_Type_IsVirtualItem_DateCreated",
                table: "BaseItems",
                columns: new[] { "TopParentId", "Type", "IsVirtualItem", "DateCreated" });

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_Type_CleanName",
                table: "BaseItems",
                columns: new[] { "Type", "CleanName" });

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_Type_SeriesPresentationUniqueKey_ParentIndexNumbe~",
                table: "BaseItems",
                columns: new[] { "Type", "SeriesPresentationUniqueKey", "ParentIndexNumber", "IndexNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_Type_TopParentId_SortName",
                table: "BaseItems",
                columns: new[] { "Type", "TopParentId", "SortName" });

            migrationBuilder.CreateIndex(
                name: "IX_BaseItemProviders_ProviderId_ItemId_ProviderValue",
                table: "BaseItemProviders",
                columns: new[] { "ProviderId", "ItemId", "ProviderValue" });

            migrationBuilder.CreateIndex(
                name: "IX_BaseItemImageInfos_ItemId_ImageType",
                table: "BaseItemImageInfos",
                columns: new[] { "ItemId", "ImageType" });

            migrationBuilder.CreateIndex(
                name: "IX_LinkedChildren_ChildId_ChildType",
                table: "LinkedChildren",
                columns: new[] { "ChildId", "ChildType" });

            migrationBuilder.CreateIndex(
                name: "IX_LinkedChildren_ParentId_ChildType",
                table: "LinkedChildren",
                columns: new[] { "ParentId", "ChildType" });

            // Keep orphan markers for the upstream code migration that removes orphaned extras.
            migrationBuilder.Sql("""
                UPDATE "BaseItems" AS item
                SET "OwnerId" = '00000000-0000-0000-0000-000000000001'::uuid
                WHERE "OwnerId" IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM "BaseItems" AS owner WHERE owner."Id" = item."OwnerId");
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_BaseItems_BaseItems_OwnerId",
                table: "BaseItems",
                column: "OwnerId",
                principalTable: "BaseItems",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Jellyfin 12 data migrations cannot be reversed safely. Restore the pre-upgrade PostgreSQL backup to downgrade.");
        }
    }
}
