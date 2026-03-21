#!/usr/bin/env bash

set -euo pipefail

root_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
migrations_dir="$root_dir/OCPP.Core.Database/Migrations"
snapshot="$migrations_dir/OCPPCoreContextModelSnapshot.cs"
latest_designer="$(find "$migrations_dir" -maxdepth 1 -type f -name '*.Designer.cs' | sort | tail -n 1)"
pattern='HasColumnType\("(TEXT|INTEGER|REAL)"\)'

if [[ ! -f "$snapshot" ]]; then
  echo "Missing snapshot file: $snapshot" >&2
  exit 1
fi

if [[ -z "$latest_designer" || ! -f "$latest_designer" ]]; then
  echo "Could not find a migration designer file under $migrations_dir" >&2
  exit 1
fi

check_file() {
  local file="$1"

  if grep -nE "$pattern" "$file" >/dev/null; then
    echo "SQLite-shaped migration metadata detected in $file" >&2
    grep -nE "$pattern" "$file" >&2
    exit 1
  fi
}

check_file "$snapshot"
check_file "$latest_designer"

echo "Migration metadata looks SQL Server-shaped."
