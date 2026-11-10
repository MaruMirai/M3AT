#!/bin/bash
# Runs the AutoTest scenarios (Source/AutoTest.cs) in the game, then puts the normal build back.
#   ./autotest.sh [scenario,...|all] [--video] [--out folder]
# Pictures, the probe log and report.md go to <out>/<date-time>/ (default: the game's config folder, M3AT/autotest).
set -e
cd "$(dirname "$0")"
which="${1:-all}"
shift || true
args=()
out=""
while [ $# -gt 0 ]; do
    case "$1" in
        --video) args+=(-m3atTestVideo) ;;
        --out) out="$2"; shift ;;
    esac
    shift
done
[ -n "$out" ] && args+=("-m3atTestOut=$(realpath "$out")")
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
(cd ~/.local/share/Steam/steamapps/common/RimWorld && timeout 1800 ./RimWorld -quicktest "-m3atTest=$which" "${args[@]}" -m3atTestQuit >/dev/null 2>&1) || true
grep -a "\[AutoTest\]" "$log" | sed 's/^.*\[AutoTest\] //'
