#!/usr/bin/env bash
# Makes a garment looser without Alt+S's armpit fold (anim_scripts/inflate_garment.py):
#   ./inflate_garment.sh <in.glb> <out.glb> <offset> [steps] [smooth]
# offset: how far out, in the file's own units. steps: how many pushes it goes out in, each followed by a smoothing pass
# (default 10). smooth: how far each pass pulls a vertex to the middle of its neighbours, 0 to 1 (default 0.5).
set -euo pipefail

BLENDER=/home/t/Programs/blender-5.2.3-linux-x64/blender

if [ $# -lt 3 ]; then
    sed -n '2,5p' "$0" | sed 's/^# \?//'
    exit 1
fi

# The paths are made absolute before moving to the mod's folder, so they can be given from anywhere.
in=$(realpath "$1")
out=$(realpath -m "$2")
shift 2
cd "$(dirname "$0")"

"$BLENDER" -b --python anim_scripts/inflate_garment.py -- "$in" "$out" "$@"
