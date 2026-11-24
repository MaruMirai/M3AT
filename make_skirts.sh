#!/bin/bash
# Makes the skirts and dresses from Maru's hand-made skirts in Models/bases/skirts/ (each a closed skirt from a ring at the
# waist to the hem, saved without the rig): each weighted from make_duster.py's skirt of its length and written to
# Models/garments/ as its skirt, and with the dress top joined on as its dress (anim_scripts/reweight_skirt.py). Only the
# lengths named, or all: ./make_skirts.sh [mini|short|knee|midi|long ...]. Then ./export_model.sh takes them to the game.
# A sleeveless set too, from Models/bases/skirts/dresstop_sleeveless.glb (SleevelessDress...).
# The dress top is Models/bases/skirts/dresstop.glb if there (Maru's V neck, anim_scripts/extract_dress_top.py), else cut
# from the T-shirt's group along the ring.
# A new length is a skirt in Models/bases/skirts/ and a line below (and its names in export_frames.py's SKIRTS and
# DRESSES).
set -e
cd "$(dirname "$0")"
BLENDER=/home/t/Programs/blender-5.2.3-linux-x64/blender
# length: master file, skirt garment, dress garment
declare -A master=([mini]=skirtmini [short]=skirt [knee]=skirtknee [midi]=skirtmidi [long]=skirtlong)
declare -A skirt=([mini]=SkirtMini [short]=Skirt [knee]=SkirtMedium [midi]=SkirtMidi [long]=SkirtLong)
declare -A dress=([mini]=DressMini [short]=DressShort [knee]=Dress [midi]=DressMidi [long]=DressLong)
declare -A sleeveless=([mini]=SleevelessDressMini [short]=SleevelessDressShort [knee]=SleevelessDress [midi]=SleevelessDressMidi [long]=SleevelessDressLong)
top=()
[ -f Models/bases/skirts/dresstop.glb ] && top=(--dress-top Models/bases/skirts/dresstop.glb)
lengths=("$@")
[ ${#lengths[@]} -eq 0 ] && lengths=(mini short knee midi long)
for length in "${lengths[@]}"; do
    [ -z "${master[$length]}" ] && { echo "make_skirts.sh: no length $length" >&2; exit 1; }
    "$BLENDER" -b Models/pawnHumanRig.blend --python anim_scripts/reweight_skirt.py -- \
        "Models/bases/skirts/${master[$length]}.glb" "Models/garments/${skirt[$length]}.glb" --name "${skirt[$length]}" \
        --dress "Models/garments/${dress[$length]}.glb" --dress-name "${dress[$length]}" "${top[@]}" 2>&1 \
        | grep -E "^(REFERENCE|SKIRT|DRESS|GLB)|Error|rror:|reweight_skirt" || true
    # The sleeveless dresses: the same skirt with Maru's sleeveless top (anim_scripts/extract_dress_top.py, out of
    # Models/unused/sleevelessdress.blend's Dress).
    if [ -f Models/bases/skirts/dresstop_sleeveless.glb ]; then
        "$BLENDER" -b Models/pawnHumanRig.blend --python anim_scripts/reweight_skirt.py -- \
            "Models/bases/skirts/${master[$length]}.glb" "Models/garments/${skirt[$length]}.glb" --name "${skirt[$length]}" \
            --dress "Models/garments/${sleeveless[$length]}.glb" --dress-name "${sleeveless[$length]}" \
            --dress-top Models/bases/skirts/dresstop_sleeveless.glb 2>&1 | grep -E "^(DRESS)|Error|rror:|reweight_skirt" || true
    fi
done
# UVs of their own and a painted fabric texture (anim_scripts/paint_cloth.py: hems, folds, a waistband or binding),
# which the game also projects another mod's dress or skirt art into (Source/SpriteClothes.cs).
painted=()
for length in "${lengths[@]}"; do
    painted+=("Models/garments/${skirt[$length]}.glb" "Models/garments/${dress[$length]}.glb")
    [ -f Models/bases/skirts/dresstop_sleeveless.glb ] && painted+=("Models/garments/${sleeveless[$length]}.glb")
done
"$BLENDER" -b Models/pawnHumanRig.blend --python anim_scripts/paint_cloth.py -- "${painted[@]}" --keep "" 2>&1 \
    | grep -E "^PAINT|Error|rror:" || true
