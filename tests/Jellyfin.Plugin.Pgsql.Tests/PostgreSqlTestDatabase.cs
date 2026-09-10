using System;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Jellyfin.Plugin.Pgsql.Tests;

/// <summary>
/// Provides an isolated database on a disposable external PostgreSQL server or a test container.
/// </summary>
internal sealed class PostgreSqlTestDatabase : IAsyncDisposable
{
    private readonly PostgreSqlContainer? _container;
    private readonly string? _externalConnectionString;
    private string _connectionString = string.Empty;
    private string? _databaseName;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgreSqlTestDatabase"/> class.
    /// </summary>
    public PostgreSqlTestDatabase()
    {
        _externalConnectionString = Environment.GetEnvironmentVariable("JELLYFIN_TEST_POSTGRES_CONNECTION_STRING")
            ?? Environment.GetEnvironmentVariable("JELLYFIN_CATALOG_TEST_POSTGRES");
        if (_externalConnectionString is null)
        {
            _container = new PostgreSqlBuilder("postgres:18-alpine")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("pg_isready"))
                .Build();
        }
    }

    /// <summary>
    /// Starts the container or creates an empty database unique to this test.
    /// </summary>
    /// <returns>The asynchronous operation.</returns>
    public async Task StartAsync()
    {
        if (_container is not null)
        {
            await _container.StartAsync().ConfigureAwait(false);
            _connectionString = _container.GetConnectionString();
            return;
        }

        var databaseName = "jellyfin_test_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(_externalConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand("CREATE DATABASE " + databaseName, connection);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        _databaseName = databaseName;
        _connectionString = new NpgsqlConnectionStringBuilder(_externalConnectionString) { Database = databaseName, Pooling = false }.ToString();
    }

    /// <summary>
    /// Gets the test database connection string.
    /// </summary>
    /// <returns>The connection string.</returns>
    public string GetConnectionString() => _connectionString;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
        else if (_databaseName is not null)
        {
            await using var connection = new NpgsqlConnection(_externalConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = new NpgsqlCommand("DROP DATABASE " + _databaseName, connection);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }
}
