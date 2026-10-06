FROM mcr.microsoft.com/devcontainers/dotnet:10.0-noble@sha256:9eb314432a5fa67773a53ab754cdc381cae614906162f9fc44cdb7f7837b91c7

# Configure inner Docker daemon to use the host DNS relay (10.255.255.254) instead of
# defaulting to 8.8.8.8/8.8.4.4, which is required when external DNS is blocked on the network.
RUN mkdir -p /etc/docker && echo '{"dns":["10.255.255.254"]}' > /etc/docker/daemon.json

# [Optional] Uncomment this section to install additional OS packages.
# RUN apt-get update && export DEBIAN_FRONTEND=noninteractive \
#     && apt-get -y install --no-install-recommends <your-package-list-here>

# [Optional] Uncomment this line to install global node packages.
# RUN su vscode -c "source /usr/local/share/nvm/nvm.sh && npm install -g <your-package-here>" 2>&1
