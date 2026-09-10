# Build the provider and matching patched server from the pinned submodule.
FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:4ea6fe75dd36706bb6d8c3c293d4c4315840f5d76ea28ac97def77e3ec487fa5 AS build
WORKDIR /src
COPY jellyfin.ruleset Jellyfin.Plugin.Pgsql.sln build.yaml ./
COPY Jellyfin.Plugin.Pgsql/ ./Jellyfin.Plugin.Pgsql/
COPY jellyfin/ ./jellyfin/
RUN dotnet publish Jellyfin.Plugin.Pgsql/Jellyfin.Plugin.Pgsql.csproj \
    -c Release -o /app/plugin
RUN mkdir /app/provider && cp /app/plugin/Jellyfin.Plugin.Pgsql.dll /app/plugin/Npgsql*.dll /app/provider/
RUN dotnet publish jellyfin/Jellyfin.Server/Jellyfin.Server.csproj \
    -c Release -r linux-x64 --self-contained true -o /app/server

# CI-only target shares the exact published server with the production image.
FROM build AS tests
COPY tests/ ./tests/
COPY docker/ ./docker/
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 wget ca-certificates gnupg lsb-release \
    && wget --quiet -O - https://www.postgresql.org/media/keys/ACCC4CF8.asc | gpg --dearmor -o /usr/share/keyrings/postgresql-keyring.gpg
RUN echo "deb [signed-by=/usr/share/keyrings/postgresql-keyring.gpg] https://apt.postgresql.org/pub/repos/apt/ $(lsb_release -cs)-pgdg main" > /etc/apt/sources.list.d/pgdg.list \
    && apt-get update && apt-get install -y --no-install-recommends postgresql-client-18 pgloader \
    && rm -rf /var/lib/apt/lists/*
RUN dotnet build tests/Jellyfin.Plugin.Pgsql.Tests/Jellyfin.Plugin.Pgsql.Tests.csproj -c Release
RUN python3 -m unittest discover -s tests -p 'test_*.py'
ENV JELLYFIN_TEST_SERVER_DLL=/app/server/jellyfin.dll
ENV JELLYFIN_TEST_PGLOADER=/usr/bin/pgloader
ENTRYPOINT ["dotnet", "test", "tests/Jellyfin.Plugin.Pgsql.Tests/Jellyfin.Plugin.Pgsql.Tests.csproj", "-c", "Release", "--no-build", "--logger", "console;verbosity=normal"]

# Final stage - Jellyfin with plugin
FROM jellyfin/jellyfin:12.0@sha256:baba630419915985442f315f08b0cf46d9f4c8a0cc4bd38e94a6d35751dd5ef5

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
COPY --from=build /app/provider/ /jellyfin-pgsql/plugin/
# Keep all server assemblies and the runtime aligned with the pinned fork.
# The official v12 base supplies its matching web client and FFmpeg.
COPY --from=build /app/server/ /jellyfin/
COPY docker/entrypoint.sh /entrypoint.sh
COPY docker/database.xml /jellyfin-pgsql/database.xml
# COPY docker/jellyfindb.load /jellyfin-pgsql/jellyfindb.load
# COPY docker/jellyfin.PgsqlMigrator.dll /jellyfin-pgsql/jellyfin.PgsqlMigrator.dll

# Make entrypoint executable
RUN chmod +x /entrypoint.sh

ENTRYPOINT ["/entrypoint.sh"]
