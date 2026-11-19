#!/bin/bash
# Runs the AutoTest scenarios (Source/AutoTest.cs) in the game, then puts the normal build back.
#   ./autotest.sh [scenario,...|all] [--stills] [--out folder] [--seed n|none]
#       (a video of each scenario by default, one .mp4 each, rendered from IsoRim's scene images a tick at a time with the
#        time frozen while each is taken, so it plays smoothly at the game's own pace however slow the capture (--fps 30,
#        the default, or 60: frames a second of game time); --screen: the game window recorded at normal speed instead;
#        --stills: paused, a tick a frame, with pictures at the scenario's Shot steps instead)
#       (--seed: IsoRim's -isorimseed (Api 72), so the quick test's world, colonists and every tick's random numbers are the
#        same each run; 1 by default, none for a new world each time)
#   ./autotest.sh shots --load <save> [--from tick] [--ticks n] [--every n] [--out folder]   (stills of a save run on, from its view)
#   ./autotest.sh perf --load <save> [--seconds s] [--rounds n] [--counts 0,5,10,20f] [--switch frames] [--out folder]
#       (frame times with the chibi, IsoRim's figures and none; or with the chibi on that many colonists, f: fewer poses on,
#        n: a draw per face layer, as before FaceLayers.DrawStacked)
# Pictures, the probe log and report.md go to <out>/<date-time>/ (default: the game's config folder, M3AT/autotest), linked from autotest-latest.
set -e
cd "$(dirname "$0")"
which="${1:-all}"
shift || true
args=()
out=""
load=""
seed=1
stills=""
while [ $# -gt 0 ]; do
    case "$1" in
        --video) ;;   # (the default now)
        --stills) stills=1 ;;
        --screen) args+=(-m3atTestScreen) ;;
        --fps) args+=("-m3atTestTicksPerFrame=$(( 60 / $2 ))"); shift ;;
        --out) out="$2"; shift ;;
        --load) load="$2"; shift ;;
        --seed) seed="$2"; shift ;;
        --ticks) args+=("-m3atShotTicks=$2"); shift ;;
        --every) args+=("-m3atShotEvery=$2"); shift ;;
        --from) args+=("-m3atShotFrom=$2"); shift ;;
        --seconds) args+=("-m3atPerfSeconds=$2"); shift ;;
        --rounds) args+=("-m3atPerfRounds=$2"); shift ;;
        --counts) args+=("-m3atPerfCounts=$2"); shift ;;
        --switch) args+=("-m3atPerfSwitch=$2"); shift ;;
        --stack) args+=("-m3atStackSize=$2"); shift ;;
    esac
    shift
done
[ -n "$out" ] && args+=("-m3atTestOut=$(realpath "$out")")
# Videos by default, to watch the animations at their own pace (stepped a tick a frame they stutter); not for the save
# runs, which take pictures of their own.
[ -z "$stills" ] && [ "$which" != shots ] && [ "$which" != perf ] && args+=(-m3atTestVideo)
[ "$seed" != none ] && args+=("-isorimseed=$seed")
# One run at a time: the test build goes into the mod's own DLL and the results are read from the one Player.log,
# so a second run (another session) waits here for the first to finish. The lock is held until the script exits,
# after the trap below has put the normal build back.
lock="${XDG_STATE_HOME:-$HOME/.local/state}/m3at-autotest.lock"
mkdir -p "$(dirname "$lock")"
exec 9>"$lock"
if ! flock -n 9; then
    echo "Another autotest is running; waiting for it to finish..."
    flock 9
fi
# (9>&-: the build leaves dotnet's compiler server running, which would otherwise keep the lock open after this exits,
# and every later run would wait on it for good)
dotnet build Source/M3AT.csproj -p:DefineConstants=AUTOTEST -v q -nologo 9>&- | grep -E " error |Build succeeded" | sort -u
trap 'dotnet build Source/M3AT.csproj -v q -nologo 9>&- | grep -E " error |Build succeeded" | sort -u' EXIT
log="$HOME/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Logs/Player.log"
start=(-quicktest); [ -n "$load" ] && start=("-m3atTestLoad=$load")
(cd ~/.local/share/Steam/steamapps/common/RimWorld && timeout 1800 ./RimWorld "${start[@]}" "-m3atTest=$which" "${args[@]}" -m3atTestQuit >/dev/null 2>&1) || true
# The launcher can hand the game on to another process and return at once (Waylith's Vulkan fallback relaunch), which
# would put the normal build back while the game still loads mods, so the test never runs: wait for the game itself.
for _ in $(seq 1 1800); do
    pgrep -f -- "^[^ ]*RimWorld[^ ]* .*-m3atTest=" >/dev/null || break
    sleep 1
done
grep -a "\[AutoTest\]" "$log" | sed 's/^.*\[AutoTest\] //'
# The newest run's folder linked at the repository's root as autotest-latest (git-ignored), so its videos and pictures
# can be opened from links to files in the workspace; only for a run in the default folder, as a run with --out (another
# session's, into its own scratch folder) re-pointed it away from the run being looked at.
done_dir=$(grep -a "\[AutoTest\] done: " "$log" | tail -1 | sed 's/^.*\[AutoTest\] done: //; s#/report.md$##')
[ -z "$out" ] && [ -n "$done_dir" ] && [ -d "$done_dir" ] && ln -sfn "$done_dir" autotest-latest || true
