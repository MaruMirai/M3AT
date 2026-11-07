#!/usr/bin/env bash
# Sets the face placement, re-exports the mod's Models/chibi (MOD) and rebuilds the mod.
# Edit the four numbers below, then run: ./apply_face.sh
set -euo pipefail

FACE_SPAN=3.0      # the face texture's width and height, in head heights (bigger = larger face features)
FACE_EYES=0.31     # the eye line's height above the chin, in head heights
MOUTH_ZOOM=1       # how much the mouth is enlarged round itself
MOUTH_RAISE=0.012  # how far up the face the mouth is moved, in texture widths

BLENDER=/home/t/Programs/blender-5.2.3-linux-x64/blender
cd "$(dirname "$0")"
MOD="M3AT: Maru's 3D Animated Toons"   # the mod as the game loads it; the rest of this folder is tools

sed -i -E "s/^FACE_SPAN = [0-9.]+ */FACE_SPAN = $FACE_SPAN         /; s/^FACE_EYES = [0-9.]+ */FACE_EYES = $FACE_EYES         /" anim_scripts/export_frames.py
sed -i -E "s/(MouthZoom = )[0-9.]+f/\1${MOUTH_ZOOM}f/; s/(MouthRaise = )[0-9.]+f/\1${MOUTH_RAISE}f/" Source/FaceLayers.cs

"$BLENDER" -b Models/chibirigged.blend --python anim_scripts/export_frames.py -- "$MOD/Models/chibi" | tail -2
dotnet build Source/M3AT.csproj | grep -E "Error\(s\)|Warning\(s\)"
