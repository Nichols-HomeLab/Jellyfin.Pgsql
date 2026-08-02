using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Jellyfin.Plugin.Pgsql.Database;

/// <summary>
/// Configures jellyfin to use an Postgres database.
/// </summary>
[JellyfinDatabaseProviderKey("Jellyfin-PgSql")]
public sealed class PgSqlDatabaseProvider : IJellyfinDatabaseProvider
{
    private const string BackupFolderName = "PgsqlBackups";
    private readonly ILogger<PgSqlDatabaseProvider> _logger;
    private readonly IApplicationPaths _applicationPaths;

    /// <summary>
    /// Initializes a new instance of the <see cref="PgSqlDatabaseProvider"/> class.
    /// </summary>
    /// <param name="applicationPaths">Service to construct the backup paths.</param>
    /// <param name="logger">A logger.</param>
    public PgSqlDatabaseProvider(IApplicationPaths applicationPaths, ILogger<PgSqlDatabaseProvider> logger)
    {
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    /// <inheritdoc/>
    public IDbContextFactory<JellyfinDbContext>? DbContextFactory { get; set; }

    /// <inheritdoc/>
    public void Initialise(DbContextOptionsBuilder options, DatabaseConfigurationOptions databaseConfiguration)
    {
        var customOptions = databaseConfiguration.CustomProviderOptions?.Options;

        var connectionBuilder = GetConnectionBuilder(customOptions);
        connectionBuilder.ApplicationName = $"jellyfin+{FileVersionInfo.GetVersionInfo(Assembly.GetEntryAssembly()!.Location).FileVersion}";

        options
            .UseNpgsql(connectionBuilder.ToString(), pgSqlOptions =>
            {
                pgSqlOptions.MigrationsAssembly(GetType().Assembly.FullName);
            });

        var enableSensitiveDataLogging = GetCustomDatabaseOption(customOptions, "EnableSensitiveDataLogging", e => e.Equals(bool.TrueString, StringComparison.OrdinalIgnoreCase), () => false);
        if (enableSensitiveDataLogging)
        {
            options.EnableSensitiveDataLogging(enableSensitiveDataLogging);
            _logger.LogInformation("EnableSensitiveDataLogging is enabled on PostgreSQL connection");
        }
    }

    /// <inheritdoc/>
    public async Task RunScheduledOptimisation(CancellationToken cancellationToken)
    {
        if (DbContextFactory is null)
        {
            return;
        }

        var context = await DbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            if (context.Database.IsNpgsql())
            {
                await context.Database.ExecuteSqlRawAsync("VACUUM ANALYZE", cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("PostgreSQL database optimized successfully");
            }
        }
    }

    /// <inheritdoc/>
    public void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Use C collation for consistent case-sensitive behavior matching SQLite BINARY
        modelBuilder.UseCollation("C");

        // These access paths are heavily used by library browse, Next Up, media
        // segment refresh, and item removal queries. Keep them in the provider
        // model so new installations and future migrations retain the indexes.
        modelBuilder.Entity<MediaSegment>()
            .HasIndex(e => e.ItemId)
            .HasDatabaseName("IX_MediaSegments_ItemId");
        modelBuilder.Entity<BaseItemEntity>()
            .HasIndex(e => new { e.TopParentId, e.PresentationUniqueKey, e.Id })
            .HasDatabaseName("IX_BaseItems_TopParentId_PresentationUniqueKey_Id");
        modelBuilder.Entity<UserData>()
            .HasIndex(e => new { e.UserId, e.ItemId, e.LastPlayedDate })
            .HasDatabaseName("IX_UserData_UserId_ItemId_LastPlayedDate");

        // Configure all DateTime properties to ensure UTC for PostgreSQL compatibility, matching SQLite provider
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?))
                {
                    property.SetValueConverter(
                        new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
                            v => v.ToUniversalTime(),
                            v => DateTime.SpecifyKind(v, DateTimeKind.Utc)));
                }
            }
        }
    }

    /// <inheritdoc/>
    public Task RunShutdownTask(CancellationToken cancellationToken)
    {
        // Clear Npgsql connection pools on shutdown
        NpgsqlConnection.ClearAllPools();
        _logger.LogInformation("PostgreSQL connection pools cleared on shutdown");

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
    }

    /// <inheritdoc/>
    public async Task<string> MigrationBackupFast(CancellationToken cancellationToken)
    {
        var key = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var backupFolder = Path.Combine(_applicationPaths.DataPath, BackupFolderName);
        Directory.CreateDirectory(backupFolder);

        var connectionBuilder = GetConnectionBuilder(null);
        var backupFile = Path.Combine(backupFolder, $"{key}_{connectionBuilder.Database}.sql");

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "pg_dump",
                Arguments = $"--host={connectionBuilder.Host} --port={connectionBuilder.Port} --username={connectionBuilder.Username} --dbname={connectionBuilder.Database} --file=\"{backupFile}\" --no-password --verbose --clean --if-exists",
                Environment = { ["PGPASSWORD"] = connectionBuilder.Password },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        _logger.LogInformation("Starting PostgreSQL backup: {BackupFile}", backupFile);

        process.Start();

        // Read both pipes while pg_dump runs. Its --verbose output goes to stderr,
        // and leaving the redirected pipe unread will deadlock once it fills
        // (JPVenson/Jellyfin.Pgsql#39).
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await stdoutTask.ConfigureAwait(false);
        var error = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            _logger.LogError("pg_dump failed with exit code {ExitCode}: {Error}", process.ExitCode, error);
            throw new InvalidOperationException($"pg_dump failed: {error}");
        }

        _logger.LogInformation("PostgreSQL backup completed successfully: {BackupFile}", backupFile);
        return key;
    }

    /// <inheritdoc/>
    public async Task RestoreBackupFast(string key, CancellationToken cancellationToken)
    {
        NpgsqlConnection.ClearAllPools();

        var connectionBuilder = GetConnectionBuilder(null);
        var backupFile = Path.Combine(_applicationPaths.DataPath, BackupFolderName, $"{key}_{connectionBuilder.Database}.sql");

        if (!File.Exists(backupFile))
        {
            _logger.LogCritical("Tried to restore a backup that does not exist: {Key}", key);
            return;
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "psql",
                Arguments = $"--host={connectionBuilder.Host} --port={connectionBuilder.Port} --username={connectionBuilder.Username} --dbname={connectionBuilder.Database} --file=\"{backupFile}\" --no-password --quiet",
                Environment = { ["PGPASSWORD"] = connectionBuilder.Password },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        _logger.LogInformation("Starting PostgreSQL restore from: {BackupFile}", backupFile);

        process.Start();

        // Same reason as MigrationBackupFast: read the pipes while psql runs so a
        // large restore can't fill the redirected output buffer and deadlock.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await stdoutTask.ConfigureAwait(false);
        var error = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            _logger.LogError("psql restore failed with exit code {ExitCode}: {Error}", process.ExitCode, error);
            throw new InvalidOperationException($"psql restore failed: {error}");
        }

        _logger.LogInformation("PostgreSQL restore completed successfully from: {BackupFile}", backupFile);
    }

    /// <inheritdoc/>
    public Task DeleteBackup(string key)
    {
        var connectionBuilder = GetConnectionBuilder(null);
        var backupFile = Path.Combine(_applicationPaths.DataPath, BackupFolderName, $"{key}_{connectionBuilder.Database}.sql");

        if (!File.Exists(backupFile))
        {
            _logger.LogCritical("Tried to delete a backup that does not exist: {Key}", key);
            return Task.CompletedTask;
        }

        File.Delete(backupFile);
        _logger.LogInformation("Deleted backup file: {BackupFile}", backupFile);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task PurgeDatabase(JellyfinDbContext dbContext, IEnumerable<string>? tableNames)
    {
        ArgumentNullException.ThrowIfNull(tableNames);

        var truncateQueries = new List<string>();
        foreach (var tableName in tableNames)
        {
            truncateQueries.Add($"TRUNCATE TABLE \"{tableName}\" RESTART IDENTITY CASCADE;");
        }

        var truncateAllQuery = string.Join('\n', truncateQueries);

        await dbContext.Database.ExecuteSqlRawAsync(truncateAllQuery).ConfigureAwait(false);
        _logger.LogInformation("PostgreSQL database tables purged successfully");
    }

    private T? GetCustomDatabaseOption<T>(ICollection<CustomDatabaseOption>? options, string key, Func<string, T> converter, Func<T>? defaultValue = null)
    {
        if (options is null)
        {
            return defaultValue is not null ? defaultValue() : default;
        }

        var value = options.FirstOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (value is null)
        {
            return defaultValue is not null ? defaultValue() : default;
        }

        return converter(value.Value);
    }

    private NpgsqlConnectionStringBuilder GetConnectionBuilder(ICollection<CustomDatabaseOption>? options)
    {
        var includeErrorDetail = GetCustomDatabaseOption(options, "IncludeErrorDetail", e => e.Equals(bool.TrueString, StringComparison.OrdinalIgnoreCase), () => false);
        var logParameters = GetCustomDatabaseOption(options, "LogParameters", e => e.Equals(bool.TrueString, StringComparison.OrdinalIgnoreCase), () => false);
        var configuredConnectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
            ?? Environment.GetEnvironmentVariable("JELLYFIN_POSTGRES_CONNECTION_STRING");

        NpgsqlConnectionStringBuilder connectionBuilder;
        if (!string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            try
            {
                connectionBuilder = new NpgsqlConnectionStringBuilder(configuredConnectionString);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException("POSTGRES_CONNECTION_STRING is not a valid Npgsql connection string", ex);
            }
        }
        else
        {
            connectionBuilder = new NpgsqlConnectionStringBuilder
            {
                Host = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "jellyfin",
                Port = int.Parse(Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? "5432", CultureInfo.InvariantCulture),
                Database = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "jellyfin",
                Username = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "jellyfin",
                Password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? throw new InvalidOperationException("PostgreSQL password must be provided via POSTGRES_PASSWORD environment variable")
            };
        }

        var commandTimeout = Environment.GetEnvironmentVariable("POSTGRES_COMMAND_TIMEOUT");
        if (commandTimeout is not null)
        {
            connectionBuilder.CommandTimeout = int.Parse(commandTimeout, CultureInfo.InvariantCulture);
        }

        if (includeErrorDetail)
        {
            connectionBuilder.IncludeErrorDetail = includeErrorDetail;
        }

        if (logParameters)
        {
            connectionBuilder.LogParameters = logParameters;
        }

        // Log the full connection string without password
        var safeConnectionString = new NpgsqlConnectionStringBuilder(connectionBuilder.ToString())
        {
            Password = null
        }.ToString();

        _logger.LogInformation("PostgreSQL connection string: {ConnectionString}", safeConnectionString);

        return connectionBuilder;
    }
}
