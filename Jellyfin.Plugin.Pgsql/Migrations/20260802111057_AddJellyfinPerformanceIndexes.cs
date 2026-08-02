using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Plugin.Pgsql.Migrations
{
    /// <inheritdoc />
    public partial class AddJellyfinPerformanceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_MediaSegments_ItemId\" ON \"MediaSegments\" (\"ItemId\");");
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_BaseItems_TopParentId_PresentationUniqueKey_Id\" ON \"BaseItems\" (\"TopParentId\", \"PresentationUniqueKey\", \"Id\");");
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_UserData_UserId_ItemId_LastPlayedDate\" ON \"UserData\" (\"UserId\", \"ItemId\", \"LastPlayedDate\");");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_UserData_UserId\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_UserData_UserId\" ON \"UserData\" (\"UserId\");");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_UserData_UserId_ItemId_LastPlayedDate\";");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_BaseItems_TopParentId_PresentationUniqueKey_Id\";");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_MediaSegments_ItemId\";");
        }
    }
}
