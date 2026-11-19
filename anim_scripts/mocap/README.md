# From a tracker take to a game clip

The steps that took the planting take (`plant`) into the game, for the next clip. Paths are from the repository's root;
`BLENDER` is `/home/t/Programs/blender-5.2.3-linux-x64/blender`.

## Recording (Maru)

- `./record_mocap.sh`, then `n <clip>` to name the takes (they go to `animDLs/mocap/<date>/<clip>_NN.csv`). `c` checks
  that all nine devices are named; `s` shows what is tracked.
- Start and stop: both wands together over the head for a second; with the wands taped to a tool (the pick), both
  held still over the head for a second and a half instead (import with `--tool`). After "rest", stand still with the wands pointing
  forward until "go" (forward is taken from them). Hold the work for several cycles, then stand and stop.
- Knee trackers instead of elbow trackers (for legs that spread, kneel or lie): worn on the outside of each shin just
  below the knee; `take.assign` names them by height and the importer bends the knees toward them and the elbows
  `ELBOW_WAY`. Not with `--tool`. The hips tracker may go anywhere on the belt (the side, for a take lying on the back),
  except with `--tool`, which takes forward from it at the back.
- A loop wants the same motion repeated a few times in a row; the importer picks the stretch whose ends match best.
- The chibi's arms are short for its body: work done low (kneeling) needs the trunk fairly upright and the hands reaching
  forward of the knees, or its hands stop well short of the ground (the first planting takes).

## Looking at a take

1. What it does over time: print each part's height by the second (`take.read` gives each device's samples; the
   header's `device` lines name them). Pick the stretch of the work, after getting into it and before getting up.
2. The whole take on the chibi, to check the retarget:
   `BLENDER -b Models/pawnHumanRig.blend --python anim_scripts/mocap/import_take.py -- <take.csv> <from> <to> --as <name>whole --once`
   then `BLENDER -b Models/pawnHumanRig.blend --python anim_scripts/mocap/render_clip.py -- <name>whole <out folder> 10 --floor`
   and look at `<out folder>/sheet.png` (front three-quarter over side). Delete `anim_scripts/<name>whole_clip.json` after.
3. The loop: `import_take.py -- <take.csv> <from> <to> --as <name> --loop <shortest> <longest>` (seconds). It prints
   the cycle it chose and how far apart its ends are (a few cm closes well). Render it the same way with 8 frames;
   `--zoom hand.R` gets close on a bone, `--at <name>_at` renders through the walk.py function once there is one, and
   `NAME=value` arguments change walk.py numbers for the render.
4. Things to check: lost trackers (`IMPORT <part> lost ...`, carried with a neighbour), SteamVR's floor (`IMPORT
   SteamVR's floor is ... off`: taken from the feet), the hands (`--wrist <share>` of the wrist's bend, 0.5 by default)
   and how near the ground the hands and knees come (measure each fingertip's height over the loop in Blender).

## Into the game

1. walk.py: `<NAME>_CLIP = clip_file('<name>')`, `<NAME>_SECONDS` from its frames and fps, and `<name>_at(t)`
   returning `(pose, overreach)`: `kneel_at(<NAME>_CLIP, t)` sets it on the floor by its lowest toe; laid over the sit
   or the gait from the waist up instead, see how `eat_sit`, `research` or `talk` are done. Write where the clip came
   from in the comment above it (the take, the stretch, the import command).
2. export_frames.py: export it as a loop (`export_loop('work_<name>', ...)`, as the work by place loop does), with an
   `over` mask if it is laid over something.
3. preview.py (CLAUDE.md says what each needs): the clip list (`clip_items`), `loop_pose`, `loop_seconds`,
   `CLIP_SECTION`, `CATEGORY_OF`, and `OVERS` if the game lays it over the gait.
4. The game: which jobs play it (`WalkFigures.WorkOf` for work: a kind `<name>` plays `work_<name>`; `PlaceWork`'s
   `ground` kind for work at the ground, falling back to `crouch` on an export without it).
5. `./export_model.sh` (about five minutes; it reads the clip files when it starts, so re-run it after changing one),
   check `anim.txt` has its `clip work_<name>` line, then `dotnet build Source/M3AT.csproj` (not while an autotest
   holds `~/.local/state/m3at-autotest.lock`).
6. CLAUDE.md: the clip in the pipeline and runtime notes, as `plant` is.
