using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // Generated migration column arrays are created only once.

namespace Jellyfin.Plugin.Pgsql.Migrations
{
    /// <inheritdoc />
    public partial class AddQueryGeneratorSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE FUNCTION public.jellyfin_uuid_min(uuid, uuid)
                RETURNS uuid
                LANGUAGE sql
                IMMUTABLE
                PARALLEL SAFE
                AS 'SELECT LEAST($1, $2)';

                CREATE AGGREGATE public.min(uuid)
                (
                    SFUNC = public.jellyfin_uuid_min,
                    STYPE = uuid,
                    COMBINEFUNC = public.jellyfin_uuid_min,
                    PARALLEL = SAFE,
                    SORTOP = operator(<)
                );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_UserData_UserId_LastPlayedDate_ItemId",
                table: "UserData",
                columns: new[] { "UserId", "LastPlayedDate", "ItemId" },
                filter: "\"LastPlayedDate\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BaseItems_Type_TopParentId_SeriesPresentationUniqueKey",
                table: "BaseItems",
                columns: new[] { "Type", "TopParentId", "SeriesPresentationUniqueKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserData_UserId_LastPlayedDate_ItemId",
                table: "UserData");

            migrationBuilder.DropIndex(
                name: "IX_BaseItems_Type_TopParentId_SeriesPresentationUniqueKey",
                table: "BaseItems");

            migrationBuilder.Sql(
                """
                DROP AGGREGATE IF EXISTS public.min(uuid);
                DROP FUNCTION IF EXISTS public.jellyfin_uuid_min(uuid, uuid);
                """);
        }
    }
}
