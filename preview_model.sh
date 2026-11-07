#!/usr/bin/env bash
# Opens Models/pawnHumanRig.blend in Blender with the live pose preview (anim_scripts/preview.py): the "Pose" tab in the
# 3D view's sidebar (N). Pick a clip (walk, idle, drafted, carry, sit, lie, crawl, limp, wade, swim, tread, jump, work), tune walk.py's numbers there,
# "Write to walk.py", then ./export_model.sh for the game.
set -euo pipefail

BLENDER=/home/t/Programs/blender-5.2.3-linux-x64/blender
cd "$(dirname "$0")"

"$BLENDER" Models/pawnHumanRig.blend --python anim_scripts/preview.py
