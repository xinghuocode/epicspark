#!/usr/bin/env sh
# EpicSpark 局域网展示端（Linux / macOS）
set -e
cd "$(dirname "$0")"
exec python3 epicspark_viewer.py "$@"
