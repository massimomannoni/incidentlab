#!/usr/bin/env bash
set -euo pipefail
lab_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
if [[ ! -f "$lab_root/tools/dist/cli.js" ]]; then
  echo 'Build tools first: npm --prefix tools ci && npm --prefix tools run build' >&2
  exit 1
fi
exec node "$lab_root/tools/dist/cli.js" "$@"
