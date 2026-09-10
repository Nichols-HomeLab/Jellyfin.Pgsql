using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Plugin.Pgsql.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Jellyfin.Plugin.Pgsql.Tests;

/// <summary>Exercises the shipped pgloader script against both real database providers.</summary>
public sealed class SQLiteConversionTests
{
    [PgloaderFact]
    public async Task ConvertV12SQLite_PreservesDataSchemaAndIdentitySequences()
    {
        var directory = Path.Combine(Path.GetTempPath(), "jellyfin_conversion_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await using var target = new PostgreSqlTestDatabase();
        await target.StartAsync();
        try
        {
            var sqlitePath = Path.Combine(directory, "jellyfin.db");
            var userId = Guid.NewGuid();
            var movieId = Guid.NewGuid();
            var playlistId = Guid.NewGuid();
            await SeedSQLiteAsync(sqlitePath, userId, movieId, playlistId);

            await using var dataSource = NpgsqlDataSource.Create(target.GetConnectionString());
            await using var context = CreatePostgresContext(dataSource);
            await context.Database.MigrateAsync();
            var history = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
            var indexes = await ReadIndexesAsync(context);
            var foreignKeys = await ReadForeignKeysAsync(context);

            var connection = new NpgsqlConnectionStringBuilder(target.GetConnectionString());
            var host = connection.Host!.Contains(':', StringComparison.Ordinal) ? "[" + connection.Host + "]" : connection.Host;
            var targetUri = "pgsql://" + Uri.EscapeDataString(connection.Username!)
                + ":" + Uri.EscapeDataString(connection.Password ?? string.Empty)
                + "@" + host + ":" + connection.Port + "/" + Uri.EscapeDataString(connection.Database!);
            var load = (await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "jellyfindb.load")))
                .Replace("sqlite:///config/data/jellyfin.db", "sqlite://" + sqlitePath, StringComparison.Ordinal)
                .Replace("pgsql://${POSTGRES_USER}:${POSTGRES_PASSWORD}@${POSTGRES_HOST}:${POSTGRES_PORT}/${POSTGRES_DB}", targetUri, StringComparison.Ordinal);
            var loadPath = Path.Combine(directory, "conversion.load");
            await File.WriteAllTextAsync(loadPath, load);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(loadPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            await RunPgloaderAsync(loadPath, directory, connection.Password);

            Assert.Equal(history, (await context.Database.GetAppliedMigrationsAsync()).ToArray());
            Assert.Equal(indexes, await ReadIndexesAsync(context));
            Assert.Equal(foreignKeys, await ReadForeignKeysAsync(context));
            Assert.False(context.Database.HasPendingModelChanges());
            var user = await context.Users.SingleAsync();
            Assert.Equal(userId, user.Id);
            Assert.Equal("élise", user.Username);
            Assert.Equal("ÉLISE", user.NormalizedUsername);
            var state = await context.UserData.SingleAsync();
            Assert.Equal(userId, state.UserId);
            Assert.Equal(movieId, state.ItemId);
            Assert.True(state.Played);
            Assert.True(state.IsFavorite);
            Assert.Equal(7, state.PlayCount);
            Assert.Equal(123456789L, state.PlaybackPositionTicks);
            Assert.Equal(new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc), state.LastPlayedDate);
            var children = await context.LinkedChildren.OrderBy(child => child.SortOrder).ToListAsync();
            Assert.Equal(2, children.Count);
            Assert.All(children, child => Assert.Equal(movieId, child.ChildId));
            Assert.All(children, child => Assert.Equal(playlistId, child.ParentId));
            Assert.Equal(new[] { 0, 1 }, children.Select(child => child.SortOrder));
            Assert.Equal("eng", (await context.BaseItems.SingleAsync(item => item.Id == movieId)).OriginalLanguage);
            Assert.True((await context.MediaStreamInfos.SingleAsync()).IsOriginal);

            // pgloader must reset PostgreSQL's identity sequence beyond imported explicit IDs.
            Assert.Equal(123, (await context.ActivityLogs.SingleAsync()).Id);
            var log = new ActivityLog("After conversion", "Conversion", userId);
            context.ActivityLogs.Add(log);
            await context.SaveChangesAsync();
            Assert.True(log.Id > 123);

            // Verify that pgloader restored functioning constraints, not just their catalog names.
            context.LinkedChildren.Add(new LinkedChildEntity
            {
                ParentId = playlistId,
                ChildId = Guid.NewGuid(),
                ChildType = LinkedChildType.Manual,
                SortOrder = 2
            });
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    private static async Task SeedSQLiteAsync(string path, Guid userId, Guid movieId, Guid playlistId)
    {
        var options = new DbContextOptionsBuilder<JellyfinDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString(),
                sqlite => sqlite.MigrationsAssembly(typeof(SqliteDatabaseProvider).Assembly.FullName));
        var provider = new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance);
        await using var context = new JellyfinDbContext(options.Options, NullLogger<JellyfinDbContext>.Instance,
            provider, new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
        await context.Database.MigrateAsync();
        var user = new User("élise", "auth", "reset") { Id = userId };
        var movie = new BaseItemEntity { Id = movieId, Type = "Movie", Name = "Conversion movie", OriginalLanguage = "eng" };
        var playlist = new BaseItemEntity { Id = playlistId, Type = "Playlist", Name = "Repeated movie", IsFolder = true };
        context.Users.Add(user);
        context.BaseItems.AddRange(movie, playlist);
        context.UserData.Add(new UserData
        {
            UserId = userId, User = user, ItemId = movieId, Item = movie, CustomDataKey = "watch-state",
            Played = true, IsFavorite = true, PlayCount = 7, PlaybackPositionTicks = 123456789L,
            LastPlayedDate = new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc)
        });
        context.LinkedChildren.AddRange(
            new LinkedChildEntity { ParentId = playlistId, ChildId = movieId, ChildType = LinkedChildType.Manual, SortOrder = 0 },
            new LinkedChildEntity { ParentId = playlistId, ChildId = movieId, ChildType = LinkedChildType.Manual, SortOrder = 1 });
        context.MediaStreamInfos.Add(new MediaStreamInfo { ItemId = movieId, Item = movie, StreamIndex = 0, StreamType = MediaStreamTypeEntity.Video, IsOriginal = true });
        var log = new ActivityLog("Imported log", "Conversion", userId);
        context.ActivityLogs.Add(log);
        context.Entry(log).Property(entry => entry.Id).CurrentValue = 123;
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlRawAsync("INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('20990101000000_SQLiteOnlySentinel', 'CodeMigration')");
    }

    private static JellyfinDbContext CreatePostgresContext(NpgsqlDataSource dataSource)
    {
        var options = new DbContextOptionsBuilder<JellyfinDbContext>()
            .UseNpgsql(dataSource, postgres => postgres.MigrationsAssembly(typeof(PgSqlDatabaseProvider).Assembly.FullName));
        return new JellyfinDbContext(options.Options, NullLogger<JellyfinDbContext>.Instance,
            new PgSqlDatabaseProvider(null!, NullLogger<PgSqlDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }

    private static Task<string[]> ReadIndexesAsync(JellyfinDbContext context) => context.Database.SqlQueryRaw<string>(
        "SELECT indexname || ':' || indexdef AS \"Value\" FROM pg_indexes WHERE schemaname = 'public' ORDER BY indexname").ToArrayAsync();

    private static Task<string[]> ReadForeignKeysAsync(JellyfinDbContext context) => context.Database.SqlQueryRaw<string>(
        "SELECT conname || ':' || pg_get_constraintdef(oid) AS \"Value\" FROM pg_constraint WHERE contype = 'f' AND connamespace = 'public'::regnamespace ORDER BY conname").ToArrayAsync();

    private static async Task RunPgloaderAsync(string loadPath, string directory, string? password)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(PgloaderFactAttribute.FindPgloader()!)
            {
                WorkingDirectory = directory, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add(loadPath);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            process.Kill(true);
            await process.WaitForExitAsync();
            throw;
        }

        var output = await stdout + await stderr;
        if (!string.IsNullOrEmpty(password))
        {
            output = output.Replace(password, "[redacted]", StringComparison.Ordinal)
                .Replace(Uri.EscapeDataString(password), "[redacted]", StringComparison.Ordinal);
        }

        Assert.True(process.ExitCode == 0, output);
        Assert.DoesNotMatch(@"(?im)\b(?:ERROR|FATAL|PANIC)\b", output);
        Assert.Matches(@"(?im)^\s*Total import time\s+(?:0|✓)\s", output);
    }
}

/// <summary>Skips the real conversion only when pgloader is unavailable.</summary>
public sealed class PgloaderFactAttribute : FactAttribute
{
    public PgloaderFactAttribute()
    {
        if (FindPgloader() is null)
        {
            Skip = "Install pgloader or set JELLYFIN_TEST_PGLOADER to run the real SQLite conversion.";
        }
    }

    internal static string? FindPgloader()
    {
        var configured = Environment.GetEnvironmentVariable("JELLYFIN_TEST_PGLOADER");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory, "pgloader")).FirstOrDefault(File.Exists);
    }
}
