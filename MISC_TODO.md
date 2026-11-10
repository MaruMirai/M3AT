# Misc to-do

Ideas that don't fit the apparel, gene or animation lists. Add to it freely.

## Looks
- [ ] Clothing damage: show worn or torn clothes as their hit points drop (`Apparel.HitPoints` against `MaxHitPoints`). The game draws no damage on clothes itself, so this is ours to design: rips cut into the garment texture or mesh, a dirtier tint, or both.
- [x] Injuries: wounds, scars, bandages and missing parts. The game draws wound overlays on the head and body sprites from the pawn's hediffs. Missing limbs would need parts of the body hidden, as garments hide body triangles now.
- [x] Holstered weapons and equipment
- [x] Drafted melee/unarmed anim to use normal idle unless enemies close

## Pawns
- [x] Children: Biotech babies and children use their own body types (`Baby`, `Child`), which the model has no keys for yet. They need a smaller figure with child proportions, along with kid clothes (see the children's section of [APPAREL_TODO.md](APPAREL_TODO.md)) and probably their own walk.
- [ ] integrate biotech patch style approach for mods

## Other mods
- [ ] Romance on the Rim: animations for its dates, hangouts and other romance jobs. Find its `JobDef` names and fit them into the motion groups in [ANIMATION_TODO.md](ANIMATION_TODO.md) where they can share a motion.
- [x] CE: Reloads, apparel
- [x] animal cuddling