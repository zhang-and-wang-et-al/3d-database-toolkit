#!/bin/bash
# Command line version (Linux/macOS), e.g.:  ./showcase_batch.sh /mnt/share/models --preset quick --jobs 4
HERE="$(cd "$(dirname "$0")" && pwd)"
PY="$(command -v python3)"
[ -z "$PY" ] && { echo "python3 not found"; exit 1; }
exec "$PY" "$HERE/setup_env.py" --run batch -- "$@"
