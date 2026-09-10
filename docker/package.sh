#!/usr/bin/env bash
# Package the complete Docker build context, including every provider migration.
set -euo pipefail
repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
package_path="${1:-$PWD/jellyfin-pgsql-plugin.tar.gz}"
staging_dir="$(mktemp -d)"
trap 'rm -rf "$staging_dir"' EXIT
git -C "$repo_root" submodule update --init jellyfin
git -C "$repo_root" archive HEAD | tar -x -C "$staging_dir"
mkdir -p "$staging_dir/jellyfin"
git -C "$repo_root/jellyfin" archive HEAD | tar -x -C "$staging_dir/jellyfin"
tar -czf "$package_path" -C "$staging_dir" .
echo "Created $package_path from committed provider and server sources."
echo "Extract and run: docker build --platform linux/amd64 -f docker/Dockerfile -t jellyfin-pgsql:12.0-local ."
