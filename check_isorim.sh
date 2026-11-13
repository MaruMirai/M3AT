#!/usr/bin/env bash
# Tells you when the Workshop's IsoRim (3819577715) has changed since you last looked.
#
#   ./check_isorim.sh          check now; on a change, notify, write a report and try a build
#   ./check_isorim.sh --ack    take the installed IsoRim as the new baseline (after checking it)
#   ./check_isorim.sh --status print the pending report, if any (used by the Claude session hook)
#
# Run hourly by the systemd user timer m3at-isorim-watch.timer. State lives in
# ~/.local/state/m3at-isorim/: the manifest last acknowledged, a copy of IsoRim's Source, Docs,
# Shaders and Defs as they were then (so the report can diff them), and the pending report.
set -u

ITEM=3819577715
WORKSHOP="$HOME/.local/share/Steam/steamapps/workshop"
ACF="$WORKSHOP/appworkshop_294100.acf"
ISORIM="$WORKSHOP/content/294100/$ITEM"
REPO="$(cd "$(dirname "$0")" && pwd)"
STATE="$HOME/.local/state/m3at-isorim"
BASE="$STATE/baseline"
REPORT="$STATE/pending.txt"
PARTS=(Source Docs Shaders Defs About LoadFolders.xml)
# The IsoRim files this mod copies from or leans on hardest; changes to them are listed first.
WATCHED=(Shaders/IsoRim.vert.glsl Shaders/IsoRimSolid.frag.glsl Shaders/IsoRimTex.vert.glsl
         Docs/Api.md Source/Api.cs)

mkdir -p "$STATE"

# The item's value for a key in the acf's WorkshopItemDetails block (the second block naming it).
acf_value() {
    awk -v item="\"$ITEM\"" -v key="\"$1\"" '
        $1 == "\"WorkshopItemDetails\"" { details = 1 }
        details && $1 == item { inside = 1 }
        inside && $1 == key { gsub(/"/, "", $2); print $2; exit }
        inside && $1 == "}" { inside = 0 }' "$ACF"
}

api_version() { grep -oP 'public static int Version => \K[0-9]+' "$1/Source/Api.cs" 2>/dev/null; }

snapshot() {
    rm -rf "$BASE"; mkdir -p "$BASE"
    for p in "${PARTS[@]}"; do [ -e "$ISORIM/$p" ] && cp -a "$ISORIM/$p" "$BASE/"; done
    acf_value manifest > "$STATE/manifest"
}

case "${1:-}" in
    --ack)
        snapshot; rm -f "$REPORT"
        echo "Baseline set to IsoRim manifest $(cat "$STATE/manifest") (Api.Version $(api_version "$BASE"))."
        exit 0 ;;
    --status)
        [ -f "$REPORT" ] && cat "$REPORT"
        exit 0 ;;
esac

installed=$(acf_value manifest)
latest=$(acf_value latest_manifest)
if [ -z "$installed" ]; then echo "IsoRim not found in $ACF" >&2; exit 1; fi

# First run: take what is installed as the baseline.
if [ ! -f "$STATE/manifest" ] || [ ! -d "$BASE" ]; then
    snapshot
    echo "First run: baseline is IsoRim manifest $installed (Api.Version $(api_version "$BASE"))."
    exit 0
fi

known=$(cat "$STATE/manifest")

# Steam knows of a newer version but hasn't downloaded it yet: say so once, check again later.
if [ -n "$latest" ] && [ "$latest" != "$installed" ] && [ "$(cat "$STATE/told_latest" 2>/dev/null)" != "$latest" ]; then
    notify-send -a "M3AT" "IsoRim update on the Workshop" \
        "Steam hasn't downloaded it yet. You'll get another note once it's installed."
    echo "$latest" > "$STATE/told_latest"
fi

[ "$installed" = "$known" ] && exit 0

# Steam may note the new manifest before it has finished writing the files (it stages them under
# workshop/downloads and temp, then moves them in), so a check in between diffed the old files
# against themselves. Wait while anything of the item is still staged or any of its files was
# written in the last SETTLE_MINUTES; the hourly timer comes back.
SETTLE_MINUTES=10
for staged in "$WORKSHOP/downloads/294100/$ITEM" "$WORKSHOP/temp/294100/$ITEM"; do
    if [ -e "$staged" ]; then echo "IsoRim still downloading ($staged); checking later."; exit 0; fi
done
if [ -n "$(find "$ISORIM" -newermt "-$SETTLE_MINUTES minutes" -print -quit 2>/dev/null)" ]; then
    echo "IsoRim's files changed in the last $SETTLE_MINUTES minutes; checking later."; exit 0
fi

# Already reported this one and not acknowledged yet.
[ -f "$REPORT" ] && grep -q "^installed manifest $installed\$" "$REPORT" && exit 0

# Settled, and nothing this mod watches changed (only models, textures or Assets.zip): take it as
# the baseline without a note.
unchanged() {
    local p
    for p in "${PARTS[@]}"; do diff -rq "$BASE/$p" "$ISORIM/$p" >/dev/null 2>&1 || return 1; done
}
if unchanged; then
    snapshot; echo "IsoRim manifest $installed changes nothing watched; taken as the baseline."; exit 0
fi

{
    echo "IsoRim has updated since the last acknowledged version."
    echo "installed manifest $installed"
    echo "previous manifest $known"
    echo "Api.Version: $(api_version "$BASE") -> $(api_version "$ISORIM")"
    echo "Workshop timeupdated: $(date -d @"$(acf_value timeupdated)" 2>/dev/null)"
    echo
    echo "== Files this mod copies or leans on =="
    for f in "${WATCHED[@]}"; do
        cmp -s "$BASE/$f" "$ISORIM/$f" || echo "CHANGED $f"
    done
    echo
    echo "== All changed files =="
    for p in "${PARTS[@]}"; do
        diff -rq "$BASE/$p" "$ISORIM/$p" 2>/dev/null | sed "s|$BASE/||; s|$ISORIM/||"
    done
    echo
    echo "== Build against the new IsoRim.dll (to a scratch folder, the shipped DLL untouched) =="
    if out=$(dotnet build "$REPO/Source/M3AT.csproj" -nologo -v q -p:OutputPath="$STATE/build/" 2>&1); then
        echo "Build OK."
    else
        echo "BUILD FAILED:"; echo "$out" | grep -E 'error' | sort -u | head -40
    fi
    echo
    echo "Full diff:  diff -ru ~/.local/state/m3at-isorim/baseline/Source \"$ISORIM/Source\""
    echo "When done:  ./check_isorim.sh --ack"
} > "$REPORT"

changed=$(grep -c '^CHANGED' "$REPORT")
build=$(grep -q 'BUILD FAILED' "$REPORT" && echo "build FAILS" || echo "build OK")
notify-send -a "M3AT" -u critical "IsoRim updated (Api $(api_version "$BASE") → $(api_version "$ISORIM"))" \
    "$build; $changed watched file(s) changed. Report: $REPORT"
echo "IsoRim updated; report in $REPORT"
