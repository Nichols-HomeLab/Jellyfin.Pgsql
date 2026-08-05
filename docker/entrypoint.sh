#!/bin/bash

set -euo pipefail

# Multiple HA replicas share /config and can start concurrently. Serialize the
# plugin/config initialization and replace the plugin directory atomically so a
# peer can never observe a partially copied provider.
mkdir -p /config/plugins /config/config
exec 9>/config/.pgsql-init.lock
flock -x 9

plugin_payload_hash() {
    (
        cd "$1"
        find . -type f -print0 | sort -z | xargs -0 sha256sum | sha256sum | cut -d ' ' -f 1
    )
}

SourcePluginHash="$(plugin_payload_hash /jellyfin-pgsql/plugin)"
InstalledPluginHash=""
if [ -d /config/plugins/PostgreSQL ]; then
    InstalledPluginHash="$(plugin_payload_hash /config/plugins/PostgreSQL)"
fi

if [ "${SourcePluginHash}" != "${InstalledPluginHash}" ]; then
    PluginStage="$(mktemp -d /config/plugins/.PostgreSQL.XXXXXX)"
    PluginBackup="/config/plugins/.PostgreSQL.previous.$$"
    trap 'rm -rf "${PluginStage:-}" "${PluginBackup:-}"' EXIT
    cp -a /jellyfin-pgsql/plugin/. "${PluginStage}/"

    if [ -d /config/plugins/PostgreSQL ]; then
        mv /config/plugins/PostgreSQL "${PluginBackup}"
    fi

    mv "${PluginStage}" /config/plugins/PostgreSQL
    if [ -d "${PluginBackup}" ]; then
        rm -rf "${PluginBackup}"
    fi
    trap - EXIT
fi

# Create database.xml if it doesn't exist
if [ ! -f /config/config/database.xml ]; then
    cp /jellyfin-pgsql/database.xml /config/config/database.xml
fi

# Check database.xml correctly configured
ConfiguredPluginName="$(xmlstarlet select -t -m '//DatabaseConfigurationOptions/CustomProviderOptions/PluginName' -v . -n /config/config/database.xml)"
if [ "${ConfiguredPluginName}" != "PostgreSQL" ]; then
    echo "Plugin name is not set to PostgreSQL. abort."
    exit 2;
fi

# Prefer one Npgsql connection string. Keep the legacy variables as a fallback
# for compatibility with existing deployments.
ConnectionString="${POSTGRES_CONNECTION_STRING:-${JELLYFIN_POSTGRES_CONNECTION_STRING:-}}"
if [ -z "${ConnectionString}" ]; then
    if [ -z "${POSTGRES_HOST:-}" ] || [ -z "${POSTGRES_USER:-}" ] || [ -z "${POSTGRES_PASSWORD:-}" ]; then
        echo "PostgreSQL connection is unset. Set POSTGRES_CONNECTION_STRING or the legacy POSTGRES_HOST, POSTGRES_PORT, POSTGRES_DB, POSTGRES_USER, and POSTGRES_PASSWORD variables."
        exit 3
    fi

    ConnectionString="Password=${POSTGRES_PASSWORD};User ID=${POSTGRES_USER};Host=${POSTGRES_HOST};Port=${POSTGRES_PORT:-5432};Database=${POSTGRES_DB:-jellyfin}"

    if [ -n "${POSTGRES_SSLMODE:-}" ]; then
        ConnectionString="${ConnectionString};SSL Mode=${POSTGRES_SSLMODE}"
    fi

    if [ -n "${POSTGRES_TRUSTSERVERCERTIFICATE:-}" ]; then
        ConnectionString="${ConnectionString};Trust Server Certificate=${POSTGRES_TRUSTSERVERCERTIFICATE}"
    fi
fi

# Update database.xml with connection string
xmlstarlet edit -L -u '//DatabaseConfigurationOptions/CustomProviderOptions/ConnectionString' -v "${ConnectionString}" /config/config/database.xml

# Release the shared initialization lock before starting the long-running
# Jellyfin process. The descriptor must not be inherited by Jellyfin.
flock -u 9
exec 9>&-

# Migrate jellyfin.db if exists
# if [ ! -f /config/data/jellyfin.db ]; then

#     # run the EFbundle to migrate db to current state
#     dotnet run /jellyfin-pgsql/jellyfin.PgsqlMigrator.dll --connection "${ConnectionString}"
#     # run pgloader to move data
#     pgloader /jellyfin-pgsql/jellyfindb.load
#     # rename jellyfin db
#     mv /config/data/jellyfin.db /config/data/jellyfin.db.pgsql
# fi


# Run original Jellyfin entrypoint
exec /jellyfin/jellyfin "$@"
