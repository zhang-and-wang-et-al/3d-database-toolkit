#!/bin/bash
# Double-click in Finder to start 3D Database Toolkit (first start sets up .venv).
cd "$(dirname "$0")" || exit 1
PY=""
# prefer a Python that has Tkinter (needed for the window), fall back to any Python 3.9+
for need_tk in 1 0; do
    CHECK="import sys; sys.exit(sys.version_info < (3, 9))"
    [ "$need_tk" = 1 ] && CHECK="$CHECK; import tkinter"
    for c in python3.13 python3.12 python3.11 python3.10 python3 /usr/local/bin/python3 /opt/homebrew/bin/python3 /usr/bin/python3; do
        if command -v "$c" >/dev/null 2>&1 && "$c" -c "$CHECK" 2>/dev/null; then PY="$c"; break 2; fi
    done
done
if [ -z "$PY" ]; then
    echo "Python 3.9+ not found. Please install it from https://www.python.org/downloads/ and start again."
    open "https://www.python.org/downloads/"
    read -r -p "Press Enter to close" _
    exit 1
fi
"$PY" setup_env.py --run gui || read -r -p "Press Enter to close" _
