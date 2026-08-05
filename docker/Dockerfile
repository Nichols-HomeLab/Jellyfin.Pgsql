# Build stage
# Context hint: project root ../
# sudo docker build -t ghcr.io/jpvenson/jellyfin.pgsql:10.11.0-1 -f ./Dockerfile ../
FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
WORKDIR /src

# Copy project files
COPY jellyfin.ruleset .
COPY Jellyfin.Plugin.Pgsql.sln .
COPY build.yaml .
COPY Jellyfin.Plugin.Pgsql/ ./Jellyfin.Plugin.Pgsql/
COPY jellyfin/ ./jellyfin/

# Restore and publish the provider, then build the patched Jellyfin query
# generator from the pinned Jellyfin fork.
RUN dotnet restore Jellyfin.Plugin.Pgsql.sln
RUN dotnet restore jellyfin/Jellyfin.Server.Implementations/Jellyfin.Server.Implementations.csproj
RUN dotnet publish Jellyfin.Plugin.Pgsql/Jellyfin.Plugin.Pgsql.csproj -c Release --no-restore -o /app/publish
RUN dotnet build jellyfin/Jellyfin.Server.Implementations/Jellyfin.Server.Implementations.csproj \
    -c Release --no-restore -o /app/jellyfin-server

# Final stage - Jellyfin with plugin
FROM jellyfin/jellyfin:10.11.11

# Install PostgreSQL 18 client tools for backup/restore functionality
RUN apt-get update && \
    apt-get install -y wget ca-certificates gnupg lsb-release && \
    wget --quiet -O - https://www.postgresql.org/media/keys/ACCC4CF8.asc | gpg --dearmor -o /usr/share/keyrings/postgresql-keyring.gpg && \
    echo "deb [signed-by=/usr/share/keyrings/postgresql-keyring.gpg] http://apt.postgresql.org/pub/repos/apt/ $(lsb_release -cs)-pgdg main" > /etc/apt/sources.list.d/pgdg.list && \
    apt-get update && \
    apt-get install -y postgresql-client-18 xmlstarlet util-linux && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

# Copy the published plugin and config files
COPY --from=build /app/publish/ /jellyfin-pgsql/plugin/
# Replace only the server implementation assembly. The fork is pinned to the
# exact Jellyfin base-image version, so all other server binaries stay stock.
COPY --from=build /app/jellyfin-server/Jellyfin.Server.Implementations.dll /jellyfin/Jellyfin.Server.Implementations.dll
COPY docker/entrypoint.sh /entrypoint.sh
COPY docker/database.xml /jellyfin-pgsql/database.xml
# COPY docker/jellyfindb.load /jellyfin-pgsql/jellyfindb.load
# COPY docker/jellyfin.PgsqlMigrator.dll /jellyfin-pgsql/jellyfin.PgsqlMigrator.dll

# Make entrypoint executable
RUN chmod +x /entrypoint.sh

ENTRYPOINT ["/entrypoint.sh"]
