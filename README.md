# PostgreSQL adapter for Jellyfin 12

This fork runs Jellyfin with a PostgreSQL database provider and supports manual
transfer of an existing SQLite library using pgloader. It includes the matching
patched Jellyfin server from the pinned `jellyfin` submodule. The container uses
.NET 10 and the official Jellyfin 12 web client and FFmpeg.

The provider is experimental. Back up the complete Jellyfin configuration/data
directory and database before changing versions or database providers. Keep the
original SQLite instance stopped and intact until the migrated library has been
verified.

## Run

The Gitea workflow publishes
`git.nicholstech.org/nichols-homelab/jellyfin.pgsql`. Select a versioned image tag
or immutable digest from the successful build rather than relying on `latest`
for upgrades.

```yaml
services:
  jellyfin:
    image: git.nicholstech.org/nichols-homelab/jellyfin.pgsql:latest
    ports:
      - "8096:8096"
    volumes:
      - /path/to/config:/config
      - /path/to/cache:/cache
      - /path/to/media:/media:ro
    environment:
      POSTGRES_CONNECTION_STRING: Host=postgres;Port=5432;Database=jellyfin;Username=jellyfin;Password=change-me
```

Supply a reachable PostgreSQL server and an existing database owned by the
configured user. The container installs the provider and updates
`/config/config/database.xml` before starting Jellyfin. It does not automatically
copy an existing SQLite database to PostgreSQL.

`POSTGRES_CONNECTION_STRING` takes precedence over its alias
`JELLYFIN_POSTGRES_CONNECTION_STRING`. Legacy `POSTGRES_HOST`, `POSTGRES_PORT`
(default `5432`), `POSTGRES_DB` (default `jellyfin`), `POSTGRES_USER`, and
`POSTGRES_PASSWORD` remain supported. With the legacy variables,
`POSTGRES_SSLMODE` and `POSTGRES_TRUSTSERVERCERTIFICATE` are optional.
Include SSL, pooling, command timeout, and session options directly in a full
connection string. `POSTGRES_COMMAND_TIMEOUT` also configures the provider's
command timeout in seconds (default 30; zero means no limit).

The fork retains PostgreSQL indexes for `MediaSegments`, `BaseItems`, and
`UserData`, along with the companion server's PostgreSQL query optimizations.
The provider and companion server must be upgraded together. For an existing
PostgreSQL installation, first upgrade older provider versions to this fork's
10.11.11 release, including migrations through
`20260802113543_AddQueryGeneratorSupport`, before moving to v12. Older provider
schemas have different schema/code-migration ordering and are not a supported
direct upgrade source.

## Build

Use the .NET 10 SDK. Initialize only the top-level server submodule; nested
server plugin submodules are not needed.

```bash
git submodule update --init jellyfin
dotnet tool restore
dotnet build Jellyfin.Plugin.Pgsql.sln -c Release
bash docker/build.sh jellyfin-pgsql:12.0-local
```

The Docker build currently targets `linux/amd64`. It publishes the complete
pinned server and packages the provider with its Npgsql dependencies. The
Jellyfin 12 base image supplies the matching official web client.

For a local checkout of the matching companion server outside the submodule,
pass `-p:JellyfinSourceRoot=/absolute/path/to/jellyfin` to dotnet commands.
`bash docker/package.sh /path/to/package.tar.gz` packages committed provider
and pinned server sources as a complete Docker build context.

For a manual installation, copy `Jellyfin.Plugin.Pgsql.dll` and its Npgsql
assemblies from the publish output into `plugins/PostgreSQL` under the matching
server's data directory. Do not copy shared Jellyfin or Microsoft assemblies
into the plugin directory. Configure `database.xml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<DatabaseConfigurationOptions xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <DatabaseType>PLUGIN_PROVIDER</DatabaseType>
  <CustomProviderOptions>
    <PluginAssembly>Jellyfin.Plugin.Pgsql.dll</PluginAssembly>
    <PluginName>PostgreSQL</PluginName>
    <ConnectionString>Host=localhost;Database=jellyfin;Username=jellyfin;Password=change-me</ConnectionString>
  </CustomProviderOptions>
  <LockingBehavior>NoLock</LockingBehavior>
</DatabaseConfigurationOptions>
```

## Manual SQLite transfer

1. Back up the existing database and the entire Jellyfin configuration/data
   directory. Upgrade a copy of the SQLite instance to Jellyfin 12 and let its
   migrations finish. Stop that instance before copying its database; the source
   SQLite schema must match the target Jellyfin 12 schema.
2. Start this adapter with a separate, empty configuration directory and an
   empty PostgreSQL database. Allow Jellyfin to initialize the PostgreSQL schema,
   then stop it. Do not point the initial setup at the existing SQLite data.
3. Install pgloader. Adapt [docker/jellyfindb.load](docker/jellyfindb.load) to
   the stopped Jellyfin 12 SQLite database and the initialized PostgreSQL
   database. This replaces data in the target; use only the disposable target
   initialized in step 2. Run `pgloader /path/to/jellyfindb.load` and require
   zero errors in its summary, including index and foreign-key recreation.
   Preserve the provider's
   PostgreSQL schema and `__EFMigrationsHistory`; SQLite migration history is
   not interchangeable with PostgreSQL history. The load file excludes both
   SQLite migration tables and explicitly resets PostgreSQL identity sequences
   after copying data; pgloader's built-in reset alone misses these identities.
4. Export the source's completed **server code** migrations, then apply them to
   the target using PostgreSQL's `psql` client. Run from this checkout with its
   pinned `jellyfin` submodule initialized:

   ```bash
   python3 docker/export-code-migrations.py /path/to/upgraded/jellyfin.db > code-migrations.sql
   psql -v ON_ERROR_STOP=1 -f code-migrations.sql
   ```

   Configure `PGHOST`, `PGPORT`, `PGUSER`, `PGDATABASE`, and password credentials
   for the disposable target before invoking `psql`. The export validates the
   v12 source schema and foreign keys, preserves PostgreSQL schema migration
   rows, and replaces only server code-migration state with the source state.
5. Restore the matching upgraded Jellyfin 12 configuration/data files to the
   target instance, preserving the target PostgreSQL provider configuration and
   connection string. Keep the source's completed code-migration state with its
   data. Do not pair older configuration/migration state with converted v12 data.
6. Start the adapter and verify users, libraries, metadata, play state, and media
   playback before retiring the original instance. Keep backups for rollback;
   running an older Jellyfin binary against the upgraded database is unsupported.

## Provider migrations

After updating the pinned server, compare its database model with the provider
and generate the corresponding PostgreSQL migration:

```bash
dotnet tool restore
dotnet ef migrations add MigrationName --project Jellyfin.Plugin.Pgsql -- --migration-provider Jellyfin-PgSql
```

Validate both a fresh database and an upgrade from the previous provider schema.
The container's normal Jellyfin startup applies provider migrations; a separate
EF migration bundle is not part of the container startup flow.
