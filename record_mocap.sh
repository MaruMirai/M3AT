#!/usr/bin/env bash
# Records Vive tracker and wand takes from SteamVR (anim_scripts/mocap/record.py), one CSV per take, into
# animDLs/mocap/<date>/ or the folder given. SteamVR must be running with the trackers and wands on.
# Hold both wands together above your head for a second to start or stop a take, or with the wands on a tool hold
# them above your head and still for a second and a half (or press Enter);
# n <clip> names the takes, c checks which tracker is which, s shows tracking, q quits.
# It runs in its own Python environment (anim_scripts/mocap/.venv, made on first run with openvr and numpy),
# so it doesn't matter which python3 a terminal finds.
set -euo pipefail

cd "$(dirname "$0")"

VENV=anim_scripts/mocap/.venv
if [ ! -x "$VENV/bin/python" ]; then
    echo "Setting up $VENV (once)..."
    python3 -m venv "$VENV"
    "$VENV/bin/pip" install --quiet openvr numpy
fi

if ! pgrep -x vrserver > /dev/null; then
    echo "SteamVR isn't running: start it and switch the trackers and wands on first."
    exit 1
fi

exec "$VENV/bin/python" anim_scripts/mocap/record.py "$@"
