#!/usr/bin/env bash
# Logs the system's memory, the kernel's memory pressure, RimWorld's resident memory and each GPU's memory in use, every
# EVERY seconds while RimWorld runs, synced to the disk line by line, so a freeze of the whole system (the game filling
# the memory: with no swap the system thrashes for minutes before the OOM killer acts) leaves on disk what climbed.
# The game's own side is <config>/M3AT/trail.txt (Source/LoadTrail.cs); this one sees the rest of the system and the GPU.
#
# ./watch_memory.sh            runs in the foreground
# ./watch_memory.sh --install  sets it up as the systemd user service m3at-memwatch.service, always on
# Logs go to ~/.local/state/m3at-memory/memory-<date>.log (the newest KEEP kept). After a forced restart:
#   tail -40 "$(ls -1t ~/.local/state/m3at-memory/memory-*.log | head -1)"
#   journalctl -b -1 -k | grep -iE "oom|NVRM|Xid"
set -uo pipefail

EVERY=2       # seconds between lines while RimWorld runs
IDLE=10       # seconds between looks for RimWorld while it doesn't
TOP_EVERY=15  # lines between lists of the biggest processes
KEEP=20
DIR="${M3AT_MEMORY_DIR:-$HOME/.local/state/m3at-memory}"

if [[ "${1:-}" == "--install" ]]; then
    UNIT="$HOME/.config/systemd/user"
    mkdir -p "$UNIT"
    cat > "$UNIT/m3at-memwatch.service" <<EOF
[Unit]
Description=Log memory and GPU use while RimWorld runs (M3AT freeze hunting)

[Service]
ExecStart=/usr/bin/bash "$(cd "$(dirname "$0")" && pwd)/watch_memory.sh"
Restart=always
RestartSec=5

[Install]
WantedBy=default.target
EOF
    systemctl --user daemon-reload
    systemctl --user enable m3at-memwatch.service
    systemctl --user restart m3at-memwatch.service
    echo "Installed: logs go to $DIR"
    exit 0
fi

mkdir -p "$DIR"

meminfo() { awk -v k="$1:" '$1 == k { print int($2 / 1024) }' /proc/meminfo; }
pressure() { awk '{ split($2, a, "="); printf "%s %s%% ", $1, a[2] }' /proc/pressure/memory 2>/dev/null; }
gpus() {
    command -v nvidia-smi >/dev/null || { echo "no nvidia-smi"; return; }
    timeout 5 nvidia-smi --query-gpu=index,memory.used,memory.total,utilization.gpu --format=csv,noheader,nounits 2>/dev/null \
        | awk -F', ' '{ printf "gpu%s %s/%s MB %s%% ", $1, $2, $3, $4 }'
}

log=""
n=0
while true; do
    pid=$(pgrep -x RimWorld | head -1)
    if [[ -z "$pid" ]]; then
        log=""
        sleep "$IDLE"
        continue
    fi
    if [[ -z "$log" ]]; then
        log="$DIR/memory-$(date +%Y-%m-%d_%H%M%S).log"
        echo "RimWorld pid $pid; memory $(meminfo MemTotal) MB, swap $(meminfo SwapTotal) MB" >> "$log"
        ls -1t "$DIR"/memory-*.log | tail -n +$((KEEP + 1)) | xargs -r rm --
        n=0
    fi
    rss=$(awk '/^VmRSS:/ { print int($2 / 1024) }' "/proc/$pid/status" 2>/dev/null)
    line="$(date +%H:%M:%S) avail $(meminfo MemAvailable) MB, RimWorld ${rss:-?} MB, $(pressure)| $(gpus)"
    if (( n % TOP_EVERY == 0 )); then
        line+=$'\n'"  biggest: $(ps -eo rss=,comm= --sort=-rss | head -5 | awk '{ printf "%s %d MB, ", $2, $1 / 1024 }')"
    fi
    echo "$line" >> "$log"
    sync -d "$log" 2>/dev/null || sync
    n=$((n + 1))
    sleep "$EVERY"
done
