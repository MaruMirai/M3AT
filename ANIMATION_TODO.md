# Animation to-do

Every action and state a colonist can be seen in that might want its own animation. Jobs are the `JobDef` names, read from the installed game's `Data/*/Defs` (Core, Royalty, Ideology, Biotech, Anomaly, Odyssey; about 330 jobs). States that are not jobs (posture, mental states, health) are from the game's code and IsoRim's.

Most jobs share a handful of motions, so the list is grouped by motion, not by job. Where IsoRim already animates something on its own figure, the IsoRim file is named: it says how the game decides the state and what the pose should line up with.

Each motion is marked **body** (whole figure), **upper** (arms, spine and head over whatever the legs are doing) or **add** (a small motion added on top of any pose). See [Layering](#layering) for why that matters.

## Suggested order

1. Drafted stand and walk, an aim layer and a melee swing. Combat is where the model is looked at most.
2. Carrying walk. Haulers are on screen all the time.
3. Bend to pick up and set down. One short clip that dozens of jobs start and end with.
4. Hand work at standing and bench height, split by tool the way IsoRim's `Work.cs` is (chop, pick, hammer, kneel, sweep).
5. Sit (idle, eat) and lie (sleep, downed). A first sit, lie and crawl are in (`sit`, `lie`, `crawl`); eating, sleeping curled and the rest still use them as they are.
6. Chat, sad wander and flinch/stagger. Cheap, and they give the colony life.

## Moving
- [x] Idle stand (`idle`): **Wait**, **Wait_Wander**, **Wait_MaintainPosture**, **StandAndStare** — body
- [x] Walk and run (`walk`, `run1..6`) — body
- [x] Idle breathing and weight shifts, so a standing pawn is not frozen — baked into the `idle` loop for now (`walk.stand_at`, 12 s, three breaths and a shift onto each foot), not yet a true additive layer
- [x] Drafted stand and walk, weapon held ready, more upright — upper (`drafted_<kind>` per weapon kind: rifle, pistol, bow, melee, unarmed, laid over the gait with `walk.UPPER`; the weapon is IsoRim's sprite slab in the hands, `WalkFigures.DrawWeapon`)
- [x] Carrying walk, arms forward holding the load (`carry_item`, `walk.carry_at`, laid over the gait like the drafted stance; IsoRim draws the thing between the hands, `WalkFigures.DrawLoad`): **HaulToCell**, **HaulToContainer**, **DeliverFood**, **DeliverToCell**, **HaulToTransporter**, **HaulToPortal**, **HaulToAtomizer**, **GiveToPackAnimal** — upper (IsoRim `Carried.cs` places the item at hand height)
- [x] Carrying a person (`carry_bridal` in the arms, `carry_fireman` over the shoulder for prisoners, hostiles, arrests, captures and kidnaps; the carried person's own `carried_<kind>` loop rides the carrier's chest bone, `WalkFigures.DrawHeld`). Not yet slower or heavier in the legs: **Rescue**, **Capture**, **Arrest**, **Kidnap**, **CarryDownedPawnDrafted**, **CarryDownedPawnToExit**, **CarryToCryptosleepCasket**, **TakeWoundedPrisonerToBed**, **TakeToBedToOperate**, **HaulCorpseToPublicPlace**, **BringBabyToSafety** — upper, plus a slower walk (`Carried.cs` lifts the carried pawn to the hands, `LiftOf`)
- [ ] Pushing something along the ground beside the pawn (`Carried.cs`) — upper
- [x] Limp, for a missing or hurt leg — body (`limp_L`, `limp_R`, `walk.limp_at`: the bad foot down for less of the cycle, a short stiff step swung out round the other, the pelvis hitched, the trunk leaned over it, that hand on the hip; in place of the walk and run by how much worse one leg works than the other, `WalkFigures.LimpOf`. Standing still does not favour it yet)
- [x] Holding a wound, for an injury anywhere but the legs — upper (`hurt_<place>_<side>`, `walk.hurt_at`: head, chest and belly held by the hand on the wound's side, the neck by that hand reaching across to the far side of it, a hurt arm held across the belly and clutched by the other hand near the elbow; hunched over it from the waist, breathing short. Laid over whatever the legs do, the free arm keeping its swing, by how hurt the worst place is, `WalkFigures.HurtOf`; dropped while drafted or carrying. Not yet a pain-only hunch for illness, or a hand on a hurt leg when standing)
- [x] Crawl, downed in 1.7: `Pawn.Crawling` (jobs with `isCrawlingIfDowned`) — body (`crawl`, `walk.crawl_at`: made upright, tipped face down by IsoRim's `Crawl.ChestLift`; a belly crawl, by the ground covered)
- [x] Wading and swimming: **GoSwimming** — body (`wade`, `walk.wade_at`: high knees, arms out, in place of the gait as the water comes up, the figure sunk to the depth, `WalkFigures.WaterOf`; `swim`, `walk.swim_at`, a front crawl laid face down, and `tread`, `walk.tread_at`, still, both sunk to the neck. Deep water cannot be walked in 1.7, so chest-deep is the deepest)
- [x] **FleeAndCower**, **FleeAndCowerShort**
- [x] Jump (take-off, in the air, landing): **CastJump**, jump packs (PawnFlyer; `Carried.cs` gives the height) — body (`jump`, `walk.jump_at`: pushed off straight with the arms thrown up, knees tucked and arms out at the top, legs reaching down to land, played by how far through the flight the pawn is, `WalkFigures.FlightOf`; `land`, `walk.land_pose`, a crouch sunk into over the jump's warm-up and dropped into on landing, `WalkFigures.CrouchOf`. Not yet: a tumble of its own for the stun throw (`PawnFlyer_Stun`), which uses the jump for now)
- [x] Leading on a rope: **Follow**, **FollowClose**, **RopeToPen**, **RopeRoamerToHitchingPost**, **RopeRoamerToUnenclosedPen**, **Unrope** — upper (`lead`, `walk.lead_at`: the right arm reached a little back and down, tugged gently with the breath, laid over the gait on that arm only (`LEAD_UPPER`) whenever the game's rope tracker has someone tied to the pawn, `WalkFigures.Leading`. The game still draws the rope from the pawn's middle, not the hand)

## Working
IsoRim's `Work.cs` already turns the figure to the work, sorts these jobs by tool and times strokes by game ticks; the tool it draws should end up in the model's hand. First pass in: one stroke per tool as a whole-figure clip in place of the idle (`work_<kind>`, `walk.WORK`, `walk.work_at`), IsoRim's tool model riding the right hand (`WalkFigures.WorkOf`, `DrawTool`). Not yet: strikes aimed at the work's height (a roof, a wall, a frame on the ground all get the same blow), seated bench work, kneeling.

- [x] Chop: **CutPlant**, **CutPlantDesignated**, **Harvest** on trees — body for now (`work_axe`)
- [x] Pickaxe: **Mine** — body for now (`work_pickaxe`); **ClearPollution** uses the hoe (`work_hoe`)
- [x] Hammer: **FinishFrame**, **Repair**, **Deconstruct**, **DeconstructForBlueprint**, **Uninstall**, **FixBrokenDownBuilding**, **BuildRoof**, **RemoveRoof**, **PlaceNoCostFrame**, **RepairMech** — body for now (`work_hammer`, the left hand steadying the work). Not yet **BuildCubeSculpture**
- [x] Smoothing and chiselling: **SmoothFloor**, **SmoothWall** — the hammer for now, as IsoRim
- [ ] Kneel and pluck: **Sow**, **PlantSeed**, **Harvest** (crops), **HarvestDesignated**, **Forage**, **Replant**, **ExtractTree**, **PruneGauranlenTree** — body (first pass in: `work_pluck`, `walk.pluck_at`, Meshy's mocap kneel on the left knee, leant over the right leg with the right hand pawing at the ground, for **PlantSeed**, **Forage**, **Replant**, **ExtractTree**, **PruneGauranlenTree**. **Sow** still uses the hoe and crop **Harvest** the scythe)
- [x] Sweep: **Clean**, **ClearSnow** (`work_broom`); harvesting crops (`work_scythe`, **Harvest**, **HarvestDesignated**, **CutPlant** on non-trees); sowing with the hoe (`work_hoe`, **Sow**). **RemoveFloor** and **RemoveFoundation** use the hammer, as IsoRim
- [ ] Bench work, hands at table height (**DoBill**), by bench — upper:
  - [x] Butchering (butcher table): `work_knife`, standing
  - [x] Hammering (smithy, machining, fabrication, stonecutter, smelter, crafting, sculpting): `work_hammer`, standing
  - [ ] Stirring (stoves, drug lab)
  - [ ] Fine work (tailoring, crafting, sculpting, art)
  - [ ] Any other bench, and typing at a terminal: `work_hands` for now (both hands busy at bench height, standing) for **DoBill** at the other benches, **Research** standing (sat at the bench it plays `work_research`, the mocap desk work from `animDLs/research.glb`, over the sit), **UseCommsConsole**, **AnalyzeItem**, **Hack**. Still to do: a real typing motion, and **ControlMech**, **PilotConsole**, **StudyInteract**
- [ ] Kneel by a patient: **TendPatient**, **FeedPatient**, **TendEntity**, **InstallImplant**, **VisitSickPawn**, **Resurrect**, **PrisonerBloodfeed** — body (first pass in: `work_tend`, `walk.tend_at`, Meshy's mocap kneel as it is, cut before its left hand digs forward, for all of these but **PrisonerBloodfeed** and **VisitSickPawn**, which talks instead: stood or sat on a chair by a bed, `talk` laid on; by a sleeping spot or bedroll sat cross-legged beside them talking, `work_visit`; autotest `visit`)
- [ ] Crouched animal handling: **Milk**, **Shear**, **Slaughter**, **Tame**, **Train**, **Nuzzle**, **ReleaseAnimalToWild** — body (first pass in for **Milk** and **Shear**: by the animal's drawn back height, `WalkFigures.AnimalKind`, stood at it with `work_bench` from `AnimalHigh` (0.5 cells), else knelt by it with `work_crouch`; and for **Tame** and **Train**: `work_tame`, the right hand held out palm up coaxing, stood at a tall animal, or `work_tame_kneel` on the `work_crouch` kneel by a low one, sorted the same way. Not yet exported or checked in game)
- [ ] Painting: **PaintBuilding**, **PaintFloor**, **RemovePaintBuilding**, **RemovePaintFloor** — upper
- [ ] Beating out fire: **BeatFire**, **ExtinguishFiresNearby** — upper; putting out a fire on itself: **ExtinguishSelf** — body (first pass in: `roll`, `walk.roll_at`, stop, drop and roll: down by the grenade dive, then rolled over each way onto the back and back with the arms crossed on the chest, `WalkFigures.Rolling` through the hunker's `DuckOf`. Needs an export)
- [x] Bend to pick up or set down, for now only when what a pawn carries or its count changes, so topping up a stack too (`bend`, `walk.bend_pose`, a squat; on the move `bend_walk`, a fold at the waist over the stride, played faster: one still pose eased in and out over `BEND_SECONDS` of game time, `WalkFigures.BendOf`; the game takes the thing in one tick, so the bend comes just after, the load rising from the ground in the hands). Still to do for the jobs below that pick up without carrying: **Equip**, **DropEquipment**, **Wear**, **RemoveApparel**, **Strip**, **Refuel**, **RefuelAtomic**, **EjectFuel**, **Reload**, **RearmTurret**, **FillFermentingBarrel**, **TakeBeerOutOfFermentingBarrel**, **EmptyThingContainer**, **EmptyWasteContainer**, **TakeInventory**, **UnloadInventory**, **PickupToHold**, **ExtractBioferrite** — body, short one-shot
- [ ] Reaching to a switch or lid: **Flick**, **Open**, **TriggerObject**, **InteractThing**, **Seal** — upper, short one-shot
- [ ] Operating a machine: **OperateDeepDrill**, **OperateScanner**, **ManTurret**, **Maintain**, **InspectGravEngine** — upper
- [x] Fishing (cast, then hold the line): **Fish** — upper (first pass in: `work_fish`, a posed `WORK` stroke with this mod's own rod, `make_rod.py`: held out over the water with a slow sway and a jig of the tip once a stroke, the line drawn from the tip to the water cell, `WalkFigures.DrawLine`. Still to do: the cast at the start, reeling in and a catch)
- [ ] Digging and filling: **FillIn**, **ExtractSkull**, **ExtractRelic** — body

## Combat
IsoRim's `FigureWeapons.cs` already works out the aim (warm-up, burst, cool-down), the weapon's angle and the recoil; the arms should follow it.

- [x] Aim and fire: rifle, pistol, heavy weapon, bow (draw and loose): **AttackStatic**, **Hunt**, **Wait_Combat** — upper (`aim_<name>`, `fire_<name>`, a bow's `nock_<name>`, `walk.aim_at`: one template per kind in `walk.AIMED` (rifle at the shoulder under the chin, pistol two-handed out in front, heavy from the hip, bow side on and drawn to the chin), baked per weapon with the hand turned so that weapon's own hold points exactly along the aim; a frame per pitch, picked by IsoRim's aim pitch; the kick of each shot by the weapon's recoil, a bow drawn over the warm-up and loosed, `WalkFigures.LayAim`. Not yet: grenades and thrown weapons (they keep the drafted stance), the Doomsday launcher and minigun want tuning per weapon, and aiming steeply up still brings the stock into the big chin)
- [x] Melee: blunt swing, slash, stab, fist: **AttackMelee**, **SocialFight** — upper (`melee_smash`, `melee_slash`, `melee_stab`, `melee_fist_L`/`_R`, `walk.strike_at`: wound up, the blow landing and followed through, from each weapon's drafted guard; picked by the blow's maneuver, or the fist's side, and played from the tick it lands, `WalkFigures.StrikeOf`; the spear and ikwa have their own. Not yet: tuned per weapon by eye in the game (the preview has no strike sliders), a two-handed grip for the long weapons, or a wind-up before the blow, which the game only picks as it lands)
- [x] Throw (grenades, molotovs, EMP, pila): **UseVerbOnThing**, **UseVerbOnThingStatic** — upper (`throw_R`, `throw_pack`, `walk.THROW`: cocked behind the head over the warm-up, let go up in front and followed through, with the hand that holds it; grenades are held in the right hand and thrown by `throw_R`, posed from the pilum's guard. `throw_pack`, `walk.PACK_TAKE`, the right taking one from a pack in the left, is still exported but unplayed)
- [x] Cast a psycast or ability, hand raised: **CastAbilityOnThing**, **CastAbilityTouch**, **CastAbilityOnThingMelee**, **CastAbilityOnWorldTile**, **CastAbilityGoToThing** — upper (`cast`, `walk.cast_at`: the left hand raised to the target, palm out, pushed out with the breath, the left shoulder leading; `cast_touch` reaches it out low in front for a touch ability. The left hand, so a weapon in the right keeps its stance; eased in over the ability's warm-up, facing the target, `WalkFigures.CastOf`. Laid on with `SkinnedFigure.Top`, over the drafted stance rather than blended with it. Not yet: a motion per ability or psycast, or the release as it goes off)
- [x] Hit flinch — add (`flinch_F`/`_B`/`_L`/`_R`, `walk.flinch_at`: one jolt, the trunk thrown the way the hit pushes, the head held up out of it, shoulders shrugged and forearms drawn up. The first true additive layer, `SkinnedFigure.Nudge`: each bone's turn from the clip's first frame, put on top of any pose, so it works standing, walking, aiming, seated or lying. Mixed by where the hit came from, as strong as its damage, a deflected hit weaker, over `FlinchTicks`, from the game's own `Pawn_DrawTracker.Notify_DamageApplied`/`Notify_DamageDeflected`, `WalkFigures.FlinchOf`)
- [x] Stagger and stun — body (`reel`, `walk.reel_at`: a bigger flinch that stays on, thrown back and rocking with the right arm up overhead and the left out for balance, from the waist up over the slowed gait while the game's `StaggerHandler.Staggered`; `daze`, `walk.daze_at`: slumped on soft knees, arms hanging, the head lolling round, the whole figure in place of the idle and the drafted stance while `StunHandler.Stunned`, from the waist up on the move. Not yet: a stumble in the legs, or a fall for a long stun)
- [x] Knocked down: going from standing to downed — body, one-shot
- [x] Death fall, ending in the corpse pose — body, one-shot

## Sitting
IsoRim's `Poses.cs` decides who is sitting: any `isSittable` building (stools, chairs, couches, pews, thrones, kneel pillows), whatever the job.

- [x] Sit idle — body (`sit`, `walk.sit_at`: breathing, hands on the thighs; the seat goes under the hips, `SkinnedFigure.SeatOf`)
- [x] Eat at a table, spoon to mouth: **Ingest** (table), **EatAtCannibalPlatter** — upper over the sit (`Carried.cs` puts the food on the table) (first pass in for **Ingest**: `eat_sit`, `walk.eat_sit_at`, Meshy's sit and drink with the cup's fetching cut out, the right hand from the table to the mouth, laid over the sit, or the idle when standing at a table, `WalkFigures.EatOf`. Not yet **EatAtCannibalPlatter**, or a spoon)
- [x] Eat standing or on the floor: **Ingest** without a table — upper (first pass in: `eat_stand`, `walk.eat_stand_at`, Meshy's stand and drink with the head kept level, mirrored to eat with the right hand, brought only to the mouth, and the meal held in the left palm, drawn there by `WalkFigures.DrawLoad`)
- [x] Bench work seated (the bench motions above, over the sit) — upper
- [ ] Read: **Reading**, **ReadDatacore**, **StudyItem**, **ApplyTechprint** — upper
- [x] Board and card games: **Play_Chess**, **Play_Poker**, **Play_GameOfUr** — upper (first pass in: `talk`, `walk.talk_at`, the first six seconds or so of Meshy's angry talk from the waist up, over the sit, the whole game when someone else plays at the same board, `WalkFigures.TalkOf`; also for **GiveSpeech** and **SpeakOnCellNoSpeechBubbles**, and now and then at a party, `WalkFigures.BoutOf`. Not yet: moving pieces or cards)
- [ ] Watch: **WatchTelevision**, **SpectateCeremony**, **Workwatching** — add (head and small shifts)
- [x] Play music, by instrument (harp, harpsichord, piano, drums): **Play_MusicalInstrument** — upper (first pass in: `work_piano` for the piano and harpsichord, `work_harp`, `work_drum` (the one large tribal drum, beaten with the palms in a pattern with an accent), each `_sit` on a chair put at it, laid over the sit; `walk.MUSIC`, posed by hand to IsoRim's models of them, the figure drawn stepped up to the instrument, `WalkFigures.InstrumentCloser`, `InstrumentKind`. Not yet: checked by eye in the game beyond the autotest's `music` scenario; seated at the harp the hands fall short of the strings, and a child's shorter arms short of all of them; no sound sync)
- [x] Meditate and pray, cross-legged or kneeling: **Meditate**, **MeditatePray**, **Pray**, **Reign** (throne) — body
- [x] Lessons: **Lessongiving**, **Lessontaking** — upper (first pass in for **Lessongiving**: `teach`, `walk.teach_at`, explaining with the hands (presenting palm up, beating a point, opening out to the pupil, both hands spread, patting the desk), keyed by hand, over the idle or the sit, `WalkFigures.TeachOf`; the head turned part way to the pupil beside it, `LessonLookOf`, `SkinnedFigure.Look`, and the pupil's part way back to the teacher. Not yet: anything for the pupil's hands, or the blackboard)

## Joy and social
- [ ] Chat: gestures and nods turned to the other pawn: **SocialRelax**, **StandAndBeSociallyActive**, **GotoAndBeSociallyActive**, and every interaction (chat, deep talk, romance, **TryRomance**, **PrisonerAttemptRecruit**, **PrisonerConvert**, **PrisonerEnslave**, **PrisonerReduceWill**, **SlaveSuppress**, **TalkCreepJoiner**, **PrisonerInterrogateIdentity**) — upper
- [ ] Insult, pointing: **Insult**, **HateChanting** — upper
- [ ] Dance: **Dance** — body
- [x] Speech with big gestures: **GiveSpeech**, **BestowingCeremony**, ritual leader roles — upper (first pass in for **GiveSpeech** and a ritual's **SpeakOnCellNoSpeechBubbles**: `talk`, the whole speech. Not yet **BestowingCeremony** or the other leader roles)
- [ ] Look at the sky: **Skygaze**, **Skydreaming**; bend to an eyepiece: **UseTelescope** — body
- [ ] Look at something with head bowed: **ViewArt**, **VisitGrave** — add
- [x] Throwing games: **Play_Horseshoes**, **Play_Hoopstone** — upper; lean over a cue: **Play_Billiards** — body
- [ ] Build a snowman: **BuildSnowman**; draw on the floor: **Floordrawing** — body
- [ ] Children's play: **PlayToys**, **PlayStatic**, **PlayWalking**, **NatureRunning**, **BabyPlay**, **Radiotalking** — body
- [x] Go for a walk (a relaxed walk): **GoForWalk** — body
- [ ] Marriage and rituals: **MarryAdjacentPawn**, **Sacrifice**, **Scarify**, **Blind**, executions (**PrisonerExecution**, **SlaveExecution**, **GuiltyColonistExecution**, **ExecuteEntity**) — upper

## Lying down
- [x] Sleep in bed: **LayDown**, **Wait_Asleep**, **Deathrest**, **Wait_WithSleeping** — body (`lie`, `walk.lie_at`, on the back and breathing slowly; for now every lying pawn, awake, downed or dead, uses it)
- [x] Sleep on the ground, curled — body
- [x] Get up from lying (woken, no longer downed) — body, one-shot (`getup`, `walk.getup_at`, Meshy's Arise cut before its step back; `RiseOf`, played `RiseHurry` times faster)
- [ ] Rest awake, lying, turning a little: **LayDownAwake**, **LayDownResting** — body
- [x] Downed, slow breathing and the odd writhe: **Wait_Downed** — body
- [x] Lovin' under a blanket (a subtle bob): **Lovin** — add
- [p] Feeding a baby, sitting or standing: **Breastfeed**, **BottleFeedBaby**, **BabySuckle** — upper (for now the `carry_cradle` hold, laid over the sit too: `FeedsBaby`)
- [ ] Getting into a pod or casket: **EnterCryptosleepCasket**, **EnterBiosculpterPod**, **EnterBuilding**, **CreateAndEnterCocoon** — body, one-shot

## Mental states and conditions
- [ ] Sad wander: slumped and slow (Wander_Sad, Wander_Psychotic) — body (a walk variant)
- [ ] Dazed or catatonic, standing vacant or lying (Catatonic) — body
- [ ] Berserk and tantrum: stomping and smashing: **SmashThing** — upper
- [ ] Binging: eating or drinking on the floor — upper over the sit
- [ ] On fire: arms flailing over the run — upper
- [ ] Drunk or high: a sway over the walk — add
- [x] Vomit, bent over: **Vomit** — body (`work_vomit`, `walk.vomit_at`: the end of Meshy's Catching_Breath, hands on the knees, panting, with a heave a loop; played as a work kind facing the cell)
- [x] Hunker under fire, Combat Extended's **HunkerDown** — body (`duck_R`/`_L`, `walk.duck_at`, Meshy's CrouchLookAroundBow, crouched low looking round, the weapon arm held in by IK; `WalkFigures.DuckOf`. Not yet checked in game)
- [x] Dive from a live explosive, added to the game's sprint away — body (`dive_R`/`_L`, `walk.dive_at`, played backwards to get up; `prone_R`/`_L`, `walk.prone_at`; `WalkFigures.DivesFromBlast`, `ProneOf`. Not yet checked in game)
- [x] Cower, crouched with the hands drawn up in front of the chest: **FleeAndCower** — body (`hunker`, `walk.hunker_at`: crouched on bent knees, hunched forward with the head a little bowed and the cower's hands, in place of the idle when it stands still; on the move `cower` over the run)
- [ ] Shivering (hypothermia), panting (heatstroke) — add (first pass in for the cold: `rub`, `walk.rub_at`, the hands rubbed for warmth now and then while hypothermic or colder than comfortable, `WalkFigures.ColdOf`, `BoutOf`. Not yet a shiver, or panting)

## Ages and builds
These are not new actions but variants of the clips above.

- [x] Babies: crawl, sit, lie, being carried (**Carried**)
- [ ] Children: a shorter stride, plus the play motions
- [x] Body types (Male, Female, Thin, Fat, Hulk): only if the shape keys and the joint correction in `End` are not enough (for example a wider stance for Fat and Hulk)

## Layering

Most of the list above is an **upper** or **add** motion. If every combination is baked as a whole-body clip, the number of clips multiplies: aim × (stand, walk, run1..6, crawl), carry × every gait, and so on. Separate layers make each motion one clip.

**What the runtime does now.** `anim.txt` still stores each bone's turn in model space, but `ReadAnim` turns them into parent-relative turns on load, and `End` rebuilds the chain parents first: only the root (hips) keeps the clip's position, and every other head is placed from its parent's turn (which also does the body-type joint correction). `Add` blends whole clips, every bone alike. `Over` lays a clip on top with a weight per bone from the clip's `over` line in `anim.txt` (written from `walk.UPPER`), slerping each bone's local turn toward it. The drafted stance uses this. Clips can also be timed by the game clock (a clip line's fourth field, in game seconds): the idle and drafted loops are.

**What layering needs:**

1. **Parent-relative turns.** Convert each frame to local turns (`local = parent⁻¹ × turn`), either in the exporter or once at load. Then compose down the chain in `End`, which already walks bones parents-first (`boneOrder`) and rebuilds each head from its parent's turn for the shape correction. Only the root (hips) keeps a model-space position. Heads then come from the chain instead of from the clip, so the arm follows whatever the torso does.
2. **Bone masks.** Use a weight per bone per layer, not one per clip. The usual split for these motions:
   - lower: hips, legs and feet, from the gait (idle, walk, run, sit, kneel);
   - upper: from `spine.003` (chest) up through the arms, neck and head, from the action;
   - the lower spine shared (e.g. 50/50) so the waist twists between the two instead of snapping.
   The mask could be a custom property on the Blender action (a list of bones, or `upper`/`lower`/`body`) written into `anim.txt` with the clip.
3. **Additive layers.** Breathing, flinch, drunk sway, shivering, head-bow and look-at are stored as the difference from a reference pose (usually the clip's first frame or the rest pose) and multiplied on top of the blended pose after the masked layers. Then one flinch works on a standing, walking, seated or aiming pawn alike.
4. **One-shot and held clips.** `Add` loops every clip (`Mathf.Repeat`). Actions need their own timing: a one-shot played once and held on its last frame (pick up, death fall, knock-down), or a clip timed by the game (the verb's warm-up, burst and cool-down that `FigureWeapons.cs` reads; the stroke timing `Work.cs` uses), with short cross-fades in and out per pawn, like the gait eases now.
5. **Props in the hand.** Started: `SkinnedFigure.JointOf` gives a joint's posed place, and `WalkFigures.DrawWeapon` puts the weapon slab along the stance's hands. IsoRim places tools (`Work.cs`) and weapons (`FigureWeapons.cs`) in figure space. With animated arms they should ride a hand bone. That could mean a hand bone's transform exposed from `End`, plus a grip offset per tool or weapon kind. Two-handed grips (rifles, axes) may want a small IK fix for the off hand.
6. **Procedural on top.** Aim pitch toward the target, the head turning to a chat partner or the work, and the torso yaw toward the aim (so the legs can keep walking the path) can all be set as bone turns after the clips, without baking any of them.

**Exporter side.** `walk.py` builds the walk and run procedurally. New motions are easier keyframed by hand as named Blender actions on the metarig. `export_frames.py` could then export every action in the .blend as a clip (with its mask and loop/one-shot flag from custom properties). Static `*.obj` frames are only needed for the non-skinned fallback in `WalkFigures.cs`, so layered clips could skip them.

**Order of work.** Steps 1 and 2 are done in a basic form (one overlay buffer, masks from the exporter). Step 3 has a first form: `SkinnedFigure.Nudge` adds a clip's turns from its first frame on top of any pose (the flinch uses it). Breathing could move onto it next: it is baked into the idle now, so a walking or drafted-walking pawn does not breathe. Without them, nothing in the upper column can be combined with the gaits. Step 4 comes next, since most actions are not loops. Steps 3, 5 and 6 can come as each motion needs them.
