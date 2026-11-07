#!/usr/bin/env bash
# Copies Models/pawnHumanRig.blend into ~/Backups/pawnHumanRig/ with the time it was saved in its name, if it has
# changed since the newest copy there. The .blend is in git only as each commit has it, so these copies are its history between commits.
# Run by the systemd user timer pawn-rig-backup.timer (./backup_rig.sh --install sets it up, every 10 minutes);
# run by hand to take a copy now. Keeps the newest KEEP copies.
set -euo pipefail

cd "$(dirname "$0")"
SRC="$PWD/Models/pawnHumanRig.blend"
DEST="${BACKUP_DIR:-$HOME/Backups/pawnHumanRig}"
KEEP=500

if [[ "${1:-}" == "--install" ]]; then
    UNIT="$HOME/.config/systemd/user"
    mkdir -p "$UNIT"
    cat > "$UNIT/pawn-rig-backup.service" <<EOF
[Unit]
Description=Back up pawnHumanRig.blend

[Service]
Type=oneshot
ExecStart=/usr/bin/bash "$PWD/backup_rig.sh"
EOF
    cat > "$UNIT/pawn-rig-backup.timer" <<EOF
[Unit]
Description=Back up pawnHumanRig.blend every 10 minutes

[Timer]
OnBootSec=2min
OnUnitActiveSec=10min
Persistent=true

[Install]
WantedBy=timers.target
EOF
    systemctl --user daemon-reload
    systemctl --user enable pawn-rig-backup.timer
    systemctl --user restart pawn-rig-backup.timer
    echo "Installed: backups go to $DEST"
    exit 0
fi

mkdir -p "$DEST"
[[ -f "$SRC" ]] || { echo "No $SRC" >&2; exit 1; }

# Blender saves to a temporary file and renames it over the .blend, so the file is never caught half written.
NEWEST=$(ls -1t "$DEST"/pawnHumanRig_*.blend 2>/dev/null | head -n 1 || true)
if [[ -n "$NEWEST" ]] && cmp -s "$SRC" "$NEWEST"; then
    exit 0
fi

STAMP=$(date -r "$SRC" +%Y-%m-%d_%H%M%S)
cp -p "$SRC" "$DEST/pawnHumanRig_$STAMP.blend"
echo "Backed up to $DEST/pawnHumanRig_$STAMP.blend"

ls -1t "$DEST"/pawnHumanRig_*.blend | tail -n +$((KEEP + 1)) | xargs -r rm --
