# Archived garment scripts

Make scripts whose garments are no longer what the game draws (moved here from `anim_scripts/` on 2026-11-27), kept
for reference: each was run fresh against the rig and compared with `Models/garments`, and either nothing it makes is
used any more or the drawn garment is a very different one, so running it could only replace the drawn one by mistake.
What each made and what is drawn instead: `dev_notes/garment_scripts.md`, "Archived scripts".

They import `walk` from their own folder, so they no longer run from here. To run one (to borrow a technique or remake
a base), copy it back into `anim_scripts/` and write to a scratch folder, never over `Models/garments`.
