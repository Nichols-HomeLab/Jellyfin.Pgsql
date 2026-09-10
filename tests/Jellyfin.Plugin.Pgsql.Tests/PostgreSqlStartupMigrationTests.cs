using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Plugin.Pgsql.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Jellyfin.Plugin.Pgsql.Tests;

/// <summary>
/// Runs the complete server migration startup against a disposable PostgreSQL database.
/// Set JELLYFIN_TEST_POSTGRES_CONNECTION_STRING and JELLYFIN_TEST_SERVER_DLL to enable.
/// </summary>
public sealed class PostgreSqlStartupMigrationTests
{
    // Completed code migrations from companion server 8c6ad6d258 (the pre-v12 baseline).
    private static readonly string[] LegacyMigrationIds =
    [
        "20250420000000_CreateNetworkConfiguration",
        "20250420030000_MigrateEncodingOptions",
        "20250420020000_MigrateMusicBrainzTimeout",
        "20250420010000_MigrateNetworkConfiguration",
        "20250420040000_RenameEnableGroupingIntoCollections",
        "20260522092304_UpdateNormalizedUsername",
        "20250420160000_AddDefaultCastReceivers",
        "20250420090000_AddDefaultPluginRepository",
        "20251009200000_CleanMusicArtist",
        "20250420060000_CreateUserLoggingConfigFile",
        "20250420050000_DisableTranscodingThrottling",
        "20250420180000_FixAudioData",
        "20250620180000_FixDates",
        "20260206200000_FixLibrarySubtitleDownloadLanguages",
        "20250420150000_FixPlaylistOwner",
        "20250420070000_MigrateActivityLogDb",
        "20250420140000_MigrateAuthenticationDb",
        "20250420120000_MigrateDisplayPreferencesDb",
        "20250421000000_MigrateKeyframeData",
        "20250420200000_MigrateLibraryDb",
        "20250420193000_MigrateLibraryDbCompatibilityCheck",
        "20250618010000_MigrateLibraryUserData",
        "20250420220000_MigrateRatingLevels",
        "20250420100000_MigrateUserDb",
        "20250420210000_MoveExtractedFiles",
        "20250420230000_MoveTrickplayFiles",
        "20250420110000_ReaddDefaultPluginRepository",
        "20250420230000_RefreshInternalDateModified",
        "20250420130000_RemoveDownloadImagesInAdvance",
        "20250420080000_RemoveDuplicateExtras",
        "20250420190000_RemoveDuplicatePlaylistChildren",
        "20250730215000_ReseedFolderFlag",
        "20250420170000_UpdateDefaultPluginRepository",
    ];

    [StandaloneStartupTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullStartup_UpgradesAndIsIdempotent(bool populatedLegacyDatabase)
    {
        var adminConnectionString = Environment.GetEnvironmentVariable("JELLYFIN_TEST_POSTGRES_CONNECTION_STRING")!;
        var databaseName = "jellyfin_startup_test_" + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), databaseName);
        Directory.CreateDirectory(directory);
        await using var admin = new NpgsqlConnection(adminConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand("CREATE DATABASE " + databaseName, admin))
        {
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = databaseName }.ToString();
        try
        {
            await using var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
            if (populatedLegacyDatabase)
            {
                await SeedLegacyDatabaseAsync(dataSource);
            }

            var configDirectory = Path.Combine(directory, "config");
            Directory.CreateDirectory(configDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(configDirectory, "database.xml"),
                "<DatabaseConfigurationOptions><DatabaseType>PLUGIN_PROVIDER</DatabaseType><CustomProviderOptions><PluginAssembly>Jellyfin.Plugin.Pgsql.dll</PluginAssembly><PluginName>PostgreSQL</PluginName></CustomProviderOptions><LockingBehavior>NoLock</LockingBehavior></DatabaseConfigurationOptions>");
            if (populatedLegacyDatabase)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(configDirectory, "system.xml"),
                    "<ServerConfiguration><IsStartupWizardCompleted>true</IsStartupWizardCompleted><EnableMetrics>false</EnableMetrics></ServerConfiguration>");
            }

            InstallPlugin(directory);
            await RunServerAsync(directory, connectionString);
            string[] applied;
            await using (var context = CreateContext(dataSource))
            {
                Assert.Empty(await context.Database.GetPendingMigrationsAsync());
                Assert.False(context.Database.HasPendingModelChanges());
                applied = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
                if (populatedLegacyDatabase)
                {
                    await AssertPreservedDataAsync(context);
                    Assert.Contains(applied, id => id.EndsWith("_MigrateLinkedChildren", StringComparison.Ordinal));
                }
            }

            await RunServerAsync(directory, connectionString);
            await using (var context = CreateContext(dataSource))
            {
                Assert.Equal(applied, (await context.Database.GetAppliedMigrationsAsync()).ToArray());
                if (populatedLegacyDatabase)
                {
                    await AssertPreservedDataAsync(context);
                }
            }
        }
        finally
        {
            await using var drop = new NpgsqlCommand("DROP DATABASE " + databaseName, admin);
            await drop.ExecuteNonQueryAsync();
            Directory.Delete(directory, true);
        }
    }

    private static async Task RunServerAsync(string directory, string connectionString)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("JELLYFIN_TEST_DOTNET_HOST") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[]
        {
            Environment.GetEnvironmentVariable("JELLYFIN_TEST_SERVER_DLL")!, "--mode", "MigrateSystem", "--nowebclient", "--nonetchange",
            "--datadir", Path.Combine(directory, "data"), "--configdir", Path.Combine(directory, "config"),
            "--cachedir", Path.Combine(directory, "cache"), "--logdir", Path.Combine(directory, "logs")
        })
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["POSTGRES_CONNECTION_STRING"] = connectionString;
        start.Environment["JELLYFIN_PublishedServerUrl"] = "http://127.0.0.1";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            await process.WaitForExitAsync();
            Assert.Fail("Migration startup timed out: " + await output + await errors);
        }

        var log = await output + await errors;
        Assert.True(process.ExitCode == 0, log);
        Assert.False(log.Contains("Error while starting server", StringComparison.Ordinal), log);
    }

    private static JellyfinDbContext CreateContext(NpgsqlDataSource dataSource)
    {
        var provider = new PgSqlDatabaseProvider(null!, NullLogger<PgSqlDatabaseProvider>.Instance);
        var options = new DbContextOptionsBuilder<JellyfinDbContext>();
        options.UseNpgsql(dataSource, pg => pg.MigrationsAssembly(typeof(PgSqlDatabaseProvider).Assembly.FullName));
        return new JellyfinDbContext(options.Options, NullLogger<JellyfinDbContext>.Instance, provider, new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }

    private static void InstallPlugin(string directory)
    {
        var destination = Path.Combine(directory, "data", "plugins", "PostgreSQL");
        Directory.CreateDirectory(destination);
        var source = Path.GetDirectoryName(typeof(PgSqlDatabaseProvider).Assembly.Location)!;
        foreach (var file in Directory.GetFiles(source, "Npgsql*.dll").Append(Path.Combine(source, "Jellyfin.Plugin.Pgsql.dll")))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        }
    }

    private static async Task AssertPreservedDataAsync(JellyfinDbContext context)
    {
        var user = await context.Users.SingleAsync();
        Assert.Equal("ÉLISE", user.NormalizedUsername);
        var watchState = await context.UserData.SingleAsync();
        Assert.Equal(7, watchState.PlayCount);
        Assert.True(watchState.Played);
        Assert.True(watchState.IsFavorite);
        Assert.Equal(1234, watchState.PlaybackPositionTicks);
        Assert.Equal(2, await context.LinkedChildren.CountAsync());
        Assert.Equal(2, await context.LinkedChildren.Select(link => link.ParentId).Distinct().CountAsync());
        Assert.All(await context.LinkedChildren.ToListAsync(), link =>
            Assert.Equal(Guid.Parse("10000000-0000-0000-0000-000000000001"), link.ChildId));
        Assert.Equal(Guid.Parse("10000000-0000-0000-0000-000000000001"), watchState.ItemId);
    }

    private static async Task SeedLegacyDatabaseAsync(NpgsqlDataSource dataSource)
    {
        await using var context = CreateContext(dataSource);
        await context.GetService<IMigrator>().MigrateAsync("20260802113543_AddQueryGeneratorSupport");
        var history = context.GetService<IHistoryRepository>();
        foreach (var id in LegacyMigrationIds)
        {
            await context.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(id, "10.11.0")));
        }

        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO "BaseItems" ("Id", "Type", "IsMovie", "IsLocked", "IsSeries", "IsRepeat", "IsInMixedFolder", "IsFolder", "IsVirtualItem", "Data") VALUES
            ('10000000-0000-0000-0000-000000000001', 'MediaBrowser.Controller.Entities.Movies.Movie', true, false, false, false, false, false, false, NULL),
            ('10000000-0000-0000-0000-000000000002', 'MediaBrowser.Controller.Playlists.Playlist', false, false, false, false, false, true, false, '{{"LinkedChildren":[{{"ItemId":"10000000000000000000000000000001","Type":"Manual"}}]}}'),
            ('10000000-0000-0000-0000-000000000003', 'MediaBrowser.Controller.Entities.Movies.BoxSet', false, false, false, false, false, true, false, '{{"LinkedChildren":[{{"ItemId":"10000000000000000000000000000001","Type":"Manual"}}]}}');
            INSERT INTO "Users" ("Id", "Username", "NormalizedUsername", "MustUpdatePassword", "AuthenticationProviderId", "PasswordResetProviderId", "InvalidLoginAttemptCount", "MaxActiveSessions", "SubtitleMode", "PlayDefaultAudioTrack", "DisplayMissingEpisodes", "DisplayCollectionsView", "EnableLocalPassword", "HidePlayedInLatest", "RememberAudioSelections", "RememberSubtitleSelections", "EnableNextEpisodeAutoPlay", "EnableAutoLogin", "EnableUserPreferenceAccess", "InternalId", "SyncPlayAccess", "RowVersion") VALUES ('30000000-0000-0000-0000-000000000001', 'élise', 'ÉLISE', false, 'auth', 'reset', 0, 0, 0, false, false, false, false, false, false, false, false, false, false, 0, 0, 0);
            INSERT INTO "UserData" ("ItemId", "UserId", "CustomDataKey", "PlaybackPositionTicks", "PlayCount", "IsFavorite", "Played") VALUES ('10000000-0000-0000-0000-000000000001', '30000000-0000-0000-0000-000000000001', 'movie-watch-state', 1234, 7, true, true);
            """);
    }
}

/// <summary>Enables real process tests when a matching published server is supplied.</summary>
public sealed class StandaloneStartupTheoryAttribute : TheoryAttribute
{
    /// <summary>Initializes a new instance of the <see cref="StandaloneStartupTheoryAttribute"/> class.</summary>
    public StandaloneStartupTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("JELLYFIN_TEST_POSTGRES_CONNECTION_STRING") is null
            || Environment.GetEnvironmentVariable("JELLYFIN_TEST_SERVER_DLL") is null)
        {
            Skip = "Set JELLYFIN_TEST_POSTGRES_CONNECTION_STRING and JELLYFIN_TEST_SERVER_DLL for real startup tests.";
        }
    }
}
