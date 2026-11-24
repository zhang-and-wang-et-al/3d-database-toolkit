#!/bin/bash
# Linux launcher: ./start_toolkit.sh      (first start sets up .venv)
cd "$(dirname "$(readlink -f "$0")")" || exit 1
PY=""
# prefer a Python that has Tkinter (needed for the window), fall back to any Python 3.9+
for need_tk in 1 0; do
    CHECK="import sys; sys.exit(sys.version_info < (3, 9))"
    [ "$need_tk" = 1 ] && CHECK="$CHECK; import tkinter"
    for c in python3.13 python3.12 python3.11 python3.10 python3; do
        if command -v "$c" >/dev/null 2>&1 && "$c" -c "$CHECK" 2>/dev/null; then PY="$c"; break 2; fi
    done
done
if [ -z "$PY" ]; then
    echo "Python 3.9+ not found. Install it, e.g.: sudo apt install python3 python3-venv python3-tk"
    exit 1
fi
exec "$PY" setup_env.py --run gui
