using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Pgsql.Database;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Jellyfin.Plugin.Pgsql.Tests;

/// <summary>
/// Serializes tests which set the provider's process-wide connection environment.
/// </summary>
[CollectionDefinition("PostgreSQL restore", DisableParallelization = true)]
public sealed class PostgreSqlRestoreCollection
{
}

/// <summary>
/// Exercises actual pg_dump/psql restoration, including SQL errors and atomic rollback.
/// </summary>
[Collection("PostgreSQL restore")]
[Trait("Category", "RequiresDocker")]
public sealed class PostgreSqlRestoreTests : IAsyncLifetime
{
    private readonly PostgreSqlTestDatabase _database = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jellyfin-pgsql-restore-" + Guid.NewGuid().ToString("N"));
    private string? _originalConnectionString;
    private PgSqlDatabaseProvider _provider = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _database.StartAsync();
        Directory.CreateDirectory(Path.Combine(_directory, "PgsqlBackups"));
        _originalConnectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        Environment.SetEnvironmentVariable("POSTGRES_CONNECTION_STRING", _database.GetConnectionString());
        _provider = new PgSqlDatabaseProvider(new TestPaths(_directory), NullLogger<PgSqlDatabaseProvider>.Instance);
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("POSTGRES_CONNECTION_STRING", _originalConnectionString);
        NpgsqlConnection.ClearAllPools();
        await _database.DisposeAsync();
        Directory.Delete(_directory, true);
    }

    /// <summary>
    /// Missing rollback material must fail rather than report successful restoration.
    /// </summary>
    /// <returns>The asynchronous test.</returns>
    [Fact]
    public async Task MissingBackupThrows()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => _provider.RestoreBackupFast("missing", CancellationToken.None));
    }

    /// <summary>
    /// SQL failure must be reported and must roll back statements preceding the error.
    /// </summary>
    /// <returns>The asynchronous test.</returns>
    [Fact]
    public async Task SqlErrorThrowsAndRollsBackPartialRestore()
    {
        var databaseName = new NpgsqlConnectionStringBuilder(_database.GetConnectionString()).Database;
        var file = Path.Combine(_directory, "PgsqlBackups", $"broken_{databaseName}.sql");
        await File.WriteAllTextAsync(file, "CREATE TABLE restore_probe(value integer); INSERT INTO restore_probe VALUES (1); SELECT 1 / 0; INSERT INTO restore_probe VALUES (2);");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _provider.RestoreBackupFast("broken", CancellationToken.None));
        Assert.Contains("division by zero", error.Message, StringComparison.OrdinalIgnoreCase);

        await using var connection = new NpgsqlConnection(_database.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT to_regclass('public.restore_probe') IS NULL", connection);
        Assert.True((bool)(await command.ExecuteScalarAsync())!);
    }

    /// <summary>
    /// A real clean pg_dump backup restores existing schema and data successfully.
    /// </summary>
    /// <returns>The asynchronous test.</returns>
    [Fact]
    public async Task CleanDumpRestoresExistingData()
    {
        await using (var connection = new NpgsqlConnection(_database.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("CREATE TABLE restore_probe(value integer); INSERT INTO restore_probe VALUES (1)", connection);
            await command.ExecuteNonQueryAsync();
        }

        var key = await _provider.MigrationBackupFast(CancellationToken.None);
        await using (var connection = new NpgsqlConnection(_database.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("UPDATE restore_probe SET value = 2", connection);
            await command.ExecuteNonQueryAsync();
        }

        await _provider.RestoreBackupFast(key, CancellationToken.None);
        await using (var connection = new NpgsqlConnection(_database.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT value FROM restore_probe", connection);
            Assert.Equal(1, (int)(await command.ExecuteScalarAsync())!);
        }
    }

    private sealed class TestPaths(string dataPath) : IApplicationPaths
    {
        public string DataPath => dataPath;

        public string ProgramDataPath => dataPath;

        public string WebPath => dataPath;

        public string ProgramSystemPath => dataPath;

        public string ImageCachePath => dataPath;

        public string PluginsPath => dataPath;

        public string PluginConfigurationsPath => dataPath;

        public string LogDirectoryPath => dataPath;

        public string ConfigurationDirectoryPath => dataPath;

        public string SystemConfigurationFilePath => dataPath;

        public string CachePath => dataPath;

        public string TempDirectory => dataPath;

        public string VirtualDataPath => dataPath;

        public string TrickplayPath => dataPath;

        public string BackupPath => dataPath;

        public void MakeSanityCheckOrThrow() => throw new NotSupportedException();

        public void CreateAndCheckMarker(string path, string markerName, bool recursive = false) => throw new NotSupportedException();
    }
}
