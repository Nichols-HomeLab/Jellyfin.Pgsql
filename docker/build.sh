#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
git submodule update --init jellyfin
docker build --platform linux/amd64 -f docker/Dockerfile -t "${1:-jellyfin-pgsql:12.0-local}" .
