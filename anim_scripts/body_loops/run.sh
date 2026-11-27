#!/usr/bin/env bash
# Remakes the body with edge loops round the joints (anim_scripts/body_loops/, dev_notes/garment_scripts.md "The loop
# body"). Reads Models/pawnHumanRig.blend (the current Body and the bones) and Models/unused/chibifullpoly.blend (the
# sculpt); writes everything into a work folder and the result as <work>/loopbody.blend: a copy of the rig file with
# the new Body (the old one kept as Body_old, a quad copy as Body_quads, neither exported). Never touches the rig file.
#   anim_scripts/body_loops/run.sh <work folder> [--checks]
# PY must have numpy, scipy, trimesh, rtree and pillow (e.g. python3 -m venv v && v/bin/pip install numpy scipy trimesh rtree pillow).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"; HERE="$ROOT/anim_scripts/body_loops"
BLENDER=/home/t/Programs/blender-5.2.3-linux-x64/blender
PY="${PY:-python3}"
WORK="$(mkdir -p "$1" && cd "$1" && pwd)"; shift || true
cd "$WORK"
"$BLENDER" -b "$ROOT/Models/pawnHumanRig.blend" --python "$HERE/dump.py" -- Body "$WORK/body.npz" | grep DUMPED
"$BLENDER" -b "$ROOT/Models/pawnHumanRig.blend" --python "$HERE/dump.py" -- Head "$WORK/head.npz" | grep DUMPED
"$BLENDER" -b "$ROOT/Models/unused/chibifullpoly.blend" --python "$HERE/dump.py" -- Object_2 "$WORK/full.npz" | grep DUMPED
"$BLENDER" -b "$ROOT/Models/pawnHumanRig.blend" --python "$HERE/bones.py" -- "$WORK/bones.json" > /dev/null
"$PY" "$HERE/target.py"
"$PY" "$HERE/gen.py" | tail -1
"$PY" "$HERE/relax.py" | tail -2
"$PY" - <<'PYEOF'
import numpy as np, json
g = np.load('newbody.npz', allow_pickle=True)
F = [[int(i) for i in f if i >= 0] for f in g['F']]
# each quad turned so its chosen diagonal runs from its first corner to its third (build_body.py triangulates FIXED)
F = [f[1:] + f[:1] if len(f) == 4 and d == 1 else f for f, d in zip(F, g['diag'])]
json.dump({'V': g['V'].tolist(), 'F': F, 'names': [str(n) for n in g['names']]}, open('newbody.json', 'w'))
PYEOF
"$BLENDER" -b "$ROOT/Models/pawnHumanRig.blend" --python "$HERE/build_body.py" -- "$WORK/newbody.json" "$WORK/loopbody.blend" | grep BUILD
# the body's UV atlas as the rig's (Pants and LongJohns read it), in place of build_body.py's smart projection
"$BLENDER" -b "$WORK/loopbody.blend" --python "$ROOT/anim_scripts/unwrap_pawn.py" -- "$WORK/uv_report" --save | grep -E "UNWRAP (Body|Head|saved)"
"$PY" "$HERE/metric.py"
if [ "${1:-}" = "--checks" ]; then
    "$PY" "$HERE/view.py"; "$PY" "$HERE/errmap.py"
    "$BLENDER" -b "$ROOT/Models/pawnHumanRig.blend" --python "$HERE/pose_test.py" -- "$WORK/pose_old.npz" | grep POSED
    "$BLENDER" -b "$WORK/loopbody.blend" --python "$HERE/pose_test.py" -- "$WORK/pose_new.npz" | grep POSED
    "$PY" "$HERE/posecmp3.py"
    echo "pictures: $WORK/nb_*.png (views, hands, feet, error map), $WORK/pj_*.png (old | new in each pose)"
fi
