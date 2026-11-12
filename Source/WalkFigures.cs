using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace M3AT
{
    // People drawn as the chibi model (Models/pawnHumanRig.blend), walking, in place of IsoRim's figure
    // (People.Draw). The walk is baked in Blender to one posed OBJ per frame (anim_scripts/export_frames.py ->
    // Models/chibi); the frame shown follows
    // IsoRim's own stride tracker (Animals.Track), which steps a phase by the ground the pawn covers, so the feet keep
    // pace with how fast the pawn really moves. Standing still shows the idle pose.
    //
    // Faster than RunFrom (cells per game second, from the game's own path cost, so the same at any game speed) the
    // walk gives way to a run, fully at RunFull. The exporter bakes the steps between (run1 .. runN, walk.RUN blended
    // in), and each pawn eases through them over about half a second; the stride handed to the tracker is blended
    // with them, so the swing phase carries on unbroken and the feet stay in step through the change.
    //
    // With Facial Animation the pawn's face is drawn on the head (FaceLayers), on the face patch the exporter writes with
    // each frame (face_*.obj); an export without them leaves the head bare.
    //
    // With Skinned on and the export's skin.txt and anim.txt present, the figure is skinned at runtime instead
    // (SkinnedFigure): the same clips, but blended between frames, between the gait steps either side of the pawn's pace
    // and from the idle pose into the walk as IsoRim's gait comes up, rather than snapped to the nearest baked frame.
    // It takes the shape of the pawn's body type (the body's shape key of that name, if it has one), with its gender's key
    // added on a Thin, Fat or Hulk body by GenderOnBuilds and the head shrunk by HeadShrink (the mod's options), and wears the garments the export has for what the pawn has on: each worn apparel on a garment's
    // layer covering one of its body part groups, in that apparel's colour as IsoRim shows it. Its hair is made from that
    // hairstyle's sprites (SkinnedFigure.DrawHair), built in the background the first time it is seen (HairBuilder) from
    // whichever hair textures are loaded, in its hair colour; Shaved and Bald leave the head bare.
    //
    // Standing, the skinned figure breathes and shifts its weight (the export's idle loop, on the game clock). Drafted, it
    // eases into a more upright stance with its hands held for the weapon it has (KindOf: rifle, pistol, bow, melee or
    // fists, the fists only with an enemy near: ThreatNear), and keeps that upper body over the walk and run (SkinnedFigure.Over); a one-handed stance leaves the free
    // arm swinging with the stride. Aiming, it brings the weapon up out of that stance and points it at the target, pitched
    // as IsoRim pitches its own figure's, kicking with each shot; a bow is drawn over the warm-up and loosed (LayAim).
    // Standing still it takes the aim's own stance, legs and all. Fighting hand to hand it swings, slashes, stabs or
    // punches with each blow, butts or jabs with a gun or bow, and bends down to a target on the ground, and it throws a grenade or a pilum overhand (StrikeOf).
    //
    // The skinned figure also sits (the export's sit loop, its seat put on the chair's: SkinnedFigure.SeatOf), lies on its
    // back in bed, on the ground or in a building that shows who is inside (the lie loop, laid down and placed as IsoRim
    // places its own figure; a corpse at its first, breathless frame), and crawls when downed (the crawl, by the ground
    // covered like the walk, tipped face down by the game's crawl angle). In a bed whose blanket covers the sleeper, the
    // body is sunk under it so only the head shows. Everyone IsoRim draws as a person is handled, not only colonists.
    //
    // Left to IsoRim's figure: statues, corpses gone to bones or in a gibbet cage, people floating upright in a vat and
    // people another mod places (Api.AttachedPlace). Apparel without a garment mesh is not drawn on the model; the held
    // weapon is IsoRim's solid sprite in the stance's hands (DrawWeapon), on the skinned figure only. The baked frames have
    // no hair and only stand and walk (anything else falls through to IsoRim's).
    //
    // Carrying (skinned figure only), the arms, chest and head take the load's carry loop over the gait as the drafted
    // stance does (CarryClip): a hauled thing is held out in front and drawn by IsoRim between the hands (Carried.Draw); a
    // person is carried in the arms, or over the shoulder if they are a prisoner, hostile to the carrier or being arrested,
    // captured or kidnapped (Fireman). The person, alive or dead, is posed by the export's carried_<kind> loop, which is
    // made in the carrier's rest space, so it rides the carrier's chest bone as posed (Hold, DrawHeld).
    //
    // A pawn with one leg working worse than the other (hurt, missing, a poor prosthetic) limps on the move, favouring it
    // (LimpOf: the export's limp_L or limp_R in place of the walk and run, by how much worse), and one with a leg gone
    // hops on the other (hop_L or hop_R, the same way, whatever the injury animations option says). In water it is sunk to the
    // depth, or to the knee in marsh (WaterOf; IsoRim's water is an opaque sheet and its marsh ground, so what is under
    // them is hidden) and wades (the export's wade, in
    // place of the walk, run and limp, as the water comes up to shallow). Swimming (the game's GoSwimming) it does the
    // front crawl on the move, laid face down like the crawl with the head end raised, and treads water upright when
    // still, sunk to the neck. A pawn hurt badly enough somewhere other than its legs holds the worst of it and hunches
    // over it (HurtOf: the export's hurt_<place>_<side>, from the waist up over whatever the legs do). A pawn fleeing and
    // cowering (FleeAndCower, FleeAndCowerShort) hunches forward with both hands drawn up in front of its chest, from the
    // waist up over the run (the export's cower), and standing still crouches on bent knees as well (hunker). A pawn
    // leading an animal on a rope reaches its right hand a little back to hold it (the export's lead, that arm only). A pawn
    // casting a psycast or ability raises its left hand to the target (CastOf: the export's cast or cast_touch), one hit
    // flinches away from it (FlinchOf: flinch_<way>, added on top of any pose), one staggered stumbles to one knee and gets
    // up again (reel, by how far through the stagger it is: ReelOf) and one stunned stands slumped and dazed (daze). A pawn jumping (a
    // jump pack, a mech's jump launcher: the game's PawnFlyer) crouches over the jump's warm-up, plays the export's jump
    // through the flight by how far along it is (FlightOf), and drops into the crouch again on landing (land). A pawn standing
    // at its work (WorkOf: the jobs IsoRim's Work.cs gives a tool, sorted the same way) faces it and plays one stroke of
    // that tool over and over (the export's work_<kind>, the whole figure in place of the idle), with IsoRim's own tool
    // model riding its right hand (DrawTool). All of these are on the skinned figure only.
    [StaticConstructorOnStartup]
    public static partial class WalkFigures
    {
        private const string Tag = "[M3AT] ";
        internal const string Folder = "Models/chibi";
        private const float ScaleUp = 1.2f;   // on top of IsoRim's size for the life stage: the baked figure is 1.25 cells tall, which looks tiny beside furniture
        private const bool ColonistsOnly = false;   // true: everyone else keeps IsoRim's figure, to compare side by side
        private const float WalkingGait = 0.5f;   // the placing's gait (0 still .. 1 walking) from which the walk shows
        private const float RunFrom = 1.5f;   // cells per game second: the run starts to blend in (a colonist ambles at ~1.5,
        private const float RunFull = 2.5f;   // walks at ~2.3 and hurries at ~4.6 on a floor) .. and is full
        private const float RunEase = 2f;   // how fast a pawn's run amount follows its speed, per real second
        private const float DraftEase = 3f;   // how fast a pawn takes up (or drops) the drafted stance, per real second
        private const float WeaponScale = 0.9f;   // a held weapon's size, a share of the size the game draws it at on the floor
                                                  // (the chibi's short arms want it smaller)
        private const float HandGrip = 0.03f;   // model units from the wrist along the forearm to where the hand closes
        private const float WeaponShown = 0.5f;   // how far into the stance the weapon is put in its hands
        private const bool Skinned = true;   // the runtime-skinned figure (SkinnedFigure), when the export has it; false: the baked frames
        private const float GenderOnBuilds = 1f;   // 0 .. 1 of the Male or Female key added to a Thin, Fat or Hulk body (skinned figure only)
        private const float BirthSize = 0.25f;   // a newborn's size against a grown figure's (before ScaleUp), growing along Growth to a grown one's
                                                 // by the race's last life stage, in place of IsoRim's size by life stage
        // How far from BirthSize to grown a pawn has come (0 .. 1), by its age as a share of the race's grown age. From
        // children's median heights (boys' and girls' averaged, grown at 18): fast as a baby, steady through childhood, then
        // a last spurt in the early teens that is mostly done by 16, so a teenager is nearly grown rather than three quarters.
        private static readonly SimpleCurve Growth = new SimpleCurve
        {
            new CurvePoint(0f, 0f),        // birth
            new CurvePoint(0.056f, 0.21f), // 1
            new CurvePoint(0.111f, 0.31f), // 2
            new CurvePoint(0.167f, 0.38f), // 3
            new CurvePoint(0.278f, 0.49f), // 5
            new CurvePoint(0.444f, 0.65f), // 8
            new CurvePoint(0.556f, 0.74f), // 10
            new CurvePoint(0.667f, 0.84f), // 12
            new CurvePoint(0.778f, 0.93f), // 14
            new CurvePoint(0.889f, 0.97f), // 16
            new CurvePoint(1f, 1f),        // 18
        };
        private const float GenderFrom = 0.61f;   // of the race's grown age: the gender gap starts opening (11 of 18)
        private const float GenderFull = 0.89f;   // of the race's grown age: the gender gap is all there (16 of 18)
        private const float GenderGap = 0.1f;   // of the standard size: how much bigger a grown male is drawn than a grown female, split between them
                                                // (men's mean height is about 8% over women's)
        private const float HeightMost = 2.5f;   // standard deviations: the furthest from the usual size a pawn is drawn (HeightSize)
        private const uint HeightSeed = 0x4d1a7u;   // mixed with the pawn's id, so its height doesn't follow any other roll made from its id
        private const uint BuildSeed = 0x2b9e3u;   // and its build (VariedBuild), so a tall pawn isn't also always a heavy one
        private const float BuildSteps = 8f;   // varied bodies: each key's amount goes in eighths, so pawns share shapes
        private const float BuildMost = 2.5f;   // standard deviations: the furthest a varied build is put from its body type's
        private const float FatShare = 0.4f;   // of the spread: how far a varied build that isn't the Fat body type goes into the Fat key,
                                               // which is extreme, so its full share made too many plain pawns look heavy
        private const float BabyYears = 3f;   // years: the age the baby's shape grows over
        private const float PregnantSteps = 20f;   // how many shapes a pregnancy grows through (each is made and kept once per body type)
        private const float BabyLimbsYears = 10f;   // years: the age the Babylimbs key, full at birth, is gone by (skinned figure only)
        private const float BabyLimbsSteps = 20f;   // how many shapes the limbs grow through (each is made and kept once per body type)
        private const float BabyHeadShrink = 0.25f;   // of the mod options' head shrink a newborn takes, growing to all of it as the Babylimbs key goes (HeadShrinkOf)
        private const string BabyBody = "Fat";   // the body key a newborn takes in full, with no gender's, gone by BabyYears: round and chubby (skinned figure only)
        private const string BedMiddleClip = "sleep";   // the lying clip the others are lined up with in a bed (LaidMiddleOf)
        private const float LieMiddle = 0.6f;   // of the head's height: how far along a figure lying on the ground (or crawling)
                                                // the spot it is drawn at is, from its feet (in bed it is the head)
        private const float CarryEase = 4f;   // how fast a pawn takes up (or drops) a carrying pose, per real second
        private const float PlayEase = 1.5f;   // how fast the baby in its arms is rocked (carry_play) or let settle, per real second
        private const float BendDown = 0.2f;   // of the bend's length (walk.BEND_SECONDS): reaching the ground
        private const float BendHold = 0.35f;   // and staying there until this far in; the rest is coming back up
        private const float BendHurry = 2.5f;   // how much faster the bend plays at a full stride
        private const int DropTicks = 120;   // the longest a person set down is still drawn in the carrier's arms (Dropping)
        private const float LoadReach = 0.2f;   // model units: a hauled thing's middle is at least this far in front
        private const string CarryBone = "spine.002";   // the bone a carried person rides (walk.CHEST_BONE)
        private const float Lift = 0.04f;   // model units (cells before size): IsoRim's outline hull reaches 0.03 past the
                                            // soles, which sit at y 0, so unlifted the ground hides the rim under the feet
        private const float LimpFrom = 0.1f;   // how much worse one leg works than the other (0 .. 1) before a pawn limps
        private const float LimpFull = 0.6f;   // .. and from which it limps fully (a missing leg is 1, a peg leg about 0.4)
        private const int LimpCheck = 120;   // game ticks between looks at a pawn's legs
        private const int HandFingers = 4;   // fingers on a hand besides the thumb: all gone, the chibi's finger bone is cut
        private const int FootToes = 5;   // toes on a foot: all gone, the chibi's toe bone is cut
        private const float ThreatRange = 15f;   // cells: an enemy this near puts a bare-handed drafted pawn's fists up
        private const int ThreatCheck = 60;   // game ticks between looks round for one
        private const float BalanceEase = 3f;   // how fast a pawn on one leg takes its weight over the foot or off it, per real second
        private const float LimpEase = 1f;   // how fast a pawn's limp follows its legs, per real second
        private const float HurtFrom = 0.15f;   // how hurt a place is (HurtOf: its damage over its health) before a pawn holds
                                                // it (all the way: a hand half way to a wound looks wrong) and starts to hunch
        private const float HurtFull = 0.5f;   // .. and from which it hunches all the way
        private const float HoldEase = 2.5f;   // how fast a hand goes to a wound or leaves it, per real second
        private const float HurtHealth = 15f;   // a part's health counted as at least this, so a cut finger is not a crushed chest
        private const float HurtTended = 0.5f;   // share of a tended wound's damage that still counts
        private const float CowerEase = 3f;   // how fast a pawn takes up (or drops) cowering, per real second
        private const float LeadEase = 3f;   // how fast a pawn's hand goes to a rope it leads by, or leaves it, per real second
        private const float SadEase = 1f;   // how fast a pawn slumps into a sad wander (Sad), or straightens out of it, per real second
        private const float DrunkFrom = 0.2f;   // AlcoholHigh's severity from which a pawn sways (tipsy) ..
        private const float DrunkFull = 0.75f;   // .. and from which it sways all the way (hammered)
        private const float DrunkEase = 0.3f;   // how fast the sway comes on or wears off, per real second
        private const float DrunkWeave = 0.07f;   // cells a pawn drunk all the way is drawn to each side of its path on the move, with the sway
        private const float DrunkAiming = 0.5f;   // share of the sway kept while aiming, as it steadies itself for the shot
        private const float StirRestFrom = 5f;   // game seconds a cook works between bouts of stirring, at least
        private const float StirRestTo = 14f;   // .. and at most
        private const float BurnEase = 4f;   // how fast a pawn on fire takes to patting at the flames (Burning), or leaves off, per real second
        private const int GlanceTicks = 150;   // game ticks a pawn looks at whom it had a social interaction with (InteractedPostfix)
        private const float GlanceLook = 0.8f;   // share of the way it turns its head round toward them (LessonLookOf)
        private const float MealScale = 0.5f;   // of the carried size: a meal being eaten (DrawMeal), by the game's FoodMeals category
        private const float FoodScale = 0.3f;   // .. and any other food being eaten (pemmican, berries, chocolate)
        private const float MealTurn = 90f;   // degrees about the vertical a meal being eaten is turned
        private const float MealTableTurn = 0f;   // .. and one on a table: its long side across the eater, not toward it
        private const float MealFromEdge = 0.15f;   // cells in from the table's edge on the eater's side to a meal's middle there
        private const float PalmLift = 0.022f;   // model units from the hand's bone up to its palm's face (walk.HAND_HALF), where a meal eaten standing sits
        private const float CarriedItemScale = 0.8f;   // as IsoRim draws a carried item: its share of the game's size
        private const float EatEase = 3f;   // how fast a pawn takes up eating (or leaves it), per real second
        private const float TeachLook = 0.55f;   // share of the way a teacher turns its head round toward the pupil (LessonLookOf)
        private const float PupilLook = 0.35f;   // and a pupil toward the teacher
        private const float LookMost = 70f;   // degrees a look turns the head at most, either way
        private const float LookSpeed = 120f;   // degrees a second the head turns to a look, or back
        private const float TeachGlance = 0.74f;   // where in the teach loop it pats the desk and looks down at it (walk.TEACH_AT[6])
        private const float TeachGlanceWidth = 0.12f;   // the look eases off within this much of the loop either side of it
        private const float TeachGlanceKeep = 0.25f;   // share of the look kept at the glance
        private const int TossInterval = 400;   // game ticks between throws at horseshoes or hoopstone (JobDriver_PlayHorseshoes' HorseshoeThrowInterval)
        private const float TossRelease = 0.6f;   // walk.TOSS_RELEASE: how far through the toss clip the thing leaves the hand
        private const float TossIn = 0.2f;   // walk.TOSS_AT[1]: the toss is eased in from the idle over this much of it
        private const float TossOut = 0.85f;   // and back out to it from here, as the foot steps back
        private const int SnowInterval = 40;   // walk.SNOW_INTERVAL: game ticks between snowballs (Romance on the Rim's JobDriver_SnowballFight)
        private const float SnowRelease = 0.55f;   // walk.SNOW_RELEASE: how far through the snowball clip it leaves the hand
        private const float KissGap = 0.42f;   // cells between two grown kissers' middles as drawn (KissOf), so the faces meet
        private const float ShareLook = 0.6f;   // share of the way a pawn sat beside a reader turns its head toward the book (LessonLookOf)
        private const float ShareBook = 0.3f;   // cells in front of the reader the book is looked at
        private const float ShareCloser = 0.15f;   // cells a reader and its watcher sat side by side are each drawn toward the other (SharePartnerOf)
        private const float ShareTurn = 25f;   // degrees each is turned on its seat toward the other, at most
        private const float ShareLean = 5f;   // degrees each leans sideways toward the other from the waist
        private const float ShowEase = 1.5f;   // how fast a reader holds the book out to a watcher sat beside (read_show_), or takes it back, per real second
        private const float ListenCloser = 0.3f;   // cells a listener at a performance is drawn toward the performer
        private const float ListenGap = 0.55f;   // but no nearer than this to the performer's middle, cells at a grown pawn's size
        private const float ListenEase = 0.8f;   // how fast it steps in, or back, per real second
        private const float ListenLook = 1f;   // share of the way it turns its head the rest of the way toward the performer
        private const float TalkEase = 2f;   // how fast a pawn takes up talking (a board game, a speech) or stops, per real second
        private const float PlayerReach = 3f;   // cells round a board game the other players are looked for in (PlaysWithOthers)
        private const float BoutEdge = 0.1f;   // of a bout (BoutOf): eased into at its start and out of at its end
        private const float RubRestFrom = 15f;   // game seconds a cold pawn rests between bouts of rubbing its hands, at least
        private const float RubRestTo = 40f;   // .. and at most
        private const float RubEase = 3f;   // how fast a pawn's hands leave off rubbing as it sets off somewhere, or may again, per real second
        private const float ChatRestFrom = 6f;   // game seconds a pawn at a party rests between bouts of talking, at least
        private const float ChatRestTo = 20f;   // .. and at most
        private const float ChatReach = 4.5f;   // cells round a pawn relaxing socially (or at a party) it looks for someone to talk to in (ListenerOf)
        private const int ChatLook = 60;   // game ticks between looks for someone to talk to while none is there
        private const float ChatSeatTwist = 60f;   // degrees a seated pawn turns its trunk toward whom it talks to, at most
        private const float ReloadEase = 4f;   // how fast a pawn takes up reloading (or leaves it), per real second
        private const float CastEase = 3f;   // how fast a pawn raises its hand to cast (or lowers it), per real second
        private const int FlinchTicks = 24;   // game ticks a flinch takes, from the hit back to the stance (walk.FLINCH_FRAMES)
        private const float FlinchLeast = 0.5f;   // a hit's flinch for the least damage, as a share of the full one
        private const float FlinchFull = 12f;   // and the damage from which it is full
        private const float FlinchDeflected = 0.4f;
        private const float FlinchMissed = 0.4f;   // a missed or dodged melee attack's flinch, as a share of the least hit's   // a deflected hit's flinch (armour stopped it), as a share of a hit's
        private const int HitStagger = 95;   // game ticks the game staggers a pawn for when it is hit (Bullet, melee, blasts), before StaggerDurationFactor
        // Whether a stagger plays the reel (a stumble to one knee) at all. Off: pawns spend much of a fight staggered, and a
        // stumble each time looked wrong, so a staggered pawn keeps on with whatever it was doing; a hit's flinch shows it.
        private const bool StaggerReels = false;
        private const float ReelEase = 4f;   // how fast a pawn is thrown off balance (staggered) or finds it again, per real second
        private const float ReelHold = 0.5f;   // of the stumble (walk.REEL_AT): down on the knee, where a longer stagger holds it from
        private const float DazeEase = 2f;   // how fast a pawn slumps when stunned or comes round, per real second
        // Mining: the game strikes the rock (its sound and flash, EffecterDefOf.Mine) when JobDriver_Mine's ticksToPickHit
        // runs out, every round(100 / MiningSpeed) ticks, so work_pickaxe is played from that count (PickPhase) rather
        // than the clock, the blow landing on that tick. PickHit is where in the stroke the point goes in (walk.work_stroke's
        // chop: the blow ends at 0.7), PickBounce how far the small bounce off the rock after it runs (to the end of the
        // stroke, where the pick rests in the rock again).
        private const float PickHit = 0.7f;
        private const float PickBounce = 0.3f;
        private const float PickSlowest = 1.25f;   // a stroke is played at most this much slower than the clip's own length; a slower miner rests the pick in the rock between strokes
        // Beating out a fire: the game beats once each time its beat verb's 1.1 s cool-down ends (Verb_BeatFire), so
        // work_stomp and work_pat are played from those beats (BeatPhase) rather than the clock, a blow landing on the tick
        // the game beats. Where in each clip a blow lands, and how many blows a loop (walk.STOMP_HIT, walk.PAT_HIT and
        // walk.PAT_HITS: the stomp is one foot's stamp a loop, the pat a slap with each hand in turn).
        private const float StompHit = 0.5f;
        private const float PatHit = 0.25f;
        private const int PatHits = 2;
        private const int BeatLongest = 180;   // ticks between beats past which the last one is forgotten (a new fire, a break)
        private const float WorkEase = 3f;   // how fast a pawn takes up its work stance (or leaves it), per real second
        private const float ToolShown = 0.5f;   // how far into the work stance before the tool shows in its hand
        private const float WorkReach = 8f;   // squared cells: work further than this is still being fetched for (as Work.cs)
        private const float BenchFill = 0.3f;   // work filling this much of its cell or more is at a bench, less at the ground (PlaceWork)
        private const float AnimalHigh = 0.5f;   // cells: an animal whose back is drawn this high or more is milked or shorn stood at it (bench), lower knelt by it (crouch; AnimalKind)
        private const float AnimalBigBody = 1f;   // the same by body size (times its life stage's factor), when IsoRim can't say how tall it is drawn
        private const float BuildHigh = 0.3f;   // cells: something being built whose drawn top is this high or more is built stood at it (bench), lower knelt at the ground (crouch; BuildKind)
        // Kneeling by someone lying on the ground (KneelBeside):
        private static readonly string[] LyingBones = { "spine", "spine.002", "spine.004", "spine.006", "upper_arm.L", "forearm.L", "hand.L",
            "upper_arm.R", "forearm.R", "hand.R", "thigh.L", "shin.L", "foot.L", "toe.L", "thigh.R", "shin.R", "foot.R", "toe.R" };   // the joints of theirs it keeps off
        private static readonly string[] KneelBones = { "spine", "thigh.L", "shin.L", "foot.L", "toe.L", "thigh.R", "shin.R", "foot.R", "toe.R" };   // and of its own, those below KneelLow
        private const float KneelLow = 0.3f;   // cells, at size 1: how high a joint of the kneel may be and still be on the ground
        private const float KneelClear = 0.14f;   // cells, at size 1: how near one of its joints may come to one of theirs
        private const float ChestInset = 0.1f;   // cells, at size 1: how far out from their chest's middle its hands go (on its near side)
        private const float VisitBack = 0.4f;    // cells, at size 1: how much further out a visitor sat beside them puts its hands (on its knees), as it sits back to talk rather than reaching to tend
        private const int KneelWays = 24;   // the ways round the chest it looks at
        private const float KneelOverlap = 100f;   // the cost of a joint (cells into the clearance, squared)
        private const float KneelSquare = 0.3f;   // of kneeling along the body rather than square to it
        private const float KneelKeep = 0.4f;   // of turning right round from the way it kneels already
        private const float KneelNear = 0.15f;   // of kneeling on the far side of the body from where it is
        private const float KneelCame = 1f;   // off a bed: of kneeling off the side it came from (which beats square to the body)
        private const float KneelCameSpread = 50f;   // degrees: and the furthest round from that side it looks, to keep off the limbs
        private const float FeedCloser = 0.35f;   // cells: how far a pawn feeding a patient in a bed (tame) is drawn toward them from its cell's middle
        private const float BenchCloser = 0.3f;   // cells: how far a pawn stood at a bench (work_bench) is drawn toward it from its cell's middle
        private const float FireCloser = 0.1f;   // cells: how far a pawn sat at a campfire (work_stir_campfire) is drawn toward it, its roasting stick out over the fire (walk.ROAST_AIM) and its head short of it
        // Playing music (InstrumentKind): how far a pawn is drawn toward the instrument from the middle of its
        // interaction cell, stood at it and sat on a seat there (cells; walk.MUSIC's 'closer', keep the two in step). The
        // keyboards are two cells off and the harp and drum one, out of the chibi's reach; a chair is drawn in the
        // middle of the cell, so a seated figure is drawn only a little off it.
        private static readonly Dictionary<string, float> InstrumentCloser = new Dictionary<string, float>
        {
            { "piano", 0.35f }, { "piano_sit", 0.3f }, { "harp", 0.62f }, { "harp_sit", 0.33f }, { "drum", 0.45f }, { "drum_sit", 0.33f },
        };
        private const float LandDown = 0.1f;   // of the landing crouch's length (walk.LAND_SECONDS): sinking into it
        private const float TakeOff = 0.15f;   // of the flight: springing up out of the warm-up's crouch
        private const float RiseHurry = 2.5f;   // how much faster than the export's getup a pawn gets up (the game stands it up at once)
        private const float RiseOut = 0.75f;   // of the getup: from here it is eased into the standing pose
        private const float DropRise = 1.2f;   // how much faster than the dive (backwards) a pawn gets up
        // Taking cover (CoverOf): crouched low (SkinnedFigure.Crouch) while standing still in one of Combat Extended's crouching jobs
        // (or, without it, waiting or holding fire drafted) with an awake enemy in sight within the weapon's range, as deep as
        // still lets the hands clear the cover beside it as IsoRim draws it.
        private const int CoverCheck = 60;   // game ticks between looks for an enemy in sight and at the cover round about
        private const int CoverHold = 180;   // game ticks it stays down after it last saw one, so it doesn't bob up as one goes behind a wall
        private const float CoverClear = 0.08f;   // cells the drafted hands are kept over the top of the cover
        private const float CoverDown = 0.35f, CoverUp = 0.5f;   // game seconds to get all the way down into the crouch, and up out of it
        private const float DuckDown = 0.25f, DuckUp = 0.4f;   // game seconds to get down flat under fire, and up again, on an export without the dive
        private const float DropIn = 0.12f;   // of the dive: taken over from what it was doing by here
        private const float DropLoop = 0.85f;   // of the dive: from here it is eased into the loop lying there
        private const float DropRest = 0.7f;   // of the dive: from here it is let down onto its lowest point (before that, only kept out of the ground)
        private const float WeaponLaidFrom = 0.3f, WeaponLaidTo = 0.75f;   // of the dive: a held weapon goes from its stance's line to lying along the ground
        private const float WeaponLaidOut = 0.12f;   // that line's way out from the figure's front, per cell along it, past the head
        private const float FallHurry = 1.5f;   // how much faster than the export's falls (knockdown, die) a pawn goes down
        private const float FallOut = 0.8f;   // of a fall: from here it is eased into the lying loop it ends in
        // Ragdolls (Ragdoll.cs): a pawn that goes down on the ground where it stood falls limp from the canned fall, and
        // lies as it landed. Drawn only, unless flung (Flings): the game otherwise keeps the pawn where it was.
        public static bool Ragdolls   // false: the canned fall and lying loops only (the mod's options set it)
        {
            get { return SettingsMod.Settings == null || SettingsMod.Settings.ragdolls; }
        }
        public static bool FlingBodies   // a big enough blow moves the body in the game (Flings); ragdolls only
        {
            get { return Ragdolls && SettingsMod.Settings != null && SettingsMod.Settings.flingBodies; }
        }
        // What the option scales a blow's speed by. The option is in distance, FlingDistance of it at 100%, and a thrown
        // thing goes about as far as the square of its speed, as both its time in the air and its slide along the ground go
        // up with it.
        private const float FlingDistance = 1.5f;
        public static float FlingPower
        {
            get { return Mathf.Sqrt(FlingDistance * (SettingsMod.Settings != null ? SettingsMod.Settings.flingPower : 1f)); }
        }
        public static bool InjuryAnims   // false: no limp (LimpOf) and no holding a hurt place (HurtOf); flinches and hopping on one leg stay
        {
            get { return SettingsMod.Settings == null || SettingsMod.Settings.injuryAnims; }
        }
        public static bool VisibleWounds   // false: no injuries drawn on the pawn (Wounds)
        {
            get { return SettingsMod.Settings == null || SettingsMod.Settings.visibleWounds; }
        }
        public static bool Beards   // false: no beard drawn or built (BeardOf)
        {
            get { return SettingsMod.Settings == null || SettingsMod.Settings.beardMode != BeardMode.Off; }
        }
        public static bool FlatBeards   // true: beards are the sprite drawn flat on the face patch, not 3D meshes (DrawDressed)
        {
            get { return SettingsMod.Settings == null ? true : SettingsMod.Settings.beardMode == BeardMode.Flat; }
        }
        public static bool FlingWeapons   // the weapon dropped on going down flies from the hand (Flings); ragdolls only
        {
            get { return Ragdolls && SettingsMod.Settings != null && SettingsMod.Settings.flingWeapons; }
        }
        public static bool RetexturedWeaponModels   // the game's own weapons keep IsoRim's model whoever supplies their art (WeaponRule)
        {
            get { return SettingsMod.Settings == null || SettingsMod.Settings.retexturedWeaponModels; }
        }
        public static bool BedBlankets   // IsoRim's blanket over a sleeper in a head-only bed (DrawTuckedIn); false: lying on top of the bed
        {
            get { return SettingsMod.Settings != null && SettingsMod.Settings.sleepWear == SleepWear.Blankets; }
        }
        public static bool VatClothes   // a pawn in a growth vat or biosculpter pod is drawn in its clothes; false: without them (InVat)
        {
            get { return SettingsMod.Settings != null && SettingsMod.Settings.vatClothes; }
        }
        public static bool GroundGarments   // apparel on the map is baked as the chibi's garment (GroundClothes); false: IsoRim's look
        {
            get { return SettingsMod.Settings != null && SettingsMod.Settings.groundGarments; }
        }
        // Flinging a body (Flings). A blast flings from FlingBlastFrom damage, any other blow with a way to it from
        // FlingMeleeFrom or, a shot, FlingShotFrom: speeds in cells per game second along the ground, at the least and at
        // the Full damage, for a grown body (a lighter one goes further, within FlingLightest..FlingHeaviest times).
        private const float FlingBlastFrom = 10f, FlingBlastFull = 200f, FlingBlastLeast = 2f, FlingBlastMost = 5f;
        private const float FlingMeleeFrom = 5f, FlingMeleeFull = 60f, FlingMeleeLeast = 1f, FlingMeleeMost = 3.5f;
        private const float FlingShotFrom = 5f, FlingShotFull = 40f, FlingShotLeast = 1f, FlingShotMost = 2.2f;
        private const int FlingGatherTicks = 2;   // shots or blows this close together count as one (a shotgun's pellets; the fastest guns are 2 ticks a round)
        private const float FlingSameWay = 45f;   // degrees: and only if they came within this of the same way
        private const float FlingLetGo = 2f;      // cells per game second: thrown faster than this, a falling body leaves the canned fall at once
        private const float FlingLightest = 1.25f, FlingHeaviest = 0.7f;
        private const float FlingUp = 0.5f;       // of its speed along the ground a body leaves the ground at
        private const float FlingSettle = 0.5f;   // cells per game second the place its hips are held to comes back to the middle of the cell it landed in
        private const float FlingLost = 3f;       // cells: a ragdoll this far from the body, not stepped while off screen, is dropped
        private const float UpperLetGo = 0.45f;   // of the fall: the upper body let go of the canned fall by here (a light blow)
        private const float LegsLetGo = 0.65f;    // and the legs
        private const float PushLeast = 0.3f;     // cells per game second the felling blow throws the body, at the least
        private const float PushMost = 1.6f;      // and at the most, from PushFull damage
        private const float PushFull = 30f;
        private const float PushBlast = 1.6f;     // times that for a blast
        private const float PushLying = 0.5f;     // of that for a blow to a body already lying
        private const int BlowTicks = 60;         // a blow this recent felled the pawn
        private const float RiseFromDoll = 0.35f; // of the getup: by here it has left the pose the ragdoll lay in
        private const float LieTurnSeconds = 1.2f;   // game seconds to turn from one lying loop into another (lieTurn)
        private const float TendRollSeconds = 1.5f;   // game seconds to ease a ragdoll out onto its back to be tended (dollOut)
        private const float BlockFill = 0.2f;     // a building filling this much of its cell is furniture a body lies against
        private const int BlockReach = 2;         // cells round the body looked at for walls and furniture
        private const float Shallow = 0.4f;   // cells of water over the ground: shallow water, just over an adult's knee
        private const float ChestDeep = 0.8f;   // and chest-deep water, to an adult's chest (deep water cannot be walked)
        private const float Marsh = 0.3f;   // and marsh, to an adult's knee (IsoRim draws it as ground, which hides what is under it too)
        private const float WaterLine = 0.012f;   // cells: the height IsoRim draws water at (the terrain's, plus 0.012)
        private const float FloatSink = 0.5f;   // a figure lying in water (downed, dead, fallen) sunk this share of its body's thickness, floating
        private const float HeadClear = 0.06f;   // model units: a wading figure's neck kept at least this far above the water
        private const float WaterEase = 2.5f;   // cells of depth a pawn sinks or rises by per game second, going in or out
        private const float SwimEase = 3f;   // how fast a pawn takes up (or drops) swimming, per game second
        private const float SwimLift = 15f;   // degrees a swimmer's head end is raised from level
        private const float SwimTurn = 1.5f;   // game seconds a swimmer takes to tip from treading water into the front crawl (or back)
        private const string NeckBone = "spine.004";   // the joint a swimmer is sunk to (walk.NECK)
        private const string ChestBone = "spine.002";   // the joint someone working on a lying body faces (WorkOf)
        private const float TrackTurn = 500f;   // degrees per real second IsoRim's tracker turns a figure
        private const float TurnLate = 0.1f;   // game seconds a figure takes to come round to its target once it is already firing
        private const float StepSlack = 40f;   // degrees the trunk turns over planted feet (to track an aim, a foe) before they step round
        private const float StepDone = 4f;   // and within how many degrees of the trunk's way they stop stepping
        private const float StepSettle = 0.75f;   // real seconds the trunk holds its way (StepHeld degrees a second or less) before the feet square up under it
        private const float StepHeld = 20f;
        private const float TwistMost = 70f;   // degrees the trunk turns over the feet at most: past it, the feet are swung round with it
        private const float StepSpeed = 240f;   // degrees per real second the feet turn while stepping round
        private const float StepTurn = 45f;   // degrees turned per step (half a walk cycle, one foot)
        private const float StepShare = 0.6f;   // the walk's weight on the legs while stepping round, against the standing pose's 1
        private const float StepEase = 8f;   // how fast the steps come in and out, per real second
        private const float AimEase = 6f;   // how fast a pawn brings its weapon up to aim (or lowers it), per real second
        private const int AimHold = 30;   // game ticks a pawn keeps facing its last aim once it stops aiming (the game turns a drafted pawn south between attacks)
        private const float AimHeight = 0.78f;   // model units: the aimed weapon over the floor (walk.AIMED's rifle fist), where the pitch is taken from
        private const int KickTicks = 15;   // game ticks a shot's kick takes to settle, at least (the weapon's recoilRelaxation if longer)
        private const float KickLeast = 0.5f;   // the kick of a weapon with little recoil (recoilPower), as a share of the full one
        private const int LooseTicks = 30;   // game ticks a bow's string hand takes to come back from the loose
        private const float StrikeRaised = 0.4f;   // walk.STRIKE_AT: how far through a blow or throw clip it is wound up,
        private const float StrikeHit = 0.5f;   // lands (or lets go)
        private const float StrikeThrough = 0.6f;   // and has followed through
        private const float PackTake = 0.15f;   // walk.PACK_TAKE['at']: how far through throw_pack the right hand is at the pack
        private const int WindTicks = 12;   // game ticks from a melee blow's tick to its landing as drawn (wound up and brought down)
        private const int RecoverTicks = 36;   // and from there through and back to the guard
        private const int ThrowRecover = 40;   // game ticks a throw takes from leaving the hand back to the guard

        private static ModContentPack content;
        private static ModContentPack isoRim;   // IsoRim's own folder: its tool models (WorkOf, DrawTool)
        private static readonly Dictionary<string, ModelHandle> toolModels = new Dictionary<string, ModelHandle>();
        private static ModelHandle idle;
        private static ModelHandle[][] cycles;   // the walk, then run1 .. runN (clip.txt's run_levels; the last is the full run)
        private static float[] groundPerCycle;   // cells an adult covers in one cycle of each (clip.txt)
        private static float crawlGround = 0.3f;   // and of the crawl
        private static float limpGround = 0.4f;   // the limp, the hop, the wade and the swim (an export without them: unused)
        private static float hopGround = 0.7f;
        private static float wadeGround = 0.4f;
        private static float swimGround = 1f;
        private static FaceLayers.Patch idleFace;
        private static FaceLayers.Patch[][] cycleFaces;   // the face patch of each frame of cycles; null without them
        private static SkinnedFigure skin;   // null: the baked frames are drawn
        private static string[] clipNames;   // the clips in anim.txt for the walk, then run1 .. runN
        private static readonly Dictionary<int, Ease> eases = new Dictionary<int, Ease>();
        private static readonly List<int> stale = new List<int>();
        private static bool tried, ready;
        private static readonly AccessTools.FieldRef<PawnFlyer, int> flyingTicks = AccessTools.FieldRefAccess<PawnFlyer, int>("ticksFlying");
        private static readonly AccessTools.FieldRef<PawnFlyer, int> flightTicks = AccessTools.FieldRefAccess<PawnFlyer, int>("ticksFlightTime");
        private static readonly AccessTools.FieldRef<JobDriver_Mine, int> pickHitIn = AccessTools.FieldRefAccess<JobDriver_Mine, int>("ticksToPickHit");

        private class Ease
        {
            public float run;   // 0 walking .. 1 running
            public float drafted;   // 0 at ease .. 1 in the drafted stance
            public string stance;   // the drafted clip for the weapon it holds (DraftedClip); null: the export has none
            public float armsOut;   // this draw: how much of the stance's hold is laid on, its hands pushed out past armour by (ArmsOut)
            public bool handHeld;   // the game shows its weapon in its hands (StepDraw)
            public float drawAt = -1f;   // 0 .. 1 through the draw clip, going up to draw and down to holster; less than 0: none
            public string drawClip;   // the draw clip it plays or last played (DrawClipOf)
            public int drawWeapon;   // the weapon it draws (thingIDNumber)
            public string fromStance;   // the one it is changing from, when it swaps weapons
            public float swap = 1f;   // 0 still in fromStance .. 1 in stance
            public float aim;   // 0 .. 1: how far its weapon is brought up to aim (AimName's clips)
            public string aimName;   // the weapon or kind its aim clips are for (kept while it lowers it)
            public float carry;   // 0 empty-handed .. 1 in the carrying pose
            public string carryClip;   // the carry loop for what it holds, or last held while it eases out of it
            public float play;   // 0 .. 1: how far a cradled baby is rocked in its arms (carry_play: PlaysWithBaby)
            public int load;   // what it holds (thingIDNumber), 0 nothing
            public int loadCount;   // and how many (a stack topped up or partly set down changes it)
            public float bent = 1f;   // how far through its last bend to the ground (BendOf), 0 .. 1; 1: done
            public int bentTick;   // the game tick it was last moved on
            public int bentRoll;   // picks the bend's variant (SkinnedFigure.Variant), new with each bend
            public int holding;   // the person it carried when last drawn (thingIDNumber; a corpse's pawn), 0 none
            public int dropped;   // the person it is setting down (Dropping), 0 none
            public int droppedTick;   // the game tick it let go of them
            public float limp;   // 0 .. 1: how badly it limps, eased toward limpBy
            public float limpBy;   // what its legs ask for (LimpOf), looked at every LimpCheck ticks
            public float balance;   // 0 .. 1: how far a pawn with a leg gone has its hips over the other foot (SkinnedFigure.Balance)
            public int balanceFrame;   // the frame that was last worked out
            public string limpClip;   // limp_L, limp_R, hop_L or hop_R: the leg it favours (kept while it eases out of it)
            public int limpTick;   // the game tick its legs were last looked at
            public float hurt;   // 0 .. 1: how far it hunches over its worst wound, eased toward hurtBy
            public float hold;   // 0 .. 1: its hand on the way to that wound (1 whenever hurtBy is more than 0)
            public float hurtBy;   // what its wounds ask for (HurtOf), looked at with its legs
            public string hurtClip;   // hurt_<place>_<side>: the wound it holds (kept while it eases out of it)
            public float cower;   // 0 .. 1: how far it cowers, hands raised (Cowering), eased as it starts or stops
            public float lead;   // 0 .. 1: its right hand on the way back to a rope it leads by (Leading)
            public float shield;   // 0 .. 1: how far its left hand is up in the shield guard (ShieldUp)
            public float shieldOut;   // 0 .. 1: how far a shield carried on the back is through being taken into the guard (StepShield)
            public string shieldClip;   // its shield_draw clip while that plays it; null: none
            public float eat;   // 0 .. 1: how far into eating it is (EatOf)
            public string eatClip;   // eat_sit or eat_stand (kept while it eases out of it)
            public float pillAt;   // how far through the pill clip it is, 0 .. 1 (PillOf; kept as it eases out, so it stays down)
            public int pillTicks;   // game ticks the chew it is timed by takes (the most left of it seen)
            public float look;   // degrees the head is turned about the vertical, eased (LessonLookOf: at a lesson)
            public float talk;   // 0 .. 1: how far into talking it is (TalkOf: a board game, a speech)
            public float toss;   // 0 .. 1: how far into pitching horseshoes or hoopstone it is (Tossing)
            public string tossClip;   // toss, or snowball for Romance on the Rim's snowball fight (kept while it eases out)
            public Vector3 kissWay;
            public float share;   // 0 .. 1: how far a reader and its watcher have drawn together (SharePartnerOf), eased
            public Vector3 shareWay;   // the way and distance to the other, flat, cells
            public float readShow;   // 0 .. 1: how far the book is held out to a watcher sat beside (ShowSideOf), eased
            public string readShowSide;   // L or R, the watcher's side (kept while it eases back)
            public float listen;   // 0 .. 1: how far a listener at a performance has stepped toward the performer (ListeningTo), eased
            public Vector3 listenWay;   // the way and distance to the performer, flat, cells   // the way and distance to the partner while kissing (KissOf), flat, cells
            public float tossAt = -1f;   // how far through the toss clip it is this frame, 0 .. 1; -1: between throws (TossPhase)
            public float? tossFace;   // which way the pin is (degrees clockwise from north); null: none
            public bool cold;   // colder than it is comfortable (ColdOf), looked at with its legs
            public Bout rub = new Bout();   // rubbing its hands for warmth now and then while cold
            public float rubFree;   // 0 .. 1: how far it is at leisure to rub them (AtLeisure)
            public Bout chat = new Bout();   // talking now and then at a party (Partying)
            public float rubAt = -1f;   // how far through the bout of each it is this frame, 0 .. 1; -1: between bouts
            public float waveAt = -1f;   // how far through a wave in passing it is this frame, 0 .. 1; -1: none (WaveOf)
            public float sad;   // 0 .. 1: slumped in a sad wander (Sad)
            public float drunkBy;   // 0 .. 1: how drunk it is (DrunkOf), looked at with its legs
            public float drunk;   // .. and how far it sways with it, eased toward that
            public float weave;   // cells it is drawn to its right this frame, weaving drunk on the move (DrunkWeave)
            public Bout stir = new Bout();   // stirring the pot now and then while cooking (StirOf)
            public float stirAt = -1f;   // how far through a bout of stirring it is this frame, 0 .. 1; -1: between bouts
            public string stirClip;   // the stirring clip for the work it does (work_stir, work_stir_crouch); null when not cooking
            public float burn;   // 0 .. 1: patting at the flames on itself (Burning)
            public float chatAt = -1f;
            public Pawn chatWith;   // whom the bout of talking under way is to (ListenerOf); null: no one in particular
            public float? chatFace;   // which way they are (degrees clockwise from north), kept if they leave
            public int chatLooked = -99999;   // the game tick it last looked for someone to talk to
            public float reload;   // 0 .. 1: how far into reloading it is (ReloadOf)
            public float reloadAt;   // how far through the reload it is, 0 .. 1
            public int reloadTicks;   // game ticks the reload it is in takes (the most left of it seen)
            public float cast;   // 0 .. 1: its left hand raised to cast (CastOf)
            public string castClip;   // cast or cast_touch (kept while it lowers it)
            public float? castFace;   // which way its target is (degrees clockwise from north); null: none
            public float hipOut;   // 0 in its slot .. 1 in the left hand: how far through hip_draw the hip gear is (StepHip)
            public float hipAim;   // 0 .. 1: how far the hip gear is brought up to point it
            public bool hipUsing;   // using the hip gear now (HipVerb)
            public float? hipFace;   // which way its target was (degrees clockwise from north); null: none yet
            public float reel;   // 0 .. 1: how far off balance it is (the game's stagger)
            public float reelAt;   // how far through the stumble to one knee it is, 0 .. 1 (ReelOf)
            public int reelTicks;   // game ticks the stumble is played over (the most of the stagger left seen); 0: none
            public string reelClip;   // reel or one of its variants, picked as the stagger began
            public float daze;   // 0 .. 1: how dazed it is (the game's stun)
            public string strikeClip;   // the blow or throw it is in (StrikeOf: melee_<kind> or throw_<side>); null: none
            public float strikePhase;   // how far through it, 0 .. 1 (the guard at both ends)
            public float? strikeFace;   // which way its target is (degrees clockwise from north); null: none
            public float aimFace;   // which way it last aimed (degrees clockwise from north)
            public int aimTick = -99999;   // and the game tick it last did
            public float work;   // 0 .. 1: how far into the stance for its work (WorkOf)
            public string workKind;   // the walk.WORK kind it works with (kept while it eases out of it)
            public float shownWork;   // how much of the work clip it was drawn with (the tool shows past ToolShown)
            public float workFace;   // which way the work is from it (degrees clockwise from north), while it works
            public bool flying;   // in mid-jump (FlightOf)
            public float crouch;   // 0 .. 1: how far down in the crouch it was on the ground (CrouchOf), sprung up from in flight
            public float landed = 1f;   // how far through coming up out of its last landing, 0 .. 1; 1: done
            public int landedTick;   // the game tick it was last moved on
            public int landedRoll;   // picks the crouch's variant, new with each landing
            public float depth;   // cells of water or marsh it is in (WaterOf), eased as it goes in or out
            public float swim;   // 0 walking or wading .. 1 swimming
            public float stroke;   // 0 treading water .. 1 laid down in the front crawl (StrokeOf)
            public int strokeFrame = -99;   // when it was last moved on
            public bool lay;   // drawn lying on its back (downed, asleep, on a couch) at layFrame
            public int layFrame = -99;
            public float lieYaw;   // the yaw, model units along and cells up it was laid at then (the getup starts from there)
            public float lieAlong;
            public float headAlong = float.NaN;   // the head bone's joint along the bed from the placing's head, cells (DrawTuckedIn; for AutoTest)
            public float blanketAlong = float.NaN;   // where the blanket's lump starts along the bed from the placing's head, cells (DrawTuckedIn)
            public float blanketScale = float.NaN;   // the lump's size for this sleeper's body (BlanketScaleOf), IsoRim's scale
            public float blanketDepth, blanketWidth;   // and the body measured for it, model units (for AutoTest)
            public SkinnedFigure.Shape blanketShape;   // the shape it was measured in
            public float tuckNeck = float.NaN;   // under the blanket: the neck's distance down the bed from its head end before TuckedDown moves it, cells
            public float tuckShift;   // and how far TuckedDown moved the figure toward the feet, cells
            public int blanketFrame = -1;   // the frame IsoRim last drew (or queued) the blanket over this sleeper (DrawTuckedIn; for AutoTest)
            public float lovinWay;     // and with no blanket, which way it goes (+1 the figure's +x), kept as it eases back out
            public float lovinShift;   // under the blanket at lovin: cells the head and lump are drawn across the bed toward the partner, eased (LovinOf)
            public float lieFloor;
            public float risen = 1f;   // how far through getting up, 0 .. 1; 1: done (RiseOf)
            public int risenTick;   // the game tick it was last moved on
            public int risenRoll;   // picks the getup's variant, new each time it gets up
            public float duck;   // 0 .. 1: how far down in the crouch under fire it is (DuckOf)
            public float cover;   // 0 .. 1: how far down in the crouch behind cover it is (CoverOf)
            public float coverWant;   // how far down it would be: by the cover beside it, 0 with no enemy in sight
            public int coverChecked = -9999;   // the game tick it last looked (CoverCheck)
            public int coverSeen = -9999;   // the game tick it last saw an enemy
            public int coverTick;   // the game tick cover was last moved on
            public int coverFrame = -99;   // when it was last moved on
            public int duckTick;   // the game tick it was last moved on
            public int duckFrame = -99;   // when it was last moved on
            public string duckHand;   // the hand that holds the weapon as it crouches: which of the duck_ clips
            public float? duckFace;   // the way it crouches, toward the fire (SuppressorOf) if known, kept until it is up again
            public string duckLoop;   // the loop played once down: duck_<hand> hunkering, roll putting out a fire on itself
            public int upFrame = -99;   // when it was last drawn standing on its feet (a fall starts from there)
            public float upYaw;   // and the way it faced then
            public float? turnYaw;   // aiming: the way it is drawn facing, ahead of the tracker's (TurnedFor); null: the tracker's
            public float? footYaw;   // standing: the way its feet face, the trunk turned from it to the way it faces (StepOf); null: as it faces
            public bool stepping;   // its feet are stepping round to face the way the trunk does
            public float step;   // 0 .. 1: how far the steps are shown
            public float stepCycle;   // 0 .. 1 round the walk the steps are played from
            public float stepYaw;   // the way the trunk faced when last moved on
            public float stepHeld;   // real seconds it has held its way
            public int stepFrame = -99;   // when it was last moved on
            public string fallClip;   // knockdown or die: the fall it is in, or was last in
            public float fallen = 1f;   // how far through that fall, 0 .. 1; 1: done (FallOf)
            public int fallenTick;   // the game tick it was last moved on
            public float? fellYaw;   // lying where it fell: the yaw it fell at, kept while it lies on the ground
            public SkinnedFigure.Ragdoll doll;   // lying limp where it fell (Ragdolls), or getting up out of it; null: none
            public bool dollNew;   // a fall has just begun: the ragdoll is made from its first frame
            public bool dollMoved;   // the body has been flung to another cell since it was last stepped (Flung)
            public Vector3 heldAt, heldAim, heldTop;   // where the weapon in its hands was last drawn (true cells), and its turn
            public int heldFrame = -99;   // and when
            public Vector3 lyingChest;   // where its chest was last drawn lying (true cells), for those who tend it to face
            public Vector3 lyingAlong;   // and the way from its hips to its head there, flat
            public Vector3[] lyingJoints;   // and its LyingBones' joints (true cells), which someone kneeling by it keeps off
            public float lyingSize;   // and its size
            public int lyingFrame = -99;   // and when
            public bool onBack;   // downed on the ground and tended there: laid on its back, as on a sleeping spot, until it is up (TendedOnBack)
            public string lieNow, lieWas;   // the lying loop it is drawn in, and the one it is turning from (LieTurnSeconds)
            public float lieTurn = 1f;   // 0 .. 1: how far from lieWas into lieNow
            public int lieTurnTick;   // the game tick lieTurn was last moved on
            public float dollOut;   // 0 .. 1: how far its ragdoll is eased out into the lying loop, laid on its back to be tended
            public int dollOutTick;   // the game tick dollOut was last moved on
            public Vector3? besideWay;   // kneeling by someone lying: the way out from their chest it kneels on (WorkOf), kept to
            public int besideOf = -1;   // and whom (thingIDNumber)
            public Vector3 walkedFrom;   // where it was last drawn (true cells), for cameWay
            public Vector3? cameWay;   // the way it last walked (flat, a unit): someone it tends on the ground is tended from that side
            public Vector3? workBeside;   // working on someone lying on the ground: where it is drawn, kneeling beside them (true cells)
            public int flashShot = -1;   // the LastShotTick of the last shot it flashed for (MuzzleFlash)
            public float flashAt = -99f;   // and when that flash began (real seconds)
            public bool dollSaved;   // it is at rest, and kept with the map (RagdollRests)
            public float dollHit;   // how hard the felling blow was, 0 .. 1
            public float dollHeld = 1f;   // how hard the body is held to the canned fall, 0 .. 1: less the harder a blow sends it the other way
            public int dollFrame = -1;   // the frame it was last stepped in
            public int workRoll;   // picks the work stroke's variant, new each time it sets to work
            public int pickSeen = int.MinValue;   // JobDriver_Mine's ticksToPickHit as last read (PickPhase)
            public int pickSeenTick;   // the game tick it was read at
            public int pickPeriod;   // the ticks between the last strike and the next, as the game reset it
            public int pickJob = -1;   // the mining job those are for (its loadID)
            public int beatTick = int.MinValue;   // the tick the pawn last beat a fire (its beat verb's cool-down began; BeatPhase)
            public int beatPeriod;   // the ticks between its last two beats; 0 before a second
            public int beatCount;   // how many beats it has made, which hand or foot is next
            public string toolKind;   // the tool line of the work stroke it was drawn in (its kind, or a variant's)
            public int frame;   // when it was last moved on
        }

        // A clip played once through now and then rather than round and round (BoutOf): rubbing cold hands, talking at a
        // party.
        private class Bout
        {
            public int start = -1;   // the game tick the bout under way began; -1: none
            public int next = -1;   // the game tick from which the next may begin; -1: not picked yet
        }

        // Where a carried person is drawn this frame, set by its carrier before the game draws it (Carried.Draw).
        private struct Hold
        {
            public int frame;
            public string clip;   // the carried_<kind> loop
            public float phase;   // where the carrier is in its carry loop, so the two keep step (a rocked baby swings with the arms)
            public float play;   // how far the baby is rocked (carried_play over clip: Ease.play), and where in that loop
            public float playPhase;
            public Vector3 at;   // the carrier's place, yaw and size
            public float yaw;
            public float size;
            public Matrix4x4 chest;   // the carrier's CarryBone as posed (SkinnedFigure.BoneOf)
            public bool released;   // just set down (Dropping): the game draws them on their own, maybe before the carrier
        }
        private static readonly Dictionary<int, Hold> holds = new Dictionary<int, Hold>();

        // The pawn or corpse's pawn in a carried thing; null for anything else.
        private static Pawn HeldPawn(Thing load)
        {
            Corpse corpse = load as Corpse;
            return corpse != null ? corpse.InnerPawn : load as Pawn;
        }

        // Over the shoulder rather than in the arms: a prisoner, someone hostile to the carrier, or someone being arrested,
        // captured or kidnapped. A baby is always cradled.
        private static bool Fireman(Pawn carrier, Pawn held)
        {
            if (held.DevelopmentalStage.Baby())
            {
                return false;
            }
            if (held.IsPrisoner || held.HostileTo(carrier))
            {
                return true;
            }
            JobDef job = carrier.CurJobDef;
            return job == JobDefOf.Arrest || job == JobDefOf.Capture || job == JobDefOf.Kidnap;
        }

        // Playing with the baby it holds: Biotech's PlayWalking, which ambles about with it in its arms and stops now and
        // then to play (the game only bobs the sprite, JobDriver_PlayWalking.ForcedBodyOffset, which PlayOffsetPrefix
        // takes out for a figure this mod draws). By defName, so a game without Biotech needs nothing of it.
        private static bool PlaysWithBaby(Pawn pawn)
        {
            JobDef job = pawn.CurJobDef;
            return job != null && job.defName == "PlayWalking";
        }

        // Feeding the baby it holds: Biotech's Breastfeed and BottleFeedBaby, which pick it up, carry it to a seat (or a spot
        // to stand) and feed it there still in the arms. By defName, as above.
        private static bool FeedsBaby(Pawn pawn)
        {
            JobDef job = pawn.CurJobDef;
            return job != null && (job.defName == "Breastfeed" || job.defName == "BottleFeedBaby");
        }

        // Playing with a baby left on the floor: Biotech's PlayStatic (stood by it where it lies), drawn sat cross-legged
        // rocking it in its arms (WorkOf's "play", HoldBabyPlayed), and PlayToys (by a toy box, once it has set it down),
        // drawn knelt beside it waving a hand over it ("toys"). The job's defName, else null. By defName, as above.
        private static string PlaysByBaby(Pawn pawn)
        {
            JobDef job = pawn.CurJobDef;
            return job != null && (job.defName == "PlayStatic" || job.defName == "PlayToys") ? job.defName : null;
        }

        // Sat on the floor playing with a baby (PlayStatic, WorkOf's "play"): the game leaves the baby lying beside it, but
        // it is drawn in the arms, rocked as PlayWalking's is (carried_play on the chest as posed), once the sit is
        // mostly taken up. The baby is drawn with the rest of the map, before or after the player, so the hold is
        // taken a frame late too (released).
        private static void HoldBabyPlayed(Pawn pawn, Ease ease, SkinnedFigure.Posed fig, Vector3 at, float yaw, float size)
        {
            Pawn baby = pawn.CurJob != null ? pawn.CurJob.targetA.Thing as Pawn : null;
            Matrix4x4 chest;
            if (ease.workKind != "play" || ease.work < 0.5f || baby == null || !baby.Spawned || !Handles(baby)
                || !skin.Has("carried_cradle") || !skin.Has("carried_play") || !skin.BoneOf(fig, CarryBone, out chest))
            {
                return;
            }
            holds[baby.thingIDNumber] = new Hold { frame = Time.frameCount, clip = "carried_cradle", phase = LoopPhase(pawn, "carry_cradle"),
                play = 1f, playPhase = LoopPhase(pawn, "carry_play"), at = at, yaw = yaw, size = size, chest = chest, released = true };
            if (holds.Count > 200)
            {
                PruneHolds();
            }
        }

        // The clip a WorkOf kind plays: work_<kind>, but playing with a baby on the floor on an export without work_play
        // sits as for meditating (with carry_play laid over it, PlayOver).
        private static string WorkClipOf(string kind)
        {
            return kind == "play" && !skin.Has("work_play") ? "work_meditate" : "work_" + kind;
        }

        // Cuddling a small pet held in its arms: [DG]Lovly Animal Cuddling's AC_CuddleAnimal (local.animalcuddling), which
        // carries the animal about or sits down with it on a chair. By defName, so that mod is not referenced.
        private static bool Cuddling(Pawn pawn, Thing load)
        {
            JobDef job = pawn.CurJobDef;
            Pawn animal = load as Pawn;
            return job != null && job.defName == "AC_CuddleAnimal" && animal != null && !animal.Dead && animal.RaceProps != null && animal.RaceProps.Animal;
        }

        // An animal being cuddled (Cuddling): IsoRim holds its facing (Api.AddFacingHold), so it doesn't walk its legs in
        // the air as it is carried about; it faces the way DrawLoad turns it.
        private static bool CuddledAnimal(Pawn animal)
        {
            Pawn_CarryTracker hands = animal != null ? animal.ParentHolder as Pawn_CarryTracker : null;
            return hands != null && hands.pawn != null && Handles(hands.pawn) && Cuddling(hands.pawn, animal);
        }

        // The loop eased in over a carry loop by Ease.play: a cradled baby rocked (carry_play), a cuddled pet stroked
        // (carry_cuddle_stroke); null for the other carries.
        private static string CarryOverOf(string carryClip)
        {
            return carryClip == "carry_cradle" ? "carry_play" : carryClip == "carry_cuddle" ? "carry_cuddle_stroke" : null;
        }

        // JobDriver_PlayWalking.ForcedBodyOffset: none for a pawn this mod draws, which rocks the baby itself (carry_play);
        // the game's sway would shake the whole figure about.
        public static bool PlayOffsetPrefix(JobDriver __instance, ref Vector3 __result)
        {
            if (skin == null || __instance.pawn == null || !Handles(__instance.pawn))
            {
                return true;
            }
            __result = Vector3.zero;
            return false;
        }

        // The carry loop for what the pawn holds: carry_bridal, carry_fireman or (a baby) carry_cradle for a person this mod draws, carry_item for
        // anything else (a downed animal too, which IsoRim draws across the hands); null if it holds nothing or the export
        // has no such loop.
        private static string CarryClip(Pawn pawn)
        {
            Thing load = pawn.carryTracker != null ? pawn.carryTracker.CarriedThing : null;
            if (load == null)
            {
                return null;
            }
            Pawn held = HeldPawn(load);
            if (Cuddling(pawn, load) && skin.Has("carry_cuddle"))
            {
                return "carry_cuddle";
            }
            string clip = held != null && Handles(held) ? (Fireman(pawn, held) ? "carry_fireman" : Baby(held) ? "carry_cradle" : "carry_bridal") : "carry_item";
            if (clip == "carry_cradle" && !skin.Has(clip))
            {
                clip = "carry_bridal";   // an export from before the cradle
            }
            return skin.Has(clip) ? clip : skin.Has("carry_item") ? "carry_item" : null;
        }

        // How a weapon is held drafted: the export has a drafted_<kind> clip for each (walk.DRAFTED). By ThingDef, so
        // modded weapons are sorted by the same signs: melee, a bow by name (not a crossbow, held like a rifle), a pistol,
        // revolver or grenade one-handed, any other gun two-handed; nothing equipped, fists.
        private static string KindOf(ThingDef weapon)
        {
            if (weapon == null || (!weapon.IsMeleeWeapon && !weapon.IsRangedWeapon))
            {
                return "unarmed";
            }
            if (weapon.IsMeleeWeapon)
            {
                return "melee";
            }
            string name = (weapon.defName + " " + weapon.label).ToLowerInvariant();
            if (name.Contains("bow") && !name.Contains("crossbow"))
            {
                return "bow";
            }
            bool oneHanded = name.Contains("pistol") || name.Contains("revolver");
            for (int i = 0; !oneHanded && weapon.weaponTags != null && i < weapon.weaponTags.Count; i++)
            {
                string tag = weapon.weaponTags[i];
                oneHanded = tag.Contains("Pistol") || tag.Contains("Revolver") || tag.StartsWith("Grenade");
            }
            return oneHanded ? "pistol" : "rifle";
        }

        // A weapon that is itself thrown: the vanilla verb's throw log (as IsoRim marks one), or Combat
        // Extended's grenades and pila, whose verbs CE replaces with its own (Verb_ShootCEOneUse, no throw log) and marks
        // by tag instead. CE's disposable rocket launcher shares that verb, so the verb alone won't do.
        private static bool Thrown(ThingDef weapon)
        {
            if (weapon == null)
            {
                return false;
            }
            for (int i = 0; weapon.IsWeapon && weapon.Verbs != null && i < weapon.Verbs.Count; i++)
            {
                RulePackDef log = weapon.Verbs[i].rangedFireRulepack;
                if (log != null && log.defName == "Combat_RangedFire_Thrown")
                {
                    return true;   // the vanilla verb's throw log, as IsoRim tells a thrown weapon
                }
            }
            for (int i = 0; weapon.weaponTags != null && i < weapon.weaponTags.Count; i++)
            {
                string tag = weapon.weaponTags[i];
                if (tag == "CE_AI_Grenade" || tag == "CE_Pila")
                {
                    return true;
                }
            }
            return false;
        }

        private static readonly Dictionary<ThingDef, string> stanceOf = new Dictionary<ThingDef, string>();
        private static readonly Dictionary<ThingDef, string> aimOf = new Dictionary<ThingDef, string>();
        private static string unarmedStance;
        private static bool unarmedKnown;

        private const string StandInGrenade = "Weapon_GrenadeFrag";   // walk.THROW_PACK: the grenade the export poses
        private static readonly Dictionary<ThingDef, string> borrowed = new Dictionary<ThingDef, string>();

        // The weapon's own name in the export (walk.DRAFTED, walk.WEAPONS): its defName, a unique (Odyssey) or persona
        // (Royalty) weapon's the plain one's, since they look the same, and a grenade the export doesn't name (Combat
        // Extended's own, another mod's) the frag grenade's, so it is held and thrown as one.
        private static string WeaponName(ThingDef weapon)
        {
            string name;
            if (borrowed.TryGetValue(weapon, out name))
            {
                return name;
            }
            name = OwnName(weapon);
            if (skin != null && name != StandInGrenade && Grenade(weapon) && !skin.Has("drafted_" + name)
                && skin.HoldOf(name) == null && skin.WeaponTypeOf(name) == null)
            {
                name = StandInGrenade;
            }
            if (skin != null)
            {
                borrowed[weapon] = name;   // (only once the export is read: before, nothing is known to borrow)
            }
            return name;
        }

        // A thrown weapon that is a grenade, not a pilum: in the game's Grenades category (vanilla's and Combat Extended's)
        // or tagged as one by CE.
        private static bool Grenade(ThingDef weapon)
        {
            if (!Thrown(weapon))
            {
                return false;
            }
            ThingCategoryDef grenades = DefDatabase<ThingCategoryDef>.GetNamedSilentFail("Grenades");
            if (grenades != null && weapon.IsWithinCategory(grenades))
            {
                return true;
            }
            return weapon.weaponTags != null && weapon.weaponTags.Contains("CE_AI_Grenade");
        }

        // The game's own art for each of its grenades (their defs' texPaths before any mod patches them), by WeaponName.
        private static readonly Dictionary<string, string> GrenadeArt = new Dictionary<string, string>
        {
            { "Weapon_GrenadeFrag", "Things/Item/Equipment/WeaponRanged/Grenades" },
            { "Weapon_GrenadeMolotov", "Things/Item/Equipment/WeaponRanged/Molotov" },
            { "Weapon_GrenadeEMP", "Things/Item/Equipment/WeaponRanged/EMPGrenades" },
            { "Weapon_GrenadeTox", "Things/Item/Equipment/WeaponRanged/ToxGrenades" },
        };
        private static readonly Dictionary<ThingDef, Material> fitArt = new Dictionary<ThingDef, Material>();

        // The sprite a weapon's IsoRim model is fitted to (WeaponFit.HoldOf): for one of the game's own weapons (Core or an
        // expansion) the game's own art for it, else its own sprite. IsoRim's models are made after the game's art, and the
        // holds and holster slots were set on it in the preview, but a mod may draw it otherwise at the same texture path:
        // Combat Extended's recurve bow is a narrower reflex bow with its string further over, which slid the model down
        // the stave in the fist. For a grenade it is the art for the grenade it is held as (WeaponName): its model is
        // nearly as wide as it is long, so the fit turns it by the picture, and Combat Extended's stack sprites (one, a
        // few, a heap) each laid it another way.
        private static Material FitArt(ThingDef def, Material mat)
        {
            Material art;
            if (!fitArt.TryGetValue(def, out art))
            {
                string path = null;
                if (Thrown(def))
                {
                    GrenadeArt.TryGetValue(WeaponName(def), out path);
                }
                else if (def.modContentPack != null && def.modContentPack.IsOfficialMod && def.graphicData != null
                    && def.graphicData.graphicClass == typeof(Graphic_Single))
                {
                    path = def.graphicData.texPath;
                }
                Texture2D tex = path != null ? OwnArt(path) : null;
                art = tex != null ? MaterialPool.MatFrom(new MaterialRequest(tex, ShaderDatabase.Cutout)) : null;
                fitArt[def] = art;
            }
            return art ?? mat;
        }

        // The game's own picture at a texture path, passing over any other mod's at the same path (as ContentFinder would
        // find it first): the expansions' content, last loaded first, then Core's, which is in the base resources.
        private static Texture2D OwnArt(string path)
        {
            List<ModContentPack> mods = LoadedModManager.RunningModsListForReading;
            for (int i = mods.Count - 1; i >= 0; i--)
            {
                if (!mods[i].IsOfficialMod)
                {
                    continue;
                }
                ModContentHolder<Texture2D> holder = mods[i].GetContentHolder<Texture2D>();
                Texture2D found = holder.contentList.Count != 0 ? holder.Get(path) : null;
                if (found != null)
                {
                    return found;
                }
            }
            return Resources.Load<Texture2D>(GenFilePaths.ContentPath<Texture2D>() + path);
        }

        private static string OwnName(ThingDef weapon)
        {
            string name = weapon.defName;
            if (name.EndsWith("_Unique"))
            {
                return name.Substring(0, name.Length - "_Unique".Length);
            }
            if (name.EndsWith("Bladelink"))
            {
                name = name.Substring(0, name.Length - "Bladelink".Length);
                return name == "MeleeWeapon_ZeusHammer" ? "MeleeWeapon_Zeushammer" : name;
            }
            return name;
        }

        // The export's sprite slab clips and holds (walk.SLAB_DRAFTED, SLAB_WEAPONS, SLAB_AIMED) are named as the model's plus
        // this: the stances and holds as they were before they were redone for IsoRim's weapon models.
        private const string Slab = "_slab";
        private static readonly Dictionary<ThingDef, string> slabStanceOf = new Dictionary<ThingDef, string>();
        private static readonly Dictionary<ThingDef, string> slabAimOf = new Dictionary<ThingDef, string>();

        // Whether DrawSlab will draw this weapon as its sprite's slab rather than IsoRim's model of it: models off or failed,
        // none for it, or one that doesn't fit its sprite (as DrawModel decides).
        private static bool AsSlab(Thing weapon)
        {
            return weapon != null && !ShowsModel(weapon);
        }

        private static bool ShowsModel(Thing weapon)
        {
            ThingModelInfo info;
            WeaponFit.Hold h;
            return HeldModel(weapon, out info, out h);
        }

        // The weapon's IsoRim model as IsoRim would show it held (Api.ReadThingModel: its "Weapons as 3D models" setting, a
        // model bound, the art and model rules) and our fit of it to its sprite (WeaponFit); false for neither.
        private static bool HeldModel(Thing weapon, out ThingModelInfo info, out WeaponFit.Hold hold)
        {
            hold = null;
            if (!Api.ReadThingModel(weapon, ThingModelUse.Held, out info) || info.Model == null)
            {
                return false;
            }
            Graphic g = weapon.Graphic;
            Material mat = g != null ? g.MatSingleFor(weapon) : null;
            hold = mat != null ? WeaponFit.HoldOf(info.Model, info.BarrelEnd, FitArt(weapon.def, mat), g.drawSize, weapon.def.equippedAngleOffset) : null;
            return hold != null;
        }

        // The size of a weapon's model on the ground (cells per model unit, its ModelDef's scale); 0 if IsoRim gives none.
        private static float GroundScaleOf(Thing weapon)
        {
            ThingModelInfo g;
            return Api.ReadThingModel(weapon, ThingModelUse.Ground, out g) ? g.Scale : 0f;
        }

        // The name a weapon drawn as a slab takes its prefix + name clips under: its own name's slab clips, else its kind's,
        // else plain (the name it plays with its model), as the export writes a slab clip only where that chain would pose
        // it otherwise.
        private static string SlabName(ThingDef weapon, string prefix, string plain)
        {
            return skin.Has(prefix + WeaponName(weapon) + Slab) ? WeaponName(weapon) + Slab
                : skin.Has(prefix + KindOf(weapon) + Slab) ? KindOf(weapon) + Slab : plain;
        }

        // The export's hold for a weapon by name (walk.WEAPONS), its slab hold first if it is drawn as a slab.
        private static SkinnedFigure.Hold HoldOf(string name, bool slab)
        {
            return (slab ? skin.HoldOf(name + Slab) : null) ?? skin.HoldOf(name);
        }

        // The export's hold for this weapon: its own, else its kind's.
        private static SkinnedFigure.Hold HoldOf(Thing weapon)
        {
            bool slab = AsSlab(weapon);
            return HoldOf(WeaponName(weapon.def), slab) ?? HoldOf(KindOf(weapon.def), slab);
        }

        // The drafted clip for what the pawn holds: the weapon's own (drafted_<defName>), or its kind's (drafted_<kind>),
        // or the rifle's, or an older export's one drafted clip; null without any. A weapon drawn as its sprite's slab
        // takes the slab's (SlabName). With a shield up, a one-handed weapon or bare hands take the shield stance instead
        // (ShieldClip).
        private static string DraftedClip(Pawn pawn)
        {
            ThingWithComps held = HeldWeapon(pawn);
            string shielded = ShieldUp(pawn) ? ShieldClip(held != null ? held.def : null) : null;
            if (shielded != null)
            {
                return shielded;
            }
            string clip = PlainDraftedClip(held);
            if (held == null || clip == null || !clip.StartsWith("drafted_") || !AsSlab(held))
            {
                return clip;
            }
            string slab;
            if (!slabStanceOf.TryGetValue(held.def, out slab))
            {
                slab = "drafted_" + SlabName(held.def, "drafted_", clip.Substring("drafted_".Length));
                slabStanceOf[held.def] = slab;
            }
            return slab;
        }

        private static string PlainDraftedClip(ThingWithComps held)
        {
            ThingDef weapon = held != null ? held.def : null;
            string clip;
            if (weapon == null && unarmedKnown)
            {
                return unarmedStance;
            }
            if (weapon != null && stanceOf.TryGetValue(weapon, out clip))
            {
                return clip;
            }
            clip = weapon != null && skin.Has("drafted_" + WeaponName(weapon)) ? "drafted_" + WeaponName(weapon) : "drafted_" + KindOf(weapon);
            clip = skin.Has(clip) ? clip : skin.Has("drafted_rifle") ? "drafted_rifle" : skin.Has("drafted") ? "drafted" : null;
            if (weapon == null)
            {
                unarmedStance = clip;
                unarmedKnown = true;
            }
            else
            {
                stanceOf[weapon] = clip;
            }
            return clip;
        }

        // IsoRim's Api version this needs: the figure drawers, placing, mesh submitting and model rules (Docs/Api.md part 43).
        private const int MinimumApi = 62;

        // IsoRim's options this mod goes by, read by their field names (Api.GetOption, a lookup by name) once a frame.
        private static int optionsFrame = -1;
        private static bool heldWeapons3D = true, workAnimations = true;

        private static void ReadOptions()
        {
            if (optionsFrame == Time.frameCount)
            {
                return;
            }
            optionsFrame = Time.frameCount;
            object held = Api.GetOption("heldWeapons3D"), work = Api.GetOption("workAnimations");
            heldWeapons3D = !(held is bool) || (bool)held;
            workAnimations = !(work is bool) || (bool)work;
        }

        private static bool HeldWeapons3D   // IsoRim's "Held weapons in 3D"
        {
            get { ReadOptions(); return heldWeapons3D; }
        }

        private static bool WorkAnimations   // IsoRim's work animations
        {
            get { ReadOptions(); return workAnimations; }
        }

        static WalkFigures()
        {
            try
            {
                if (Api.Version < MinimumApi)
                {
                    Log.Warning(Tag + "IsoRim's Api is version " + Api.Version + ", " + MinimumApi + " or later is needed; IsoRim's figures are kept.");
                    return;
                }
                foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
                {
                    if (mod.assemblies != null && mod.assemblies.loadedAssemblies.Contains(typeof(WalkFigures).Assembly))
                    {
                        content = mod;
                    }
                    if (mod.assemblies != null && mod.assemblies.loadedAssemblies.Contains(typeof(Api).Assembly))
                    {
                        isoRim = mod;
                    }
                }
                if (content == null)
                {
                    Log.Warning(Tag + "this mod's folder was not found; IsoRim's figures are kept.");
                    return;
                }
                Api.AddFigureDrawer(DrawFigure, FigureKinds.People);
                Api.AddFacingHold(CuddledAnimal);
                Harmony harmony = new Harmony("MaruMirai.M3AT");
                // Hits, for the flinch (the game's own jolt of the sprite is told the same way).
                MethodInfo damaged = AccessTools.Method(typeof(Pawn_DrawTracker), nameof(Pawn_DrawTracker.Notify_DamageApplied));
                MethodInfo deflected = AccessTools.Method(typeof(Pawn_DrawTracker), nameof(Pawn_DrawTracker.Notify_DamageDeflected));
                if (damaged != null && deflected != null)
                {
                    harmony.Patch(damaged, postfix: new HarmonyMethod(typeof(WalkFigures), nameof(DamagedPostfix)));
                    harmony.Patch(deflected, postfix: new HarmonyMethod(typeof(WalkFigures), nameof(DeflectedPostfix)));
                }
                // What a pawn drops as it goes down, for the weapon flying from its hand.
                MethodInfo drop = AccessTools.Method(typeof(Pawn), nameof(Pawn.DropAndForbidEverything));
                if (drop != null)
                {
                    harmony.Patch(drop, prefix: new HarmonyMethod(typeof(WalkFigures), nameof(DropEverythingPrefix)),
                                  postfix: new HarmonyMethod(typeof(WalkFigures), nameof(DropEverythingPostfix)));
                    Flings.Patch(harmony);
                }
                // Melee attacks, hit or not: the game staggers the pawn attacked either way, so a miss flinches too.
                MethodInfo melee = AccessTools.Method(typeof(Verb_MeleeAttack), "TryCastShot");
                if (melee != null)
                {
                    harmony.Patch(melee, postfix: new HarmonyMethod(typeof(WalkFigures), nameof(MeleePostfix)));
                }
                // Whether a melee blow is at a target already down, seen before it is dealt (LowBlows).
                MethodInfo burst = AccessTools.Method(typeof(Verb), "TryCastNextBurstShot");
                if (burst != null)
                {
                    harmony.Patch(burst, prefix: new HarmonyMethod(typeof(WalkFigures), nameof(BurstPrefix)));
                }
                // Weapons and clothes taken up off the ground, for the bend (nothing is carried to tell it by).
                MethodInfo equip = AccessTools.Method(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.AddEquipment));
                MethodInfo wear = AccessTools.Method(typeof(Pawn_ApparelTracker), nameof(Pawn_ApparelTracker.Wear));
                if (equip != null)
                {
                    harmony.Patch(equip, postfix: new HarmonyMethod(typeof(WalkFigures), nameof(EquippedPostfix)));
                }
                if (wear != null)
                {
                    harmony.Patch(wear, postfix: new HarmonyMethod(typeof(WalkFigures), nameof(WornPostfix)));
                }
                // Social interactions, for a glance at the other and a wave in passing (InteractedPostfix).
                MethodInfo interact = AccessTools.Method(typeof(Pawn_InteractionsTracker), nameof(Pawn_InteractionsTracker.TryInteractWith));
                if (interact != null)
                {
                    harmony.Patch(interact, postfix: new HarmonyMethod(typeof(WalkFigures), nameof(InteractedPostfix)));
                }
                // A baby rocked in the arms by the figure, not by the game's sway (PlayOffsetPrefix).
                MethodInfo playOffset = AccessTools.PropertyGetter(typeof(JobDriver_PlayWalking), nameof(JobDriver_PlayWalking.ForcedBodyOffset));
                if (playOffset != null)
                {
                    harmony.Patch(playOffset, prefix: new HarmonyMethod(typeof(WalkFigures), nameof(PlayOffsetPrefix)));
                }
                // A meal eaten standing, drawn in the hand that holds it (DrawLoad).
                MethodInfo held = AccessTools.Method(typeof(JobDriver_Ingest), nameof(JobDriver_Ingest.ModifyCarriedThingDrawPos));
                if (held != null)
                {
                    harmony.Patch(held, prefix: new HarmonyMethod(typeof(WalkFigures), nameof(HeldPrefix)));
                }
                FaceLayers.PatchEyes(harmony);
                FaceLayers.PatchBlush(harmony);
                RomanceOnTheRim.PatchShareReading(harmony);
                Api.AddWeaponModelRule(WeaponRule);
                Api.AddGroundModelRule(GroundRule);
                try
                {
                    PatchGroundOutlines(harmony);
                }
                catch (Exception e)
                {
                    Log.Warning(Tag + "items on the ground keep IsoRim's outline: " + e.Message);
                }
                Log.Message(Tag + "people use the chibi model.");
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "failed, IsoRim's figures are kept: " + e);
            }
        }

        // Dev mode: each weapon def's first answer, once (WeaponRule), and why a weapon with no model drew its slab (DrawModel).
        private static readonly HashSet<ThingDef> artNoted = new HashSet<ThingDef>(), slabNoted = new HashSet<ThingDef>();

        // Asked by IsoRim (Api.AddWeaponModelRule) whether a weapon shows its model (held, on the ground, thrown) once it has
        // a usable one for it; null leaves it to IsoRim's own rules (its own binding, its own art). Two of those are changed:
        //   - IsoRim shows a model bound to the weapon's category only for that very weapon. Its Grenades category is bound to
        //     its grenade, which Combat Extended's own grenades are in, so they were drawn from their art: a grenade bound by
        //     its category counts too.
        //   - IsoRim (Api 50) shows a weapon's model only while its art is its own: any sprite from a mod other than the game,
        //     the weapon's own or the model's draws the sprite instead. Combat Extended ships its own art at the vanilla guns'
        //     paths, so with it every vanilla gun and grenade lost its model. The RetexturedWeaponModels option keeps it for
        //     the game's own weapons (Core and the DLCs, whose models were made for art of that shape), bound by their own name,
        //     a model rule or their picture; another mod's weapon is left to IsoRim. With it off, a weapon whose ModelDef
        //     RestoredModels put back (another mod had removed it) shows its sprite, as that mod meant.
        // A plain lookup (the option, the binding, the def): IsoRim may ask it off the main thread. It answers on every call,
        // so the option takes at once for what is drawn each frame; the map's baked weapons are written again when it changes
        // (RebakeGroundWeapons).
        private static bool? WeaponRule(ThingDef def)
        {
            if (def == null || def.IsApparel)
            {
                return null;
            }
            string how = Api.HowBound(def);
            if (how == null)
            {
                return null;
            }
            bool? answer = null;
            if (how.StartsWith("category:"))
            {
                answer = Grenade(def) ? true : (bool?)null;
            }
            else if (RetexturedWeaponModels)
            {
                bool official = def.modContentPack != null && def.modContentPack.IsOfficialMod;
                bool own = how == "thingDef" || how.StartsWith("rule:") || how.StartsWith("graphicPath:");
                answer = official && own ? true : (bool?)null;
            }
            else if (RestoredModels.Has(def))
            {
                answer = false;
            }
            if (Prefs.DevMode && UnityData.IsInMainThread && artNoted.Add(def))
            {
                Log.Message(Tag + "weapon models: " + def.defName + " (" + (def.modContentPack != null ? def.modContentPack.PackageIdPlayerFacing : "no mod")
                    + ", bound by " + how + ") " + (answer.HasValue ? "drawn as " + (answer.Value ? "its model" : "its sprite") : "left to IsoRim") + ".");
            }
            return answer;
        }

        // Loaded on first draw (main thread, IsoRim ready). False if any file is missing: IsoRim's figure is drawn instead.
        private static bool Ready()
        {
            if (tried)
            {
                return ready;
            }
            tried = true;
            LoadTrail.Note("loading the chibi (first figure drawn)" + LoadTrail.MemoryNow());
            try
            {
                int frames = 0, levels = 0;
                float walkGround = 0.5f;
                float[] runGround = new float[0];
                foreach (string raw in File.ReadAllLines(Path.Combine(content.RootDir, Folder, "clip.txt")))
                {
                    string[] t = raw.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (t.Length < 2 || t[0].StartsWith("#"))
                    {
                        continue;
                    }
                    if (t[0] == "frames")
                    {
                        frames = int.Parse(t[1]);
                    }
                    else if (t[0] == "ground_per_cycle")
                    {
                        walkGround = float.Parse(t[1], System.Globalization.CultureInfo.InvariantCulture);
                    }
                    else if (t[0] == "crawl_ground_per_cycle")
                    {
                        crawlGround = float.Parse(t[1], System.Globalization.CultureInfo.InvariantCulture);
                    }
                    else if (t[0] == "limp_ground_per_cycle")
                    {
                        limpGround = float.Parse(t[1], System.Globalization.CultureInfo.InvariantCulture);
                    }
                    else if (t[0] == "hop_ground_per_cycle")
                    {
                        hopGround = float.Parse(t[1], System.Globalization.CultureInfo.InvariantCulture);
                    }
                    else if (t[0] == "wade_ground_per_cycle")
                    {
                        wadeGround = float.Parse(t[1], System.Globalization.CultureInfo.InvariantCulture);
                    }
                    else if (t[0] == "swim_ground_per_cycle")
                    {
                        swimGround = float.Parse(t[1], System.Globalization.CultureInfo.InvariantCulture);
                    }
                    else if (t[0] == "run_levels")
                    {
                        levels = int.Parse(t[1]);
                    }
                    else if (t[0] == "run_ground_per_cycle")
                    {
                        runGround = new float[t.Length - 1];
                        for (int i = 1; i < t.Length; i++)
                        {
                            runGround[i - 1] = float.Parse(t[i], System.Globalization.CultureInfo.InvariantCulture);
                        }
                    }
                }
                if (runGround.Length < levels)
                {
                    levels = 0;   // an older export without the run: walk only
                }
                string root = Path.Combine(content.RootDir, Folder);
                groundPerCycle = new float[levels + 1];
                clipNames = new string[levels + 1];
                for (int c = 0; c <= levels; c++)
                {
                    groundPerCycle[c] = c == 0 ? walkGround : runGround[c - 1];
                    clipNames[c] = c == 0 ? "walk" : "run" + c;
                }
                if (Skinned)
                {
                    Wounds.Root = content.RootDir;
                    skin = SkinnedFigure.Load(root);
                    for (int c = 0; skin != null && c <= levels; c++)
                    {
                        if (!skin.Has(clipNames[c]))
                        {
                            skin = null;
                        }
                    }
                    if (skin != null && skin.Has("idle"))
                    {
                        string garments = "";
                        for (int i = 1; i < skin.Pieces; i++)
                        {
                            garments += (i > 1 ? ", " : "") + skin.PieceName(i);
                        }
                        Log.Message(Tag + "the chibi is skinned at runtime (" + Folder + "/skin.txt, anim.txt), garments: "
                            + (garments.Length > 0 ? garments : "none") + ".");
                        ready = true;
                        LoadTrail.Note("chibi loaded, skinned" + LoadTrail.MemoryNow());
                        return ready;
                    }
                    skin = null;
                    Log.Message(Tag + Folder + " has no skin.txt and anim.txt for every clip; the baked frames are drawn.");
                }
                idle = Api.LoadModel(content, Folder + "/idle.obj");
                idleFace = FaceLayers.Load(Path.Combine(root, "face_idle.obj"));
                bool withFaces = idleFace != null;
                cycles = new ModelHandle[levels + 1][];
                cycleFaces = new FaceLayers.Patch[levels + 1][];
                for (int c = 0; c <= levels; c++)
                {
                    string prefix = c == 0 ? "/walk_" : "/run" + c + "_";
                    cycles[c] = new ModelHandle[frames];
                    cycleFaces[c] = new FaceLayers.Patch[frames];
                    for (int i = 0; i < frames; i++)
                    {
                        string name = prefix + i.ToString("00") + ".obj";
                        cycles[c][i] = Api.LoadModel(content, Folder + name);
                        if (cycles[c][i] == null)
                        {
                            idle = null;
                        }
                        if (withFaces)
                        {
                            cycleFaces[c][i] = FaceLayers.Load(Path.Combine(root, "face_" + name.Substring(1)));
                            withFaces = cycleFaces[c][i] != null;
                        }
                    }
                }
                if (!withFaces)
                {
                    idleFace = null;
                    cycleFaces = null;
                    Log.Message(Tag + Folder + " has no face patches (face_*.obj); the chibi's head stays bare.");
                }
                ready = idle != null && frames > 0;
                if (!ready)
                {
                    Log.Warning(Tag + "the chibi models in " + Folder + " are missing or unreadable; IsoRim's figures are kept.");
                }
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "could not read " + Folder + ", IsoRim's figures are kept: " + e);
            }
            return ready;
        }

        // Whether the chibi stands in for IsoRim's figure (see the top), by what the pawn is; the figure drawer (DrawFigure)
        // also leaves to IsoRim what its placing says the chibi doesn't do (attached, in an upright vat, in a gibbet).
        private static bool Handles(Pawn pawn)
        {
            if (pawn == null || pawn.story == null || (ColonistsOnly && !pawn.IsColonist))
            {
                return false;
            }
            if (Api.FigureModeOf(pawn) == FigureMode.Card || Invisible(pawn))
            {
                return false;
            }
            if (pawn.Dead)
            {
                // Not gone to bones, nor in a grave (only spawned or carried corpses, or one hung in a gibbet cage).
                Corpse corpse = pawn.Corpse;
                return corpse != null && (corpse.Spawned || corpse.ParentHolder is Pawn_CarryTracker || corpse.ParentHolder is Building_GibbetCage cage && cage.Spawned)
                    && RotOf(pawn) != RotDrawMode.Dessicated;
            }
            if (pawn.ParentHolder is IThingHolderWithDrawnPawn)
            {
                // In a building that shows who is inside: on its couch (a vat that floats them upright is IsoRim's, by the
                // placing's InVat).
            }
            else if (!pawn.Spawned && !(pawn.ParentHolder is Pawn_CarryTracker) && !(pawn.ParentHolder is PawnFlyer))
            {
                return false;   // somewhere else IsoRim knows how to place
            }
            Vector3 point;
            float yaw;
            return !Api.AttachedPlace(pawn, out point, out yaw);
        }

        // People who are invisible must stay so: IsoRim leaves them their sprite, which the game hides (as IsoRim tells them,
        // by a hediff named so).
        private static bool Invisible(Pawn pawn)
        {
            List<Hediff> all = pawn.health != null && pawn.health.hediffSet != null ? pawn.health.hediffSet.hediffs : null;
            for (int i = 0; all != null && i < all.Count; i++)
            {
                if (all[i].def != null && all[i].def.defName != null && all[i].def.defName.IndexOf("Invisib", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        // The building a pawn sits on, as IsoRim seats its figures (on its feet, not moving, on a sittable edifice, at a life
        // stage that sits: the game shifts its sprite there); null for none.
        private static Building SeatUnder(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned || pawn.Dead || pawn.ageTracker == null || pawn.pather == null || pawn.pather.MovingNow
                || pawn.GetPosture() != PawnPosture.Standing)
            {
                return null;
            }
            LifeStageDef stage = pawn.ageTracker.CurLifeStage;
            if (stage == null || !stage.sittingOffset.HasValue)
            {
                return null;
            }
            Map map = pawn.Map;
            Building b = map != null && pawn.Position.InBounds(map) ? pawn.Position.GetEdifice(map) : null;
            return b != null && b.def.building != null && b.def.building.isSittable ? b : null;
        }

        // How far the game's own sitting shift has got (0 .. 1): it slides the sprite up the screen onto the seat.
        private static float SeatAmount(Pawn pawn, Vector3 drawLoc)
        {
            LifeStageDef stage = pawn.ageTracker.CurLifeStage;
            float offset = stage != null && stage.sittingOffset.HasValue ? stage.sittingOffset.Value : 0f;
            float dz = drawLoc.z - (pawn.Position.z + 0.5f);
            return offset > 0.001f ? Mathf.Clamp(dz, 0f, offset) / offset : 1f;
        }

        // The skin's colour as the game draws it now: rotting, a corpse's is the game's rotten colour.
        private static Color SkinOf(Pawn pawn)
        {
            Color skin = pawn.story.SkinColor;
            return pawn.Dead && RotOf(pawn) == RotDrawMode.Rotting ? PawnRenderUtility.GetRottenColor(skin) : skin;
        }

        private static RotDrawMode RotOf(Pawn pawn)
        {
            return pawn.Drawer != null && pawn.Drawer.renderer != null ? pawn.Drawer.renderer.CurRotDrawMode : RotDrawMode.Fresh;
        }

        // What the baked frames can show: standing or walking.
        private static bool Standing(Pawn pawn)
        {
            if (pawn.Dead || !pawn.Spawned || pawn.GetPosture() != PawnPosture.Standing || pawn.Crawling)
            {
                return false;
            }
            return SeatUnder(pawn) == null;
        }

        private static float SizeOf(Pawn pawn)
        {
            float grown = GrownAge(pawn);
            if (grown > 0f)
            {
                return Mathf.Lerp(BirthSize, 1f, Growth.Evaluate(Mathf.Clamp01(pawn.ageTracker.AgeBiologicalYearsFloat / grown))) * ScaleUp * PawnSize * GenderSize(pawn) * HeightSize(pawn);
            }
            float factor = pawn.ageTracker != null && pawn.ageTracker.CurLifeStage != null ? pawn.ageTracker.CurLifeStage.bodySizeFactor : 1f;
            float size = Api.FigureSizeFor(factor);
            return size * ScaleUp * PawnSize * GenderSize(pawn) * HeightSize(pawn);
        }

        private const float MostGrownAge = 10000f;   // years: a life stage starting later than this is not one a pawn ages into (GrownAge)

        // Of the standard size: the mod options' size for every pawn, on top of ScaleUp.
        private static float PawnSize
        {
            get { return SettingsMod.Settings != null ? SettingsMod.Settings.pawnSize : 1f; }
        }

        // Of the standard size: GenderGap, if the mod's options have it on, split between the genders, a male that half of it bigger and a
        // female that half smaller, so both stay near the standard. As in people, it is none through childhood and opens
        // up over puberty, from GenderFrom to GenderFull of the race's grown age (11 to 16 of 18), eased at both ends
        // (all at once for a race with no stages to grow through).
        private static float GenderSize(Pawn pawn)
        {
            float side = pawn.gender == Gender.Male ? 0.5f : pawn.gender == Gender.Female ? -0.5f : 0f;
            if (side == 0f)
            {
                return 1f;
            }
            float gap = SettingsMod.Settings == null || SettingsMod.Settings.genderSizes ? GenderGap : 0f;
            float grown = GrownAge(pawn);
            float amount = grown > 0f ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(GenderFrom, GenderFull, pawn.ageTracker.AgeBiologicalYearsFloat / grown)) : 1f;
            return 1f + side * gap * amount;
        }

        // Of the standard size: each pawn's own height, a normal spread about its gender's size with the mod options'
        // heightSpread as its standard deviation (people's heights within a sex spread about 4% each way, about 7 cm in
        // 170), cut off at HeightMost of it. The pawn keeps its place in the spread all its life, as children mostly keep
        // their percentile as they grow. Drawn from the pawn's id, so it is the same every draw and after loading.
        private static float HeightSize(Pawn pawn)
        {
            float spread = SettingsMod.Settings != null ? SettingsMod.Settings.heightSpread : 0.04f;
            if (spread <= 0f)
            {
                return 1f;
            }
            // Box-Muller from two uniforms hashed from the id; the first is kept off 0 for its log. Gen.HashCombineInt
            // leaves pawns with ids in a row (a raid, a starting colony) at nearly the same height, so this mixes harder.
            uint a = Mix((uint)pawn.thingIDNumber ^ HeightSeed);
            uint b = Mix(a ^ HeightSeed);
            float u1 = ((a >> 8) + 0.5f) / 16777216f;
            float u2 = (b >> 8) / 16777216f;
            float z = Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * Mathf.PI * u2);
            return 1f + spread * Mathf.Clamp(z, -HeightMost, HeightMost);
        }

        // MurmurHash3's finalizer: every bit of the result depends on every bit of x.
        private static uint Mix(uint x)
        {
            x ^= x >> 16;
            x *= 0x85ebca6bu;
            x ^= x >> 13;
            x *= 0xc2b2ae35u;
            x ^= x >> 16;
            return x;
        }

        // Years: the age the pawn's race is grown at (its last life stage's), or 0 if it has no stages to grow through.
        private static float GrownAge(Pawn pawn)
        {
            // The last stage of a believable age: Character Editor adds its body-size stages to the end of the race's list at
            // float.MaxValue, so the game never ages a pawn into them, and taking the last as the grown age drew every pawn of
            // that race at its newborn size.
            List<LifeStageAge> stages = pawn.ageTracker != null ? pawn.RaceProps.lifeStageAges : null;
            if (stages == null || stages.Count < 2)
            {
                return 0f;
            }
            for (int i = stages.Count - 1; i > 0; i--)
            {
                if (stages[i].minAge < MostGrownAge)
                {
                    return stages[i].minAge;
                }
            }
            return 0f;
        }

        private static bool Baby(Pawn pawn)
        {
            return pawn.DevelopmentalStage.Baby();
        }

        // How far a baby has grown, 0 at birth to 1 at BabyYears.
        private static float BabyGrowth(Pawn pawn)
        {
            return pawn.ageTracker != null ? Mathf.Clamp01(pawn.ageTracker.AgeBiologicalYearsFloat / BabyYears) : 1f;
        }

        // How far along a pregnancy is, 0 (none) to 1 (full term, and in labour), in PregnantSteps so the shapes it
        // takes on the way are few.
        private static float PregnancyOf(Pawn pawn)
        {
            HediffSet set = pawn.health != null ? pawn.health.hediffSet : null;
            if (set == null)
            {
                return 0f;
            }
            if (set.GetFirstHediff<Hediff_Labor>() != null || set.GetFirstHediff<Hediff_LaborPushing>() != null)
            {
                return 1f;
            }
            Hediff_Pregnant pregnant = set.GetFirstHediff<Hediff_Pregnant>();
            return pregnant != null ? Mathf.Ceil(Mathf.Clamp01(pregnant.GestationProgress) * PregnantSteps) / PregnantSteps : 0f;
        }

        // How much of the Babylimbs key the pawn has: all at birth, none by BabyLimbsYears, in BabyLimbsSteps so the
        // shapes it takes on the way are few.
        private static float BabyLimbsOf(Pawn pawn)
        {
            if (pawn.ageTracker == null)
            {
                return 0f;
            }
            float grown = Mathf.Clamp01(pawn.ageTracker.AgeBiologicalYearsFloat / BabyLimbsYears);
            return Mathf.Round((1f - grown) * BabyLimbsSteps) / BabyLimbsSteps;
        }

        // The body's shape for the pawn: its body type's key with its gender's added on a build, or a baby's (BabyBody, less of it as it grows),
        // with the Pregnant key as far as its pregnancy has come and the Babylimbs key as young as it is.
        private static float HeadShrink
        {
            get { return SettingsMod.Settings != null ? SettingsMod.Settings.headShrink : 0f; }   // 0 .. 1 of the Shrinkhead key, from the mod's options
        }

        // The head shrink for a pawn with limbs of the Babylimbs key: a young head stays nearer the full chibi one, from
        // BabyHeadShrink of the options' shrink at birth to all of it by BabyLimbsYears. It goes in the limbs' steps, so it
        // makes no shapes of its own.
        private static float HeadShrinkOf(float limbs)
        {
            return HeadShrink * Mathf.Lerp(1f, BabyHeadShrink, limbs);
        }

        // The body keys named for a gene (the same name in the .blend), added whole while the gene is active, and the one that
        // takes the chibi's own ears off for a pawn whose genes give it animal ears. Null with none of them, as most pawns have,
        // so the shape's lookup makes no list for them. The hand genes draw nothing in the game, but their descriptions
        // say what the hands look like, and the chibi's hands show; their keys move the finger joints too (skin.txt's db),
        // so what the hands hold still meets them.
        private static readonly string[] GeneKeys = { "Ears_Pointed", "Brow_Heavy", "Jaw_Heavy", "Head_Gaunt", NosePig,
            "Hands_Pig", "ElongatedFingers", "WebbedPhalanges" };
        private const string NosePig = "Nose_Pig";   // also the gene whose nostrils go on the face patch (NoseDesign)
        private const string NoseDesign = "snout";   // ridges_snout.png, the Nose_Pig key's nostrils on the face patch
        private const string NoHumanEarsKey = "No_Human_Ears";
        private static List<string> GeneKeysOf(Pawn pawn)
        {
            if (pawn.genes == null)
            {
                return null;
            }
            GeneDef[] defs = GeneKeyDefs();
            List<string> keys = null;
            for (int i = 0; i < GeneKeys.Length; i++)
            {
                if (defs[i] != null && pawn.genes.HasActiveGene(defs[i]))
                {
                    (keys = keys ?? new List<string>()).Add(GeneKeys[i]);
                }
            }
            if (EarFileOf(pawn) != null)
            {
                (keys = keys ?? new List<string>()).Add(NoHumanEarsKey);
            }
            return keys;
        }
        private static GeneDef[] geneKeyDefs;

        private static GeneDef[] GeneKeyDefs()
        {
            if (geneKeyDefs == null)
            {
                geneKeyDefs = new GeneDef[GeneKeys.Length];
                for (int i = 0; i < GeneKeys.Length; i++)
                {
                    geneKeyDefs[i] = DefDatabase<GeneDef>.GetNamedSilentFail(GeneKeys[i]);
                }
            }
            return geneKeyDefs;
        }

        // Whether the pawn has the gene of one of GeneKeys active, without making GeneKeysOf's list.
        private static bool HasGeneKey(Pawn pawn, string key)
        {
            int i = Array.IndexOf(GeneKeys, key);
            GeneDef def = i >= 0 ? GeneKeyDefs()[i] : null;
            return def != null && pawn.genes != null && pawn.genes.HasActiveGene(def);
        }

        // The design of facial ridges the pawn's gene gives it, by ridges_<name>.png's name (always the same one, by its
        // id, as the game picks one of its three sprites per pawn); null with no such gene.
        private static readonly string[] RidgeDesigns = { "a", "b", "c" };
        private static string RidgesOf(Pawn pawn)
        {
            if (!ridgeGeneLooked)
            {
                ridgeGeneLooked = true;
                ridgeGene = DefDatabase<GeneDef>.GetNamedSilentFail("FacialRidges");
            }
            if (ridgeGene == null || pawn.genes == null || !pawn.genes.HasActiveGene(ridgeGene))
            {
                return null;
            }
            return RidgeDesigns[Mix((uint)pawn.thingIDNumber) % (uint)RidgeDesigns.Length];
        }
        private static GeneDef ridgeGene;
        private static bool ridgeGeneLooked;

        private static SkinnedFigure.Shape ShapeFor(Pawn pawn)
        {
            float limbs = BabyLimbsOf(pawn);
            float shrink = HeadShrinkOf(limbs);
            List<string> genes = GeneKeysOf(pawn);
            if (Baby(pawn))
            {
                return skin.ShapeOf(BabyBody, null, 0f, shrink, 1f - BabyGrowth(pawn), 0f, limbs, null, genes);
            }
            BodyTypeDef body = pawn.story.bodyType;
            string gender = pawn.gender == Gender.Female ? "Female" : pawn.gender == Gender.Male ? "Male" : null;
            float spread = SettingsMod.Settings != null && SettingsMod.Settings.variedBodies ? SettingsMod.Settings.bodySpread : 0f;
            List<KeyValuePair<string, float>> builds;
            if (spread > 0f && body != null && (builds = VariedBuild(pawn, body.defName, spread)) != null)
            {
                // The gender's key whole, as a Male or Female body has it and a build is added on (GenderOnBuilds), and the
                // builds on top; a child's on the base body, with no gender's.
                string key = body.defName == "Child" ? null : gender ?? (body.defName == "Male" || body.defName == "Female" ? body.defName : null);
                return skin.ShapeOf(key, null, 0f, shrink, 1f, PregnancyOf(pawn), limbs, builds, genes);
            }
            return skin.ShapeOf(body != null ? body.defName : null, gender, GenderOnBuilds, shrink, 1f, PregnancyOf(pawn), limbs, null, genes);
        }

        // Varied bodies (the mod options): the pawn's build as amounts of the Thin, Fat and Hulk keys, spread about its body
        // type's on two axes, weight (-1 all Thin .. 1 all Fat) and muscle (0 .. 1 of Hulk). A Male, Female or Child body
        // sits at (0, 0), Thin at (-1, 0), Fat at (1, 0), Hulk at (0, 1). Each axis is moved by a normal spread with the
        // options' bodySpread as its standard deviation, cut off at BuildMost of it, and turned back in off either end, so
        // a Thin pawn is never thinner than the Thin key but may be less thin, and an average one varies both ways (though
        // only FatShare as far toward Fat, as that key is extreme); but
        // muscle that would go below none stays at none, so half the Male, Female and Child bodies have no Hulk at all. Drawn
        // from the pawn's id, so it is the same every draw and after loading, and keeps its place in the spread as it
        // grows up and gets its grown body type. Null for a body type it has no place for (a mod's own).
        private static List<KeyValuePair<string, float>> VariedBuild(Pawn pawn, string bodyType, float spread)
        {
            float weight, muscle;
            switch (bodyType)
            {
                case "Male": case "Female": case "Child": weight = 0f; muscle = 0f; break;
                case "Thin": weight = -1f; muscle = 0f; break;
                case "Fat": weight = 1f; muscle = 0f; break;
                case "Hulk": weight = 0f; muscle = 1f; break;
                default: return null;
            }
            // Box-Muller as HeightSize, its two normals from one pair of uniforms.
            uint a = Mix((uint)pawn.thingIDNumber ^ BuildSeed);
            uint b = Mix(a ^ BuildSeed);
            float u1 = ((a >> 8) + 0.5f) / 16777216f;
            float u2 = (b >> 8) / 16777216f;
            float r = Mathf.Sqrt(-2f * Mathf.Log(u1));
            weight = Within(weight + spread * Mathf.Clamp(r * Mathf.Cos(2f * Mathf.PI * u2), -BuildMost, BuildMost), -1f, 1f);
            // Only the Fat body type keeps its whole share of the Fat key; any other is spread less far into it.
            if (weight > 0f && bodyType != "Fat")
            {
                weight *= FatShare;
            }
            // Muscle below none is none, not turned back in, so half the average bodies keep the plain shape.
            muscle = Mathf.Max(0f, Within(muscle + spread * Mathf.Clamp(r * Mathf.Sin(2f * Mathf.PI * u2), -BuildMost, BuildMost), float.NegativeInfinity, 1f));
            weight = Mathf.Round(weight * BuildSteps) / BuildSteps;
            muscle = Mathf.Round(muscle * BuildSteps) / BuildSteps;
            return new List<KeyValuePair<string, float>>
            {
                new KeyValuePair<string, float>("Thin", -weight),   // ShapeOf leaves out keys at 0 or less
                new KeyValuePair<string, float>("Fat", weight),
                new KeyValuePair<string, float>("Hulk", muscle),
            };
        }

        // v turned back in off whichever end of lo .. hi it went past, as off a mirror.
        private static float Within(float v, float lo, float hi)
        {
            if (v < lo)
            {
                v = lo + (lo - v);
            }
            if (v > hi)
            {
                v = hi - (v - hi);
            }
            return Mathf.Clamp(v, lo, hi);
        }

        // How fast the pawn is going along its path, in cells per game second: the step it is taking over the ticks the
        // game charges for it (terrain, doors and its urgency, from ambling to hurrying, are all in that cost).
        private static float SpeedOf(Pawn pawn)
        {
            Pawn_PathFollower pather = pawn.pather;
            if (pather == null || !pather.Moving || pather.nextCellCostTotal <= 0f)
            {
                return 0f;
            }
            IntVec3 at = pawn.Position, next = pather.nextCell;
            float step = at.x != next.x && at.z != next.z ? 1.4142f : 1f;
            return step * GenTicks.TicksPerRealSecond / pather.nextCellCostTotal;
        }

        // The pawn's run (0 walking .. 1 running) eased toward what its speed asks for, its drafted stance toward whether
        // it is drafted, and from one weapon's stance to another's when it swaps, once a frame.
        private static Ease EaseOf(Pawn pawn)
        {
            int frame = Time.frameCount;
            float run = Mathf.InverseLerp(RunFrom, RunFull, SpeedOf(pawn));
            // Carrying, in the pose for its load. Otherwise in the stance whenever the weapon shows (drafted, hunting, guard
            // duty, aiming), and drafted with bare hands.
            string carryClip = skin != null ? CarryClip(pawn) : null;
            float carry = carryClip != null ? 1f : 0f;
            float playing = carryClip == "carry_cradle" && PlaysWithBaby(pawn) && skin.Has("carry_play") ? 1f : 0f;
            if (carryClip == "carry_cuddle" && skin.Has("carry_cuddle_stroke") && (pawn.pather == null || !pawn.pather.Moving))
            {
                playing = 1f;   // a cuddled pet stroked while it stands or sits still
            }
            // Bare-handed, the fists only come up with an enemy near; otherwise it stands at ease.
            bool armed = pawn.equipment != null && pawn.equipment.Primary != null;
            // Fighting hand to hand or throwing, it takes the stance too (bare-handed in a social fight).
            float strikePhase = 0f;
            float? strikeFace = null;
            string strikeClip = skin != null && carryClip == null ? StrikeOf(pawn, out strikePhase, out strikeFace) : null;
            string stance = skin != null ? DraftedClip(pawn) : null;
            // Aiming, the weapon brought up to the aim, if the export has aim clips for it.
            float aimGame, aimDrawn;
            // (Not while it uses its hip gear: IsoRim aims the weapon for any verb the stance is busy with.)
            bool aiming = carryClip == null && !HipVerb(pawn) && AimOf(pawn, out aimGame, out aimDrawn);
            string aimName = skin != null && aiming ? AimName(pawn) : null;
            float aim = aimName != null ? 1f : 0f;
            int now = Find.TickManager.TicksGame;
            float water = WaterOf(pawn);
            float swim = pawn.Swimming && water > 0f ? 1f : 0f;   // marsh too, as the game swims in it
            float cowering = Cowering(pawn) ? 1f : 0f;
            float leading = Leading(pawn) ? 1f : 0f;
            // Reading goes the same way as eating: laid over the sit or the idle from the waist up, in place of the carry.
            string eatClip = skin != null ? EatOf(pawn) ?? ReadOf(pawn) ?? TeachOf(pawn) ?? KissOf(pawn) : null;
            float eating = eatClip != null ? 1f : 0f;
            float talking = skin != null && skin.Has("talk") && TalkOf(pawn) ? 1f : 0f;
            string tossClip = skin != null ? TossClipOf(pawn) : null;
            float tossing = tossClip != null && skin.Has(tossClip) ? 1f : 0f;
            float tossAt = tossing > 0f ? TossPhase(pawn, tossClip) : -1f;
            // (a horseshoe at the pin, target A; a snowball at the partner, target B)
            float? tossFace = tossing > 0f ? FaceOf(pawn, tossClip == "snowball" ? pawn.CurJob.targetB : pawn.CurJob.targetA) : null;
            float facing = 0f;
            Vector3? workBeside = null;
            string workKind = skin != null ? WorkOf(pawn, out facing, out workBeside) : null;
            float working = workKind != null ? 1f : 0f;
            if (workKind != null && carryClip == "carry_item")
            {
                // At work with an item in hand (a doctor's medicine, which the game carries through the whole tend; a
                // meal fed to a patient): the work clip, not the carry, as the carry would weigh the work out entirely.
                // The item is still drawn between the hands (DrawLoad).
                carryClip = null;
                carry = 0f;
            }
            // Anything else it is doing takes the place of the stance, the weapon put away first and drawn again after,
            // unless it is fighting with it. Work it is about to reach counts, so it arrives with its hands free; work it is
            // already at, or the like, wants them now (StepDraw hurries). A wound or the cold doesn't: they are only
            // what it does with idle hands, and would keep a hurt soldier from ever holding its weapon.
            bool wantedNow = workKind != null || eatClip != null || talking > 0f || tossing > 0f || cowering > 0f || leading > 0f;
            float unused;
            Vector3? unusedBeside;
            bool handsWanted = strikeClip == null && !aiming && !Reloading(pawn)
                && (wantedNow || (skin != null && WorkOf(pawn, out unused, out unusedBeside, true) != null));
            bool weaponShows = carryClip == null && !handsWanted && WeaponShows(pawn);
            float drafted = carryClip == null && !handsWanted && (weaponShows || pawn.Drafted && (armed || ThreatNear(pawn)) || strikeClip != null) ? 1f : 0f;
            float? castFace = null;
            string castClip = skin != null && carryClip == null ? CastOf(pawn, out castFace) : null;
            float casting = castClip != null ? 1f : 0f;
            float dazed = pawn.stances != null && pawn.stances.stunner != null && pawn.stances.stunner.Stunned ? 1f : 0f;
            bool flying = FlightOf(pawn) >= 0f;
            // A shield shown is held up in the guard on its feet, unless carrying (Shields.cs).
            float shielding = skin != null && carryClip == null && swim <= 0f && !flying && pawn.GetPosture() == PawnPosture.Standing
                && ShieldUp(pawn) ? 1f : 0f;
            // The hip gear is taken in the left hand on its feet with nothing else in its hands (a shield is).
            bool hipCan = skin != null && carryClip == null && eatClip == null && workKind == null && swim <= 0f && !flying
                && pawn.GetPosture() == PawnPosture.Standing && shielding <= 0f;
            Ease e;
            if (!eases.TryGetValue(pawn.thingIDNumber, out e))
            {
                e = new Ease();
            }
            if (eatClip == "pill")
            {
                PillOf(pawn, e);
            }
            if (!eases.ContainsKey(pawn.thingIDNumber))
            {
                e.run = run;   // first seen: already at its pace and in its stance
                e.drafted = drafted;
                e.handHeld = weaponShows;
                e.stance = stance;
                e.aim = aim;
                e.aimName = aimName;
                e.carry = carry;
                e.carryClip = carryClip;
                e.play = playing;
                LoadOf(pawn, out e.load, out e.loadCount);
                e.limpBy = LimpOf(pawn, out e.limpClip);
                e.limp = e.limpBy;
                e.limpTick = now;
                e.hurtBy = HurtOf(pawn, out e.hurtClip);
                e.hurt = e.hurtBy;
                e.hold = e.hurtBy > 0f ? 1f : 0f;
                e.cower = cowering;
                e.eat = eating;
                e.eatClip = eatClip;
                e.lead = leading;
                e.shield = shielding;
                e.talk = talking;
                e.toss = tossing;
                e.tossClip = tossClip;
                e.tossAt = tossAt;
                e.tossFace = tossFace;
                e.cold = ColdOf(pawn);
                e.rubFree = AtLeisure(pawn) ? 1f : 0f;
                e.cast = casting;
                e.castClip = castClip;
                e.castFace = castFace;
                e.reel = StaggerReels && skin != null && skin.Has("reel") ? ReelOf(pawn, e, skin) : 0f;
                e.daze = dazed;
                e.strikeClip = strikeClip;
                e.strikePhase = strikePhase;
                e.strikeFace = strikeFace;
                e.work = working;
                e.workKind = workKind;
                e.workRoll = Gen.HashCombineInt(pawn.thingIDNumber, now);
                e.workFace = facing;
                e.workBeside = workBeside;
                e.flying = flying;
                e.depth = water;
                e.swim = swim;
                if (hipCan)
                {
                    StepHip(pawn, e, true, false, 0f);   // first seen: already holding it if it would be
                }
                if (skin != null)
                {
                    StepShield(pawn, e, false, shielding, false, 0f);   // and its shield where it would be
                }
                e.frame = frame;
                eases[pawn.thingIDNumber] = e;
                Prune(frame);
                return e;
            }
            if (e.frame != frame)
            {
                bool recent = frame - e.frame <= 2;
                Vector3 drawn = pawn.DrawPos;
                if (recent && pawn.pather != null && pawn.pather.Moving)
                {
                    Vector3 walked = drawn - e.walkedFrom;
                    walked.y = 0f;
                    if (walked.sqrMagnitude > 1e-6f)
                    {
                        e.cameWay = walked.normalized;
                    }
                }
                e.walkedFrom = drawn;
                e.run = recent ? Mathf.MoveTowards(e.run, run, RunEase * Time.deltaTime) : run;
                e.drafted = recent ? Mathf.MoveTowards(e.drafted, drafted, DraftEase * Time.deltaTime) : drafted;
                e.carry = recent ? Mathf.MoveTowards(e.carry, carry, CarryEase * Time.deltaTime) : carry;
                e.carryClip = carryClip ?? e.carryClip;   // a new load's pose straight away; set down, eased out of the last
                e.play = recent ? Mathf.MoveTowards(e.play, playing, PlayEase * Time.deltaTime) : playing;
                int load, loadCount;
                LoadOf(pawn, out load, out loadCount);
                if (load != e.load || loadCount != e.loadCount)
                {
                    // Picked up, set down, swapped or a stack topped up: bent to the ground, unless it ate what it held. A
                    // bend still under way starts over from as far down as it is, rather than snapping up first.
                    bool eaten = pawn.CurJobDef == JobDefOf.Ingest;
                    e.bent = recent && !eaten ? BendDown * Bent(e.bent) : 1f;
                    e.bentTick = Find.TickManager.TicksGame;
                    e.bentRoll = Gen.HashCombineInt(pawn.thingIDNumber, e.bentTick);
                    e.load = load;
                    e.loadCount = loadCount;
                    // A person set down is still drawn in its arms until the hands reach the ground (Dropping).
                    e.dropped = recent && !eaten ? e.holding : 0;
                    e.droppedTick = Find.TickManager.TicksGame;
                    e.holding = 0;
                }
                if (Dropping(e))
                {
                    e.carry = 1f;   // its arms keep them until then
                }
                int took;
                if (taken.TryGetValue(pawn.thingIDNumber, out took))
                {
                    // A weapon or clothes taken up off the ground: the same bend as a pick-up.
                    taken.Remove(pawn.thingIDNumber);
                    int since = Find.TickManager.TicksGame - took;
                    if (recent && since >= 0 && since <= TakenTicks)
                    {
                        e.bent = BendDown * Bent(e.bent);
                        e.bentTick = Find.TickManager.TicksGame;
                        e.bentRoll = Gen.HashCombineInt(pawn.thingIDNumber, e.bentTick);
                    }
                }
                if (stance != e.stance)
                {
                    // A new weapon: from the stance it shows now (the nearer of the two, if it was mid-swap), unless it is
                    // not in a stance at all to swap from.
                    e.fromStance = e.swap >= 0.5f ? e.stance : e.fromStance;
                    e.stance = stance;
                    e.swap = recent && e.drafted > 0f && e.fromStance != null ? 0f : 1f;
                }
                e.swap = Mathf.MoveTowards(e.swap, 1f, DraftEase * Time.deltaTime);
                // Another weapon's aim swaps in at once (one aim is blended at a time).
                bool sameAim = aimName == null || aimName == e.aimName;
                e.aim = recent && sameAim ? Mathf.MoveTowards(e.aim, aim, AimEase * Time.deltaTime) : aim;
                e.aimName = aimName ?? e.aimName;
                if (now - e.limpTick >= LimpCheck || now < e.limpTick)
                {
                    string limpClip;
                    e.limpBy = LimpOf(pawn, out limpClip);
                    e.limpClip = limpClip ?? e.limpClip;
                    e.limpTick = now;
                    string hurtClip;
                    e.hurtBy = HurtOf(pawn, out hurtClip);
                    e.hurtClip = hurtClip ?? e.hurtClip;
                    e.cold = ColdOf(pawn);
                    e.drunkBy = DrunkOf(pawn);
                }
                e.limp = recent ? Mathf.MoveTowards(e.limp, e.limpBy, LimpEase * Time.deltaTime) : e.limpBy;
                e.hurt = recent ? Mathf.MoveTowards(e.hurt, e.hurtBy, LimpEase * Time.deltaTime) : e.hurtBy;
                float hold = e.hurtBy > 0f ? 1f : 0f;
                e.hold = recent ? Mathf.MoveTowards(e.hold, hold, HoldEase * Time.deltaTime) : hold;
                float game = Time.deltaTime * Find.TickManager.TickRateMultiplier;
                StepDraw(pawn, e, weaponShows, carryClip == null && strikeClip == null && swim <= 0f && !flying
                    && (pawn.GetPosture() == PawnPosture.Standing || Hunkering(pawn)), drafted, recent, game, wantedNow);
                // What takes the stance's place waits until the weapon is away (a cowering pawn doesn't).
                float putAway = e.drawAt >= 0f && !e.handHeld ? 0f : 1f;
                e.cower = recent ? Mathf.MoveTowards(e.cower, cowering, CowerEase * Time.deltaTime) : cowering;
                e.lead = recent ? Mathf.MoveTowards(e.lead, leading * putAway, LeadEase * Time.deltaTime) : leading;
                e.drunk = recent ? Mathf.MoveTowards(e.drunk, e.drunkBy, DrunkEase * Time.deltaTime) : e.drunkBy;
                float sad = skin != null && skin.Has("sad") && Sad(pawn) ? 1f : 0f;
                e.sad = recent ? Mathf.MoveTowards(e.sad, sad, SadEase * Time.deltaTime) : sad;
                float burning = skin != null && skin.Has("burning") && Burning(pawn) && swim <= 0f && !flying ? 1f : 0f;
                e.burn = recent ? Mathf.MoveTowards(e.burn, burning, BurnEase * Time.deltaTime) : burning;
                if (skin != null)
                {
                    // A shield carried on the back is taken from there into the guard on its feet with its hands free
                    // (shield_draw), and put back the same way.
                    StepShield(pawn, e, carryClip == null && swim <= 0f && !flying && pawn.GetPosture() == PawnPosture.Standing,
                        shielding, recent, game);
                }
                else
                {
                    e.shield = recent ? Mathf.MoveTowards(e.shield, shielding, ShieldEase * Time.deltaTime) : shielding;
                }
                e.eat = recent ? Mathf.MoveTowards(e.eat, eating * putAway, EatEase * Time.deltaTime) : eating;
                e.eatClip = eatClip ?? e.eatClip;
                e.talk = recent ? Mathf.MoveTowards(e.talk, talking * putAway, TalkEase * Time.deltaTime) : talking;
                e.toss = recent ? Mathf.MoveTowards(e.toss, tossing * putAway, TalkEase * Time.deltaTime) : tossing;
                e.tossClip = tossClip ?? e.tossClip;
                e.tossAt = tossAt;
                e.tossFace = tossFace ?? e.tossFace;
                float reloading = skin != null && skin.Has("reload") ? ReloadOf(pawn, e) : 0f;
                e.reload = recent ? Mathf.MoveTowards(e.reload, reloading, ReloadEase * Time.deltaTime) : reloading;
                e.cast = recent ? Mathf.MoveTowards(e.cast, casting, CastEase * Time.deltaTime) : casting;
                e.castClip = castClip ?? e.castClip;
                e.castFace = castFace;
                if (skin != null)
                {
                    StepHip(pawn, e, hipCan, recent, game);
                }
                float reeling = StaggerReels && skin != null && skin.Has("reel") ? ReelOf(pawn, e, skin) : 0f;
                e.reel = recent ? Mathf.MoveTowards(e.reel, reeling, ReelEase * Time.deltaTime) : reeling;
                e.daze = recent ? Mathf.MoveTowards(e.daze, dazed, DazeEase * Time.deltaTime) : dazed;
                e.strikeClip = strikeClip;
                e.strikePhase = strikePhase;
                e.strikeFace = strikeFace;
                // Going straight from one kind of work to another swaps at once (one work clip is blended at a time).
                if (workKind != null)
                {
                    bool swapped = workKind != e.workKind && e.work > 0f;
                    if (workKind != e.workKind || e.work <= 0f)
                    {
                        e.workRoll = Gen.HashCombineInt(pawn.thingIDNumber, now);   // set to work: a stroke variant for this bout
                    }
                    e.work = recent && !swapped ? Mathf.MoveTowards(e.work, putAway, WorkEase * Time.deltaTime) : 1f;
                    e.workKind = workKind;
                    e.workFace = facing;
                e.workBeside = workBeside;
                }
                else
                {
                    e.work = recent ? Mathf.MoveTowards(e.work, 0f, WorkEase * Time.deltaTime) : 0f;
                }
                // Cooking: stirring the pot now and then in place of the bench or ground work, once at it.
                e.stirClip = skin != null && workKind != null ? StirOf(pawn, workKind) : null;
                e.stirAt = e.stirClip != null ? BoutOf(pawn, e.stir, e.stirClip, e.work >= 1f, StirRestFrom, StirRestTo) : -1f;
                if (e.flying && !flying)
                {
                    e.landed = recent ? 0f : 1f;   // just landed
                    e.landedTick = now;
                    e.landedRoll = Gen.HashCombineInt(pawn.thingIDNumber, now);
                }
                e.flying = flying;
                // Into and out of the water on game time, so a pawn hurried along at a higher speed still sinks as it
                // steps in.
                e.depth = recent ? Mathf.MoveTowards(e.depth, water, WaterEase * game) : water;
                e.swim = recent ? Mathf.MoveTowards(e.swim, swim, SwimEase * game) : swim;
                // Now and then, with the hands free and nothing else to do with them: rubbing them while cold, talking
                // at a party (neither while it talks anyway).
                bool idleHands = skin != null && carryClip == null && drafted <= 0f && eatClip == null && workKind == null
                    && castClip == null && strikeClip == null && cowering <= 0f && tossing <= 0f && swim <= 0f && !flying;
                bool atLeisure = AtLeisure(pawn);
                e.rubFree = recent ? Mathf.MoveTowards(e.rubFree, atLeisure ? 1f : 0f, RubEase * Time.deltaTime) : (atLeisure ? 1f : 0f);
                e.rubAt = skin != null && skin.Has("rub") ? BoutOf(pawn, e.rub, "rub", idleHands && talking <= 0f && e.cold && atLeisure, RubRestFrom, RubRestTo) : -1f;
                // Talking now and then at a party, or while relaxing socially with someone near (to whom it turns).
                e.chatAt = skin != null && skin.Has("talk") ? ChatOf(pawn, e, idleHands && talking <= 0f && e.rubAt < 0f) : -1f;
                // A quick wave in passing at whom it just chatted with, with the hands free (WaveOf).
                e.waveAt = skin != null && skin.Has("wave") && idleHands && pawn.GetPosture() == PawnPosture.Standing ? WaveOf(pawn) : -1f;
                e.frame = frame;
            }
            return e;
        }

        private static void LoadOf(Pawn pawn, out int id, out int count)
        {
            Thing load = pawn.carryTracker != null ? pawn.carryTracker.CarriedThing : null;
            id = load != null ? load.thingIDNumber : 0;
            count = load != null ? load.stackCount : 0;
        }

        // How badly the pawn limps (0 .. 1) and the clip for the leg it favours (limp_L or limp_R; null: it has not two
        // legs). With one leg gone it can't limp on it, so it hops (hop_L or hop_R, fully, and with the injury
        // animations off too, as there is no walking on a leg the figure doesn't have). Otherwise from how much worse its worse leg works than its better one, each as the game works it out with its
        // foot (a missing one 0, a prosthetic at its own efficiency, nothing counted above a natural leg's). The legs are
        // told apart by the body's own untranslated labels ("left leg"), else taken left first. Two legs hurt alike do
        // not limp: the pawn is only slower.
        private static float LimpOf(Pawn pawn, out string clip)
        {
            clip = null;
            HediffSet set = pawn.health != null ? pawn.health.hediffSet : null;
            BodyDef body = pawn.RaceProps != null ? pawn.RaceProps.body : null;
            if (set == null || body == null || pawn.Dead)
            {
                return 0f;
            }
            float left = -1f, right = -1f;
            bool leftGone = false, rightGone = false;
            List<BodyPartRecord> parts = body.AllParts;
            for (int i = 0; i < parts.Count; i++)
            {
                BodyPartRecord part = parts[i];
                if (part.def.tags == null || !part.def.tags.Contains(BodyPartTagDefOf.MovingLimbCore))
                {
                    continue;
                }
                string label = part.untranslatedCustomLabel ?? "";
                bool isLeft = label.IndexOf("left", StringComparison.OrdinalIgnoreCase) >= 0
                    || (label.IndexOf("right", StringComparison.OrdinalIgnoreCase) < 0 && left < 0f);
                if (isLeft && left < 0f)
                {
                    left = LegWorks(set, part);
                    leftGone = set.PartIsMissing(part);
                }
                else if (!isLeft && right < 0f)
                {
                    right = LegWorks(set, part);
                    rightGone = set.PartIsMissing(part);
                }
            }
            if (left < 0f || right < 0f)
            {
                return 0f;
            }
            string hop = leftGone == rightGone ? null : leftGone ? "hop_L" : "hop_R";
            if (hop != null && skin != null && skin.Has(hop))
            {
                clip = hop;
                return 1f;
            }
            if (!InjuryAnims)
            {
                return 0f;
            }
            clip = left < right ? "limp_L" : "limp_R";
            return Mathf.InverseLerp(LimpFrom, LimpFull, Mathf.Abs(left - right));
        }

        // Cuts off the figure's limbs the pawn is missing (SkinnedFigure.Cut), each at the joint the game's part starts
        // at: a neck where it leaves the shoulders, a head at the top of the neck, a shoulder or arm at the shoulder, a hand at the wrist, a leg at the hip, a foot at the ankle. The chibi's
        // fingers are one bone and its toes another, so they go only when all of them are gone (the thumb has its own).
        // A prosthetic takes the missing part's place in the game, so the limb is drawn whole. The game keeps the list of
        // missing parts (the outermost of each) cached, so this is cheap enough to do every frame.
        private static void CutLimbs(Pawn pawn, SkinnedFigure.Posed fig)
        {
            HediffSet set = pawn.health != null ? pawn.health.hediffSet : null;
            if (set == null)
            {
                return;
            }
            skin.Fit(fig, LimbsOf(set));   // and its prosthetics (Prosthetics.cs)
            List<Hediff_MissingPart> missing = set.GetMissingPartsCommonAncestors();
            int fingersL = 0, fingersR = 0, toesL = 0, toesR = 0;
            for (int i = 0; i < missing.Count; i++)
            {
                BodyPartRecord part = missing[i].Part;
                if (part == null || part.depth != BodyPartDepth.Outside)
                {
                    continue;   // a bone inside a limb (a destroyed humerus) leaves the limb's shape as it was
                }
                if (part.def.defName == "Neck")
                {
                    skin.Cut(fig, "spine.005");
                    continue;
                }
                if (part.def.defName == "Head")
                {
                    skin.Cut(fig, "spine.005");
                    continue;
                }
                string side = SideOf(part);
                if (side == null)
                {
                    continue;
                }
                string name = part.def.defName;
                if (name == "Shoulder" || name == "Arm")
                {
                    skin.Cut(fig, "upper_arm." + side);
                }
                else if (name == "Hand")
                {
                    skin.Cut(fig, "hand." + side);
                }
                else if (name == "Leg")
                {
                    skin.Cut(fig, "thigh." + side);
                }
                else if (name == "Foot")
                {
                    skin.Cut(fig, "foot." + side);
                }
                else if (name == "Finger")
                {
                    string label = part.untranslatedCustomLabel ?? "";
                    if (label.IndexOf("thumb", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        skin.Cut(fig, "thumb." + side);
                    }
                    else if (side == "L")
                    {
                        fingersL++;
                    }
                    else
                    {
                        fingersR++;
                    }
                }
                else if (name == "Toe")
                {
                    if (side == "L")
                    {
                        toesL++;
                    }
                    else
                    {
                        toesR++;
                    }
                }
            }
            if (fingersL >= HandFingers)
            {
                skin.Cut(fig, "hand.L.001");
            }
            if (fingersR >= HandFingers)
            {
                skin.Cut(fig, "hand.R.001");
            }
            if (toesL >= FootToes)
            {
                skin.Cut(fig, "toe.L");
            }
            if (toesR >= FootToes)
            {
                skin.Cut(fig, "toe.R");
            }
        }

        // The side of the body a part is on, "L" or "R" as the bones are named: the first left or right in its untranslated
        // label or that of a part it is in; null if none says.
        internal static string SideOf(BodyPartRecord part)
        {
            for (BodyPartRecord p = part; p != null; p = p.parent)
            {
                string label = p.untranslatedCustomLabel ?? "";
                if (label.IndexOf("left", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return "L";
                }
                if (label.IndexOf("right", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return "R";
                }
            }
            return null;
        }

        // How well a leg works, 0 .. 1: the worst of it and the parts of it it walks on (the foot; not the toes). A prosthetic
        // counts at its own efficiency, capped at a natural leg's: a peg or a simple leg limps, a bionic one doesn't.
        private static float LegWorks(HediffSet set, BodyPartRecord leg)
        {
            float worst = 1f;
            foreach (BodyPartRecord part in leg.GetPartAndAllChildParts())
            {
                if (part != leg && (part.def.tags == null || !part.def.tags.Contains(BodyPartTagDefOf.MovingLimbSegment)))
                {
                    continue;
                }
                if (part != leg && set.PartOrAnyAncestorHasDirectlyAddedParts(part) && !set.HasDirectlyAddedPartFor(part))
                {
                    continue;   // replaced with the leg's prosthetic, which the game marks missing: the prosthetic's own works is counted
                }
                float works = set.PartIsMissing(part) ? 0f : PawnCapacityUtility.CalculatePartEfficiency(set, part);
                worst = Mathf.Min(worst, works);
            }
            return Mathf.Clamp01(worst);
        }

        // How far the pawn hunches over its worst wound (0 .. 1; more than 0, it holds it) and the clip for it
        // (hurt_<place>_<side>; null: nothing to hold).
        // Each place is how hurt it is: the damage of its fresh wounds (scars do not count, tended ones count HurtTended
        // of theirs, a part freshly cut off all its health) over its health, added up over the parts in it. A part goes to
        // the place it is in, found up the body from it (HurtPlaceOf); the legs are left to the limp. The side is the
        // first left or right in the part's label or one it is in, else the right.
        private static float HurtOf(Pawn pawn, out string clip)
        {
            clip = null;
            HediffSet set = pawn.health != null ? pawn.health.hediffSet : null;
            if (!InjuryAnims || set == null || pawn.Dead)
            {
                return 0f;
            }
            Dictionary<string, float> hurts = null;
            List<Hediff> hediffs = set.hediffs;
            for (int i = 0; i < hediffs.Count; i++)
            {
                Hediff h = hediffs[i];
                if (h.Part == null || set.PartOrAnyAncestorHasDirectlyAddedParts(h.Part))
                {
                    // Nothing on or under a prosthetic hurts to hold: the game marks the parts a fitted one replaces (a
                    // prosthetic arm's hand and fingers) freshly missing, and every pawn with one clutched it. A worse
                    // prosthetic leg still limps (LimpOf, by how well the leg works).
                    continue;
                }
                float damage;
                Hediff_Injury injury = h as Hediff_Injury;
                Hediff_MissingPart missing = h as Hediff_MissingPart;
                if (injury != null && !injury.IsPermanent())
                {
                    damage = injury.Severity * (injury.IsTended() ? HurtTended : 1f);
                }
                else if (missing != null && missing.IsFresh)
                {
                    damage = h.Part.def.GetMaxHealth(pawn);
                }
                else
                {
                    continue;
                }
                string place = HurtPlaceOf(h.Part);
                if (place == null)
                {
                    continue;
                }
                hurts = hurts ?? new Dictionary<string, float>();
                float sum;
                hurts.TryGetValue(place, out sum);
                hurts[place] = sum + damage / Mathf.Max(h.Part.def.GetMaxHealth(pawn), HurtHealth);
            }
            if (hurts == null)
            {
                return 0f;
            }
            float worst = 0f;
            foreach (KeyValuePair<string, float> pair in hurts)
            {
                if (pair.Value > worst)
                {
                    worst = pair.Value;
                    clip = pair.Key;
                }
            }
            return worst < HurtFrom ? 0f : Mathf.Max(Mathf.InverseLerp(HurtFrom, HurtFull, worst), 1e-3f);
        }

        // How far through a jump the pawn is (0 take-off .. 1 landing; by time, as the export's jump is timed), or -1 when it
        // is not in the air.
        private static float FlightOf(Pawn pawn)
        {
            PawnFlyer flyer = pawn.ParentHolder as PawnFlyer;
            if (flyer == null)
            {
                return -1f;
            }
            int total = flightTicks(flyer);
            return total > 0 ? Mathf.Clamp01((float)flyingTicks(flyer) / total) : 0f;
        }

        // How far down in the jumper's crouch the pawn is on the ground (0 .. 1): sinking into it over the warm-up of a
        // jump (a jump pack's or a jump ability's), and after landing dropped into it fast (over the first LandDown of
        // LAND_SECONDS) and coming up slowly. Game time, so it waits while paused.
        private static float CrouchOf(Pawn pawn, Ease e)
        {
            float seconds = skin.SecondsOf("land");
            int now = Find.TickManager.TicksGame;
            if (e.landed < 1f && seconds > 0f)
            {
                e.landed = Mathf.Min(1f, e.landed + (now - e.landedTick) / 60f / seconds);
            }
            e.landedTick = now;
            float landing = 0f;
            if (e.landed < 1f)
            {
                landing = e.landed < LandDown ? Mathf.SmoothStep(0f, 1f, e.landed / LandDown)
                    : Mathf.SmoothStep(0f, 1f, (1f - e.landed) / (1f - LandDown));
            }
            float warm = 0f;
            Stance_Warmup stance = pawn.stances != null ? pawn.stances.curStance as Stance_Warmup : null;
            if (stance != null && (stance.verb is Verb_Jump || stance.verb is Verb_CastAbilityJump))
            {
                int gone = now - stance.startedTick;
                warm = gone + stance.ticksLeft > 0 ? Mathf.SmoothStep(0f, 1f, (float)gone / (gone + stance.ticksLeft)) : 1f;
            }
            return Mathf.Max(landing, warm);
        }

        // How much of the getup is shown (0 .. 1) and how far through it the pawn is (phase, 0 lying .. 1 standing): played
        // RiseHurry times faster than the export has it, as the game stands a pawn up at once, and eased into the standing
        // pose from RiseOut of the way. Game time, so it waits while paused.
        private static float RiseOf(Ease e, out float phase)
        {
            float seconds = skin.SecondsOf(skin.Variant("getup", e.risenRoll));
            int now = Find.TickManager.TicksGame;
            if (e.risen < 1f && seconds > 0f)
            {
                e.risen = Mathf.Min(1f, e.risen + (now - e.risenTick) / 60f / seconds * RiseHurry);
            }
            e.risenTick = now;
            phase = e.risen;
            return e.risen < 1f ? 1f - Mathf.SmoothStep(0f, 1f, (e.risen - RiseOut) / (1f - RiseOut)) : 0f;
        }

        // Hunkered down under fire: Combat Extended's HunkerDown job, by its defName, so CE is not referenced. CE has it stand
        // for a moment and then sets the job's posture to lying; the figure dives flat on its front as the job starts instead.
        private static bool Hunkering(Pawn pawn)
        {
            JobDef job = pawn.CurJobDef;
            return job != null && job.defName == "HunkerDown";
        }

        // Putting out a fire on itself: the game's ExtinguishSelf job only stands the pawn there until the fire is out. The
        // figure drops by the same dive as the hunker and rolls over each way on the ground (roll) instead.
        private static bool Rolling(Pawn pawn)
        {
            JobDef job = pawn.CurJobDef;
            return job == JobDefOf.ExtinguishSelf;
        }

        // How far down flat under fire the pawn is (0 up .. 1 down): through the dive to the ground (dive_), by game time, so
        // it waits while paused. Without the dive, eased over DuckDown and DuckUp.
        private static float DuckOf(Pawn pawn, Ease e, bool hunkering)
        {
            int now = Find.TickManager.TicksGame;
            bool recent = Time.frameCount - e.duckFrame <= 2;
            if (hunkering && e.duck <= 0f)
            {
                // Rolling, it goes down whichever way it faces.
                bool rolling = Rolling(pawn) && skin.Has("roll");
                e.duckFace = rolling ? (float?)null : FaceOf(pawn, SuppressorOf(pawn));
                e.duckHand = HandOf(pawn);
                e.duckLoop = rolling ? "roll" : "duck_" + e.duckHand;
                // Played from its first frame: it wasn't moved on in the frames before (DuckOf is only called while going
                // down or down), which would otherwise put it straight down.
                if (!recent)
                {
                    recent = true;
                    e.duckTick = now;
                }
            }
            float target = hunkering ? 1f : 0f;
            float seconds = skin.Has("dive_R") ? skin.SecondsOf("dive_R") : 0f;
            float rate = seconds > 0f ? (hunkering ? 1f : DropRise) / seconds : 1f / (hunkering ? DuckDown : DuckUp);
            e.duck = recent ? Mathf.MoveTowards(e.duck, target, (now - e.duckTick) / 60f * rate) : target;
            e.duckTick = now;
            e.duckFrame = Time.frameCount;
            return e.duck;
        }

        // How far down in the crouch behind cover the pawn is (0 up .. 1 as low as the cover clip goes), eased by game time, so
        // it waits while paused. The depth it wants is looked at every CoverCheck ticks (CoverWant).
        private static float CoverOf(Pawn pawn, Ease e, float size)
        {
            int now = Find.TickManager.TicksGame;
            if (now - e.coverChecked >= CoverCheck || now < e.coverChecked)
            {
                e.coverChecked = now;
                e.coverWant = CoverWant(pawn, e, size, now);
            }
            bool recent = Time.frameCount - e.coverFrame <= 2;
            float target = e.coverWant;
            e.cover = recent ? Mathf.MoveTowards(e.cover, target, (now - e.coverTick) / 60f / (target > e.cover ? CoverDown : CoverUp)) : target;
            e.coverTick = now;
            e.coverFrame = Time.frameCount;
            return e.cover;
        }

        // How far down behind cover the pawn would be. Combat Extended has every humanlike in a crouching job crouch to the
        // tallest low cover in the eight cells round it, no lower than a set height, whether or not an enemy is about; here it
        // crouches only with an awake enemy it can see within its weapon's range (seen in the last CoverHold ticks), and as
        // deep as the cover is drawn: the chibi is half again as tall beside it as Combat Extended counts a person, so its
        // share of the body would leave the chest out over sandbags. So the drafted hands go to CoverClear over the cover's
        // drawn top (IsoRim's Forms.HeightOf), or all the way down in the open; a pawn whose hands are over it already
        // (a child, or one behind an embrasure) stands.
        private static float CoverWant(Pawn pawn, Ease e, float size, int now)
        {
            if (!pawn.Spawned || pawn.Downed || !pawn.RaceProps.Humanlike || !CrouchJob(pawn.CurJobDef))
            {
                return 0f;
            }
            ThingWithComps weapon = pawn.equipment != null ? pawn.equipment.Primary : null;
            Verb verb = weapon != null && weapon.def.IsRangedWeapon && pawn.equipment.PrimaryEq != null ? pawn.equipment.PrimaryEq.PrimaryVerb : null;
            float range = verb != null && verb.verbProps != null ? verb.verbProps.range : 0f;
            if (range < 1.5f)
            {
                return 0f;   // melee or bare-handed: its own fighting stance
            }
            if (EnemyInSight(pawn, range))
            {
                e.coverSeen = now;
            }
            if (now - e.coverSeen > CoverHold || now < e.coverSeen)
            {
                return 0f;
            }
            // The stance's hands and hips, and how far the crouch lets the hips down (cells for a figure of size 1).
            string stance = e.stance != null && skin.Has(e.stance) ? e.stance : "idle";
            float hands = Mathf.Max(skin.HeadOf(stance, "hand.R").y, skin.HeadOf(stance, "hand.L").y);
            float drop = skin.CrouchDepth();
            if (drop <= 0.01f)
            {
                return 0f;
            }
            float top = CoverTop(pawn);
            if (top <= 0f)
            {
                return 1f;   // out in the open: all the way down
            }
            return Mathf.Clamp01((hands * size - (top + CoverClear)) / (drop * size));
        }

        // Whether the job is one Combat Extended crouches a pawn in (its JobDefExtensionCE's isCrouchJob, read by reflection so
        // Combat Extended is not referenced), cached per job; without it, waiting and holding fire drafted.
        private static bool CrouchJob(JobDef job)
        {
            if (job == null)
            {
                return false;
            }
            bool crouch;
            if (crouchJobs.TryGetValue(job, out crouch))
            {
                return crouch;
            }
            if (ceCrouchField == null && !ceLooked)
            {
                ceLooked = true;
                Type ext = GenTypes.GetTypeInAnyAssembly("CombatExtended.JobDefExtensionCE");
                ceCrouchField = ext != null ? ext.GetField("isCrouchJob", BindingFlags.Public | BindingFlags.Instance) : null;
            }
            if (ceCrouchField != null)
            {
                crouch = false;
                if (job.modExtensions != null)
                {
                    for (int i = 0; i < job.modExtensions.Count; i++)
                    {
                        DefModExtension x = job.modExtensions[i];
                        if (x != null && ceCrouchField.DeclaringType.IsInstanceOfType(x))
                        {
                            crouch = (bool)ceCrouchField.GetValue(x);
                        }
                    }
                }
            }
            else
            {
                crouch = job == JobDefOf.Wait_Combat || job == JobDefOf.AttackStatic;
            }
            // Cowering has its own crouch (the hunker), and the tending jobs their kneel.
            crouch &= job != JobDefOf.FleeAndCower && job.defName != "Stabilize" && job.defName != "TendSelf";
            crouchJobs[job] = crouch;
            return crouch;
        }

        private static readonly Dictionary<JobDef, bool> crouchJobs = new Dictionary<JobDef, bool>();
        private static FieldInfo ceCrouchField;
        private static bool ceLooked;

        // Whether an awake, unfogged enemy is within range cells of the pawn with a clear line to it (GenSight: walls and
        // other full cover block it, sandbags and the like don't, as with a shot). A line is a cell lookup or so per cell it
        // crosses, and it stops at the first one seen, so this is cheap at CoverCheck's pace.
        private static bool EnemyInSight(Pawn pawn, float range)
        {
            Map map = pawn.Map;
            List<IAttackTarget> targets = map.attackTargetsCache.GetPotentialTargetsFor(pawn);
            float most = range * range;
            for (int i = 0; i < targets.Count; i++)
            {
                Thing thing = targets[i].Thing;
                if (thing == null || !thing.Spawned || thing.Map != map || (thing.Position - pawn.Position).LengthHorizontalSquared > most
                    || thing.Fogged() || !GenHostility.IsPotentialThreat(targets[i]))
                {
                    continue;
                }
                if (GenSight.LineOfSightToThing(pawn.Position, thing, map, true))
                {
                    return true;
                }
            }
            return false;
        }

        // The top of the tallest low cover in the eight cells round the pawn, as IsoRim draws it (cells), as Combat Extended
        // finds the cover it crouches behind: partial fill, not a plant. 0 with none.
        private static float CoverTop(Pawn pawn)
        {
            Map map = pawn.Map;
            float top = 0f;
            for (int i = 0; i < 8; i++)
            {
                IntVec3 c = pawn.Position + GenAdj.AdjacentCells[i];
                if (!c.InBounds(map))
                {
                    continue;
                }
                Thing cover = c.GetCover(map);
                if (cover == null || cover.def.Fillage != FillCategory.Partial || cover is Plant)
                {
                    continue;
                }
                float h = Api.SurfaceHeight(cover);
                top = Mathf.Max(top, h > 0f ? h : cover.def.fillPercent);
            }
            return top;
        }

        // The hand the pawn's weapon is held in, by its hold (R with none).
        private static string HandOf(Pawn pawn)
        {
            ThingWithComps weapon = HeldWeapon(pawn);
            SkinnedFigure.Hold hold = weapon != null ? HoldOf(weapon) : null;
            return hold != null && hold.hand == "L" ? "L" : "R";
        }

        // Where the fire suppressing the pawn comes from: Combat Extended's CompSuppressable.SuppressorLoc, by reflection, so CE
        // is not referenced. Invalid without it.
        private static LocalTargetInfo SuppressorOf(Pawn pawn)
        {
            for (int i = 0; i < pawn.AllComps.Count; i++)
            {
                ThingComp comp = pawn.AllComps[i];
                if (comp.GetType().Name != "CompSuppressable")
                {
                    continue;
                }
                if (suppressorLoc == null)
                {
                    suppressorLoc = AccessTools.Property(comp.GetType(), "SuppressorLoc");
                }
                object at = suppressorLoc != null ? suppressorLoc.GetValue(comp, null) : null;
                if (at is IntVec3 && (IntVec3)at != IntVec3.Zero)
                {
                    return new LocalTargetInfo((IntVec3)at);
                }
            }
            return LocalTargetInfo.Invalid;
        }

        private static PropertyInfo suppressorLoc;

        // The lying loop for a pawn lying on its back or side: a corpse as the death fall ends it (dead), in a bed asleep
        // (sleep, downed or not) or awake (the plain lie), as a bed's blanket covers it and the downed loops' groaning and
        // curling up are for the ground; downed and awake, on its back on a couch (downed) or curled on its side on the
        // ground (downed_side); downed and unconscious (under anaesthetic, a catatonic breakdown, psychic shock), as
        // asleep (sleep: the downed loops writhe in pain); asleep (sleep), else the plain lie; the plain lie wherever the export has no such clip. A baby
        // is always downed (its life stage's alwaysDowned), so unless in pain shock it sleeps, or awake waves and kicks (baby),
        // but lies still (the plain lie) while resting: awake in a lie-down job (LayDown, LayDownAwake, LayDownResting).
        private static string LieClip(Pawn pawn, bool dead, bool inBed, bool held)
        {
            string clip = dead ? "dead" : Baby(pawn) && !pawn.health.InPainShock
                ? (!pawn.Awake() ? "sleep" : pawn.jobs?.curDriver is JobDriver_LayDown ? "lie" : "baby")
                : inBed ? (!pawn.Awake() ? "sleep" : "lie")
                : pawn.Downed ? (!pawn.Awake() ? "sleep" : held || OnBack(pawn) ? "downed" : "downed_side") : !pawn.Awake() ? "sleep" : "lie";
            if (!skin.Has(clip))
            {
                return "lie";
            }
            // Asleep, one of the sleep clip's variants (sleep~2: on the side, still), picked per sleep by the job's id so it
            // keeps one all night; on the back while someone tends it there.
            if (clip == "sleep" && !OnBack(pawn) && !held)
            {
                clip = skin.Variant(clip, Gen.HashCombineInt(pawn.thingIDNumber, pawn.CurJob != null ? pawn.CurJob.loadID : 0));
            }
            return clip;
        }

        // Whether the pawn is laid on its back to be tended on the ground (Ease.onBack): set by whoever kneels beside it to
        // tend it (WorkOf), and kept until it is on its feet, so it doesn't curl up again on its side as the doctor goes.
        private static bool OnBack(Pawn pawn)
        {
            Ease e;
            return eases.TryGetValue(pawn.thingIDNumber, out e) && e.onBack;
        }

        // How far (0 .. 1) a pawn's lying loop has turned into the one now wanted from the one before, by game time; a new
        // one wanted starts the turn again (from the one it was turning to, if more than halfway there).
        private static float LieTurnOf(Ease e, string clip)
        {
            int now = Find.TickManager.TicksGame;
            if (e.lieNow == null)
            {
                e.lieNow = clip;
                e.lieTurn = 1f;
            }
            else if (e.lieNow != clip)
            {
                e.lieWas = e.lieTurn >= 0.5f ? e.lieNow : e.lieWas;
                e.lieNow = clip;
                e.lieTurn = 0f;
            }
            else if (e.lieTurn < 1f)
            {
                e.lieTurn = Mathf.Min(1f, e.lieTurn + (now - e.lieTurnTick) / 60f / LieTurnSeconds);
            }
            e.lieTurnTick = now;
            return e.lieWas != null && e.lieWas != clip && skin.Has(e.lieWas) ? Mathf.SmoothStep(0f, 1f, e.lieTurn) : 1f;
        }

        // How much of a fall is shown (0 .. 1) and how far through it the pawn is (phase, 0 standing .. 1 down): played
        // FallHurry times faster than the export has it, as the game drops a pawn at once, and eased into the lying loop
        // from FallOut of the way. Game time, so it waits while paused.
        private static float FallOf(Ease e, out float phase)
        {
            float seconds = e.fallClip != null ? skin.SecondsOf(e.fallClip) : 0f;
            int now = Find.TickManager.TicksGame;
            if (e.fallen < 1f && seconds > 0f)
            {
                e.fallen = Mathf.Min(1f, e.fallen + (now - e.fallenTick) / 60f / seconds * FallHurry);
            }
            e.fallenTick = now;
            phase = e.fallen;
            return e.fallen < 1f && seconds > 0f ? 1f - Mathf.SmoothStep(0f, 1f, (e.fallen - FallOut) / (1f - FallOut)) : 0f;
        }

        // Draws the pawn by its ragdoll, once Add and the rest have built the canned pose (and whole lays it down): makes it
        // from the fall's first frame as a fall begins, thrown by the blow that felled the pawn; steps it on by game time,
        // held to the canned fall at first (the upper body let go of it first, then the legs, both sooner for a harder
        // blow); and puts its pose in place of the canned one, drawn where it fell (drawLoc) at its own yaw. Getting up,
        // the getup is eased out of the pose it lay in over RiseFromDoll of the getup, from where it lay.
        private static void RagdollOf(Pawn pawn, Ease ease, SkinnedFigure.Posed fig, Matrix4x4 whole, float size, bool lying, bool dead,
            float rise, float risePhase, float yaw, ref Vector3 drawLoc)
        {
            skin.Chain(fig, whole);
            if (ease.dollNew)
            {
                ease.dollNew = false;
                Blow blow;
                float speed = 0f, angle = -1f;
                if (blows.TryGetValue(pawn.thingIDNumber, out blow) && Find.TickManager.TicksGame - blow.tick <= BlowTicks)
                {
                    speed = blow.speed;
                    angle = blow.angle;
                }
                Vector3 way = BlowWay(angle, yaw);
                // Flung (Flings): at the speed it is going, and let go of the canned fall at once if that is fast, so an
                // ordinary shot still plays the fall.
                Vector3 place, flying;
                bool flung = Flings.PlaceOf(pawn, out place, out flying);
                if (flung)
                {
                    way = Quaternion.AngleAxis(-yaw, Vector3.up) * new Vector3(flying.x, 0f, flying.z);
                    speed = way.magnitude;
                }
                ease.doll = skin.NewRagdoll(fig, size, way, speed, flung ? Mathf.Max(0f, flying.y) : 0f);
                if (ease.doll == null)
                {
                    skin.Finish(fig);
                    return;
                }
                ease.doll.Yaw = yaw;
                ease.doll.Origin = drawLoc;
                ease.doll.Cell = pawn.PositionHeld;
                ease.doll.Dead = dead;
                ease.dollHit = Mathf.Clamp01(speed / (PushMost * PushBlast * FlingPower));   // how hard for the option's setting, so it lets go at the same time on any
                // A blow against the way the canned fall goes lets go of it sooner, so the body goes the way it was hit.
                float agrees = speed > 0f && ease.fallClip != null ? skin.FallAgrees(ease.fallClip, whole, way) : 1f;
                ease.dollHeld = flung && speed >= FlingLetGo ? 0f : Mathf.Lerp(1f - 0.8f * ease.dollHit, 1f, (agrees + 1f) / 2f);
                ease.dollSaved = false;
                Surroundings(pawn, ease.doll);
            }
            SkinnedFigure.Ragdoll doll = ease.doll;
            if (dead && !doll.Dead)
            {
                // Died where it lay: it goes limper, and settles again.
                doll.Dead = true;
                doll.Wake();
                ease.dollSaved = false;
            }
            float seconds = ease.dollFrame == Time.frameCount ? 0f : Time.deltaTime * Find.TickManager.TickRateMultiplier;
            ease.dollFrame = Time.frameCount;
            if (!Follow(pawn, ease, doll, seconds))
            {
                skin.Finish(fig);
                return;
            }
            if (lying)
            {
                float upper = 0f, legs = 0f;
                if (ease.fallen < 1f)
                {
                    float h = ease.dollHit, t = ease.fallen;
                    upper = (1f - Mathf.SmoothStep(0f, 1f, t / Mathf.Lerp(UpperLetGo, UpperLetGo * 0.4f, h))) * (1f - 0.7f * h) * ease.dollHeld;
                    legs = (1f - Mathf.SmoothStep(0f, 1f, t / Mathf.Lerp(LegsLetGo, LegsLetGo * 0.5f, h))) * ease.dollHeld;
                }
                // Laid on its back to be tended (onBack): the ragdoll eased out into the lying loop by game time, then
                // let go of, so it lies in the loop where it lay.
                float limp = 1f;
                if (ease.onBack && !dead)
                {
                    int now = Find.TickManager.TicksGame;
                    if (ease.dollOut > 0f)
                    {
                        ease.dollOut = Mathf.Min(1f, ease.dollOut + (now - ease.dollOutTick) / 60f / TendRollSeconds);
                    }
                    else
                    {
                        ease.dollOut = 0.0001f;
                    }
                    ease.dollOutTick = now;
                    limp = 1f - Mathf.SmoothStep(0f, 1f, ease.dollOut);
                }
                skin.Ragdolled(fig, doll, seconds, upper, legs, Matrix4x4.identity, limp);
                drawLoc.x = doll.Origin.x;
                drawLoc.z = doll.Origin.z;
                if (ease.onBack && !dead && ease.dollOut >= 1f)
                {
                    DropDoll(pawn, ease);
                    ease.dollOut = 0f;
                    ease.fellYaw = doll.Yaw;
                    skin.Finish(fig);
                    return;
                }
                if (doll.Settled && !ease.dollSaved)
                {
                    RagdollRests.Keep(pawn, doll);
                    ease.dollSaved = true;
                }
            }
            else
            {
                // From the ragdoll's own space (where it lay, at its yaw) to the figure's as it is drawn now.
                float w = rise > 0f ? 1f - Mathf.SmoothStep(0f, 1f, risePhase / RiseFromDoll) : 0f;
                Matrix4x4 frame = Matrix4x4.Scale(Vector3.one / size) * Matrix4x4.Rotate(Quaternion.AngleAxis(-yaw, Vector3.up))
                    * Matrix4x4.Translate(new Vector3(doll.Origin.x - drawLoc.x, 0f, doll.Origin.z - drawLoc.z))
                    * Matrix4x4.Rotate(Quaternion.AngleAxis(doll.Yaw, Vector3.up)) * Matrix4x4.Scale(Vector3.one * size);
                skin.Ragdolled(fig, doll, seconds, 0f, 0f, frame, w);
            }
            skin.Finish(fig);
        }

        // Where a ragdoll's hips are held to: a body in flight's place, and then the middle of the cell it landed in, come
        // back to slowly. False if it has been left behind (flung while it was off screen, so not stepped): it is dropped.
        private static bool Follow(Pawn pawn, Ease ease, SkinnedFigure.Ragdoll doll, float seconds)
        {
            Vector3 held = doll.cellMiddle;
            if (ease.dollMoved)
            {
                ease.dollMoved = false;
                Surroundings(pawn, doll);
            }
            Quaternion back = Quaternion.AngleAxis(-doll.Yaw, Vector3.up);
            Vector3 place, flying;
            doll.Following = Flings.PlaceOf(pawn, out place, out flying);
            if (doll.Following)
            {
                doll.cellMiddle = back * new Vector3(place.x - doll.Origin.x, 0f, place.z - doll.Origin.z);
                return true;
            }
            Vector3 middle = pawn.PositionHeld.ToVector3Shifted();
            Vector3 home = back * new Vector3(middle.x - doll.Origin.x, 0f, middle.z - doll.Origin.z);
            if (home == held && home == doll.cellMiddle)
            {
                return true;
            }
            if ((doll.HipsAt - home).magnitude > FlingLost)
            {
                DropDoll(pawn, ease);
                ease.fellYaw = null;
                return false;
            }
            doll.cellMiddle = Vector3.MoveTowards(held, home, FlingSettle * seconds);
            return true;
        }

        // Done with a pawn's ragdoll: it is no longer kept with the map either.
        private static void DropDoll(Pawn pawn, Ease ease)
        {
            if (ease.doll != null && ease.dollSaved)
            {
                RagdollRests.Forget(pawn);
            }
            ease.doll = null;
            ease.dollSaved = false;
        }

        // The pawn's ragdoll as it was left at rest, if the map kept one for it in the cell it lies in.
        private static SkinnedFigure.Ragdoll SavedDoll(Pawn pawn, SkinnedFigure.Shape shape, float size)
        {
            RagdollRests.Rest rest = RagdollRests.Find(pawn);
            if (rest == null || rest.cell != pawn.PositionHeld || rest.joints == null)
            {
                return null;
            }
            SkinnedFigure.Ragdoll doll = skin.RestedRagdoll(shape, size, rest.joints.ToArray());
            if (doll == null)
            {
                return null;
            }
            Vector3 middle = rest.cell.ToVector3Shifted();
            doll.Yaw = rest.yaw;
            doll.Origin = new Vector3(middle.x + rest.offset.x, 0f, middle.z + rest.offset.z);
            doll.Cell = rest.cell;
            doll.Dead = rest.dead;
            Surroundings(pawn, doll);
            return doll;
        }

        // What a ragdoll lies among: the middle of its cell (which its hips are held near) and the walls and furniture in
        // the cells round it out to BlockReach (the map's edge a wall). Not its own cell's: it lies there already.
        private static void Surroundings(Pawn pawn, SkinnedFigure.Ragdoll doll)
        {
            doll.blocks.Clear();
            Map map = pawn.MapHeld;
            IntVec3 cell = pawn.PositionHeld;
            Vector3 middle = cell.ToVector3Shifted();
            doll.cellMiddle = Quaternion.AngleAxis(-doll.Yaw, Vector3.up) * new Vector3(middle.x - doll.Origin.x, 0f, middle.z - doll.Origin.z);
            if (map == null)
            {
                return;
            }
            for (int dx = -BlockReach; dx <= BlockReach; dx++)
            {
                for (int dz = -BlockReach; dz <= BlockReach; dz++)
                {
                    if (dx == 0 && dz == 0)
                    {
                        continue;
                    }
                    IntVec3 c = new IntVec3(cell.x + dx, 0, cell.z + dz);
                    float top = 0f;
                    if (!c.InBounds(map))
                    {
                        top = 10f;
                    }
                    else
                    {
                        List<Thing> things = c.GetThingList(map);
                        for (int i = 0; i < things.Count; i++)
                        {
                            Building b = things[i] as Building;
                            if (b == null || b is Building_Door)
                            {
                                continue;
                            }
                            if (b.def.passability == Traversability.Impassable)
                            {
                                top = 10f;
                            }
                            else if (b.def.fillPercent >= BlockFill)
                            {
                                top = Mathf.Max(top, Api.SurfaceHeight(b));
                            }
                        }
                    }
                    if (top > 0.02f)
                    {
                        Vector2 min = new Vector2(c.x - doll.Origin.x, c.z - doll.Origin.z);
                        doll.blocks.Add(new SkinnedFigure.Block { min = min, max = min + Vector2.one, top = top });
                    }
                }
            }
        }

        // How far the fall's first frame, laid down as the game lays it, faces round from the standing idle's (degrees, as
        // yaw turns): the export turns each fall so it ends lying as the loop it falls into does, which can leave it
        // starting turned aside (the knockdown falls onto its side). Cached per clip.
        private static readonly Dictionary<string, float> fallFacings = new Dictionary<string, float>();

        private static float FallFacing(string clip)
        {
            float facing;
            if (!fallFacings.TryGetValue(clip, out facing))
            {
                facing = skin.FacingFrom(clip, Quaternion.AngleAxis(-90f, Vector3.right), "idle");
                fallFacings[clip] = facing;
            }
            return facing;
        }

        // Fleeing and cowering from something (the game's FleeAndCower job, or its short form): hands raised.
        private static bool Cowering(Pawn pawn)
        {
            JobDef job = pawn.CurJobDef;
            return job != null && (job.defName == "FleeAndCower" || job.defName == "FleeAndCowerShort");
        }

        // The eating clip for a pawn eating (the Ingest job, standing or sat still with what it eats): eat_sit when the game
        // sets it on a table beside the pawn (as JobDriver_Ingest.ModifyCarriedThingDrawPos does), the hand going from the
        // table to the mouth, else eat_stand, held in the left hand; null when it is not eating, or the export has no such
        // clip. Either is laid over the sit or the idle, whichever the pawn is in, as one of its variants.
        private static string EatOf(Pawn pawn)
        {
            Job job = pawn.CurJob;
            Thing food = pawn.carryTracker != null ? pawn.carryTracker.CarriedThing : null;
            if (job == null || job.def != JobDefOf.Ingest || food == null || !food.IngestibleNow || pawn.Map == null
                || (pawn.pather != null && pawn.pather.Moving))
            {
                return null;
            }
            // A drug smoked, or a pill or powder taken, has a clip of its own (DrugClipOf); a pill only over the chew it
            // is timed by (PillOf), as before it the pawn only has it in hand.
            string drug = DrugClipOf(food.def);
            if (drug != null && skin.Has(drug) && (drug != "pill" || Chewing(pawn)))
            {
                return drug;
            }
            IntVec3 c = job.GetTarget(TargetIndex.B).Cell;
            bool table = c.IsValid && c.AdjacentToCardinal(pawn.Position) && c.HasEatSurface(pawn.Map)
                && food.def.ingestible != null && food.def.ingestible.ingestHoldUsesTable;
            string clip = table ? "eat_sit" : "eat_stand";
            if (!skin.Has(clip))
            {
                return null;
            }
            // Any other drug (beer at a table, psychite tea) is drunk, never eaten with a knife and fork: it
            // takes the clip itself, the drink both eat clips were made from, and no variant.
            if (food.def.IsDrug)
            {
                return clip;
            }
            // A variant per meal (eat_sit~2: knife and fork), by the job, so it keeps the one it sat down with.
            return skin.Variant(clip, Gen.HashCombineInt(pawn.thingIDNumber, job.loadID));
        }

        // How a drug is taken, by the sound the game makes as it is (its ingestible's ingestSound, which mods' drugs take
        // up from the game's bases too), else the verb of its ingest command: "smoke" for a joint or flake smoked
        // (Ingest_Smoke), "pill" for a pill taken (Ingest_Pill) or a powder snorted (Ingest_Snort, yayo), as one; null
        // for anything else (drunk, injected, eaten), which takes the eating clips. Cached per ThingDef.
        private static string DrugClipOf(ThingDef def)
        {
            string clip;
            if (drugClips.TryGetValue(def, out clip))
            {
                return clip;
            }
            IngestibleProperties ing = def.ingestible;
            string sound = ing != null && ing.ingestSound != null ? ing.ingestSound.defName : "";
            string verb = ing != null && ing.ingestCommandString != null ? ing.ingestCommandString : "";
            if (sound == "Ingest_Smoke" || verb.StartsWith("Smoke"))
            {
                clip = "smoke";
            }
            else if (sound == "Ingest_Pill" || sound == "Ingest_Snort" || verb.StartsWith("Snort") || verb.StartsWith("Take"))
            {
                clip = "pill";
            }
            drugClips[def] = clip;
            return clip;
        }

        private static readonly Dictionary<ThingDef, string> drugClips = new Dictionary<ThingDef, string>();

        // Whether the pawn's job is in its chew (Toils_Ingest.ChewIngestible, the toil the game times eating by).
        private static bool Chewing(Pawn pawn)
        {
            return pawn.jobs != null && pawn.jobs.curDriver != null && pawn.jobs.curDriver.CurToilString == "ChewIngestible";
        }

        // Taking a pill: e.pillAt how far through the chew it is, by its ticks left against the most of them seen (the
        // chew sets them all at once as it begins, by the pill's ingest ticks and the pawn's eating speed), so the clip is
        // played once through over it whatever its length. Kept once the chew is over, so it eases out from its end.
        private static void PillOf(Pawn pawn, Ease e)
        {
            int left = Chewing(pawn) ? pawn.jobs.curDriver.ticksLeftThisToil : 0;
            if (left <= 0)
            {
                e.pillTicks = 0;
                return;
            }
            if (left > e.pillTicks)
            {
                e.pillTicks = left;   // begun
            }
            e.pillAt = Mathf.Clamp(1f - (float)left / e.pillTicks, 0f, PillLast);
        }

        private const float PillLast = 0.999f;   // the pill clip's phase no further on than this, as 1 wraps round to its start

        // The reading clip for a pawn reading (the game's Reading job, or a mod's on the same driver, once the book is open:
        // Book.IsOpen, set for the reading toil only), standing or sat still with it in hand; null when it is not reading,
        // or the export has no read clip. Laid over the sit or the idle as eating is (Ease.eat, eatClip "read"), and the
        // open book is drawn on the left hand (DrawBook).
        private static string ReadOf(Pawn pawn)
        {
            Book book = pawn.carryTracker != null ? pawn.carryTracker.CarriedThing as Book : null;
            if (book == null || !book.IsOpen || !(pawn.jobs != null && pawn.jobs.curDriver is JobDriver_Reading)
                || (pawn.pather != null && pawn.pather.Moving) || !skin.Has("read"))
            {
                return null;
            }
            return "read";
        }

        // Which side of a reader someone sits watching its book: Romance on the Rim's shared reading (RotR_Job_ShareReading,
        // by name, in its RotR_SR_Accompany toil, sat beside the reader, its target B), L or R as the reader faces; null
        // for none. The reader then holds the book out to them (read_show_L, read_show_R).
        private static string ShowSideOf(Pawn pawn)
        {
            Pawn other = WatcherOf(pawn);
            if (other == null)
            {
                return null;
            }
            Vector3 local = Quaternion.Inverse(Quaternion.AngleAxis(pawn.Rotation.AsAngle, Vector3.up)) * (other.DrawPos - pawn.DrawPos);
            return Mathf.Abs(local.x) < 0.1f ? null : local.x > 0f ? "R" : "L";
        }

        // Whoever sits beside the pawn watching its book (Romance on the Rim's shared reading, in its RotR_SR_Accompany toil,
        // the pawn its target B), or null.
        private static Pawn WatcherOf(Pawn pawn)
        {
            if (pawn.Map == null || !(pawn.jobs != null && pawn.jobs.curDriver is JobDriver_Reading))
            {
                return null;
            }
            foreach (Pawn other in pawn.Map.mapPawns.FreeColonistsSpawned)
            {
                if (Watches(other) == pawn)
                {
                    return other;
                }
            }
            return null;
        }

        // The reader the pawn sits beside watching (the other side of WatcherOf), or null.
        private static Pawn Watches(Pawn pawn)
        {
            Job job = pawn.CurJob;
            if (job == null || job.def.defName != "RotR_Job_ShareReading" || pawn.jobs.curDriver == null
                || pawn.jobs.curDriver.CurToilString != "RotR_SR_Accompany" || (pawn.pather != null && pawn.pather.Moving))
            {
                return null;
            }
            return job.targetB.Thing as Pawn;
        }

        // The one a pawn reads with at a shared reading, either way round, or null: the two are drawn closer on their seats
        // and turned toward each other (Ease.share, ShareCloser, ShareTurn).
        private static Pawn SharePartnerOf(Pawn pawn)
        {
            return Watches(pawn) ?? WatcherOf(pawn);
        }

        // The performer a pawn is listening to at Romance on the Rim's musical performance (RomanceOnTheRim_EnjoyPerformance,
        // by name, in its listening toil, beside the instrument: the performer target B), or null.
        private static Pawn ListeningTo(Pawn pawn)
        {
            Job job = pawn.CurJob;
            if (job == null || job.def.defName != "RomanceOnTheRim_EnjoyPerformance" || (pawn.pather != null && pawn.pather.Moving)
                || pawn.jobs.curDriver == null || pawn.jobs.curDriver.CurToilString != "RomanceOnTheRim_ListenToInstrument")
            {
                return null;
            }
            return job.targetB.Thing as Pawn;
        }

        // The teaching clip for a pawn giving a lesson at a school desk (Biotech's Lessongiving, once at its spot and the
        // pupil there: isReadyToTeach), standing or sat; null otherwise, or the export has no teach clip. Laid over the sit
        // or the idle as eating is (Ease.eat, eatClip "teach"); the head turns to the pupil (LessonLookOf).
        private static string TeachOf(Pawn pawn)
        {
            JobDriver_Lessongiving lesson = pawn.jobs != null ? pawn.jobs.curDriver as JobDriver_Lessongiving : null;
            if (lesson == null || !lesson.isReadyToTeach || (pawn.pather != null && pawn.pather.Moving) || !skin.Has("teach"))
            {
                return null;
            }
            return "teach";
        }

        // Romance on the Rim's kiss (RomanceOnTheRim_InitiateKiss and _ReceiveKiss, by name so the mod is not referenced: the
        // two a cell apart, east and west, facing each other, the partner target A): kiss, laid over the idle from the waist
        // up through eating's slot (Ease.eat, eatClip "kiss"), both drawn toward each other (Ease.kissWay, KissGap).
        private static string KissOf(Pawn pawn)
        {
            Job job = pawn.CurJob;
            if (job == null || !skin.Has("kiss") || (pawn.pather != null && pawn.pather.Moving) || !(job.targetA.Thing is Pawn))
            {
                return null;
            }
            string name = job.def.defName;
            return name == "RomanceOnTheRim_InitiateKiss" || name == "RomanceOnTheRim_ReceiveKiss" ? "kiss" : null;
        }

        // Degrees the head turns about the vertical from the figure's yaw at a lesson: the teacher TeachLook of the way
        // round toward the pupil beside it at the desk (easing off as it looks down to pat the desk, TeachGlance), the
        // pupil PupilLook of the way toward the teacher; 0 for anyone else. Both face the desk, the game's own facing.
        private static float LessonLookOf(Pawn pawn, Ease ease, float yaw)
        {
            JobDriver driver = pawn.jobs != null ? pawn.jobs.curDriver : null;
            float share;
            if (driver is JobDriver_Lessongiving && ease.eatClip == "teach" && ease.eat > 0f)
            {
                float off = Mathf.Abs(Mathf.Repeat(LoopPhase(pawn, "teach") - TeachGlance + 0.5f, 1f) - 0.5f);
                float glance = off < TeachGlanceWidth ? 0.5f + 0.5f * Mathf.Cos(Mathf.PI * off / TeachGlanceWidth) : 0f;
                share = TeachLook * ease.eat * (1f - glance * (1f - TeachGlanceKeep));
            }
            else if (driver is JobDriver_Lessontaking taking && taking.isReadyToLearn)
            {
                share = PupilLook;
            }
            else if (ListeningTo(pawn) != null)
            {
                share = ListenLook;   // (target B: the performer)
            }
            else if (driver != null && (driver.CurToilString == "RotR_SR_Accompany" || driver.CurToilString == "RotR_SR_Stare")
                && pawn.CurJob.targetB.Thing is Pawn reader)
            {
                // Romance on the Rim's shared reading: sat beside the reader, looking over at the book in front of them.
                Vector3 book = reader.DrawPos + reader.Rotation.FacingCell.ToVector3() * ShareBook - pawn.DrawPos;
                return book.x * book.x + book.z * book.z < 0.0001f ? 0f
                    : Mathf.Clamp(Mathf.DeltaAngle(yaw, book.AngleFlat()) * ShareLook, -LookMost, LookMost);
            }
            else
            {
                // Just after a social interaction (a chat in passing, an insult): a glance at whom it was with, wherever
                // they have got to.
                Pawn with = GlanceOf(pawn);
                if (with == null)
                {
                    return 0f;
                }
                float? toward = FaceOf(pawn, with);
                return toward.HasValue ? Mathf.Clamp(Mathf.DeltaAngle(yaw, toward.Value) * GlanceLook, -LookMost, LookMost) : 0f;
            }
            float? face = FaceOf(pawn, pawn.CurJob.targetB);
            return face.HasValue ? Mathf.Clamp(Mathf.DeltaAngle(yaw, face.Value) * share, -LookMost, LookMost) : 0f;
        }

        // The weapon the pawn is reloading: Combat Extended's ReloadWeapon job (found by name, so CE need not be loaded),
        // its weapon (target B) the equipped one or one in the inventory; null when it is not reloading.
        private static ThingWithComps Reloaded(Pawn pawn)
        {
            Job job = pawn.CurJob;
            ThingWithComps weapon = job != null && job.def.defName == "ReloadWeapon" ? job.targetB.Thing as ThingWithComps : null;
            if (weapon == null)
            {
                return null;
            }
            bool equipped = pawn.equipment != null && pawn.equipment.Primary == weapon;
            return equipped || pawn.inventory != null && pawn.inventory.innerContainer.Contains(weapon) ? weapon : null;
        }

        private static bool Reloading(Pawn pawn)
        {
            return Reloaded(pawn) != null;
        }

        // The weapon in the pawn's hands when it shows one: the one it reloads (out of the inventory too, the equipped one
        // holstered meanwhile), else the equipped one; null: none.
        private static ThingWithComps HeldWeapon(Pawn pawn)
        {
            return Reloaded(pawn) ?? (pawn.equipment != null ? pawn.equipment.Primary : null);
        }

        // 1 while the pawn waits out a reload (Reloading), with e.reloadAt how far through it is; else 0. CE's reload is one
        // timed wait (a toil of the weapon's reload time over the pawn's reload speed), done again for each round of a
        // weapon loaded a round at a time, so each is played through once. Its length is not given, so it is the most
        // ticks left seen since it started (a reload under way when the game was loaded starts the clip there).
        private static float ReloadOf(Pawn pawn, Ease e)
        {
            int left = Reloading(pawn) ? pawn.jobs.curDriver.ticksLeftThisToil : 0;
            if (left <= 0)
            {
                e.reloadTicks = 0;
                return 0f;
            }
            if (left > e.reloadTicks)
            {
                e.reloadTicks = left;   // started, or the next round
            }
            e.reloadAt = Mathf.Clamp01(1f - (float)left / e.reloadTicks);
            return 1f;
        }

        // Staggered with no hit behind it (a hit's stagger shows as its flinch, StaggeredByHit): 1, with e.reelAt how far
        // through the stumble to one knee it is, by the stagger's ticks against the most of them left seen (the game's
        // stagger is 95 ticks); else 0, e.reelAt kept so it eases out from where it got to (standing, at its end). A
        // stagger made longer part way (the game keeps the longer of the two) plays the rest over the new time from where
        // it is, held no further on than down on the knee, so it neither jumps back up nor gets up sooner.
        private static float ReelOf(Pawn pawn, Ease e, SkinnedFigure skin)
        {
            int left = pawn.stances != null && pawn.stances.stagger != null && !StaggeredByHit(pawn) ? pawn.stances.stagger.StaggerTicksLeft : 0;
            if (left <= 0)
            {
                e.reelTicks = 0;
                return 0f;
            }
            if (e.reelTicks <= 0)
            {
                e.reelTicks = left;   // begun
                e.reelClip = skin.Variant("reel", Gen.HashCombineInt(pawn.thingIDNumber, Find.TickManager.TicksGame));
            }
            else if (left > Mathf.RoundToInt(e.reelTicks * (1f - e.reelAt)) + 1)
            {
                e.reelTicks = Mathf.RoundToInt(left / (1f - Mathf.Min(e.reelAt, ReelHold)));   // made longer
            }
            e.reelAt = Mathf.Clamp01(1f - (float)left / e.reelTicks);
            return 1f;
        }

        // Sad: a sad wander, or a visitor giving up (the game's Wander_Sad, Wander_OwnRoom, Wander_Psychotic and its short
        // form, GiveUpExit), slumped as it goes.
        private static bool Sad(Pawn pawn)
        {
            MentalStateDef state = pawn.MentalStateDef;
            if (state == null)
            {
                return false;
            }
            string name = state.defName;
            return name == "Wander_Sad" || name == "Wander_OwnRoom" || name == "Wander_Psychotic" || name == "Wander_Psychotic_Short"
                || name == "GiveUpExit";
        }

        private static HediffDef alcoholHigh;

        // How drunk a pawn is, 0 .. 1: its AlcoholHigh (beer, ambrosia's isn't) from DrunkFrom (tipsy) to DrunkFull
        // (hammered).
        private static float DrunkOf(Pawn pawn)
        {
            if (alcoholHigh == null)
            {
                alcoholHigh = DefDatabase<HediffDef>.GetNamedSilentFail("AlcoholHigh");
            }
            Hediff high = alcoholHigh != null && pawn.health != null ? pawn.health.hediffSet.GetFirstHediffOfDef(alcoholHigh) : null;
            return high != null ? Mathf.InverseLerp(DrunkFrom, DrunkFull, high.Severity) : 0f;
        }

        // The stirring clip for a pawn cooking at the work kind it does (work_stir at a bench, work_stir_crouch knelt at the
        // ground; sat at a campfire, stir_campfire, it holds a roasting stick out the whole time, so none), or null: a bill whose recipe is cooked (its work skill Cooking: meals, brewing wort), not butchering.
        private static string StirOf(Pawn pawn, string kind)
        {
            Job job = pawn.CurJob;
            if (job == null || job.def != JobDefOf.DoBill || job.bill == null || job.bill.recipe == null)
            {
                return null;
            }
            RecipeDef recipe = job.bill.recipe;
            if (recipe.workSkill != SkillDefOf.Cooking
                || (recipe.specialProducts != null && recipe.specialProducts.Contains(SpecialProductType.Butchery))
                || (job.targetA.Thing != null && job.targetA.Thing.def.defName.Contains("Butcher")))
            {
                return null;
            }
            string clip = kind == "bench" ? "work_stir" : kind == "crouch" ? "work_stir_crouch" : null;
            return clip != null && skin.Has(clip) ? clip : null;
        }

        // On fire on its feet and not rolling it out (ExtinguishSelf has its own roll, Rolling): patting at the flames.
        private static bool Burning(Pawn pawn)
        {
            return pawn.IsBurning() && !pawn.Downed && !Rolling(pawn) && pawn.GetPosture() == PawnPosture.Standing;
        }

        // The last social interaction each pawn (by thingIDNumber) was in, in game ticks: whom with, and whether it waves
        // (it began a friendly one: a chat, a deep talk, kind words). Recorded as the game has it (InteractedPostfix):
        // interactions happen at once, in passing, whatever the pawns are doing, so all there is to show is a glance at
        // the other (GlanceOf) and, with the hands free, a quick wave (WaveOf).
        private struct Interaction
        {
            public int tick;
            public Pawn with;
            public bool wave;
        }
        private static readonly Dictionary<int, Interaction> interactions = new Dictionary<int, Interaction>();

        private static void InteractedPostfix(Pawn ___pawn, Pawn recipient, InteractionDef intDef, bool __result)
        {
            if (!__result || skin == null || ___pawn == null || recipient == null || intDef == null
                || !___pawn.RaceProps.Humanlike || !recipient.RaceProps.Humanlike)
            {
                return;
            }
            if (interactions.Count > 512)
            {
                interactions.Clear();
            }
            int now = Find.TickManager.TicksGame;
            string name = intDef.defName;
            bool friendly = name == "Chitchat" || name == "DeepTalk" || name == "KindWords";
            interactions[___pawn.thingIDNumber] = new Interaction { tick = now, with = recipient, wave = friendly };
            interactions[recipient.thingIDNumber] = new Interaction { tick = now, with = ___pawn, wave = false };
        }

        // Whom the pawn had a social interaction with within GlanceTicks, if they are still about; else null.
        private static Pawn GlanceOf(Pawn pawn)
        {
            Interaction i;
            if (!interactions.TryGetValue(pawn.thingIDNumber, out i))
            {
                return null;
            }
            int since = Find.TickManager.TicksGame - i.tick;
            return since >= 0 && since <= GlanceTicks && i.with != null && i.with.Spawned && i.with.Map == pawn.Map ? i.with : null;
        }

        // How far through a wave at whom it began a friendly interaction with it is (0 .. 1, once through on game time), or -1.
        private static float WaveOf(Pawn pawn)
        {
            Interaction i;
            if (!interactions.TryGetValue(pawn.thingIDNumber, out i) || !i.wave)
            {
                return -1f;
            }
            float t = (Find.TickManager.TicksGame - i.tick) / (skin.SecondsOf("wave") * GenTicks.TicksPerRealSecond);
            return t >= 0f && t < 1f ? t : -1f;
        }

        // Leading an animal (or several) on a rope: the game's rope tracker has someone tied to this pawn.
        private static bool Leading(Pawn pawn)
        {
            return pawn.roping != null && pawn.roping.IsRopingOthers;
        }

        // Talking the whole time: playing a board game (chess, poker, the game of Ur) with someone else at the same board,
        // giving a speech (a throne room's, or a ritual's speaker), or visiting someone sick in bed (VisitSickPawn, stood or
        // sat by them; the game turns the visitor to face them), once there and not on its way.
        private static bool TalkOf(Pawn pawn)
        {
            JobDef job = pawn.CurJobDef;
            if (job == null || (pawn.pather != null && pawn.pather.Moving))
            {
                return false;
            }
            string name = job.defName;
            if (name == "Play_Chess" || name == "Play_Poker" || name == "Play_GameOfUr")
            {
                return PlaysWithOthers(pawn, job);
            }
            // (a warden at a prisoner or slave: the game stands it there talking for a while, once there)
            return name == "GiveSpeech" || name == "SpeakOnCellNoSpeechBubbles" || name == "VisitSickPawn"
                || name == "PrisonerAttemptRecruit" || name == "PrisonerConvert" || name == "PrisonerEnslave" || name == "PrisonerReduceWill"
                || name == "SlaveSuppress" || name == "PrisonerInterrogateIdentity" || name == "TalkCreepJoiner";
        }

        // Pitching horseshoes or hoopstone (the game's Play_Horseshoes and Play_Hoopstone: a JobDriver_WatchBuilding that
        // throws a horseshoe or stone fleck at the pin, target A, every TossInterval ticks while it stands at its spot),
        // once at that spot (target B) and not lying in a bed, where the game throws from too but the figure lies down.
        // Romance on the Rim's snowball fight (RotR_Job_SnowballFight, by name so the mod is not referenced) throws the same
        // way: a snowball fleck at the partner, target B, every SnowInterval ticks (also IsHashIntervalTick) while it is in
        // its throwing toil, standing at its spot; the snowball clip. TossClipOf: which clip, or null when neither.
        private static string TossClipOf(Pawn pawn)
        {
            JobDef job = pawn.CurJobDef;
            if (job == null || pawn.pather != null && pawn.pather.Moving || pawn.GetPosture() != PawnPosture.Standing)
            {
                return null;
            }
            if (job.defName == "RotR_Job_SnowballFight")
            {
                return pawn.jobs.curDriver != null && pawn.jobs.curDriver.CurToilString == "RotR_ThrowSnowball" ? "snowball" : null;
            }
            if (job.defName != "Play_Horseshoes" && job.defName != "Play_Hoopstone")
            {
                return null;
            }
            LocalTargetInfo spot = pawn.CurJob.targetB;
            return !spot.IsValid || pawn.Position == spot.Cell ? "toss" : null;
        }

        // How far through the toss clip a pawn pitching is, 0 .. 1, or -1 between throws: timed so the thing leaves the hand
        // (TossRelease) on the tick the game throws its fleck, which is when (TicksGame + thingIDNumber.HashOffset()) is a
        // whole number of TossIntervals (Thing.IsHashIntervalTick), so the wind-up starts before it and the follow-through
        // goes on after.
        private static float TossPhase(Pawn pawn, string clip)
        {
            float ticks = skin.SecondsOf(clip) * 60f;
            if (ticks <= 0f)
            {
                return -1f;
            }
            bool snow = clip == "snowball";
            int interval = snow ? SnowInterval : TossInterval;
            float release = snow ? SnowRelease : TossRelease;
            int since = (Find.TickManager.TicksGame + pawn.thingIDNumber.HashOffset()) % interval;
            if (since < 0)
            {
                since += interval;
            }
            int until = since == 0 ? 0 : interval - since;
            if (until <= release * ticks)
            {
                return release - until / ticks;
            }
            if (since <= (1f - release) * ticks)
            {
                return release + since / ticks;
            }
            return -1f;
        }

        // The toss's weight by how far through it is: eased in from the idle over its first TossIn and out again from
        // TossOut, as its ends are the idle's first frame and the idle has swayed on meanwhile.
        private static float TossWeight(float at)
        {
            if (at < 0f)
            {
                return 0f;
            }
            if (at < TossIn)
            {
                return Mathf.SmoothStep(0f, 1f, at / TossIn);
            }
            return at <= TossOut ? 1f : Mathf.SmoothStep(1f, 0f, (at - TossOut) / (1f - TossOut));
        }

        // Whether someone else is playing the same game at the same board (its job's target A) as the pawn: a pawn
        // playing alone sits quiet. Looked for round the board, within PlayerReach cells, where its seats are.
        private static bool PlaysWithOthers(Pawn pawn, JobDef job)
        {
            Thing board = pawn.CurJob.targetA.Thing;
            Map map = pawn.Map;
            if (board == null || map == null)
            {
                return false;
            }
            foreach (IntVec3 c in GenRadial.RadialCellsAround(board.Position, PlayerReach, true))
            {
                if (!c.InBounds(map))
                {
                    continue;
                }
                List<Thing> things = map.thingGrid.ThingsListAtFast(c);
                for (int i = 0; i < things.Count; i++)
                {
                    Pawn other = things[i] as Pawn;
                    if (other != null && other != pawn && other.CurJobDef == job && other.CurJob.targetA.Thing == board)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // At a party (the game's joinable party, a wedding's or birthday's too): talking now and then.
        private static bool Partying(Pawn pawn)
        {
            Lord lord = pawn.GetLord();
            return lord != null && lord.LordJob is LordJob_Joinable_Party;
        }

        // Relaxing socially (the game's SocialRelax job: at a table or a gather spot), once there and not on its way.
        private static bool RelaxingSocially(Pawn pawn)
        {
            JobDef job = pawn.CurJobDef;
            return job != null && job.defName == "SocialRelax" && (pawn.pather == null || !pawn.pather.MovingNow);
        }

        // How far through a bout of talking the pawn is (BoutOf), or -1 between bouts: now and then at a party, or while
        // relaxing socially with someone to talk to within ChatReach (looked for every ChatLook ticks while a bout is due,
        // and picked as it begins). The bout faces whom it is to (chatFace), following them while they are there.
        private static float ChatOf(Pawn pawn, Ease e, bool free)
        {
            int now = Find.TickManager.TicksGame;
            bool party = Partying(pawn);
            Pawn with = null;
            bool due = e.chat.start < 0 && e.chat.next >= 0 && now >= e.chat.next;
            if (free && due && (party || RelaxingSocially(pawn)) && (now - e.chatLooked >= ChatLook || now < e.chatLooked))
            {
                e.chatLooked = now;
                with = ListenerOf(pawn);
            }
            bool wanted = free && (party || with != null);
            int started = e.chat.start;
            float at = BoutOf(pawn, e.chat, "talk", wanted, ChatRestFrom, ChatRestTo);
            if (at >= 0f && e.chat.start != started)
            {
                e.chatWith = with;   // a new bout
                e.chatFace = null;
            }
            if (at < 0f)
            {
                e.chatWith = null;
                e.chatFace = null;
            }
            else if (e.chatWith != null && e.chatWith.Spawned && e.chatWith.Map == pawn.Map)
            {
                e.chatFace = FaceOf(pawn, e.chatWith) ?? e.chatFace;
            }
            return at;
        }

        // The nearest other awake person within ChatReach cells of the pawn that it can see, or null.
        private static Pawn ListenerOf(Pawn pawn)
        {
            Map map = pawn.Map;
            if (map == null)
            {
                return null;
            }
            Pawn best = null;
            float bestSq = ChatReach * ChatReach;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(pawn.Position, ChatReach, true))
            {
                if (!c.InBounds(map))
                {
                    continue;
                }
                List<Thing> things = map.thingGrid.ThingsListAtFast(c);
                for (int i = 0; i < things.Count; i++)
                {
                    Pawn other = things[i] as Pawn;
                    if (other == null || other == pawn || other.RaceProps == null || !other.RaceProps.Humanlike || other.Dead
                        || other.Downed || !other.Awake())
                    {
                        continue;
                    }
                    float sq = (other.Position - pawn.Position).LengthHorizontalSquared;
                    if (sq < bestSq && GenSight.LineOfSight(pawn.Position, other.Position, map))
                    {
                        best = other;
                        bestSq = sq;
                    }
                }
            }
            return best;
        }

        // Cold: hypothermic, or where it is colder than the pawn (in what it wears) is comfortable in.
        private static bool ColdOf(Pawn pawn)
        {
            if (pawn.Dead || pawn.health == null || pawn.RaceProps == null || !pawn.RaceProps.Humanlike)
            {
                return false;
            }
            if (pawn.health.hediffSet.HasHediff(HediffDefOf.Hypothermia))
            {
                return true;
            }
            return pawn.Spawned && pawn.AmbientTemperature < pawn.GetStatValue(StatDefOf.ComfyTemperatureMin);
        }

        // Not on its way anywhere: standing still, wandering, or drafted, where idleHands has already left out a pawn with a
        // weapon or an enemy near. A bout of rubbing its hands begins only then, and gives way if it sets off (rubFree).
        private static bool AtLeisure(Pawn pawn)
        {
            if (pawn.Drafted || pawn.pather == null || !pawn.pather.MovingNow)
            {
                return true;
            }
            JobDef job = pawn.CurJobDef;
            return job == JobDefOf.GotoWander || job == JobDefOf.Wait_Wander;
        }

        // How far through a bout of the clip the pawn is (0 .. 1), or -1 between bouts. A bout begins only while wanted
        // and after a rest of restFrom .. restTo game seconds, picked per pawn and bout (the first rest is a share of
        // one, so pawns that start wanting it together do not all start together), and is played through once on game
        // time, even if it stops being wanted part way.
        private static float BoutOf(Pawn pawn, Bout b, string clip, bool wanted, float restFrom, float restTo)
        {
            int length = Mathf.Max(1, Mathf.RoundToInt(skin.SecondsOf(clip) * 60f));
            int now = Find.TickManager.TicksGame;
            int seed = Gen.HashCombineInt(Gen.HashCombineInt(pawn.thingIDNumber, now), clip.Length);
            int rest = Mathf.RoundToInt(Mathf.Lerp(restFrom, restTo, Rand.ValueSeeded(seed)) * 60f);
            if (b.start > now || b.next > now + Mathf.RoundToInt(restTo * 60f))
            {
                b.start = b.next = -1;   // a save loaded from earlier
            }
            if (b.start >= 0)
            {
                if (now - b.start < length)
                {
                    return (now - b.start) / (float)length;
                }
                b.start = -1;
                b.next = now + rest;
            }
            if (b.next < 0)
            {
                b.next = now + Mathf.RoundToInt(rest * Rand.ValueSeeded(seed + 1));
            }
            if (wanted && now >= b.next)
            {
                b.start = now;
                return 0f;
            }
            return -1f;
        }

        // The weight of a bout at t (BoutOf): eased in and out at its ends, none between bouts.
        private static float BoutWeight(float t)
        {
            return t < 0f ? 0f : Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(Mathf.Min(t, 1f - t) / BoutEdge));
        }

        // The clip for an ability or psycast the pawn is casting (cast; cast_touch for one cast by touch) and which way its
        // target is (face); null when it is not warming one up. Jumps have their own crouch (CrouchOf).
        private static string CastOf(Pawn pawn, out float? face)
        {
            face = null;
            Stance_Warmup warmup = pawn.stances != null ? pawn.stances.curStance as Stance_Warmup : null;
            Verb_CastAbility verb = warmup != null ? warmup.verb as Verb_CastAbility : null;
            if (verb == null || verb is Verb_CastAbilityJump || verb is Verb_CastAbilityConsumeLeap)
            {
                return null;
            }
            string clip = verb is Verb_CastAbilityTouch && skin.Has("cast_touch") ? "cast_touch" : "cast";
            if (!skin.Has(clip))
            {
                return null;
            }
            face = FaceOf(pawn, warmup.focusTarg);
            return clip;
        }

        // Which way target is from the pawn (degrees clockwise from north); null if it is invalid or where the pawn is.
        private static float? FaceOf(Pawn pawn, LocalTargetInfo target)
        {
            if (!target.IsValid)
            {
                return null;
            }
            Vector3 to = target.HasThing ? target.Thing.DrawPos : target.Cell.ToVector3Shifted();
            Vector3 d = to - pawn.DrawPos;
            return d.x * d.x + d.z * d.z > 0.001f ? d.AngleFlat() : (float?)null;
        }

        // The last hit each pawn took (by thingIDNumber): when, the way it pushed (degrees clockwise from north; -1: none
        // given) and how hard (0 .. 1). Recorded as the game shows it (DamagedPostfix), played by FlinchOf.
        private struct Hit
        {
            public int tick;
            public float angle;
            public float strength;
        }
        private static readonly Dictionary<int, Hit> hits = new Dictionary<int, Hit>();

        private static void DamagedPostfix(Pawn ___pawn, DamageInfo dinfo)
        {
            Blown(___pawn, dinfo);
            Struck(___pawn, dinfo, 1f);
        }

        // The last blow each pawn took (by thingIDNumber), dead or alive (the game says so only once a killing blow has
        // killed): what throws its ragdoll as it falls, and a ragdoll lying already is thrown by it at once.
        private struct Blow
        {
            public int tick;
            public float angle;   // degrees clockwise from north; -1: none given
            public float speed;   // cells per game second
        }
        private static readonly Dictionary<int, Blow> blows = new Dictionary<int, Blow>();

        private static void Blown(Pawn pawn, DamageInfo dinfo)
        {
            if (pawn == null || !Ragdolls)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            if (blows.Count > 256)
            {
                stale.Clear();
                foreach (KeyValuePair<int, Blow> pair in blows)
                {
                    if (now - pair.Value.tick > BlowTicks || now < pair.Value.tick)
                    {
                        stale.Add(pair.Key);
                    }
                }
                foreach (int id in stale)
                {
                    blows.Remove(id);
                }
                stale.Clear();
            }
            float amount = GatheredDamage(pawn, dinfo, now), before = amount - dinfo.Amount;
            float speed = PushSpeedOf(dinfo, amount);
            blows[pawn.thingIDNumber] = new Blow { tick = now, angle = dinfo.Angle, speed = speed };
            // Felled by a blow big enough: thrown, in the game. Not by what the screen shows: this must come out the same
            // whether or not anyone is looking.
            float fling = FlingBodies && (pawn.Dead || pawn.Downed) && dinfo.Angle >= 0f ? FlingSpeedOf(pawn, dinfo, amount) : 0f;
            bool thrown = fling > 0f && Flings.Throw(pawn, dinfo.Angle, fling, fling * FlingUp);
            Ease e;
            if (eases.TryGetValue(pawn.thingIDNumber, out e) && e.doll != null && e.fallen >= 1f)
            {
                // A push adds to the speed it has, so only what this hit adds to those it is gathered with.
                float more = thrown ? fling - (before > 0f ? FlingSpeedOf(pawn, dinfo, before) : 0f)
                    : (speed - (before > 0f ? PushSpeedOf(dinfo, before) : 0f)) * PushLying;
                e.doll.Push(BlowWay(dinfo.Angle, e.doll.Yaw), more, thrown ? more * FlingUp : 0f);
                e.dollSaved = false;
            }
        }

        // The hits of the last few ticks, of any pawn, for GatheredDamage.
        private struct RecentHit
        {
            public int pawn;      // thingIDNumber
            public int tick;
            public float angle;   // degrees clockwise from north
            public float amount;
            public bool ranged;
        }
        private static readonly List<RecentHit> recentHits = new List<RecentHit>();

        // A hit's damage with that of the same kind which came the same way in the last few ticks, so a shotgun's pellets
        // throw a body as the one big hit they are, the ones that felled it counted too. Kept for a pawn still standing, and
        // on game ticks only, so it comes out the same whether or not anyone is looking. A blast stands alone, as does a hit
        // with no way to it (nothing it could be thrown along).
        private static float GatheredDamage(Pawn pawn, DamageInfo dinfo, int now)
        {
            for (int i = recentHits.Count - 1; i >= 0; i--)
            {
                if (now - recentHits[i].tick > FlingGatherTicks || now < recentHits[i].tick)
                {
                    recentHits.RemoveAt(i);
                }
            }
            if (dinfo.Def == null || dinfo.Def.isExplosive || dinfo.Angle < 0f)
            {
                return dinfo.Amount;
            }
            float total = dinfo.Amount;
            foreach (RecentHit hit in recentHits)
            {
                if (hit.pawn == pawn.thingIDNumber && hit.ranged == dinfo.Def.isRanged && Mathf.Abs(Mathf.DeltaAngle(hit.angle, dinfo.Angle)) <= FlingSameWay)
                {
                    total += hit.amount;
                }
            }
            recentHits.Add(new RecentHit { pawn = pawn.thingIDNumber, tick = now, angle = dinfo.Angle, amount = dinfo.Amount, ranged = dinfo.Def.isRanged });
            return total;
        }

        // How hard a blow of this much damage knocks a ragdoll about (cells per game second).
        private static float PushSpeedOf(DamageInfo dinfo, float amount)
        {
            float speed = Mathf.Lerp(PushLeast, PushMost, Mathf.Clamp01(amount / PushFull));
            if (dinfo.Def != null && dinfo.Def.isExplosive)
            {
                speed *= PushBlast;
            }
            return speed * FlingPower;
        }

        // How fast a blow of this much damage throws this pawn's body (cells per game second along the ground), 0 if it
        // doesn't.
        private static float FlingSpeedOf(Pawn pawn, DamageInfo dinfo, float amount)
        {
            if (dinfo.Def == null || !pawn.RaceProps.Humanlike)
            {
                return 0f;
            }
            float speed;
            if (dinfo.Def.isExplosive)
            {
                speed = amount < FlingBlastFrom ? 0f : Mathf.Lerp(FlingBlastLeast, FlingBlastMost, Mathf.InverseLerp(FlingBlastFrom, FlingBlastFull, amount));
            }
            else if (dinfo.Def.isRanged)
            {
                speed = amount < FlingShotFrom ? 0f : Mathf.Lerp(FlingShotLeast, FlingShotMost, Mathf.InverseLerp(FlingShotFrom, FlingShotFull, amount));
            }
            else
            {
                speed = amount < FlingMeleeFrom ? 0f : Mathf.Lerp(FlingMeleeLeast, FlingMeleeMost, Mathf.InverseLerp(FlingMeleeFrom, FlingMeleeFull, amount));
            }
            return speed * FlingPower * Mathf.Clamp(1f / Mathf.Sqrt(Mathf.Max(0.05f, pawn.BodySize)), FlingHeaviest, FlingLightest);
        }

        // The blow that has just felled a pawn, if any: its way (degrees clockwise from north) and how hard it pushed.
        internal static bool BlowOf(Pawn pawn, out float angle, out float speed)
        {
            Blow blow;
            angle = -1f;
            speed = 0f;
            if (pawn == null || !blows.TryGetValue(pawn.thingIDNumber, out blow) || Find.TickManager.TicksGame - blow.tick > BlowTicks)
            {
                return false;
            }
            angle = blow.angle;
            speed = blow.speed;
            return true;
        }

        // A body has been flung into another cell (Flings): its ragdoll goes with it.
        internal static void Flung(Pawn pawn)
        {
            Ease e;
            if (eases.TryGetValue(pawn.thingIDNumber, out e) && e.doll != null)
            {
                e.doll.Cell = pawn.PositionHeld;
                e.dollMoved = true;
            }
        }

        // Where the weapon in this pawn's hands was last drawn, if only just: true cells, and its aim and top.
        internal static bool HeldLast(Pawn pawn, out Vector3 at, out Vector3 aim, out Vector3 top)
        {
            Ease e;
            at = aim = top = Vector3.zero;
            if (pawn == null || !eases.TryGetValue(pawn.thingIDNumber, out e) || Time.frameCount - e.heldFrame > 30)
            {
                return false;
            }
            at = e.heldAt;
            aim = e.heldAim;
            top = e.heldTop;
            return true;
        }

        // A weapon in flight (Flings): as it is drawn in the hand, at its size in the hand, its middle at at (true cells).
        internal static void DrawFlying(Thing weapon, Vector3 at, Vector3 aim, Vector3 top)
        {
            Vector3 tip;
            DrawSlab(weapon, ScaleOf(weapon), at, aim, top, 0f, Vector3.zero, 0f, 1f, out tip, false);   // on no figure: IsoRim's outline
        }

        // Before and after the game drops what a pawn has as it goes down: the weapon it held flies off (Flings).
        private static void DropEverythingPrefix(Pawn __instance, out ThingWithComps __state)
        {
            __state = FlingWeapons && __instance.equipment != null ? __instance.equipment.Primary : null;
        }

        private static void DropEverythingPostfix(Pawn __instance, ThingWithComps __state)
        {
            if (__state != null && __state.Spawned && (__instance.Dead || __instance.Downed))
            {
                Flings.TakeWeapon(__instance, __state);
            }
        }

        // The way a blow pushes (degrees clockwise from north; -1: none given, so from the front) in the space of a figure
        // at yaw, along the ground.
        private static Vector3 BlowWay(float angle, float yaw)
        {
            return Quaternion.AngleAxis(angle >= 0f ? angle - yaw : 180f, Vector3.up) * Vector3.forward;
        }

        private static void DeflectedPostfix(Pawn ___pawn, DamageInfo dinfo)
        {
            Struck(___pawn, dinfo, FlinchDeflected);
        }

        // When each pawn (by thingIDNumber) last dealt a melee blow at a target already on the ground, in game ticks. Looked at
        // before the blow is dealt, as a blow that fells its target should be drawn at it standing, and the clip is only
        // picked after it lands. On TryCastNextBurstShot, not TryCastShot, so Combat Extended's verb is seen too.
        private static readonly Dictionary<int, int> LowBlows = new Dictionary<int, int>();

        private static void BurstPrefix(Verb __instance)
        {
            if (!__instance.IsMeleeAttack || __instance.CasterPawn == null)
            {
                return;
            }
            Pawn target = __instance.CurrentTarget.Thing as Pawn;
            if (target != null && (target.Downed || target.GetPosture() != PawnPosture.Standing))
            {
                LowBlows[__instance.CasterPawn.thingIDNumber] = Find.TickManager.TicksGame;
            }
        }

        // A melee attack that missed or was dodged still staggers the pawn attacked, so it flinches from the attacker.
        private static void MeleePostfix(Verb_MeleeAttack __instance)
        {
            Pawn target = __instance.CurrentTarget.Thing as Pawn;
            Pawn attacker = __instance.CasterPawn;
            Hit hit;
            if (target == null || attacker == null || (hits.TryGetValue(target.thingIDNumber, out hit) && hit.tick == Find.TickManager.TicksGame))
            {
                return;
            }
            Vector3 d = target.DrawPos - attacker.DrawPos;
            float angle = d.x * d.x + d.z * d.z > 0.001f ? d.AngleFlat() : -1f;
            Struck(target, new DamageInfo(DamageDefOf.Blunt, 0f, angle: angle), FlinchMissed);
        }

        // When each pawn (by thingIDNumber) last took a weapon or clothes up off the ground (an Equip or Wear job), in game
        // ticks: the Ease picks it up as a bend on the pawn's next draw, if that comes within TakenTicks.
        private const int TakenTicks = 30;
        private static readonly Dictionary<int, int> taken = new Dictionary<int, int>();

        private static void EquippedPostfix(Pawn ___pawn)
        {
            Took(___pawn, JobDefOf.Equip);
        }

        private static void WornPostfix(Pawn ___pawn)
        {
            Took(___pawn, JobDefOf.Wear);
        }

        // Only a job's own pick-up: the game also equips and dresses pawns as they are made, from inventories and so on.
        private static void Took(Pawn pawn, JobDef job)
        {
            if (pawn == null || !pawn.Spawned || skin == null || pawn.CurJobDef != job)
            {
                return;
            }
            if (taken.Count > 256)
            {
                taken.Clear();
            }
            taken[pawn.thingIDNumber] = Find.TickManager.TicksGame;
        }

        private static void Struck(Pawn pawn, DamageInfo dinfo, float share)
        {
            if (pawn == null || !pawn.Spawned || pawn.Dead || skin == null)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            if (hits.Count > 256)
            {
                // Forget hits GarmentMask over.
                stale.Clear();
                foreach (KeyValuePair<int, Hit> pair in hits)
                {
                    if (now - pair.Value.tick > Mathf.Max(FlinchTicks, HitStagger * 4) || now < pair.Value.tick)
                    {
                        stale.Add(pair.Key);
                    }
                }
                foreach (int id in stale)
                {
                    hits.Remove(id);
                }
                stale.Clear();
            }
            float strength = Mathf.Lerp(FlinchLeast, 1f, Mathf.Clamp01(dinfo.Amount / FlinchFull)) * share;
            hits[pawn.thingIDNumber] = new Hit { tick = now, angle = dinfo.Angle, strength = strength };
        }

        // Whether the pawn's stagger came with a hit (the game staggers every pawn hit by a bullet, a melee blow or a blast):
        // that shows as the hit's flinch, the way it was pushed, and the reel only as a stagger with no hit behind it.
        private static bool StaggeredByHit(Pawn pawn)
        {
            Hit hit;
            if (!hits.TryGetValue(pawn.thingIDNumber, out hit))
            {
                return false;
            }
            int since = Find.TickManager.TicksGame - hit.tick;
            return since >= 0 && since <= HitStagger * pawn.GetStatValue(StatDefOf.StaggerDurationFactor) + 1f;
        }

        // Adds the pawn's flinch from its last hit, if it is not over (the export's flinch_<way>, by the way it was pushed
        // as the figure faces, yaw degrees clockwise from north): game time, so it waits while paused.
        private static void FlinchOf(Pawn pawn, SkinnedFigure.Posed fig, float yaw)
        {
            Hit hit;
            if (!hits.TryGetValue(pawn.thingIDNumber, out hit))
            {
                return;
            }
            int since = Find.TickManager.TicksGame - hit.tick;
            if (since < 0 || since >= FlinchTicks)
            {
                // Kept while it may still be what staggers the pawn (StaggeredByHit).
                if (since < 0 || since >= HitStagger * 4)
                {
                    hits.Remove(pawn.thingIDNumber);
                }
                return;
            }
            // With no way given, from the front (pushed back).
            float rel = (hit.angle >= 0f ? hit.angle - yaw : 180f) * Mathf.Deg2Rad;
            float forward = Mathf.Cos(rel), right = Mathf.Sin(rel);
            float phase = (float)since / FlinchTicks;
            // A variant per hit, the same one for all four ways.
            int roll = Gen.HashCombineInt(pawn.thingIDNumber, hit.tick);
            skin.Nudge(fig, skin.Variant("flinch_F", roll), phase, Mathf.Max(0f, forward) * hit.strength);
            skin.Nudge(fig, skin.Variant("flinch_B", roll), phase, Mathf.Max(0f, -forward) * hit.strength);
            skin.Nudge(fig, skin.Variant("flinch_R", roll), phase, Mathf.Max(0f, right) * hit.strength);
            skin.Nudge(fig, skin.Variant("flinch_L", roll), phase, Mathf.Max(0f, -right) * hit.strength);
        }

        // The walk.WORK kind for what the pawn is working at, sorted by tool as IsoRim's Work.cs sorts its jobs (and a few
        // more of the same motion), or for work done with the hands, by where it is done (PlaceWork: bills, research,
        // tending, stripping, sowing and cutting plants other than trees), and which way the work is (face, degrees clockwise from north); null when it is not
        // standing at work, IsoRim's work animations are off, or the export has no clip for it. Only once it has reached
        // the work, not while it fetches things for it, and not on the move (coming: the work it is about to reach instead,
        // HolsterBefore, so it can put its weapon away first). Drafted counts the same: a drafted pawn ordered to a job does it.
        // Vomiting is played the same way (bent over, facing the cell it is sick on), whatever the setting, as it is not
        // work, and so is meditating (Meditate and MeditatePray: sat cross-legged on the spot, facing the way the game
        // turns it; on a chair or throne it keeps the sit, but praying there puts its palms together and bows its head,
        // meditate_sit, laid over the sit as seated work is), and so is visiting a grave (VisitGrave: sat cross-legged
        // beside it, facing it).
        // Trial only: feeding a patient played with the taming clips (FeedPatient in WorkOf).
        internal static readonly bool FeedAsTame = Environment.GetEnvironmentVariable("M3AT_FEED_TAME") == "1";

        private static string WorkOf(Pawn pawn, out float face, out Vector3? beside, bool coming = false)
        {
            beside = null;
            face = 0f;
            Job job = pawn.CurJob;
            bool vomit = job != null && job.def == JobDefOf.Vomit;
            bool meditate = job != null && pawn.jobs != null && (pawn.jobs.curDriver is JobDriver_Meditate || pawn.jobs.curDriver is JobDriver_VisitGrave);
            string play = job != null ? PlaysByBaby(pawn) : null;
            bool music = job != null && pawn.jobs != null && (pawn.jobs.curDriver is JobDriver_PlayMusicalInstrument || PlaysForLover(pawn));
            if (job == null || job.def == null || (!vomit && !meditate && play == null && !music && !WorkAnimations) || (!coming && pawn.pather != null && pawn.pather.Moving))
            {
                return null;
            }
            LocalTargetInfo a = job.targetA;
            if (pawn.jobs != null && pawn.jobs.curDriver is JobDriver_VisitGrave && a.Thing != null && a.Thing.Spawned)
            {
                // (a grave is two cells long and is visited from beside either: faced at its nearest cell, which is
                // also what counts as being there)
                IntVec3 nearest = a.Cell;
                foreach (IntVec3 c in a.Thing.OccupiedRect())
                {
                    if ((c - pawn.Position).LengthHorizontalSquared < (nearest - pawn.Position).LengthHorizontalSquared)
                    {
                        nearest = c;
                    }
                }
                a = nearest;
            }
            if (job.def == JobDefOf.FeedPatient && job.targetB.Thing is Pawn)
            {
                // (feeding: target A is the meal, in the feeder's own hands by then, which stands on no bed; the patient
                // is B, and is what it works at and faces)
                a = job.targetB;
            }
            if (!a.IsValid || !a.Cell.IsValid)
            {
                return null;
            }
            // (flat, as drawn: work on another floor, such as rock mined from the cell below it, counts by its column)
            Vector3 to = Api.DrawnDelta(pawn.DrawPos, a.Cell.ToVector3Shifted());
            if (coming ? !HolsterBefore(pawn, a) : Api.DrawnDelta(pawn.Position.ToVector3Shifted(), a.Cell.ToVector3Shifted()).sqrMagnitude > WorkReach)
            {
                return null;
            }
            face = to.x * to.x + to.z * to.z > 0.04f ? to.AngleFlat() : pawn.Rotation.AsAngle;
            beside = null;
            string kind = null;
            if (vomit)
            {
                kind = "vomit";
            }
            else if (meditate)
            {
                kind = job.def.defName == "MeditatePray" && SeatUnder(pawn) != null && skin.Has("work_meditate_sit") ? "meditate_sit" : "meditate";
            }
            else if (music)
            {
                // (not work, so whatever IsoRim's work setting: stood at the instrument, or sat on a seat put at it, the
                // figure drawn stepped up to it, InstrumentCloser)
                kind = InstrumentKind(a.Thing);
                if (kind != null && SeatUnder(pawn) != null)
                {
                    kind += "_sit";
                }
            }
            else if (play == "PlayStatic")
            {
                // (sat on the floor rocking the baby in its arms, HoldBabyPlayed; not knelt beside it as a patient is)
                return skin.Has(WorkClipOf("play")) && skin.Has("carry_play") ? "play" : null;
            }
            else if (play == "PlayToys")
            {
                // (knelt beside the baby set down by the toy box, as by a patient, a hand waved over it; on an export
                // without the clip, just knelt)
                kind = skin.Has("work_toys") ? "toys" : "tend";
            }
            else if (pawn.jobs != null && pawn.jobs.curDriver is JobDriver_Mine)
            {
                kind = "pickaxe";   // (vanilla mining and any job built on it)
            }
            else
            {
                switch (job.def.defName)
                {
                    case "CutPlant":
                    case "CutPlantDesignated":
                    case "Harvest":
                    case "HarvestDesignated":
                    {
                        Plant plant = a.Thing as Plant;
                        kind = plant != null && plant.def.plant != null && plant.def.plant.IsTree ? "axe" : PlaceWork(pawn, a, "scythe", false);
                        break;
                    }
                    case "Mine":
                        kind = "pickaxe";
                        break;
                    case "FinishFrame":
                    case "PlaceNoCostFrame":
                    case "Repair":
                    case "Deconstruct":
                    case "DeconstructForBlueprint":
                    case "Uninstall":
                    case "FixBrokenDownBuilding":
                        kind = BuildKind(a);
                        break;
                    case "Milk":
                    case "Shear":
                        kind = AnimalKind(a, "bench", "crouch");
                        break;
                    case "Tame":
                    case "Train":
                        // (a hand held out to it palm up: stood at a tall animal, knelt by a low one; training is the
                        // same driver, JobDriver_InteractAnimal)
                        kind = AnimalKind(a, "tame", "tame_kneel");
                        break;
                    case "BeatFire":
                        kind = FireKind(pawn, a);
                        break;
                    case "SmoothFloor":
                    case "RemoveFloor":
                    case "RemoveFoundation":
                        kind = skin.Has("work_crouch") ? "crouch" : "hammer";   // (knelt at the floor)
                        break;
                    case "RepairMech":
                    case "BuildRoof":
                    case "RemoveRoof":
                    case "SmoothWall":
                        kind = "hammer";
                        break;
                    case "Sow":
                        kind = PlaceWork(pawn, a, "hoe", false);   // (knelt at the ground, as for cutting plants)
                        break;
                    case "ClearPollution":
                        kind = PlaceWork(pawn, a, "hoe", false);
                        break;
                    case "Clean":
                    case "ClearSnow":
                        kind = "broom";
                        break;
                    case "PlantSeed":
                    case "Replant":
                        kind = PlaceWork(pawn, a, "pluck", false);   // (knelt at the ground, bare hands, on an older export)
                        break;
                    case "Forage":
                    case "ExtractTree":
                    case "PruneGauranlenTree":
                        kind = PlaceWork(pawn, a, "pluck", false);   // (knelt at the ground, bare hands, on an older export)
                        break;
                    case "FeedPatient" when FeedAsTame:
                        // (trial: the taming clips, the hand held out palm up to the patient: stood at a bed, knelt by one
                        // on the ground; M3AT_FEED_TAME=1)
                        kind = PlaceWork(pawn, a, "tend", false) == "bench" ? "tame" : "tame_kneel";
                        break;
                    case "TendPatient":
                    case "TendEntity":
                    case "FeedPatient":
                    case "InstallImplant":
                    case "Resurrect":
                        // (by the patient's bed or platform: over it standing, crouched by one on the ground; knelt by it on
                        // an older export)
                        kind = PlaceWork(pawn, a, "tend", false);
                        break;
                    case "Strip":
                        // (the same: crouched by a body or a downed pawn on the ground, over one on a bed)
                        kind = PlaceWork(pawn, a, "tend", false);
                        break;
                    case "VisitSickPawn":
                    {
                        // (the game visits only a patient awake in a bed. Sat on a chair, or stood by a bed of any height,
                        // BenchFill: no work, it talks, TalkOf; by one on the ground, a sleeping spot or bedroll, sat
                        // cross-legged beside them talking, work_visit, placed as a kneel is, KneelBeside; on an export
                        // with neither clip knelt by them as the tending jobs are)
                        if (SeatUnder(pawn) != null || WorkFill(pawn, a) >= BenchFill)
                        {
                            kind = skin.Has("talk") ? null : PlaceWork(pawn, a, "tend", false);
                        }
                        else
                        {
                            kind = skin.Has("work_visit") ? "visit" : skin.Has("talk") ? null : PlaceWork(pawn, a, "tend", false);
                        }
                        break;
                    }
                    case "Research":
                        // (the export's own mocap: looking something over, sat at the bench over the sit, or with no seat
                        // sat cross-legged on the floor (stood at it on an export without that); on an older export, by
                        // where it is)
                        if (skin.Has("work_research") && SeatUnder(pawn) != null)
                        {
                            kind = "research";
                        }
                        else if (skin.Has("work_research_floor"))
                        {
                            kind = "research_floor";
                        }
                        else
                        {
                            kind = skin.Has("work_research_stand") ? "research_stand" : PlaceWork(pawn, a, "hands", true);
                        }
                        break;
                    case "Fish":
                        // (Odyssey's fishing, from the stand cell facing the water: stood holding the rod out over it, the
                        // line drawn to the water, DrawLine; knelt at the ground on an export without the clip)
                        kind = skin.Has("work_fish") ? "fish" : "crouch";
                        break;
                    case "UseCommsConsole":
                    case "AnalyzeItem":
                    case "Hack":
                        kind = PlaceWork(pawn, a, "hands", true);
                        break;
                    case "Flick":
                    case "Open":
                        // (a switch, a lamp or a lid: the right hand reached out to it if it stands tall, knelt at it if
                        // it is low; on an export without the reach, by where it is)
                        kind = skin.Has("work_reach") && a.Thing != null && DrawnTall(a.Thing.def) ? "reach" : PlaceWork(pawn, a, "hands", false);
                        break;
                    case "Refuel":
                    case "RefuelAtomic":
                    case "RearmTurret":
                    case "RearmTurretAtomic":
                    case "EjectFuel":
                    case "FillFermentingBarrel":
                    case "TakeBeerOutOfFermentingBarrel":
                        // (the hands at it for the game's wait, by where it is: stood at a generator or a turret, knelt
                        // at a campfire, a torch or a barrel; the fuel stays in hand meanwhile)
                        kind = PlaceWork(pawn, a, "hands", false);
                        break;
                    case "DoBill":
                    {
                        // Any bill (crafting, cooking, drugs, butchering, surgery) by where it is done; on an older export,
                        // by the tool the bench suggests.
                        string bench = a.Thing != null && a.Thing.def != null ? (a.Thing.def.defName ?? "") : "";
                        string tool = "hands";
                        if (bench.Contains("Butcher"))
                        {
                            tool = "knife";
                        }
                        else if (bench.Contains("Smithy") || bench.Contains("Machining") || bench.Contains("Fabrication") || bench.Contains("Stonecutter")
                            || bench.Contains("Sculpt") || bench.Contains("Crafting") || bench.Contains("Smelter"))
                        {
                            tool = "hammer";
                        }
                        // (cutting stone and sculpting are hammered down at the table, stood or sat at it; the bench work
                        // by place on an export without those strokes)
                        if (bench.Contains("Stonecutter") || bench.Contains("Sculpt"))
                        {
                            string hammer = SeatUnder(pawn) != null ? "hammer_bench_sit" : "hammer_bench";
                            kind = skin.Has("work_" + hammer) ? hammer : PlaceWork(pawn, a, tool, true);
                        }
                        // (a campfire, vanilla's or a mod's, is worked at sat cross-legged on the ground stirring a pot
                        // reached out to it the whole time, whatever its fill, as knelt the chibi's head went over the
                        // fire; knelt on an export without that; sat on a seat beside it, hunched forward reaching the
                        // spoon out to it, else by place as any bill)
                        else if (bench.Contains("Campfire") && SeatUnder(pawn) == null && (skin.Has("work_stir_campfire") || skin.Has("work_crouch")))
                        {
                            kind = skin.Has("work_stir_campfire") ? "stir_campfire" : "crouch";
                        }
                        else if (bench.Contains("Campfire") && SeatUnder(pawn) != null && skin.Has("work_stir_campfire_sit"))
                        {
                            kind = "stir_campfire_sit";
                        }
                        else
                        {
                            kind = PlaceWork(pawn, a, tool, true);
                        }
                        break;
                    }
                }
            }
            if (kind == null || !skin.Has(WorkClipOf(kind)))
            {
                return null;
            }
            // Working on someone lying (tending, feeding, stripping): facing the body as drawn, its chest, rather than its
            // cell's middle, as a lying figure is laid with its middle on that, and the game often stands the doctor on
            // the patient's own cell, where the cell gives no way at all. On the ground it is drawn kneeling beside them
            // (KneelBeside); at a bed it stays where it stands.
            Ease lying;
            if (!coming && LyingOf(a.Thing, out lying))
            {
                if (kind == "bench" || kind == "bench_sit" || kind == "tame")
                {
                    Vector3 from = pawn.DrawPos - lying.lyingChest;
                    from.y = 0f;
                    if (from.sqrMagnitude > 0.0025f)
                    {
                        face = (-from).AngleFlat();
                    }
                }
                else
                {
                    Ease mine;
                    eases.TryGetValue(pawn.thingIDNumber, out mine);
                    Pawn on = a.Thing as Pawn ?? (a.Thing as Corpse)?.InnerPawn;
                    // (off a bed or sleeping spot, from the side it came: drawn as if it knelt in the cell it came in from,
                    // though the game has often walked it on into the patient's own)
                    Vector3? came = on.CurrentBed() == null && mine != null ? mine.cameWay : null;
                    // Tended (or fed, or anything else done for them but stripping) downed on the ground: laid on their back
                    // to be seen to, as on a sleeping spot (OnBack), rather than curled on their side, where the doctor knelt
                    // over their head and shoulders. Its knees are then kept off the body as it lies once it has turned.
                    if (!on.Dead && on.Downed && on.CurrentBed() == null && job.def != JobDefOf.Strip && lying != null)
                    {
                        lying.onBack = true;
                    }
                    beside = KneelBeside(pawn, "work_" + kind, lying, mine, on.thingIDNumber, came, out face, kind == "visit" ? VisitBack : 0f);
                }
            }
            return kind;
        }

        // The pawn (or the corpse's) as it was drawn lying, if only just (its lyingChest, lyingAlong, lyingJoints).
        private static bool LyingOf(Thing thing, out Ease e)
        {
            Pawn lying = thing as Pawn ?? (thing as Corpse)?.InnerPawn;
            if (lying == null || !eases.TryGetValue(lying.thingIDNumber, out e) || Time.frameCount - e.lyingFrame > 30 || e.lyingJoints == null)
            {
                e = null;
                return false;
            }
            return true;
        }

        // Where a pawn working on someone lying on the ground is drawn (true cells, flat) and the way it faces: kneeling
        // with its hands (the clip's, first frame) on the near side of their chest, out from it on whichever way round
        // (KneelWays of them) leaves its knees and feet (the clip's joints below KneelLow) clearest of the body as drawn,
        // their arms and legs included, as a body lying on its side or limp has them well off its line. Given came (the
        // way it walked up), only ways within KneelCameSpread of the side it came from, the nearest that side liked
        // best; then square to the body, then the way it kneels already (so it doesn't hop about as the body breathes),
        // and without came, the side of the body it is on. back (cells at size 1) puts the hands that much further out, for
        // a clip whose hands don't reach for the body (a visitor sat with them on its knees, VisitBack).
        private static Vector3 KneelBeside(Pawn pawn, string clip, Ease lying, Ease mine, int whom, Vector3? came, out float face, float back = 0f)
        {
            float size = SizeOf(pawn);
            Vector3 hands = 0.5f * (skin.HeadOf(clip, "hand.L") + skin.HeadOf(clip, "hand.R")) * size;
            hands.y = 0f;
            Vector3 chest = lying.lyingChest;
            Vector3 along = lying.lyingAlong;
            Vector3 from = pawn.DrawPos - chest;
            from.y = 0f;
            Vector3? kept = mine != null && mine.besideOf == whom ? mine.besideWay : null;
            float clear = KneelClear * Mathf.Max(size, lying.lyingSize);
            float inset = (ChestInset + back) * lying.lyingSize;
            float best = float.MaxValue;
            Vector3 bestWay = Vector3.forward, bestAt = chest;
            for (int i = 0; i < KneelWays; i++)
            {
                Vector3 way = Quaternion.AngleAxis(i * 360f / KneelWays, Vector3.up) * Vector3.forward;
                float offCame = came.HasValue ? Vector3.Angle(way, -came.Value) : 0f;
                if (offCame > KneelCameSpread)
                {
                    continue;   // (only round about the side it came from)
                }
                Quaternion turned = Quaternion.AngleAxis((-way).AngleFlat(), Vector3.up);
                Vector3 at = chest + way * inset - turned * hands;
                at.y = 0f;
                float cost = 0f;
                for (int k = 0; k < KneelBones.Length; k++)
                {
                    Vector3 joint = skin.HeadOf(clip, KneelBones[k]);
                    if (joint.y > KneelLow)
                    {
                        continue;
                    }
                    Vector3 w = at + turned * (joint * size);
                    for (int j = 0; j < lying.lyingJoints.Length; j++)
                    {
                        Vector3 q = lying.lyingJoints[j] - w;
                        q.y = 0f;
                        float into = clear - q.magnitude;
                        if (into > 0f)
                        {
                            cost += into * into * KneelOverlap;
                        }
                    }
                }
                cost += Mathf.Abs(Vector3.Dot(way, along)) * KneelSquare;
                if (kept.HasValue)
                {
                    cost += Vector3.Angle(way, kept.Value) / 180f * KneelKeep;
                }
                if (came.HasValue)
                {
                    cost += offCame / 180f * KneelCame;
                }
                else if (from.sqrMagnitude > 0.0025f)
                {
                    cost += Vector3.Angle(way, from) / 180f * KneelNear;
                }
                if (cost < best)
                {
                    best = cost;
                    bestWay = way;
                    bestAt = at;
                }
            }
            if (mine != null)
            {
                mine.besideWay = bestWay;
                mine.besideOf = whom;
            }
            face = (-bestWay).AngleFlat();
            return bestAt;
        }

        // Work done with the hands at a place rather than with a tool, by where it is (the export's mocap work): stood at a
        // bench (bench) or sat at it (bench_sit), knelt at the ground (crouch, or ground in its place: sow, crouched
        // hunched over, falling back to crouch on an export without it) or sat there with no bench (chair: a crafting
        // spot with a chair on it). seats: whether the game sits a pawn for this work, on a seat at the work's spot.
        // fallback is the kind on an older export without these clips.
        private static string PlaceWork(Pawn pawn, LocalTargetInfo at, string fallback, bool seats, string ground = "crouch")
        {
            bool high = WorkFill(pawn, at) >= BenchFill;
            bool seated = seats && SeatUnder(pawn) != null;
            string kind = high ? (seated ? "bench_sit" : "bench") : (seated ? "chair" : ground);
            if (kind == ground && !skin.Has("work_" + ground))
            {
                kind = "crouch";
            }
            return skin.Has("work_" + kind) ? kind : fallback;
        }

        // How a building is built, repaired, mended, taken down or uninstalled (for a frame, by what it will be): a structure (a wall, door, fence, barricade or anything else in the Structure tab, or an
        // impassable building that fills its cell) with the hammer swung into its face, as before; anything else with the
        // hands, stood at it (bench) if what it will be is drawn BuildHigh or more tall (IsoRim's Forms height of the built
        // def; for a def IsoRim draws flat, its fillPercent against BenchFill), else knelt at the ground (crouch: floors,
        // rugs, spots, lamps' low bases). Falls back to the hammer on an export without those clips.
        private static string BuildKind(LocalTargetInfo at)
        {
            Thing thing = at.Thing;
            BuildableDef built = thing == null || thing.def == null ? null : thing.def.entityDefToBuild ?? thing.def;
            ThingDef def = built as ThingDef;
            if (built == null || (def != null && (def.IsDoor || (def.passability == Traversability.Impassable && def.fillPercent >= 0.99f)
                || (def.designationCategory != null && def.designationCategory.defName == "Structure"))))
            {
                return "hammer";
            }
            string kind = def != null && BuiltHigh(def) ? "bench" : "crouch";
            return skin.Has("work_" + kind) ? kind : "hammer";
        }

        // How a fire is beaten out (BeatFire; target A is the fire): patted out with the palms (pat) on a person standing
        // or a building drawn BuildHigh or more tall in its cell (furniture, a wall, a door: as BuildKind measures it, a
        // frame by what it will be), stomped out (stomp) anywhere else (the floor, an item, a plant, a rug, a conduit);
        // on someone lying, knelt at them as at ground work (crouch). Null (no clip, as before) on an export without it.
        private static string FireKind(Pawn pawn, LocalTargetInfo at)
        {
            Fire fire = at.Thing as Fire;
            if (fire == null || pawn.Map == null)
            {
                return null;
            }
            string kind = "stomp";
            Pawn burning = fire.parent as Pawn;
            if (burning != null)
            {
                kind = burning.GetPosture() == PawnPosture.Standing ? "pat" : "crouch";
            }
            else if (fire.parent == null && fire.Position.InBounds(pawn.Map))
            {
                List<Thing> things = fire.Position.GetThingList(pawn.Map);
                for (int i = 0; i < things.Count; i++)
                {
                    if (things[i] is Building && DrawnTall(things[i].def.entityDefToBuild as ThingDef ?? things[i].def))
                    {
                        kind = "pat";
                        break;
                    }
                }
            }
            return skin.Has("work_" + kind) ? kind : null;
        }

        // Whether a building is drawn BuildHigh or more tall (IsoRim's Forms height; for a def IsoRim draws flat, its
        // fillPercent against BenchFill), or fills its cell and stops anyone passing (a wall).
        private static bool DrawnTall(ThingDef def)
        {
            if (def == null)
            {
                return false;
            }
            if (def.passability == Traversability.Impassable && def.fillPercent >= 0.99f)
            {
                return true;
            }
            return BuiltHigh(def);
        }

        // Whether a building is drawn BuildHigh or more tall: its IsoRim model's height (Api.ModelHeight), or for one IsoRim
        // draws without a model of its own (flat, or its plain shape), its fillPercent against BenchFill.
        private static bool BuiltHigh(ThingDef def)
        {
            float h = Api.ModelHeight(def);
            return h > 0f ? h >= BuildHigh : def.fillPercent >= BenchFill;
        }

        // How an animal is worked at, as BuildKind sorts what is built: stood at it (high: bench to milk or shear it, tame to
        // tame it) if the top of its back is drawn AnimalHigh or more over the ground (IsoRim's own measure of its model,
        // Api.ReadHost's back, so a cow or muffalo), else knelt by it (low: crouch, tame_kneel: a goat, a sheep, a young one);
        // by body size when IsoRim draws it no model. Null (no work clip, as before) on an export without that clip.
        private static string AnimalKind(LocalTargetInfo at, string highKind, string lowKind)
        {
            Pawn animal = at.Thing as Pawn;
            if (animal == null)
            {
                return null;
            }
            bool high;
            HostTransform h;
            if (Api.Version >= 13 && Api.ReadHost(animal, out h) && h.model)
            {
                high = h.back >= AnimalHigh;
            }
            else
            {
                float factor = animal.ageTracker != null && animal.ageTracker.CurLifeStage != null ? animal.ageTracker.CurLifeStage.bodySizeFactor : 1f;
                high = animal.def != null && animal.def.race != null && animal.def.race.baseBodySize * factor >= AnimalBigBody;
            }
            string kind = high ? highKind : lowKind;
            return skin.Has("work_" + kind) ? kind : null;
        }

        // How much of its cell the work fills (ThingDef.fillPercent: benches, stoves and consoles 0.5, beds 0.3 to 0.4, a
        // hydroponics basin 0.3; spots none, a campfire or plant pot 0.2): for a patient, the bed or platform it lies on,
        // else whatever building stands on its cell (downed on a bed without being put in it, it lies on the bed's cell
        // all the same); for a plant, an item, a corpse or a cell, whatever building stands there.
        private static float WorkFill(Pawn pawn, LocalTargetInfo at)
        {
            Thing t = at.Thing;
            Pawn patient = t as Pawn;
            if (patient != null)
            {
                t = (Thing)patient.CurrentBed() ?? patient.ParentHolder as Thing;
                if (t == null && patient.Spawned)
                {
                    t = patient.Position.GetEdifice(patient.Map);
                }
            }
            else if (!(t is Building) && pawn.Map != null && at.Cell.InBounds(pawn.Map))
            {
                t = at.Cell.GetEdifice(pawn.Map);
            }
            return t != null && t.def != null ? t.def.fillPercent : 0f;
        }

        // Whether a work kind is played sat down, from the waist up over the sit, rather than as the whole figure standing.
        private static bool SeatedWork(string kind)
        {
            return kind == "research" || kind == "meditate_sit" || kind == "bench_sit" || kind == "chair" || kind == "hammer_bench_sit" || kind == "stir_campfire_sit"
                || kind == "piano_sit" || kind == "harp_sit" || kind == "drum_sit";
        }

        // Romance on the Rim's musical performance (RomanceOnTheRim_PlayInstrument, by name so the mod is not referenced):
        // its own driver, the instrument target A and the seat or spot B as the game's; played only once it has started
        // playing (the instrument's player set), not while it waits there for its lover to come and listen.
        private static bool PlaysForLover(Pawn pawn)
        {
            Job job = pawn.CurJob;
            return job.def.defName == "RomanceOnTheRim_PlayInstrument" && job.targetA.Thing is Building_MusicalInstrument instrument && instrument.IsBeingPlayed;
        }

        // How a musical instrument (Play_MusicalInstrument's target) is played, as walk.MUSIC poses it: Royalty's piano
        // and harpsichord at the keys ("piano"), its harp ("harp"), Ideology's drum ("drum"); another mod's by its
        // defName, else null (left standing as the game has it).
        private static string InstrumentKind(Thing instrument)
        {
            string name = instrument != null && instrument.def != null ? instrument.def.defName ?? "" : "";
            if (name.IndexOf("Drum", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "drum";
            }
            if (name.IndexOf("Harp", StringComparison.OrdinalIgnoreCase) >= 0 && name.IndexOf("Harpsichord", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return "harp";
            }
            if (name.IndexOf("Piano", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Harpsichord", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Organ", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Keyboard", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "piano";
            }
            return null;
        }

        // The tool in a working pawn's hand (the export's tool line for its kind): IsoRim's own model for it, riding the
        // hand bone as posed, sized with the figure as IsoRim's figure holds it.
        private static void DrawTool(Pawn pawn, string kind, SkinnedFigure.Posed fig, Vector3 at, float yaw, float size)
        {
            SkinnedFigure.Tool tool = skin.ToolOf(kind);
            Matrix4x4 hand;
            if (tool == null || !skin.BoneOf(fig, tool.bone, out hand))
            {
                return;
            }
            ModelHandle model = ToolModel(tool.model);
            Matrix4x4 local = Matrix4x4.Scale(new Vector3(size, size, size)) * hand * tool.place;
            Api.DrawModel(model, at, yaw, local, Color.white, "Wood");
            if (tool.model == RodModel && model != null)
            {
                DrawLine(pawn, local.MultiplyPoint3x4(new Vector3(0f, RodTip, 0f)), at, yaw, size);
            }
        }

        // A tool's model: this mod's own Models/<name>.obj if it has one (the fishing rod, anim_scripts/make_rod.py), else
        // IsoRim's; null (warned once) if neither has it.
        private static ModelHandle ToolModel(string name)
        {
            ModelHandle model;
            if (!toolModels.TryGetValue(name, out model))
            {
                string path = "Models/" + name + ".obj";
                bool own = content != null && File.Exists(Path.Combine(content.RootDir, path));
                model = own ? Api.LoadModel(content, path) : isoRim != null ? Api.LoadModel(isoRim, path) : null;
                toolModels[name] = model;
                if (model == null)
                {
                    Log.Warning(Tag + "Neither this mod nor IsoRim has " + path + "; that work is drawn without it.");
                }
            }
            return model;
        }

        private const string RodModel = "tool_rod";   // the fishing rod's model (walk.WORK's 'fish')
        private const float RodTip = 1.7f;            // cells from the rod's butt to its tip in its own model (make_rod.py's LENGTH)
        private const float LineBeyond = 0.4f;        // cells past the tip (along the ground, at a grown pawn's size) the float sits at least
        private const float LineSag = 0.06f;          // the line's dip at its middle, a share of its length
        private const int LineSegments = 5;

        // The fishing line, from the rod's tip (tip: in the figure's own space, as DrawModel's local places it) down to the
        // water: the fishing job's cell (its target A, the water it fishes), or LineBeyond past the tip along the way it
        // faces if that is nearer, at the water's height. Drawn as a few straight pieces of fishing_line.obj (one cell
        // long, up y) along a shallow dip.
        private static void DrawLine(Pawn pawn, Vector3 tip, Vector3 at, float yaw, float size)
        {
            ModelHandle line = ToolModel("fishing_line");
            if (line == null)
            {
                return;
            }
            // In the figure's own space (facing +z): straight out ahead of the tip, on the water.
            float reach = tip.z + LineBeyond * size;
            Job job = pawn.CurJob;
            if (job != null && job.targetA.IsValid && job.targetA.Cell.IsValid && job.targetA.Cell != pawn.Position)
            {
                Vector3 to = Api.DrawnDelta(pawn.DrawPos, job.targetA.Cell.ToVector3Shifted());
                reach = Mathf.Max(reach, Vector3.Dot(new Vector3(to.x, 0f, to.z), Quaternion.AngleAxis(yaw, Vector3.up) * Vector3.forward));
            }
            Vector3 bob = new Vector3(tip.x, WaterLine, reach);
            float sag = (bob - tip).magnitude * LineSag;
            Vector3 from = tip;
            for (int i = 1; i <= LineSegments; i++)
            {
                float s = (float)i / LineSegments;
                Vector3 to = Vector3.Lerp(tip, bob, s) - Vector3.up * (sag * 4f * s * (1f - s));
                Vector3 d = to - from;
                float length = d.magnitude;
                if (length > 1e-4f)
                {
                    Matrix4x4 piece = Matrix4x4.Translate(from) * Matrix4x4.Rotate(Quaternion.FromToRotation(Vector3.up, d / length))
                        * Matrix4x4.Scale(new Vector3(1f, length, 1f));
                    Api.DrawModel(line, at, yaw, piece, Color.white);
                }
                from = to;
            }
        }

        // The hurt clip for a wound on part: the place it is in (the first of the part and the parts it is in that is an
        // arm, the head, the neck or the chest, else the belly) and its side; null for a leg.
        private static string HurtPlaceOf(BodyPartRecord part)
        {
            string place = null, side = null;
            for (BodyPartRecord p = part; p != null; p = p.parent)
            {
                if (side == null)
                {
                    string label = p.untranslatedCustomLabel ?? "";
                    side = label.IndexOf("left", StringComparison.OrdinalIgnoreCase) >= 0 ? "L"
                        : label.IndexOf("right", StringComparison.OrdinalIgnoreCase) >= 0 ? "R" : null;
                }
                if (place != null)
                {
                    continue;
                }
                List<BodyPartTagDef> tags = p.def.tags;
                if (tags != null && (tags.Contains(BodyPartTagDefOf.MovingLimbCore) || tags.Contains(BodyPartTagDefOf.MovingLimbSegment)
                    || tags.Contains(BodyPartTagDefOf.MovingLimbDigit)))
                {
                    return null;
                }
                string name = p.def.defName;
                if (tags != null && (tags.Contains(BodyPartTagDefOf.ManipulationLimbCore) || tags.Contains(BodyPartTagDefOf.ManipulationLimbSegment)
                    || tags.Contains(BodyPartTagDefOf.ManipulationLimbDigit)))
                {
                    place = "arm";
                }
                else if (name == "Head")
                {
                    place = "head";
                }
                else if (name == "Neck")
                {
                    place = "neck";
                }
                else if (name == "Ribcage" || name == "Rib" || name == "Sternum" || name == "Clavicle"
                    || (tags != null && (tags.Contains(BodyPartTagDefOf.BloodPumpingSource) || tags.Contains(BodyPartTagDefOf.BreathingSource))))
                {
                    place = "chest";
                }
            }
            return "hurt_" + (place ?? "belly") + "_" + (side ?? "R");
        }

        // How deep the pawn stands in water or marsh where it is, cells: none on land or on a bridge. Deep water cannot be
        // walked, so any passable water named deep is chest-deep. Marsh is the game's water too, but IsoRim draws it as
        // ground (marshy ground is not water at all).
        private static float WaterOf(Pawn pawn)
        {
            if (!pawn.Spawned || pawn.Map == null)
            {
                return 0f;
            }
            TerrainDef t = pawn.Position.GetTerrain(pawn.Map);
            if (t == null || !t.IsWater)
            {
                return 0f;
            }
            if (!Api.IsDrawnWater(t))
            {
                return Marsh;
            }
            return t.defName != null && t.defName.IndexOf("Deep", StringComparison.OrdinalIgnoreCase) >= 0 ? ChestDeep : Shallow;
        }

        // How far bent to the ground the pawn is (0 .. 1), after it picked something up or set it down: the export's bend
        // pose, eased in fast over the first BendDown of its length (the game takes the thing in a tick, so the pose comes
        // after the fact: the hands are on the ground just as it comes into them), held to BendHold, then eased out, so
        // the hands come up with the load. Game time, so it waits while paused, and faster on the move (BendHurry at a full
        // stride: moving, 0 .. 1), since the legs keep walking under it.
        private static float BendOf(Ease e, float moving)
        {
            float seconds = skin.SecondsOf("bend");
            int now = Find.TickManager.TicksGame;
            if (e.bent < 1f && seconds > 0f)
            {
                e.bent += (now - e.bentTick) / 60f / seconds * Mathf.Lerp(1f, BendHurry, moving);
            }
            e.bentTick = now;
            return seconds > 0f ? Bent(e.bent) : 0f;
        }

        // How far a swimmer is laid down into the front crawl (0 treading water .. 1), following how far it is on the move
        // (moving, 0 .. 1) but no faster than SwimTurn of game time, so it tips forward into the stroke as it sets off and
        // rights itself as it stops, rather than as fast as the stride comes up.
        private static float StrokeOf(Ease e, float moving)
        {
            int frame = Time.frameCount;
            if (e.strokeFrame != frame)
            {
                float game = Time.deltaTime * Find.TickManager.TickRateMultiplier;
                e.stroke = frame - e.strokeFrame <= 2 ? Mathf.MoveTowards(e.stroke, moving, game / SwimTurn) : moving;
                e.strokeFrame = frame;
            }
            return Mathf.SmoothStep(0f, 1f, e.stroke);
        }

        // The bend at s (0 .. 1) through it.
        private static float Bent(float s)
        {
            if (s <= 0f || s >= 1f)
            {
                return 0f;
            }
            if (s < BendDown)
            {
                return Mathf.SmoothStep(0f, 1f, s / BendDown);
            }
            return s < BendHold ? 1f : Mathf.SmoothStep(0f, 1f, (1f - s) / (1f - BendHold));
        }

        // Where the pickaxe stroke is for a pawn mining, 0 .. 1 round it, timed so the point goes into the rock (PickHit) on
        // the tick the game strikes it: from JobDriver_Mine's own count down to the next strike (ticksToPickHit), so it
        // keeps time with the strike's sound and flash and with the miner's speed. The stroke takes as long as the time
        // between strikes, up to PickSlowest of the clip's own length: a slow miner swings at that pace and rests the pick in
        // the rock until it is time to swing again. A pawn ticked less than every tick (the game's tick intervals) counts
        // down in jumps, so the count is carried on by the game clock from when it was last seen to change. Any other job,
        // or mining not yet begun (the count is set below -100 until the first tick at the rock), keeps the clock.
        private static float PickPhase(Pawn pawn, Ease e, string clip)
        {
            JobDriver_Mine mine = pawn.jobs != null ? pawn.jobs.curDriver as JobDriver_Mine : null;
            float seconds = skin.SecondsOf(clip);
            if (mine == null || seconds <= 0f)
            {
                return LoopPhase(pawn, clip);
            }
            int left = pickHitIn(mine);
            if (left < -100)
            {
                return LoopPhase(pawn, clip);
            }
            int now = Find.TickManager.TicksGame;
            int job = mine.job != null ? mine.job.loadID : -1;
            if (job != e.pickJob)
            {
                e.pickJob = job;
                e.pickPeriod = 0;
                e.pickSeen = int.MinValue;
            }
            if (left != e.pickSeen)
            {
                if (left > e.pickSeen || e.pickPeriod <= 0)
                {
                    e.pickPeriod = Mathf.Max(left, 1);   // just reset after a strike (or first seen): the time to the next
                }
                e.pickSeen = left;
                e.pickSeenTick = now;
            }
            float until = Mathf.Max(0f, e.pickSeen - (now - e.pickSeenTick));
            float since = Mathf.Max(0f, e.pickPeriod - until);
            float stroke = Mathf.Min(e.pickPeriod, seconds * 60f * PickSlowest);   // ticks the whole stroke takes
            if (until <= PickHit * stroke)
            {
                return PickHit - until / stroke;   // winding up and bringing it down
            }
            if (since < PickBounce * stroke)
            {
                return PickHit + since / stroke;   // the bounce off the rock after the last strike
            }
            return 0f;   // resting in the rock until it is time to wind up
        }

        // Where a fire-beating clip (work_stomp, work_pat) is for a pawn beating a fire, 0 .. 1 round it, timed so a blow
        // lands (hit, and every 1 / hits round from there, a foot or hand each) on the tick the game beats: the tick its
        // beat verb's cool-down began. A beat is seen only as its cool-down is drawn, so the count of beats (which hand is
        // next) goes by those seen; the time between the last two sets the pace. Past the next beat's time it is held at
        // the blow, the foot or palm down, until the game beats again (or the job ends). Before a beat, or long after
        // the last, the clock.
        private static float BeatPhase(Pawn pawn, Ease e, string clip, float hit, int hits)
        {
            int now = Find.TickManager.TicksGame;
            Stance_Cooldown cooldown = pawn.stances != null ? pawn.stances.curStance as Stance_Cooldown : null;
            if (cooldown != null && cooldown.verb is Verb_BeatFire && cooldown.startedTick != e.beatTick)
            {
                int gap = cooldown.startedTick - e.beatTick;
                e.beatPeriod = gap > 0 && gap <= BeatLongest ? gap : 0;
                e.beatTick = cooldown.startedTick;
                e.beatCount++;
            }
            float seconds = skin.SecondsOf(clip);
            int since = now - e.beatTick;
            if (seconds <= 0f || since < 0 || since > BeatLongest)
            {
                return LoopPhase(pawn, clip);
            }
            float period = e.beatPeriod > 0 ? e.beatPeriod : seconds * 60f / hits;   // ticks a beat
            float done = Mathf.Min(since / period, 1f);
            float at = hit + ((e.beatCount - 1) % hits + done) / hits;
            return at - Mathf.Floor(at);
        }

        // Where a clock-timed clip (the idle, the drafted stance) is for this pawn, 0 .. 1 round it: game time, so it
        // stops when paused and keeps pace with the game speed, offset per pawn so a crowd does not breathe as one.
        private static float LoopPhase(Pawn pawn, string clip)
        {
            float seconds = skin.SecondsOf(clip);
            if (seconds <= 0f)
            {
                return 0f;
            }
            float t = Find.TickManager.TicksGame / 60f + (pawn.thingIDNumber % 997) * 1.618f;
            return Mathf.Repeat(t / seconds, 1f);
        }

        // Forgets pawns not drawn for a while.
        // Whether an awake, unfogged enemy is within ThreatRange of the pawn, looked at every ThreatCheck ticks.
        private static bool ThreatNear(Pawn pawn)
        {
            int now = Find.TickManager.TicksGame;
            Threat seen;
            if (threats.TryGetValue(pawn.thingIDNumber, out seen) && now - seen.tick < ThreatCheck && now >= seen.tick)
            {
                return seen.near;
            }
            seen.tick = now;
            seen.near = false;
            if (pawn.Spawned)
            {
                List<IAttackTarget> targets = pawn.Map.attackTargetsCache.GetPotentialTargetsFor(pawn);
                for (int i = 0; i < targets.Count && !seen.near; i++)
                {
                    Thing thing = targets[i].Thing;
                    seen.near = thing.Spawned && thing.Position.InHorDistOf(pawn.Position, ThreatRange) && !thing.Fogged()
                        && GenHostility.IsPotentialThreat(targets[i]);
                }
            }
            threats[pawn.thingIDNumber] = seen;
            return seen.near;
        }

        private struct Threat
        {
            public bool near;
            public int tick;   // the game tick it was looked at
        }
        private static readonly Dictionary<int, Threat> threats = new Dictionary<int, Threat>();

        private static void Prune(int frame)
        {
            if (threats.Count >= 400)
            {
                threats.Clear();   // only a cache: anyone still drafted looks again next time
            }
            if (eases.Count < 400)
            {
                return;
            }
            stale.Clear();
            foreach (KeyValuePair<int, Ease> pair in eases)
            {
                if (frame - pair.Value.frame > 600)
                {
                    stale.Add(pair.Key);
                }
            }
            for (int i = 0; i < stale.Count; i++)
            {
                eases.Remove(stale[i]);
            }
        }

        // Each garment the export has, for the worn apparel it is drawn for (by name), or else the first worn apparel with no
        // garment of its own that it stands in for (on its layer, covering one of its groups), or null; worn is reused.
        // Returns the garments found as a mask (bit g: garment g), for SkinnedFigure.HideUnder.
        private static GarmentMask WornGarments(Pawn pawn, Apparel[] worn)
        {
            int version;
            return WornGarments(pawn, worn, out version);
        }

        // The last answer for each pawn (by id), as working it out walks every garment of the export for each worn apparel,
        // every frame, though what a pawn wears rarely changes. It is worked out again when anything it goes by has changed:
        // the apparel worn (the things, in order), whether a shield shows, the shoes option, the baby romper and the model.
        private sealed class WornCache
        {
            public SkinnedFigure skin;
            public Apparel[] apparel = new Apparel[0];   // what was worn, in the game's order
            public bool shieldShows, shoes;
            public Apparel romper;
            public Apparel[] worn;   // the answer: the apparel each garment is drawn for
            public GarmentMask mask;
            public int version;   // new each time an answer is worked out (wornVersions), for HideGarments
        }
        private static readonly Dictionary<int, WornCache> wornCaches = new Dictionary<int, WornCache>();
        private static int wornVersions;
        private const int WornCacheMost = 1000;   // pawns kept at most; past it all are let go, as it is only a cache

        // The same, and which answer it is (version: the same number means the same mask and worn, so what follows from them
        // alone, SkinnedFigure.HideGarments, need not be worked out again).
        private static GarmentMask WornGarments(Pawn pawn, Apparel[] worn, out int version)
        {
            // In a vat (a growth vat, a biosculpter pod): no baby romper, and the clothes only with the vatClothes option.
            // In a bed, what the sleepWear option has it wear (SleepWear.cs): pajamas in the romper's place, or nothing.
            bool inVat = InVat(pawn);
            SleepWear sleep = inVat ? SleepWear.Blankets : SleepWearOf(pawn);
            List<Apparel> all = DressedIn(pawn, inVat, sleep);
            bool shieldShows = ShieldShows(pawn);
            bool shoes = Shoes;
            Apparel romper = inVat ? null : sleep == SleepWear.Pajamas ? PajamasOf(pawn) : BabyRomperOf(pawn, all);   // (Nothing keeps a baby's romper, as it would come and go)
            WornCache c;
            if (!wornCaches.TryGetValue(pawn.thingIDNumber, out c))
            {
                if (wornCaches.Count >= WornCacheMost)
                {
                    wornCaches.Clear();
                }
                c = new WornCache();
                wornCaches[pawn.thingIDNumber] = c;
            }
            else if (c.skin == skin && c.shieldShows == shieldShows && c.shoes == shoes && c.romper == romper
                && c.worn != null && c.worn.Length == worn.Length && SameApparel(c.apparel, all))
            {
                Array.Copy(c.worn, worn, worn.Length);
                version = c.version;
                return c.mask;
            }
            GarmentMask mask = WornGarmentsNow(pawn, all, worn, shieldShows, shoes, romper);
            c.skin = skin;
            c.shieldShows = shieldShows;
            c.shoes = shoes;
            c.romper = romper;
            int count = all != null ? all.Count : 0;
            if (c.apparel.Length != count)
            {
                c.apparel = new Apparel[count];
            }
            for (int i = 0; i < count; i++)
            {
                c.apparel[i] = all[i];
            }
            if (c.worn == null || c.worn.Length != worn.Length)
            {
                c.worn = new Apparel[worn.Length];
            }
            Array.Copy(worn, c.worn, worn.Length);
            c.mask = mask;
            c.version = ++wornVersions;
            version = c.version;
            return mask;
        }

        private static bool SameApparel(Apparel[] was, List<Apparel> now)
        {
            int count = now != null ? now.Count : 0;
            if (was.Length != count)
            {
                return false;
            }
            for (int i = 0; i < count; i++)
            {
                if (was[i] != now[i])
                {
                    return false;
                }
            }
            return true;
        }

        // Works WornGarments out (all: the worn apparel; romper: BabyRomperOf's, null for none).
        private static GarmentMask WornGarmentsNow(Pawn pawn, List<Apparel> all, Apparel[] worn, bool shieldShows, bool shoes, Apparel romper)
        {
            for (int g = 1; g < skin.Pieces; g++)
            {
                worn[g] = null;
            }
            for (int i = 0; all != null && i < all.Count; i++)
            {
                int g = skin.GarmentOf(all[i].def);
                if (g > 0 && worn[g] == null)
                {
                    worn[g] = all[i];
                }
            }
            for (int g = 1; g < skin.Pieces; g++)
            {
                for (int i = 0; worn[g] == null && all != null && i < all.Count; i++)
                {
                    if (skin.GarmentOf(all[i].def) < 0 && skin.Covers(g, all[i].def.apparel))
                    {
                        worn[g] = all[i];
                    }
                }
            }
            // A held garment (a shield) only while CE would draw it: drafted, or a job or duty that shows the weapon. One
            // carried on the back the rest of the time (CarriedOnBack) is always drawn, in the hand or on the back.
            for (int g = 1; !shieldShows && g < skin.Pieces; g++)
            {
                if (worn[g] != null && skin.IsHeld(g) && !skin.CarriedOnBack(g))
                {
                    worn[g] = null;
                }
            }
            // The shoes (with_legwear), as the game has no footwear: worn once the torso and the legs are both covered, by one
            // apparel or two (a shirt and trousers, a duster, a vacsuit), so not on a pawn in trousers alone or a shirt alone.
            // They go as the first apparel covering the legs, so body armour over it leaves them off with it, below.
            Apparel legs = null;
            bool torso = false;
            for (int i = 0; shoes && all != null && i < all.Count; i++)
            {
                List<BodyPartGroupDef> groups = all[i].def.apparel == null ? null : all[i].def.apparel.bodyPartGroups;
                if (groups == null)
                {
                    continue;
                }
                if (legs == null && groups.Contains(BodyPartGroupDefOf.Legs))
                {
                    legs = all[i];
                }
                if (groups.Contains(BodyPartGroupDefOf.Torso))
                {
                    torso = true;
                }
            }
            for (int g = 1; legs != null && torso && g < skin.Pieces; g++)
            {
                if (worn[g] == null && skin.WithLegwear(g))
                {
                    worn[g] = legs;
                }
            }
            // The baby romper, on a baby a few hours old with nothing over its trunk or legs (BabyRomperOf), or the pajamas
            // in bed (PajamasOf).
            bool pajamas = IsPajamas(romper);
            for (int g = 1; romper != null && g < skin.Pieces; g++)
            {
                if (worn[g] == null && (pajamas ? g == PajamaPiece() : skin.ForBaby(g)))
                {
                    worn[g] = romper;
                }
            }
            // Body armour leaves off what is worn under it, so a shirt can't show through.
            for (int g = 1; g < skin.Pieces; g++)
            {
                for (int h = 1; worn[g] != null && h < skin.Pieces; h++)
                {
                    if (h != g && worn[h] != null && worn[h] != worn[g] && skin.HidesClothes(g, worn[g].def.apparel, worn[h].def.apparel))
                    {
                        worn[h] = null;
                    }
                }
            }
            GarmentMask mask = 0;
            for (int g = 1; g <= SkinnedFigure.MaxHiding && g < skin.Pieces; g++)
            {
                if (worn[g] != null)
                {
                    mask |= GarmentBit(g);
                }
            }
            return mask;
        }

        public static bool ArmourHidesTail   // false: the gene tail is drawn through body armour (the mod's options set it)
        {
            get { return SettingsMod.Settings == null || SettingsMod.Settings.armourHidesTail; }
        }

        public static EarHiding EarHiding   // which headgear leaves the gene ears off (the mod's options set it)
        {
            get { return SettingsMod.Settings == null ? EarHiding.AnyHeadgear : SettingsMod.Settings.earHiding; }
        }

        public static bool Shoes   // false: the shoes are drawn only for apparel named for them, not once dressed (the mod's options set it)
        {
            get { return SettingsMod.Settings == null || SettingsMod.Settings.shoes; }
        }

        // The name the export's aim clips (aim_<name>, fire_<name>, a bow's nock_<name>) are under for the pawn's weapon: its
        // own (walk.AIMED: each weapon's hand turned so its own hold runs along the aim), else its kind's; null without
        // either (a melee weapon, a grenade, an export without aiming).
        private static string AimName(Pawn pawn)
        {
            ThingDef weapon = pawn.equipment != null && pawn.equipment.Primary != null ? pawn.equipment.Primary.def : null;
            if (weapon == null)
            {
                return null;
            }
            string name;
            if (!aimOf.TryGetValue(weapon, out name))
            {
                // A thrown weapon is thrown (StrikeOf), not aimed.
                name = Thrown(weapon) && skin.Has("throw_R") ? null : skin.Has("aim_" + WeaponName(weapon)) ? WeaponName(weapon) : skin.Has("aim_" + KindOf(weapon)) ? KindOf(weapon) : null;
                aimOf[weapon] = name;
            }
            // Drawn as its sprite's slab, the slab's aim (walk.SLAB_AIMED), held as it was before the holds were redone for
            // the models.
            if (name != null && AsSlab(pawn.equipment.Primary))
            {
                string slab;
                if (!slabAimOf.TryGetValue(weapon, out slab))
                {
                    slab = SlabName(weapon, "aim_", name);
                    slabAimOf[weapon] = slab;
                }
                return slab;
            }
            return name;
        }

        // How far up the pawn aims (degrees, down less than 0), as IsoRim pitches its own figure's weapon (Api.AimPitch: from
        // the hands, handsY cells over the floor, to the target's middle, floors between them as drawn).
        private static float AimPitchOf(Pawn pawn, float handsY)
        {
            return Api.AimPitch(pawn, handsY);
        }

        // How far into a shot's kick the pawn is (0 .. 1): all of it straight after it fired, easing out over the weapon's
        // recoil time (at least KickTicks), as strong as the weapon's recoil (KickLeast up to all of it); a bow's loose over
        // LooseTicks.
        private static float KickOf(Pawn pawn, bool bow)
        {
            Stance_Busy busy = pawn.stances != null ? pawn.stances.curStance as Stance_Busy : null;
            Verb verb = busy != null && busy.verb != null ? busy.verb
                : pawn.equipment != null && pawn.equipment.PrimaryEq != null ? pawn.equipment.PrimaryEq.PrimaryVerb : null;
            if (verb == null || pawn.equipment == null || pawn.equipment.Primary == null)
            {
                return 0f;
            }
            ThingDef def = pawn.equipment.Primary.def;
            float relax = bow ? LooseTicks : Mathf.Max(def.recoilRelaxation, KickTicks);
            float since = Find.TickManager.TicksGame - verb.LastShotTick;
            if (since < 0f || since >= relax)
            {
                return 0f;
            }
            float k = 1f - since / relax;
            return k * k * (bow ? 1f : Mathf.Clamp(def.recoilPower, KickLeast, 1f));
        }

        // How far a bow is drawn (0 nocked .. 1 at full draw): through the warm-up; between shots it is nocked again.
        private static float DrawOf(Pawn pawn)
        {
            Stance_Warmup warmup = pawn.stances != null ? pawn.stances.curStance as Stance_Warmup : null;
            if (warmup == null)
            {
                return 0f;
            }
            int done = Find.TickManager.TicksGame - warmup.startedTick;
            return Mathf.SmoothStep(0f, 1f, (float)done / Mathf.Max(1, done + warmup.ticksLeft));
        }

        // The blow or throw the pawn is in, while it fights hand to hand or throws (its stance is busy with such a verb):
        // the export's clip for it, how far through it the pawn is (0 .. 1, landing or letting go at StrikeHit, the guard
        // at both ends) and which way the target is (degrees clockwise from north). Null when it is doing neither, or the
        // export has no clip for it.
        //   A melee blow is played from the tick it landed (its Stance_Cooldown's startedTick): wound up and brought down over WindTicks,
        //   then through and back to the guard over RecoverTicks, and held at the guard for the rest of the cool-down. The
        //   game picks the next blow (a slash or a stab, the left fist or the right) only as it lands, so it cannot be
        //   wound up beforehand. By the blow's maneuver: melee_smash, melee_slash or melee_stab (the weapon's own, if it
        //   has them), or a fist (melee_fist_L, melee_fist_R) for one dealt with the body. A ranged weapon's by its kind
        //   (the weapon's own first): melee_jab_<kind> for a poke, else melee_butt_<kind>; one that is thrown is left to the
        //   aim. A blow at a target already down (LowBlows) takes the same clip's melee_down_ one.
        //   A throw (a grenade, a pilum: IsoRim's Projectiles.Thrown) is cocked back over the warm-up and let go as it
        //   ends, then followed through over ThrowRecover: throw_R for one held in the right hand, throw_pack (taken from
        //   the pack in the left hand and thrown with the right) for one held in the left.
        private static string StrikeOf(Pawn pawn, out float phase, out float? face)
        {
            phase = 0f;
            face = null;
            Stance_Busy busy = pawn.stances != null ? pawn.stances.curStance as Stance_Busy : null;
            Verb verb = busy != null ? busy.verb : null;
            if (verb == null)
            {
                return null;
            }
            int since = Find.TickManager.TicksGame - verb.LastShotTick;
            string clip;
            if (verb.IsMeleeAttack)
            {
                ThingWithComps source = verb.EquipmentSource;
                if (source == null)
                {
                    BodyPartGroupDef group = verb.tool != null ? verb.tool.linkedBodyPartsGroup : null;
                    clip = group != null && group.defName.Contains("Left") ? "melee_fist_L" : "melee_fist_R";
                }
                else if (source.def.IsMeleeWeapon)
                {
                    string m = verb.maneuver != null ? verb.maneuver.defName : null;
                    string kind = m == "Slash" ? "slash" : m == "Stab" || m == "Poke" ? "stab" : "smash";
                    string own = "melee_" + kind + "_" + WeaponName(source.def);
                    clip = skin.Has(own) ? own : "melee_" + kind;
                }
                else if (!Thrown(source.def))
                {
                    string blow = verb.maneuver != null && verb.maneuver.defName == "Poke" ? "jab" : "butt";
                    string own = "melee_" + blow + "_" + WeaponName(source.def);
                    clip = skin.Has(own) ? own : "melee_" + blow + "_" + KindOf(source.def);
                }
                else
                {
                    return null;
                }
                // The game sets LastShotTick only for ranged verbs, so a melee blow is timed from its cool-down, which
                // starts on the tick it lands (a melee verb has no warm-up).
                Stance_Cooldown cooldown = busy as Stance_Cooldown;
                if (cooldown == null)
                {
                    return null;
                }
                since = Find.TickManager.TicksGame - cooldown.startedTick;
                int lowTick;
                if (LowBlows.TryGetValue(pawn.thingIDNumber, out lowTick) && lowTick == cooldown.startedTick)
                {
                    string low = "melee_down_" + clip.Substring("melee_".Length);
                    clip = skin.Has(low) ? low : clip;
                }
                // A variant per blow, picked by the tick it lands on.
                clip = skin.Variant(clip, Gen.HashCombineInt(pawn.thingIDNumber, cooldown.startedTick));
                if (since >= 0 && since < WindTicks)
                {
                    phase = StrikeHit * since / WindTicks;
                }
                else if (since >= WindTicks && since < WindTicks + RecoverTicks)
                {
                    phase = StrikeHit + (1f - StrikeHit) * (since - WindTicks) / RecoverTicks;
                }
            }
            else
            {
                ThingWithComps weapon = pawn.equipment != null ? pawn.equipment.Primary : null;
                if (weapon == null || verb.EquipmentSource != weapon || !Thrown(weapon.def))
                {
                    return null;
                }
                SkinnedFigure.Hold hold = HoldOf(weapon);
                // One held in the left hand is a pack (grenades on an older export): the right takes one from it and
                // throws (throw_pack; an older export still throws with the left).
                clip = hold == null || hold.hand != "L" ? "throw_R" : skin.Has("throw_pack") ? "throw_pack" : "throw_L";
                Stance_Warmup warmup = busy as Stance_Warmup;
                if (warmup != null)
                {
                    int done = Find.TickManager.TicksGame - warmup.startedTick;
                    phase = StrikeHit * Mathf.Clamp01((float)done / Mathf.Max(1, done + warmup.ticksLeft));
                }
                else if (since >= 0 && since < ThrowRecover)
                {
                    phase = StrikeHit + (1f - StrikeHit) * since / ThrowRecover;
                }
            }
            if (!skin.Has(clip))
            {
                return null;
            }
            face = FaceOf(pawn, busy.focusTarg);
            return clip;
        }

        // How much of a blow or throw clip is laid on at phase (StrikeOf's): none at the guard, eased in over the wind-up
        // and out after the follow-through, so between blows each weapon keeps its own drafted stance. throw_pack is all
        // on by the time the hand is at the pack (full), so it reaches all the way there.
        private static float Swing(float phase, float full)
        {
            if (phase < full)
            {
                return Mathf.SmoothStep(0f, 1f, phase / full);
            }
            return phase <= StrikeThrough ? 1f : Mathf.SmoothStep(1f, 0f, (phase - StrikeThrough) / (1f - StrikeThrough));
        }

        // Lays the aim over the pose (SkinnedFigure.Over: the waist up) with weight w, or with whole adds all of it, legs
        // and all (SkinnedFigure.Add: standing still, its own stance): the frame for the pitch, a shot's kick on top, a
        // bow drawn from nocked over the warm-up.
        private static void LayAim(Pawn pawn, Ease ease, SkinnedFigure.Posed fig, float w, float size, bool whole)
        {
            string aim = "aim_" + ease.aimName, fire = "fire_" + ease.aimName, nock = "nock_" + ease.aimName;
            int frames = skin.FramesOf(aim);
            float reach = skin.AimPitch;
            float phase = 0f;
            if (frames > 1 && reach > 0f)
            {
                float pitch = Mathf.Clamp(AimPitchOf(pawn, AimHeight * size), -reach, reach);
                phase = (pitch + reach) / (2f * reach) * (frames - 1) / frames;   // never round past the top frame to the bottom
            }
            bool bow = skin.Has(nock);
            float kick = skin.Has(fire) ? KickOf(pawn, bow) : 0f;
            float drawn = bow ? DrawOf(pawn) : 1f;
            Lay(fig, aim, phase, w * (1f - kick) * drawn, whole);
            if (bow)
            {
                Lay(fig, nock, phase, w * (1f - kick) * (1f - drawn), whole);
            }
            if (kick > 0f)
            {
                Lay(fig, fire, phase, w * kick, whole);
            }
        }

        private static void Lay(SkinnedFigure.Posed fig, string clip, float phase, float w, bool whole)
        {
            if (whole)
            {
                skin.Add(fig, clip, phase, w);
            }
            else
            {
                skin.Over(fig, clip, phase, w);
            }
        }

        // Whether the game would draw the pawn's weapon now: aiming it, or carrying it openly (drafted, a job, duty or lord
        // that shows it), or reloading it. Not while it carries something, sheathed, or with IsoRim's held weapons turned off.
        private static bool WeaponShows(Pawn pawn)
        {
            if (HeldWeapon(pawn) == null || !HeldWeapons3D)
            {
                return false;
            }
            float game, drawn;
            return (AimOf(pawn, out game, out drawn) && !HipVerb(pawn)) || CarriesOpenly(pawn) || Reloading(pawn);
        }

        // The game's CarryWeaponOpenly, but an outsider at peace with the colony (a visitor, a trader's guard) shows its
        // weapon only when its own job calls for it or an enemy is near: the game gives them duties that always show it
        // (Defend, Escort), which would hold them in the drafted stance the whole visit.
        private static bool CarriesOpenly(Pawn pawn)
        {
            if (!PawnRenderUtility.CarryWeaponOpenly(pawn))
            {
                return false;
            }
            if (pawn.Drafted || pawn.Faction == Faction.OfPlayer || (pawn.CurJob != null && pawn.CurJob.def.alwaysShowWeapon)
                || (pawn.Faction != null && pawn.HostileTo(Faction.OfPlayer)))
            {
                return true;
            }
            return ThreatNear(pawn);
        }

        // How each stance kind holds its weapon when the export has no weapon lines (walk.WEAPONS as it was).
        private static readonly Dictionary<string, SkinnedFigure.Hold> oldHolds = new Dictionary<string, SkinnedFigure.Hold>
        {
            { "rifle", new SkinnedFigure.Hold { hand = "R", line = "hands", behind = 0.12f } },
            { "pistol", new SkinnedFigure.Hold { hand = "R", line = "forearm", behind = 0.15f, drop = 0.4f } },
            { "bow", new SkinnedFigure.Hold { hand = "L", line = "forearm" } },
            { "melee", new SkinnedFigure.Hold { hand = "R", line = "aim", behind = 0.38f, aim = new Vector3(0.45f, 1f, 0.4f) } },
        };

        // Where a hand closes on a weapon (model units): on from the wrist along the forearm (walk.HAND_GRIP).
        private static Vector3 FistOf(Vector3 wrist, Vector3 elbow)
        {
            return wrist + (wrist - elbow).normalized * (skin.HandGrip >= 0f ? skin.HandGrip : HandGrip);
        }

        // A weapon's top: straight up as far as its line lets it (its face to the front when it points straight up), then
        // turned roll degrees about the line.
        private static Vector3 TopOf(Vector3 a, float roll)
        {
            Vector3 top = Vector3.up - a * Vector3.Dot(Vector3.up, a);
            if (top.sqrMagnitude < 0.01f)
            {
                top = Vector3.forward - a * Vector3.Dot(Vector3.forward, a);
            }
            return (Quaternion.AngleAxis(roll, a) * top).normalized;
        }

        // The weapon in the chibi's hands, as IsoRim makes a held weapon: the game's own sprite as a solid slab
        // (SpriteSolids.DrawSlab), sized by WeaponScale. It runs along the line the stance's hold gives it (walk.WEAPONS,
        // SkinnedFigure.HoldOf): from the fist through the other, along its forearm, a fixed direction, or riding the hand
        // ("hand": set as it is with the hand at rest and turned as the hand turns, the fist along the hand, so a hand
        // turned in the stance aims it); the sprite's top (a gun's sights) kept up, and the game's
        // equippedAngleOffset (a sword or spear's tilt in its sprite) turned out first, as SpriteSolids.DrawHeld does.
        // Aiming, IsoRim takes the end of the barrel as drawn for where the shots start (Api.ReportMuzzle).
        private static void DrawWeapon(Pawn pawn, string stance, bool aiming, SkinnedFigure.Posed fig, Vector3 at, float yaw, float size, GarmentMask mask, Ease drawing)
        {
            ThingWithComps weapon = HeldWeapon(pawn);
            Graphic g = weapon.Graphic;
            Material mat = g != null ? g.MatSingleFor(weapon) : null;
            // The weapon's own hold (by its defName: a weapon posed as its kind is has a hold but no clip of its own), else
            // the stance's, else the weapon's kind's, else the rifle's.
            // Drawn as its sprite's slab, the slab's holds first (walk.SLAB_WEAPONS), and a slab stance named as its kind's.
            string kind = stance.StartsWith("drafted_") ? stance.Substring("drafted_".Length) : "rifle";
            if (kind.EndsWith(Slab))
            {
                kind = kind.Substring(0, kind.Length - Slab.Length);
            }
            bool slab = AsSlab(weapon);
            SkinnedFigure.Hold hold = HoldOf(WeaponName(weapon.def), slab) ?? HoldOf(kind, slab) ?? HoldOf(KindOf(weapon.def), slab) ?? skin.HoldOf("rifle");
            if (hold == null && !oldHolds.TryGetValue(kind, out hold))
            {
                hold = oldHolds["rifle"];
            }
            Vector3 wristR, elbowR, wristL, elbowL;
            if (mat == null || kind == "unarmed" || !skin.JointOf(fig, "hand.R", out wristR) || !skin.JointOf(fig, "forearm.R", out elbowR)
                || !skin.JointOf(fig, "hand.L", out wristL) || !skin.JointOf(fig, "forearm.L", out elbowL))
            {
                return;
            }
            bool left = hold.hand == "L";
            Vector3 wrist = left ? wristL : wristR, elbow = left ? elbowL : elbowR;
            Vector3 hand = FistOf(wrist, elbow);
            Matrix4x4 turn;
            Vector3 grip, a, top;
            if (hold.line == "hand" && skin.BoneOf(fig, left ? "hand.L" : "hand.R", out turn) && skin.GripOf(hold.hand, out grip))
            {
                hand = wrist + turn.MultiplyVector(grip).normalized * (skin.HandGrip >= 0f ? skin.HandGrip : HandGrip)
                    + turn.MultiplyVector(hold.at);
                Vector3 rest = (hold.aim.normalized + Vector3.down * hold.drop).normalized;
                a = turn.MultiplyVector(rest).normalized;
                top = turn.MultiplyVector(TopOf(rest, hold.roll));
                top = (top - a * Vector3.Dot(top, a)).normalized;
            }
            else
            {
                Vector3 along;
                if (hold.line == "hands")
                {
                    along = left ? FistOf(wristR, elbowR) - hand : FistOf(wristL, elbowL) - hand;
                }
                else if (hold.line == "forearm" || hold.line == "hand")   // "hand" from an export without weapon_grip
                {
                    along = wrist - elbow;
                }
                else
                {
                    along = hold.aim;
                }
                a = (along.normalized + Vector3.down * hold.drop).normalized;
                top = TopOf(a, hold.roll);
            }
            // Flat under fire: still in the hand, but brought round to lie along the ground the way the
            // figure faces, past its head. Whatever the hand's turn would make of this weapon's hold.
            float laid = drawing != null ? Mathf.SmoothStep(0f, 1f, (drawing.duck - WeaponLaidFrom) / (WeaponLaidTo - WeaponLaidFrom)) : 0f;
            if (laid > 0f)
            {
                Quaternion down = Quaternion.LookRotation(new Vector3(left ? -WeaponLaidOut : WeaponLaidOut, 0f, 1f), Vector3.up);
                Quaternion turned0 = Quaternion.Slerp(Quaternion.LookRotation(a, top), down, laid);
                a = turned0 * Vector3.forward;
                top = turned0 * Vector3.up;
            }
            // Then the whole weapon moved along the figure's own directions (walk.WEAPON_LIFT, _OUT, _FORWARD), so it keeps
            // its turn. Model space: the figure's right is +x, its front +z.
            Vector3 shift = skin.WeaponShift;
            hand += Quaternion.AngleAxis(fig.twist, Vector3.up) * (Vector3.up * shift.y + (left ? Vector3.left : Vector3.right) * shift.x + Vector3.forward * shift.z);
            float behind = hold.behind;
            // Coming out of its holster or going back: on its way from the slot to the hand, which closes the gap to whatever
            // the clip couldn't reach or wasn't made for (this weapon's own length, a coat, a backpack).
            float drawn = DrawnShare(drawing);
            int slot = drawn < 1f ? EquippedSlot(weapon.def, OnPackSides(pawn), OnBeltSlots(pawn)) : -1;
            if (slot >= 0)
            {
                Vector3 slotAt, slotAim, fromAim, fromTop;
                Matrix4x4 slotTurn;
                float slotRoll, slotBehind, slotSpin;
                skin.HolsterAt(fig, slot, mask, IsMelee(weapon.def), weapon.def.equippedAngleOffset, out slotAt, out slotTurn, out slotAim, out slotRoll, out slotBehind, out slotSpin, PackClear(weapon, slot, mask, fig, size));
                SlotLine(slotTurn, slotAim, slotRoll, slotSpin, out fromAim, out fromTop);
                Quaternion turned = Quaternion.Slerp(Quaternion.LookRotation(fromAim, fromTop), Quaternion.LookRotation(a, top), drawn);
                a = turned * Vector3.forward;
                top = turned * Vector3.up;
                hand = Vector3.Lerp(slotAt, hand, drawn);
                behind = Mathf.Lerp(slotBehind, behind, drawn);
            }
            Vector3 tip;
            Matrix4x4 world = DrawSlab(weapon, ScaleOf(weapon), hand, a, top, behind, at, yaw, size, out tip);
            Ease ease = null;
            if (world != Matrix4x4.zero && eases.TryGetValue(pawn.thingIDNumber, out ease))
            {
                // Kept for its flying off, if the pawn goes down with it (Flings).
                Quaternion turned = Quaternion.AngleAxis(yaw, Vector3.up);
                ease.heldAt = world.MultiplyPoint3x4(Vector3.zero);
                ease.heldAim = turned * a;
                ease.heldTop = turned * top;
                ease.heldFrame = Time.frameCount;
            }
            if (aiming && world != Matrix4x4.zero)
            {
                Vector2 end;
                if (float.IsNaN(tip.x) && WeaponFit.SpriteBarrelEnd(weapon, out end))
                {
                    tip = world.MultiplyPoint3x4(new Vector3(end.x - 0.5f, end.y - 0.5f, 0f));   // drawn as its slab: the sprite's
                }
                if (!float.IsNaN(tip.x))
                {
                    Api.ReportMuzzle(pawn, weapon, tip, world.MultiplyPoint3x4(Vector3.zero));
                }
                if (ease != null && MuzzleFlashes)
                {
                    DrawMuzzleFlash(pawn, weapon, ease, tip, Quaternion.AngleAxis(yaw, Vector3.up) * a);
                }
            }
        }

        // The way a figure on its feet is drawn facing. IsoRim's tracker turns it at a set rate in real time, which at high
        // game speed or with a quick weapon leaves it facing away as the shot goes. So while it aims and stands still this
        // keeps a way of its own, turned as the tracker turns it unless that is too slow to get there before the shot
        // (ShotIn, ReadyIn), or by TurnLate if it is already firing; afterward it goes back to the tracker's as that catches up.
        private static float TurnedFor(Pawn pawn, Ease ease, float yaw, float? face, bool aiming, bool upright)
        {
            bool still = pawn.pather == null || !pawn.pather.MovingNow;
            float shot = aiming && upright && still ? ShotIn(pawn, null) : -1f;
            if (!upright || Time.frameCount - ease.stepFrame > 2 || (shot < 0f && !ease.turnYaw.HasValue))
            {
                ease.turnYaw = null;
                return yaw;
            }
            float turned = ease.turnYaw ?? yaw;
            float to = face ?? yaw;
            float step = TrackTurn * Time.deltaTime;
            if (shot >= 0f)
            {
                float game = Time.deltaTime * Find.TickManager.TickRateMultiplier;
                step = Mathf.Max(step, Mathf.Abs(Mathf.DeltaAngle(turned, to)) * game / (shot > 0f ? ReadyIn(shot) : TurnLate));
            }
            turned = Mathf.MoveTowardsAngle(turned, to, step);
            if (shot < 0f && Mathf.Abs(Mathf.DeltaAngle(turned, yaw)) < 1f)
            {
                ease.turnYaw = null;
                return yaw;
            }
            ease.turnYaw = turned;
            return turned;
        }

        // Standing, the way its feet face, which is drawn as its yaw; the trunk is turned from there to yaw, the way the
        // tracker has it face (its aim, a foe, its work), by twist (SkinnedFigure.Twist). The feet stay planted while the trunk
        // turns up to StepSlack, or has held its way for StepSettle, then step round (the walk played on the spot, StepTurn a
        // step) until it faces them again; past TwistMost they are swung round with it. On the move they come round to face the way it goes; anything but
        // standing faces the way it does.
        private static float StepOf(Ease ease, float yaw, bool upright, float gait, out float twist)
        {
            int frame = Time.frameCount;
            bool fresh = frame - ease.stepFrame <= 2;
            ease.stepFrame = frame;
            if (!upright || !fresh || !ease.footYaw.HasValue)
            {
                ease.footYaw = yaw;
                ease.stepYaw = yaw;
                ease.stepHeld = 0f;
                ease.stepping = false;
                ease.step = 0f;
                twist = 0f;
                return yaw;
            }
            float dt = Time.deltaTime;
            float feet = ease.footYaw.Value;
            float off = Mathf.Abs(Mathf.DeltaAngle(feet, yaw));
            bool still = gait < 0.05f;
            ease.stepHeld = Mathf.Abs(Mathf.DeltaAngle(ease.stepYaw, yaw)) <= StepHeld * dt ? ease.stepHeld + dt : 0f;
            ease.stepYaw = yaw;
            if (!still || off > StepSlack || (ease.stepHeld > StepSettle && off > 2f * StepDone))
            {
                ease.stepping = true;
            }
            else if (off < StepDone)
            {
                ease.stepping = false;
            }
            float before = feet;
            if (ease.stepping)
            {
                feet = Mathf.MoveTowardsAngle(feet, yaw, (still ? StepSpeed : 4f * StepSpeed) * dt);
            }
            float left = Mathf.DeltaAngle(feet, yaw);
            if (Mathf.Abs(left) > TwistMost)
            {
                feet = yaw - Mathf.Sign(left) * TwistMost;
            }
            if (still)
            {
                ease.stepCycle = Mathf.Repeat(ease.stepCycle + Mathf.Abs(Mathf.DeltaAngle(before, feet)) / StepTurn * 0.5f, 1f);
            }
            ease.step = Mathf.MoveTowards(ease.step, ease.stepping && still ? 1f : 0f, StepEase * dt);
            ease.footYaw = feet;
            twist = Mathf.DeltaAngle(feet, yaw);
            return feet;
        }

        private static readonly Vector3 NoTip = new Vector3(float.NaN, 0f, 0f);

        // The drawn size of a weapon held by IsoRim's figures, of its sprite's (FigureWeapons' picture: its figure's height over
        // the game's), which Api.DrawHeldThing's scale is times.
        private const float HandPicture = 1.25f / 1.125f;

        // A weapon's sprite as IsoRim's solid slab (Api.DrawHeldThing), scale (ScaleOf) of the sprite's drawn size:
        // running along a from hand, its middle behind (a share of its length) on along a, its top toward
        // top, the def's equippedAngleOffset turned out first. A weapon with an IsoRim model draws that instead, laid
        // into the slab as WeaponFit fits it to the sprite's outline (IsoRim's fit kept level), so the holds made for the
        // sprites still put it in the hands; tip is then its barrel's end (true cells), else NoTip. Returns where the
        // slab went (true cells), zero if it has no sprite. onFigure: drawn with the figure being drawn, the model sharing its
        // outline (SkinnedFigure.DrawJoined).
        private static Matrix4x4 DrawSlab(Thing weapon, float scale, Vector3 hand, Vector3 a, Vector3 top, float behind,
            Vector3 at, float yaw, float size, out Vector3 tip, bool onFigure = true)
        {
            tip = NoTip;
            Graphic g = weapon.Graphic;
            Material mat = g != null ? g.MatSingleFor(weapon) : null;
            if (mat == null)
            {
                return Matrix4x4.zero;
            }
            float o = weapon.def.equippedAngleOffset * Mathf.Deg2Rad, c = Mathf.Cos(o), sn = Mathf.Sin(o);
            // The figure's size moves the hands but not the weapon: it is as big (and as thick) in every pawn's hands as in
            // a grown one's, so a child holds a whole rifle. Its axes are divided back out of the size the matrix below
            // scales everything by, so its scale is a share of the size the game draws it at on the floor and its
            // thickness is IsoRim's slab's own.
            float unsize = 1f / size;
            Vector2 draw = g.drawSize * scale * unsize;
            Vector3 x = (a * c - top * sn) * draw.x, y = (a * sn + top * c) * draw.y;
            Vector3 middle = hand + a * (behind * Mathf.Max(draw.x, draw.y));
            Matrix4x4 slab = new Matrix4x4(x, y, Vector3.Cross(x, y).normalized * unsize, new Vector4(middle.x, middle.y, middle.z, 1f));
            Matrix4x4 world = Matrix4x4.Translate(at) * Matrix4x4.Rotate(Quaternion.AngleAxis(yaw, Vector3.up))
                * Matrix4x4.Scale(new Vector3(size, size, size)) * slab;   // true cells
            if (DrawModel(weapon, mat, g.drawSize, scale, world, onFigure, out tip))
            {
                return world;
            }
            // IsoRim's slab of the sprite: it sizes the picture by HandPicture times its scale (and the slab's thickness by as
            // much), which is undone here, so it comes out as world places it.
            if (scale > 1e-4f)
            {
                Matrix4x4 picture = world * Matrix4x4.Scale(new Vector3(1f / (scale * g.drawSize.x), 1f / (scale * g.drawSize.y), 1f / scale));
                Api.DrawHeldThing(weapon, picture, scale / HandPicture);
            }
            return world;
        }

        // The weapon's IsoRim model in the slab world places (true cells), if it has one. The slab's thickness is IsoRim's
        // own, but the model is fitted to a slab as thick as a cell at the size its sprite is drawn, so that axis is
        // scaled as the sprite is.
        private static bool DrawModel(Thing weapon, Material mat, Vector2 drawSize, float scale, Matrix4x4 world, bool onFigure, out Vector3 tip)
        {
            tip = NoTip;
            ThingModelInfo info;
            WeaponFit.Hold h;
            if (!HeldModel(weapon, out info, out h))
            {
                if (Prefs.DevMode && slabNoted.Add(weapon.def))
                {
                    Log.Message(Tag + "retextured weapons: " + weapon.def.defName + " held as its sprite: "
                        + (info.Model == null ? "IsoRim gives no model (how " + (Api.HowBound(weapon.def) ?? "-") + ")" : "its model doesn't fit the sprite") + ".");
                }
                return false;
            }
            if (Thrown(weapon.def))
            {
                h = WeaponFit.AtGroundSize(h, info.Model, GroundScaleOf(weapon), drawSize);   // one of the heap, not the heap's size
            }
            Matrix4x4 placed = world * Matrix4x4.Scale(new Vector3(1f, 1f, scale));
            Matrix4x4 model = placed * h.slab;
            if (Vector3.Dot(Vector3.Cross(model.GetColumn(0), model.GetColumn(1)), model.GetColumn(2)) < 0f)
            {
                model = model * Matrix4x4.Scale(new Vector3(1f, 1f, -1f));   // (a mirrored placing: its far side out, still solid)
            }
            if (!onFigure || !SkinnedFigure.DrawJoined(info.Model, info.Tint, info.Swatch, model))
            {
                Api.DrawModel(info.Model, Vector3.zero, 0f, model, info.Tint, info.Swatch);
            }
            tip = placed.MultiplyPoint3x4(h.tip);
            return true;
        }

        private static void DrawGarments(Pawn pawn, Apparel[] worn, SkinnedFigure.Posed fig, Vector3 at, float yaw, float size)
        {
            for (int g = 1; g < skin.Pieces; g++)
            {
                if (worn[g] != null)
                {
                    Color colour = !skin.Tinted(g, worn[g]) ? Color.white : Api.ApparelColour(worn[g]);
                    Color? own = skin.ColourOf(g);
                    if (own.HasValue)
                    {
                        colour = own.Value;   // the shoes, worn with legwear of any colour
                    }
                    else if (IsPajamas(worn[g]))
                    {
                        colour = PajamaColourOf(pawn);   // each pawn's own pastel, not the stand-in's cloth
                    }
                    else if (worn[g].WornByCorpse && skin.Tinted(g, worn[g]) && TakesItsColour(worn[g]))
                    {
                        // Once its wearer dies, the game's DrawColor for tainted clothing is its rotten colour (three
                        // quarters a dark brown), which came out nearly black on the garment: drawn as it was worn instead.
                        colour = worn[g].GetColorIgnoringTainted();
                    }
                    skin.Draw(fig, g, at, yaw, size, colour);
                }
            }
        }

        // True if the apparel is shown in its own colour (DrawColor) rather than its sprite's mean, as IsoRim's
        // People.ApparelColour decides: made from stuff, or dyed.
        private static bool TakesItsColour(Apparel a)
        {
            CompColorable dye = a.TryGetComp<CompColorable>();
            return a.def.MadeFromStuff || (dye != null && dye.Active);
        }

        private static Apparel[] wornBuffer;

        // The posed figure's body in its skin colour (rotting, if dead), the garments for what it wears, its hair, wounds
        // and face.
        private static GarmentMask DrawDressed(Pawn pawn, SkinnedFigure.Posed fig, Vector3 at, float yaw, float size, float sat = 0f, float tailFloor = float.NegativeInfinity, bool tailOnGround = false)
        {
            if (wornBuffer == null || wornBuffer.Length != skin.Pieces)
            {
                wornBuffer = new Apparel[skin.Pieces];
            }
            SkinnedFigure.NextFigure();   // its own queues, so its outline isn't hidden by another chibi's body
            fig.flash = HitFlash.Of(pawn, drawing != null && drawing.ForOtherMod);   // the game's red damage flash, on what it is drawn in (DrawMeshes, the face's layers)
            FaceLayers.Flash = fig.flash;
            Color skinColour = SkinOf(pawn);
            int wornVersion;
            GarmentMask mask = WornGarments(pawn, wornBuffer, out wornVersion);
            skin.Wearing(fig, mask, WearsGear(pawn, "pack"), PackScaleOf(size), PackScaleOf(size) * (skin.BorrowsPack(mask, wornBuffer) ? KidPackSize : 1f));
            if (skin.CarriesShield(mask))
            {
                skin.Shielding(fig, ShieldOutOf(pawn), ShieldOverWeapons(pawn, mask, fig, size));
            }
            // A full helmet or armour that hides the head leaves the hair (and its scalp colour) or the face off, as does
            // having no head at all (CutLimbs has cut it off). A baby has none. A hat only cuts it (DrawHair).
            bool headless = pawn.health != null && !pawn.health.hediffSet.HasHead;
            HairDef hair = skin.HidesHair(mask) || headless || Baby(pawn) ? null : pawn.story.hairDef;
            skin.HairUnderHats(fig, hair, mask);
            skin.HideUnder(fig, mask, hair);
            string earFile = headless ? null : EarFileOf(pawn);
            skin.HideGarments(fig, mask, wornBuffer, wornVersion);
            skin.DrawBody(fig, hair, at, yaw, size, skinColour, pawn.story.HairColor);
            int fur = FurOf(pawn);
            if (fur > 0)
            {
                // In the skin's colour, which is the hair's on this gene (skinIsHairColor), so its roots melt into the skin.
                skin.HideFur(fig, fur, mask, hair);
                skin.Draw(fig, fur, at, yaw, size, skinColour, true);
            }
            DrawGarments(pawn, wornBuffer, fig, at, yaw, size);
            skin.DrawHair(fig, hair, mask, at, yaw, size, pawn.story.HairColor, skinColour);
            string ridgeDesign = headless ? null : RidgesOf(pawn);
            if (ridgeDesign != null && !skin.HidesFace(mask, wornBuffer))
            {
                skin.DrawRidges(fig, ridgeDesign, at, yaw, size, skinColour);
            }
            // The Nose_Pig key's nostrils, drawn as the ridges are: over the lid cover, and clear of the mouth (generationAssets/pigSnout).
            if (!headless && HasGeneKey(pawn, NosePig) && !skin.HidesFace(mask, wornBuffer))
            {
                skin.DrawRidges(fig, NoseDesign, at, yaw, size, skinColour);
            }
            BeardDef beard = headless ? null : BeardOf(pawn, mask);
            if (beard != null && FlatBeards)
            {
                // Drawn on the face patch with the face's layers, so a mask that leaves the face off leaves it off too.
                if (!skin.HidesFace(mask, wornBuffer))
                {
                    skin.DrawFlatBeard(fig, beard, at, yaw, size, pawn.story.HairColor);
                }
            }
            else if (beard != null)
            {
                skin.DrawBeard(fig, beard, at, yaw, size, pawn.story.HairColor);
            }
            if (earFile != null)
            {
                DrawGeneEars(pawn, fig, earFile, mask, wornBuffer, at, yaw, size, skinColour);
            }
            string horns = headless ? null : HornsOf(pawn);
            if (horns != null && !skin.HidesEars(mask))
            {
                skin.DrawHorns(fig, horns, at, yaw, size);   // through the hair, but not through a hat
            }
            if (HasCatTail(pawn) && !fig.tucked)
            {
                DrawCatTail(pawn, fig, mask, wornBuffer, at, yaw, size, sat, tailFloor, tailOnGround);
            }
            Wounds.Draw(pawn, skin, fig, mask, wornBuffer, at, yaw, size, skinColour);
            if (!skin.HidesFace(mask, wornBuffer) && !headless)
            {
                FaceLayers.Draw(pawn, fig.face, at, yaw, size, Wounds.EyeWoundsOn(fig), HasGeneKey(pawn, NosePig));
            }
            FaceLayers.Flash = 0f;
            return mask;
        }

        // The pawn's beard, or null if it has none or it is left off: for a baby, under what hides the face, and under
        // what SkinnedFigure.HidesBeard says.
        private static BeardDef BeardOf(Pawn pawn, GarmentMask mask)
        {
            BeardDef beard = pawn.style != null ? pawn.style.beardDef : null;
            if (!Beards || beard == null || beard.noGraphic || Baby(pawn) || skin.HidesFace(mask, wornBuffer))
            {
                return null;
            }
            bool inVat = InVat(pawn);
            List<Apparel> worn = DressedIn(pawn, inVat, inVat ? SleepWear.Blankets : SleepWearOf(pawn));
            if (worn != null)
            {
                for (int i = 0; i < worn.Count; i++)
                {
                    if (SkinnedFigure.HidesBeard(worn[i].def))
                    {
                        return null;
                    }
                }
            }
            return beard;
        }

        private static GeneDef furGene;
        private static bool furGeneLooked;

        // The piece to draw for the pawn's active Furskin gene (Biotech), or -1.
        private static int FurOf(Pawn pawn)
        {
            return HasFurskin(pawn) ? skin.FurPiece : -1;
        }

        // Whether the pawn has the Furskin gene (Biotech) active.
        public static bool HasFurskin(Pawn pawn)
        {
            if (!furGeneLooked)
            {
                furGene = DefDatabase<GeneDef>.GetNamedSilentFail("Furskin");
                furGeneLooked = true;
            }
            return furGene != null && pawn.genes != null && pawn.genes.HasActiveGene(furGene);
        }

        // Setting a person down: the game puts them on the ground in a tick, so they are still drawn in the carrier's arms
        // (DrawLoad's hold, released) through the bend down and while the hands stay on the ground, up to BendHold of
        // it, and only then left lying where the game put them. At most DropTicks, in case the bend is held up (seated).
        private static bool Dropping(Ease e)
        {
            return e.dropped != 0 && e.bent < BendHold && Find.TickManager.TicksGame - e.droppedTick <= DropTicks;
        }

        // What the pawn carries, as IsoRim draws it (Api.DrawCarried): a thing between the hands, its bottom on them and its
        // middle at least LoadReach in front. A person this mod draws is drawn here (DrawHeld), on the carrier's chest in its
        // carried_<kind> loop (holds): IsoRim would have the game draw them from inside its call, which comes back to the
        // figure drawer while this one is busy, and IsoRim then draws its own figure.
        private static void DrawLoad(Pawn pawn, Thing load, Ease ease, SkinnedFigure.Posed fig, Vector3 at, float yaw, float size)
        {
            Vector3 wristR, wristL;
            if (!skin.JointOf(fig, "hand.R", out wristR) || !skin.JointOf(fig, "hand.L", out wristL))
            {
                return;
            }
            Pawn held = HeldPawn(load);
            ease.holding = held != null && Handles(held) && HoldPerson(pawn, held.thingIDNumber, ease, fig, at, yaw, size, false) ? held.thingIDNumber : 0;
            Vector3 hands = (wristR + wristL) * 0.5f;
            hands.z = Mathf.Max(hands.z, LoadReach);
            // A cuddled pet sits along the left forearm (cuddle_pet), its head to the carrier's left, its trunk's middle
            // there (IsoRim's backAt, from its base point, as last drawn).
            Vector3 elbowL = Vector3.zero;
            bool pet = ease.carryClip == "carry_cuddle" && ease.carry >= 0.5f && held != null && !Handles(held) && Cuddling(pawn, load)
                && skin.JointOf(fig, "forearm.L", out elbowL);
            if (pet)
            {
                hands = Vector3.Lerp(elbowL, wristL, skin.PetAlong);
                hands.y += skin.PetLift;
                held.Rotation = Rot4.FromAngleFlat(yaw - 90f);
            }
            // Eating standing, the meal sits on the left palm (between the wrist and the fingers' joint, lifted from the
            // hand's bone to the palm's face, which eat_stand turns up) rather than where the game would hold it out (its
            // ingestHoldOffsetStanding), which IsoRim would otherwise put it at; turned with the hand about the vertical.
            Vector3 fingers;
            float handTurn = 0f;
            // Feeding a patient with the taming clips, the meal is on the right palm, held out to them palm up.
            bool feeding = held == null && FeedingFromHand(pawn, ease);
            // A pill or powder is held on the left palm the same way (the pill clip pinches from it).
            eatenInHand = feeding || (ease.eatClip == "eat_stand" || ease.eatClip == "pill") && ease.eat >= 0.5f;
            if (eatenInHand)
            {
                Vector3 wrist = feeding ? wristR : wristL;
                if (skin.JointOf(fig, feeding ? "hand.R.001" : "hand.L.001", out fingers))
                {
                    hands = (wrist + fingers) * 0.5f;
                    // The model space faces +z, so this is the turn that takes straight ahead onto the fingers' way.
                    Vector3 way = fingers - wrist;
                    if (way.x * way.x + way.z * way.z > 1e-8f)
                    {
                        handTurn = Mathf.Atan2(way.x, way.z) * Mathf.Rad2Deg;
                    }
                }
                else
                {
                    hands = wrist;
                }
                hands.y += PalmLift;
            }
            try
            {
                Vector3 hold = at + Quaternion.AngleAxis(yaw, Vector3.up) * (hands * size);
                if (pet)
                {
                    hold.y -= PetBackOf(held) * PetBelly;   // the forearm under its belly, not its feet on it
                }
                Hold person;
                if (held == null && ease.eatClip == "read" && ease.eat >= 0.5f && DrawBook(pawn, load, fig, at, yaw, size))
                {
                    // (the open book, on the left hand)
                }
                else if (held == null && ease.eatClip == "smoke" && ease.eat >= 0.5f && DrawJoint(fig, at, yaw, size))
                {
                    // (the joint, across the right hand's fingers)
                }
                else if (held == null && (feeding || ease.eatClip != null && ease.eat >= 0.5f))
                {
                    DrawMeal(pawn, load, hold, yaw, eatenInHand ? handTurn : 0f);
                }
                else if (ease.holding != 0 && holds.TryGetValue(ease.holding, out person))
                {
                    DrawHeld(held, person);
                }
                else if (pet && Api.DrawFigure(held, hold, yaw - 90f, Matrix4x4.identity, false))
                {
                    // (the pet, facing exactly the carrier's left: drawn by the game, it faces its Rotation, a Rot4, so it
                    // was up to 45 degrees off the forearm and flipped a quarter turn as the carrier turned past a diagonal)
                }
                else
                {
                    Api.DrawCarried(pawn, hold, yaw);
                }
            }
            finally
            {
                eatenInHand = false;
            }
        }

        // Feeding a patient with the taming clips (FeedAsTame): at it, the hand held out to them.
        private static bool FeedingFromHand(Pawn pawn, Ease ease)
        {
            return ease.work >= 0.5f && (ease.workKind == "tame" || ease.workKind == "tame_kneel") && pawn.CurJobDef == JobDefOf.FeedPatient;
        }

        // A meal being eaten, drawn as IsoRim draws a carried item (on the table where the game's job puts it, else in the
        // hand), as it shows items on the ground (Api.DrawItem: its model, else its sprite made solid), but MealScale of its
        // size (FoodScale if it isn't a meal) and turned MealTurn degrees about the vertical, plus turn (degrees, the hand
        // holding it).
        private static void DrawMeal(Pawn pawn, Thing meal, Vector3 hold, float yaw, float turn)
        {
            try
            {
                Vector3 vanilla = pawn.DrawPos;
                bool flip = false;
                bool onTable = false;
                if (pawn.jobs != null && pawn.jobs.curDriver != null && pawn.jobs.curDriver.ModifyCarriedThingDrawPos(ref vanilla, ref flip))
                {
                    hold = new Vector3(vanilla.x, hold.y, vanilla.z);
                    IntVec3 c = vanilla.ToIntVec3();
                    Map map = pawn.Map;
                    Building table = map != null && c.InBounds(map) ? c.GetEdifice(map) : null;
                    if (table != null && c != pawn.Position)
                    {
                        hold.y = Api.SurfaceHeight(table);
                        // The game puts it in the middle of the table's cell, out of the hands' reach (eat_sit eats from the
                        // near edge): moved toward the eater's cell, which is beside it, to MealFromEdge in from that edge.
                        IntVec3 toward = pawn.Position - c;
                        if (toward.x * toward.x + toward.z * toward.z == 1)
                        {
                            hold.x = c.x + 0.5f + toward.x * (0.5f - MealFromEdge);
                            hold.z = c.z + 0.5f + toward.z * (0.5f - MealFromEdge);
                            onTable = true;
                        }
                    }
                }
                float share = meal.def.IsWithinCategory(DefDatabase<ThingCategoryDef>.GetNamedSilentFail("FoodMeals")) ? MealScale : FoodScale;
                if (!Api.DrawItem(meal, hold, yaw + (onTable ? MealTableTurn : MealTurn) + turn, CarriedItemScale * share))
                {
                    Api.DrawCarried(pawn, hold, yaw);
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce(Tag + "Drawing a meal being eaten failed: " + e, 0x4d3a7e01);
            }
        }

        // The open book being read, on the left hand as the read clip holds it (its `tool read` line, walk.read_rest): IsoRim's
        // open book, map or schematic model (as its own figures hold one, Models/open_*.obj), in the book sprite's colour,
        // sized with the pawn as the hand is. False (drawn the usual way) for a closed book or without the line or model.
        private static bool DrawBook(Pawn pawn, Thing load, SkinnedFigure.Posed fig, Vector3 at, float yaw, float size)
        {
            Book book = load as Book;
            SkinnedFigure.Tool tool = book != null && book.IsOpen ? skin.ToolOf("read") : null;
            Matrix4x4 hand;
            if (tool == null || !skin.BoneOf(fig, tool.bone, out hand))
            {
                return false;
            }
            string name = book.def.defName == "Map" ? "open_map" : book.def.defName == "Schematic" ? "open_schematic" : tool.model;
            ModelHandle model = ToolModel(name);
            if (model == null)
            {
                return false;
            }
            try
            {
                Api.DrawModel(model, at, yaw, Matrix4x4.Scale(new Vector3(size, size, size)) * hand * tool.place, Api.SpriteColour(book));
            }
            catch (Exception e)
            {
                Log.ErrorOnce(Tag + "Drawing a book being read failed: " + e, 0x4d3a7e02);
            }
            return true;
        }

        // A joint being smoked: this mod's Models/tool_joint.obj (anim_scripts/make_joint.py) by the export's `tool smoke`
        // line on the right hand, sized with the pawn, in place of the drug's own picture (a bundle of them). False if the
        // export has no such line or the model isn't there.
        private static bool DrawJoint(SkinnedFigure.Posed fig, Vector3 at, float yaw, float size)
        {
            SkinnedFigure.Tool tool = skin.ToolOf("smoke");
            Matrix4x4 hand;
            if (tool == null || !skin.BoneOf(fig, tool.bone, out hand))
            {
                return false;
            }
            ModelHandle model = ToolModel(tool.model);
            if (model == null)
            {
                return false;
            }
            try
            {
                Api.DrawModel(model, at, yaw, Matrix4x4.Scale(new Vector3(size, size, size)) * hand * tool.place, Color.white);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(Tag + "Drawing a joint being smoked failed: " + e, 0x4d3a7e03);
            }
            return true;
        }

        private const string RolledJointModel = "tool_joint_unlit";   // the joint being rolled (anim_scripts/make_joint.py)
        private const float RolledJointAhead = 0.03f;   // cells (at a grown pawn's size) it lies in front of the wrists' middle
        private const float RolledJointLap = 0.04f;     // cells under the wrists' middle it is held, sat working on the lap
        private const float RolledJointInset = 0.08f;   // cells in from the bench's edge its middle lies at least
        private const float RolledJointRadius = 0.008f; // cells: the model's mean radius, so it lies on the table, not in it
        internal static int rolledJointFrame = -1;   // the frame a joint being rolled was last drawn, and where (the autotest's rolljoint)
        internal static Vector3 rolledJointAt;
        private static readonly Dictionary<RecipeDef, bool> rollsJoints = new Dictionary<RecipeDef, bool>();

        // A bill making a joint (Make_SmokeleafJoint, or a mod's: a product smoked, DrugClipOf, named a joint), cached.
        private static bool RollsJoint(RecipeDef recipe)
        {
            bool rolls;
            if (!rollsJoints.TryGetValue(recipe, out rolls))
            {
                rolls = false;
                if (recipe.products != null)
                {
                    foreach (ThingDefCountClass product in recipe.products)
                    {
                        ThingDef def = product.thingDef;
                        if (def != null && def.defName.IndexOf("Joint", StringComparison.OrdinalIgnoreCase) >= 0 && DrugClipOf(def) == "smoke")
                        {
                            rolls = true;
                        }
                    }
                }
                rollsJoints[recipe] = rolls;
            }
            return rolls;
        }

        // The joint being rolled while a bill makes one sat at it (at the drug lab or a crafting spot): an unlit joint lying
        // across in front of the worker's hands, on the bench top under them (sat at the drug lab) or in the hands (sat at a
        // crafting spot on a chair). Not stood or knelt at it, where the game's picture of the leaves lies on the spot. Only during the recipe's work itself (the driver's
        // workLeft), not while it fetches the leaves. lift: the floor's height under the figure (IsoRim's placing).
        private static void DrawRolledJoint(Pawn pawn, Ease ease, SkinnedFigure.Posed fig, Vector3 at, float yaw, float size, float lift)
        {
            Job job = pawn.CurJob;
            JobDriver_DoBill driver = pawn.jobs != null ? pawn.jobs.curDriver as JobDriver_DoBill : null;
            // (seated only: stood or knelt at it, the game's own picture of the leaves lies where the joint would)
            if (ease.workKind != "bench_sit" && ease.workKind != "chair")
            {
                return;
            }
            if (job == null || driver == null || driver.workLeft <= 0f || job.bill == null || job.bill.recipe == null || !RollsJoint(job.bill.recipe))
            {
                return;
            }
            Vector3 wristL, wristR;
            if (!skin.JointOf(fig, "hand.L", out wristL) || !skin.JointOf(fig, "hand.R", out wristR))
            {
                return;
            }
            ModelHandle model = ToolModel(RolledJointModel);
            if (model == null)
            {
                return;
            }
            try
            {
                Vector3 hands = 0.5f * (wristL + wristR) * size + Vector3.forward * (RolledJointAhead * size);
                if (ease.workKind == "chair")
                {
                    // On the lap, no table: in the hands.
                    hands.y -= RolledJointLap * size;
                }
                else
                {
                    // On the bench's top (the bill giver's), on its near edge: sat back on the chair, the hands are over
                    // the pawn's own cell, short of the bench.
                    Thing bench = job.targetA.Thing;
                    if (bench == null || !bench.Spawned)
                    {
                        return;
                    }
                    Quaternion turn = Quaternion.AngleAxis(yaw, Vector3.up);
                    Vector3 world = at + turn * hands;
                    CellRect rect = bench.OccupiedRect();
                    world.x = Mathf.Clamp(world.x, rect.minX + RolledJointInset, rect.maxX + 1f - RolledJointInset);
                    world.z = Mathf.Clamp(world.z, rect.minZ + RolledJointInset, rect.maxZ + 1f - RolledJointInset);
                    hands = Quaternion.Inverse(turn) * (world - at);
                    hands.y = lift + Mathf.Max(0f, Api.SurfaceHeight(bench)) - at.y + RolledJointRadius * size;
                }
                // Lying across in front of it (the model runs up +y from its filter end): its middle at hands, its length
                // along the figure's left-right.
                Matrix4x4 local = Matrix4x4.Translate(hands) * Matrix4x4.Scale(new Vector3(size, size, size))
                    * Matrix4x4.Rotate(Quaternion.Euler(0f, 0f, -90f)) * Matrix4x4.Translate(new Vector3(0f, -0.05f, 0f));
                Api.DrawModel(model, at, yaw, local, Color.white);
                rolledJointFrame = Time.frameCount;
                rolledJointAt = at + Quaternion.AngleAxis(yaw, Vector3.up) * hands;
            }
            catch (Exception e)
            {
                Log.ErrorOnce(Tag + "Drawing a joint being rolled failed: " + e, 0x4d3a7e04);
            }
        }

        private static bool eatenInHand;   // DrawLoad is drawing a meal in the hand: JobDriver_Ingest is not to move it (HeldPrefix)

        // JobDriver_Ingest.ModifyCarriedThingDrawPos: false (the thing is drawn where it is held) while DrawLoad draws a meal
        // eaten standing in the hand.
        public static bool HeldPrefix(ref bool __result)
        {
            if (!eatenInHand)
            {
                return true;
            }
            __result = false;
            return false;
        }

        // Tells a carried person (by thingIDNumber) where to be drawn this frame: on the carrier's chest as posed, in the
        // carried loop for its carry loop. False if the export has no such loop.
        private static bool HoldPerson(Pawn carrier, int id, Ease ease, SkinnedFigure.Posed fig, Vector3 at, float yaw, float size, bool released)
        {
            Matrix4x4 chest;
            string carried = ease.carryClip != null && ease.carryClip.StartsWith("carry_") ? "carried_" + ease.carryClip.Substring("carry_".Length) : null;
            if (carried == null || !skin.Has(carried) || !skin.BoneOf(fig, CarryBone, out chest))
            {
                return false;
            }
            float play = carried == "carried_cradle" && skin.Has("carried_play") ? ease.play : 0f;
            holds[id] = new Hold { frame = Time.frameCount, clip = carried, phase = LoopPhase(carrier, ease.carryClip), play = play,
                playPhase = play > 0f ? LoopPhase(carrier, "carry_play") : 0f, at = at, yaw = yaw, size = size, chest = chest, released = released };
            if (holds.Count > 200)
            {
                PruneHolds();
            }
            return true;
        }

        private static void PruneHolds()
        {
            stale.Clear();
            foreach (KeyValuePair<int, Hold> pair in holds)
            {
                if (pair.Value.frame < Time.frameCount - 1)
                {
                    stale.Add(pair.Key);
                }
            }
            for (int i = 0; i < stale.Count; i++)
            {
                holds.Remove(stale[i]);
            }
        }

        // A carried person (or corpse), in its carrier's arms or over its shoulder (Hold): the carried loop put on the
        // carrier's chest as posed, scaled about its hips by how much bigger or smaller it is than the carrier (a baby in an
        // adult's arms), so the hips stay where the arms hold them.
        private static bool DrawHeld(Pawn pawn, Hold hold)
        {
            try
            {
                float size = SizeOf(pawn);
                SkinnedFigure.Shape shape = ShapeFor(pawn);
                SkinnedFigure.Posed fig = skin.For(pawn.thingIDNumber, shape);
                fig.poseEveryDraw = true;
                skin.Begin(fig);
                CutLimbs(pawn, fig);
                skin.Add(fig, hold.clip, pawn.Dead ? 0f : hold.phase, 1f - hold.play);
                skin.Add(fig, "carried_play", hold.playPhase, hold.play);
                skin.End(fig, HeldOn(hold.chest, size / hold.size, hold.clip));
                DrawDressed(pawn, fig, hold.at, hold.yaw, size);
            }
            catch (Exception e)
            {
                Log.Error(Tag + "drawing a carried chibi failed, IsoRim's figures are kept: " + e);
                skin = null;
                ready = false;
                return false;
            }
            return true;
        }

        // A corpse hung in a gibbet cage, where IsoRim's placing puts it (the cage's middle, a yaw of its own, the cage floor):
        // slumped where it stands (walk.caged_pose: the knees buckled, the back curled, the head hung, the arms down), leant
        // as IsoRim leans its own figure there, its feet on the cage floor. No taller than
        // IsoRim's figure (placed.Size, already shrunk by its GibbetScale to stay under the cage's dome), which the
        // chibi's ScaleUp would otherwise put through it.
        private static bool DrawCaged(FigureDrawContext ctx, FigurePlacement placed, float size)
        {
            Pawn pawn = ctx.Pawn;
            size = Mathf.Min(size * GibbetScale, placed.Size);
            if (!ctx.ForOtherMod && !SkinnedFigure.MaybeOnScreen(placed.Position, size))
            {
                return true;
            }
            try
            {
                SkinnedFigure.Shape shape = ShapeFor(pawn);
                SkinnedFigure.Posed fig = skin.For(pawn.thingIDNumber, shape);
                skin.Begin(fig);
                CutLimbs(pawn, fig);
                if (skin.Has("caged"))
                {
                    skin.Add(fig, "caged", 0f, 1f);
                }
                else
                {
                    // An export without the caged pose: part way to the ragdoll's, whose arms are held out for lying.
                    float slump = skin.Has("ragdoll") ? GibbetSlump : 0f;
                    skin.Add(fig, "idle", 0f, 1f - slump);
                    skin.Add(fig, "ragdoll", 0f, slump);
                }
                skin.End(fig, Matrix4x4.Rotate(Quaternion.AngleAxis(GibbetLean, Vector3.right)));
                float lowest, bodyTop;
                skin.Extent(fig, out lowest, out bodyTop);
                Vector3 at = new Vector3(placed.Position.x, placed.Position.y - lowest * size, placed.Position.z);
                DrawDressed(pawn, fig, at, placed.Yaw, size);
            }
            catch (Exception e)
            {
                Log.Error(Tag + "drawing a chibi in a gibbet cage failed, IsoRim's figures are kept: " + e);
                skin = null;
                ready = false;
                return false;
            }
            return true;
        }

        // The glass over the couch of a building that shows who is inside, as IsoRim's glass model of it has it: in the
        // building's own space (x across, z along, as built facing north), an elliptic tube (or arch) along z, centred
        // centre over the building's floor, across and up its inner half widths, along its inner half length, and
        // surface the couch's height (IsoRim's surfaceHeight), all cells. Measured from growth_vat_glass.obj and
        // biosculpter_pod_glass.obj in IsoRim's Models.
        private struct GlassTube
        {
            public float across, up, centre, along, surface;
            public GlassTube(float across, float up, float centre, float along, float surface)
            {
                this.across = across;
                this.up = up;
                this.centre = centre;
                this.along = along;
                this.surface = surface;
            }
        }

        private static readonly Dictionary<string, GlassTube> Glasses = new Dictionary<string, GlassTube>
        {
            { "GrowthVat", new GlassTube(0.34f, 0.26f, 0.45f, 0.8f, 0.26f) },
            { "BiosculpterPod", new GlassTube(0.515f, 0.515f, 0.34f, 0.7f, 0.30f) },
        };

        private const float GlassClear = 0.03f;   // cells kept under the glass: the hair and the outline stand off the body
        private const float GlassMostDown = 0.2f;   // cells, the most a figure is let down into the couch

        // Whether the pawn lies in a vat drawn with glass over it (a growth vat, a biosculpter pod: Glasses), where it is
        // drawn with no baby romper and, unless the vatClothes option is on, no clothes.
        private static bool InVat(Pawn pawn)
        {
            Thing holder = pawn.ParentHolder is ThingComp comp ? comp.parent : pawn.ParentHolder as Thing;
            return holder != null && Glasses.ContainsKey(holder.def.defName);
        }

        // The last fit per pawn (how far it was let down, and slid along toward its feet), for the autotest.
        internal static readonly Dictionary<int, Vector2> glassFits = new Dictionary<int, Vector2>();

        // The move (cells) keeping a figure lying on a couch under glass (a growth vat, a biosculpter pod) inside it, as a big
        // one's head went through the glass: let down only as far as its highest point needs, never more than GlassMostDown,
        // and slid along the glass to bring a head (or feet) past its end back inside, as far as the other end has room
        // (centred if it is longer than the glass). A small one is left where it was. The body as posed at at, yaw and size.
        private static Vector3 UnderGlass(Pawn pawn, SkinnedFigure.Posed fig, Vector3 at, float yaw, float size, float floor)
        {
            Thing holder = pawn.ParentHolder is ThingComp comp ? comp.parent : pawn.ParentHolder as Thing;
            GlassTube glass;
            if (holder == null || !holder.Spawned || !Glasses.TryGetValue(holder.def.defName, out glass))
            {
                return Vector3.zero;
            }
            Vector3[] verts = skin.BodyVerts(fig);
            Vector3 middle = holder.TrueCenter();
            Quaternion turn = Quaternion.AngleAxis(yaw, Vector3.up);
            Quaternion back = Quaternion.Inverse(holder.Rotation.AsQuat);
            float ground = floor - glass.surface;
            float half = glass.across - GlassClear, up = glass.up - GlassClear, room = glass.along - GlassClear;
            float need = 0f, first = float.MaxValue, last = float.MinValue;
            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 world = at + turn * (verts[i] * size);
                Vector3 local = back * (world - middle);
                first = Mathf.Min(first, local.z);
                last = Mathf.Max(last, local.z);
                float across = local.x / half;
                if (across * across >= 1f)
                {
                    continue;   // out past the glass's sides: letting it down would not help
                }
                need = Mathf.Max(need, world.y - ground - up * Mathf.Sqrt(1f - across * across) - glass.centre);
            }
            float slide = 0f;
            if (last - first > 2f * room)
            {
                slide = -0.5f * (first + last);
            }
            else if (last > room)
            {
                slide = room - last;
            }
            else if (first < -room)
            {
                slide = -room - first;
            }
            float down = Mathf.Min(need, GlassMostDown);
            glassFits[pawn.thingIDNumber] = new Vector2(down, slide);
            return holder.Rotation.AsQuat * new Vector3(0f, 0f, slide) + Vector3.down * down;
        }

        private const float GibbetScale = 0.78f;  // IsoRim's: a corpse in a gibbet cage shrunk to stay under its dome
        private const float GibbetSlump = 0.6f;   // of the way from standing to the ragdoll pose (DrawCaged, without the caged pose)
        private const float GibbetLean = 5f;      // degrees forward about x, as IsoRim's figure hangs

        // The whole move of a carried figure (DrawHeld): its carried loop on the carrier's chest as posed, scaled about its
        // hips by k, its size over the carrier's, and that scale undone as a whole, as it is drawn at its own size.
        private static Matrix4x4 HeldOn(Matrix4x4 chest, float k, string clip)
        {
            Vector3 hips = skin.RootOf(clip);
            return Matrix4x4.Scale(Vector3.one / k) * chest * Matrix4x4.Translate(hips) * Matrix4x4.Scale(Vector3.one * k) * Matrix4x4.Translate(-hips);
        }

        private const float CradleScale = 0.31f;   // a cradled baby's size against its carrier the export placed the hands for (walk.CRADLE_SCALE)
        private static readonly string[] GripSides = { "R", "L" };

        // A person carried in the arms or over the shoulder: each of the carrier's hands put on them where the export's (and
        // the preview's) hand holds them. The carry loop's hands were placed on a carried figure of the base shape at the
        // carrier's size, so a smaller, bigger or differently built person, or a carrier of another build, would otherwise
        // be held in the air or through the body. The carried figure is posed here as DrawHeld will pose it (its shape,
        // scaled about its hips), and the arms bent to it by IK, between Chain and Finish.
        private static void GripCarried(Pawn pawn, Ease ease, SkinnedFigure.Posed fig, Matrix4x4 whole, float size)
        {
            Thing load = pawn.carryTracker != null ? pawn.carryTracker.CarriedThing : null;
            Pawn held = load != null ? HeldPawn(load) : null;
            string carryClip = ease.carryClip;
            float play = carryClip == "carry_cradle" ? ease.play : 0f;
            float w = ease.carry * (1f - play);
            if (held == null || w <= 0f || carryClip == null || !carryClip.StartsWith("carry_") || !Handles(held))
            {
                return;
            }
            string carried = "carried_" + carryClip.Substring("carry_".Length);
            Matrix4x4 chest;
            if (!skin.Has(carried) || !skin.BoneOf(fig, CarryBone, out chest))
            {
                return;
            }
            float phase = LoopPhase(pawn, carryClip);
            float k = SizeOf(held) / size;
            SkinnedFigure.Posed other = skin.For(held.thingIDNumber, ShapeFor(held));
            skin.Begin(other);
            skin.Add(other, carried, held.Dead ? 0f : phase, 1f);
            skin.Chain(other, HeldOn(chest, k, carried));
            bool reached = false;
            for (int s = 0; s < GripSides.Length; s++)
            {
                Vector3 target;
                if (skin.GripOf(carryClip, carried, carried == "carried_cradle" ? CradleScale : 1f, CarryBone, phase, GripSides[s], other, out target))
                {
                    skin.Reach(fig, whole, GripSides[s], target * k, w);   // into the carrier's units: the carried figure is drawn at its own size
                    reached = true;
                }
            }
            if (reached)
            {
                skin.Reached(fig, whole);
            }
        }

        // A cuddled pet's back is where IsoRim draws it (PetBackOf), not where the clips laid the right hand (anim.txt's
        // cuddle_pet): the hand raised or let down by the difference, at most PetBackMost (model units), between Chain and
        // Finish.
        private const float PetBackMost = 0.3f;
        private const float PetBelly = 0.5f;   // of the height of its back, how far a cuddled pet is sunk below the forearm (DrawLoad)
        private const float PetBackShare = 0.45f;   // of the height of its model (to its ears), the top of its back (a cat's, from the autotest's pictures)

        // How high a carried animal's back is drawn over its feet, cells: IsoRim's model height (Api.ModelHeight, grown)
        // at the size IsoRim draws it for its life stage (Animals.SizeOf), PetBackShare of it. Api.ReadHost would give it,
        // but only for a spawned thing, and a carried animal isn't. 0 without a model.
        private static float PetBackOf(Pawn animal)
        {
            float top = Api.ModelHeight(animal.def);
            float factor = animal.ageTracker != null && animal.ageTracker.CurLifeStage != null ? animal.ageTracker.CurLifeStage.bodySizeFactor : 1f;
            return top * Mathf.Lerp(0.5f, 1f, Mathf.Clamp01(factor)) * PetBackShare;
        }

        private static void StrokePet(Pawn pawn, Ease ease, SkinnedFigure.Posed fig, Matrix4x4 whole, float size)
        {
            Thing load = pawn.carryTracker != null ? pawn.carryTracker.CarriedThing : null;
            if (ease.carryClip != "carry_cuddle" || ease.carry <= 0f || load == null || !Cuddling(pawn, load) || size <= 0f)
            {
                return;
            }
            float back = PetBackOf((Pawn)load);
            if (back <= 0f)
            {
                return;
            }
            float by = Mathf.Clamp(back * (1f - PetBelly) / size - skin.PetBack, -PetBackMost, PetBackMost);
            skin.ReachBy(fig, whole, "R", new Vector3(0f, by, 0f), ease.carry);
            skin.Reached(fig, whole);
        }

        // The figure drawer (Api.AddFigureDrawer): asked for each person IsoRim would draw as a figure, after IsoRim's own
        // gates (the 3D view and its options, hidden pawns, races drawn as cards, statues); true when the chibi drew it, and
        // IsoRim then draws nothing of it. Until our materials can be made (IsoRim makes its own once the 3D view is up), or
        // without them, IsoRim draws its own.
        private static bool DrawFigure(FigureDrawContext ctx)
        {
            Pawn pawn = ctx.Pawn;
            if (!Handles(pawn) || !Ready() || (skin != null && !SkinnedFigure.MaterialsReady))
            {
                return false;
            }
#if AUTOTEST
            // The perf test (AutoTest's Perf): 1 leaves the figure to IsoRim, as without this mod; 2 draws no figure at all.
            if (PerfMode == 1 || PerfMode == 0 && PerfAllowed != null && !PerfAllowed.Contains(pawn.thingIDNumber))
            {
                return false;
            }
            if (PerfMode == 2)
            {
                return true;
            }
            long perfFrom = System.Diagnostics.Stopwatch.GetTimestamp();
            PerfDraws++;
#endif
            FigureDrawContext was = drawing;
            drawing = ctx;
            try
            {
                return skin == null ? DrawBaked(ctx) : DrawSkinned(ctx);
            }
            finally
            {
                drawing = was;
#if AUTOTEST
                if (was == null)
                {
                    PerfTicks += System.Diagnostics.Stopwatch.GetTimestamp() - perfFrom;   // (a carried figure drawn inside its carrier's is in the carrier's)
                }
#endif
            }
        }

        private static FigureDrawContext drawing;   // the figure drawer's call being drawn, null outside it

        // The aim each pawn's placing last gave (FigurePlacement.Aiming, AimGame, AimDrawn), by thingIDNumber, for what goes
        // by it before this frame's placing is read (EaseOf, WeaponShows): IsoRim's aim, a frame behind at most, which the
        // eases over it hide.
        private struct AimSeen
        {
            public int frame;
            public bool aiming;
            public float game, drawn;
        }

        private static readonly Dictionary<int, AimSeen> aimsSeen = new Dictionary<int, AimSeen>();

        // Whether the pawn is aiming as IsoRim's figure would (its stance busy with a ranged verb, aimed at the target as drawn),
        // as its last placing said (within the last couple of frames: else not).
        private static bool AimOf(Pawn pawn, out float aimGame, out float aimDrawn)
        {
            AimSeen seen;
            if (pawn != null && aimsSeen.TryGetValue(pawn.thingIDNumber, out seen) && Time.frameCount - seen.frame <= 2 && seen.aiming)
            {
                aimGame = seen.game;
                aimDrawn = seen.drawn;
                return true;
            }
            aimGame = aimDrawn = 0f;
            return false;
        }

        private static void SawAim(Pawn pawn, FigurePlacement p)
        {
            aimsSeen[pawn.thingIDNumber] = new AimSeen { frame = Time.frameCount, aiming = p.Aiming, game = p.AimGame, drawn = p.AimDrawn };
            if (aimsSeen.Count > 400)
            {
                stale.Clear();
                foreach (KeyValuePair<int, AimSeen> pair in aimsSeen)
                {
                    if (Time.frameCount - pair.Value.frame > 600)
                    {
                        stale.Add(pair.Key);
                    }
                }
                for (int i = 0; i < stale.Count; i++)
                {
                    aimsSeen.Remove(stale[i]);
                }
            }
        }

        // The skinned chibi: true when drawn.
        // IsoRim's blanket over a sleeper whose bed shows only the head (Api 68, Api.DrawSleeperBlanket at our own placing;
        // IsoRim draws it only for its own figure). at is the head's middle, as IsoRim places its figure in a bed, ground
        // the bed's surface (or the sleeper's slot), size the figure's. With an older IsoRim there is none, and sleepers lie
        // on top of the bed.
        //
        // The blanket is IsoRim's lump made for its own figure, whose jaw is 0.14 of its size from the head's middle. The
        // chibi's head is bigger (0.2 from its middle to the jaw), so the lump starts at the base of the chibi's neck (BlanketNeck,
        // as the neck is drawn, SkinnedFigure.TuckFrom) rather than under IsoRim's jaw. Its size (IsoRim's scale: the lump made as for a body that many times bigger, taller, longer
        // and wider, the width kept within the bed's) is the sleeper's own (BlanketScaleOf): the body as it lies under it,
        // measured in its shape (SkinnedFigure.UnderBlanket), so a fat, hulking or pregnant sleeper has a bigger lump than a
        // thin one, a child's its own size.
        private const int BlanketApi = 68;
        private const float BlanketPeak = 0.255f;   // IsoRim's lump at scale 1: its height over the bed at the chest, cells (0.15 x its Height 1.7)
        private const float BlanketHalf = 0.34f;    // and its widest half width, cells, before the dip at its sides
        private const float BlanketRoom = 0.03f;    // the lump this much over the body's highest (model units: cells at a grown pawn's size)
        private const float BlanketSides = 1.5f;    // its half width this many times the body's, as the lump's sides slope down to the bed
        private const float BlanketLeast = 0.6f, BlanketMost = 2.5f;
        private const float BlanketStep = 0.05f;    // the scale rounded to this, so IsoRim builds few lumps
        private const string BlanketNeck = "spine.004";   // its joint is the neck's base
        private const float BlanketSnap = 0.03f;   // cells the neck may move along the bed (breathing) before the lump follows it, and is built again

        private static void DrawTuckedIn(Pawn pawn, Building_Bed bed, SkinnedFigure.Posed fig, Ease ease, Vector3 at, float yaw, float size,
            Vector3 placedAt, float placedYaw, float ground)
        {
            Vector3 along = Quaternion.AngleAxis(placedYaw, Vector3.up) * Vector3.forward;   // toward the feet, as IsoRim lays it
            Vector3 neck;
            if (skin.JointOf(fig, "spine.006", out neck))
            {
                ease.headAlong = Vector3.Dot(at + Quaternion.AngleAxis(yaw, Vector3.up) * (neck * size) - placedAt, along);
            }
            if (skin.JointOf(fig, BlanketNeck, out neck))
            {
                Vector3 world = at + Quaternion.AngleAxis(yaw, Vector3.up) * (neck * size);
                float d = Vector3.Dot(world - placedAt, along);
                if (float.IsNaN(ease.blanketAlong) || Mathf.Abs(d - ease.blanketAlong) > BlanketSnap)
                {
                    ease.blanketAlong = Mathf.Round(d * 100f) / 100f;
                }
            }
            // Where it starts, cells from the head's middle toward the feet (-1: IsoRim's own, under its jaw).
            float start = float.IsNaN(ease.blanketAlong) ? -1f : ease.blanketAlong;
            if (ease.blanketShape != fig.shape || float.IsNaN(ease.blanketScale))
            {
                ease.blanketShape = fig.shape;
                ease.blanketScale = BlanketScaleOf(fig, along, yaw, out ease.blanketDepth, out ease.blanketWidth);
            }
            if (Api.DrawSleeperBlanket(pawn, bed, placedAt, placedYaw, size, ground, start, ease.blanketScale))
            {
                ease.blanketFrame = Time.frameCount;
            }
        }

        // A small sleeper (a baby, a toddler) moved down the bed under the blanket. The game lays every sleeper's head at the
        // same place on the pillow, and a small body's neck is then still on it: the blanket, which starts at the neck, lies
        // under the pillow and the bed's own turned-down sheet there, and the head looks cut off from the lump by a strip of
        // bare bed. Only the head is drawn (BedTuck.cs), so the whole figure is moved toward the feet until its neck is no
        // higher up the bed than TuckNeckFrom, about where a grown sleeper's is; grown sleepers are not moved. Cells along
        // the bed toward the feet; ease.tuckNeck keeps the neck's place before the move (for AutoTest), with TuckSnap of slack
        // so breathing doesn't rock the head.
        private const float TuckNeckFrom = 0.6f;   // cells from the bed's head end (IsoRim's bed: the pillow ends at 0.48, its turned-down sheet from 0.62)
        private const float TuckSnap = 0.02f;

        private static Vector3 TuckedDown(Building_Bed bed, SkinnedFigure.Posed fig, Ease ease, Vector3 at, float yaw, float size, float placedYaw)
        {
            Vector3 neck;
            if (!skin.JointOf(fig, BlanketNeck, out neck))
            {
                return Vector3.zero;
            }
            Vector3 toHead = Quaternion.AngleAxis(placedYaw, Vector3.up) * Vector3.back;   // IsoRim's bed yaw runs from the head toward the feet
            Vector3 headEnd = bed.TrueCenter() + toHead * (bed.def.size.z * 0.5f);
            Vector3 world = at + Quaternion.AngleAxis(yaw, Vector3.up) * (neck * size);
            float from = Vector3.Dot(headEnd - world, toHead);   // the neck's distance down the bed from its head end
            if (float.IsNaN(ease.tuckNeck) || Mathf.Abs(from - ease.tuckNeck) > TuckSnap)
            {
                ease.tuckNeck = Mathf.Round(from * 100f) / 100f;
            }
            ease.tuckShift = Mathf.Max(0f, TuckNeckFrom - ease.tuckNeck);
            return -toHead * ease.tuckShift;
        }

        // The blanket's scale for this sleeper's body as it lies (model units, so the pawn's size, which scales both the body
        // and IsoRim's lump, drops out): tall enough to be BlanketRoom over its highest and wide enough to cover it with its
        // sloping sides, whichever wants more. Measured once per shape (the body keys: build, pregnancy, age), not per breath.
        private static float BlanketScaleOf(SkinnedFigure.Posed fig, Vector3 along, float yaw, out float depth, out float halfWidth)
        {
            Vector3 across = Quaternion.Inverse(Quaternion.AngleAxis(yaw, Vector3.up)) * Vector3.Cross(Vector3.up, along);
            if (!skin.UnderBlanket(fig, across.normalized, out depth, out halfWidth))
            {
                return -1f;   // IsoRim's own
            }
            float tall = (depth + BlanketRoom) / BlanketPeak;
            float wide = halfWidth * BlanketSides / BlanketHalf;
            float scale = Mathf.Clamp(Mathf.Max(tall, wide), BlanketLeast, BlanketMost);
            return Mathf.Round(scale / BlanketStep) * BlanketStep;
        }

        // Lovin under the blanket (the sleepWear option's Blankets): the two lie side by side in a double bed, a cell apart, staring up.
        // Each is drawn LovinCloser across the bed toward the other, head and lump together, its head turned LovinLook
        // toward them, so they lie close facing each other. LovinOf: which side of the figure the partner is (+1 its +x,
        // which a look of + degrees turns the face toward, as the lying clips are laid down about x), scaled by how far
        // they have settled in (both lying in the same bed); 0 for anyone else.
        private const float LovinCloser = 0.17f;   // cells each head moves toward the other's (the slots are a cell apart)
        private const float LovinLook = 65f;   // degrees the head turns toward the partner, within LookMost
        private const float LovinEase = 0.4f;   // cells a second the shift comes in or goes out
        private const float LovinSnap = 0.02f;   // cells: the shift taken up in these steps, so IsoRim rebuilds the lump only a few times

        private static float LovinOf(Pawn pawn, FigurePlacement placed, float yaw)
        {
            if (!BedBlankets || !BlanketReady() || placed.Situation != FigureSituation.InBed || !placed.HeadOnlyInBed)
            {
                return 0f;
            }
            return LovinSideOf(pawn, placed, yaw);
        }

        // Lovin with no blanket over the bed (the sleepWear option not Blankets, a bed that shows the body, or an IsoRim
        // without the blanket): the two lie on their sides facing each other (lovin_L, lovin_R: still, breathing, from the
        // downed side lying), each drawn LovinSideCloser across the bed toward the other. The partner's side as LovinOf
        // gives it; 0 for anyone else, or where the blanket is drawn.
        private const float LovinSideCloser = 0.04f;   // cells each moves toward the other (the slots are a cell apart)

        private static float LovinBareOf(Pawn pawn, FigurePlacement placed, float yaw)
        {
            if (placed.Situation != FigureSituation.InBed || BedBlankets && BlanketReady() && placed.HeadOnlyInBed || !skin.Has("lovin_L") || !skin.Has("lovin_R"))
            {
                return 0f;
            }
            return LovinSideOf(pawn, placed, yaw);
        }

        // Which side of the figure its partner at lovin (or Romance on the Rim's pillow talk) lies, both in the same bed:
        // +1 its +x, -1 its -x, 0 none.
        private static float LovinSideOf(Pawn pawn, FigurePlacement placed, float yaw)
        {
            Job job = pawn.CurJob;
            // (Romance on the Rim's pillow talk the same: its partner target B)
            Pawn partner = job == null ? null : job.def == JobDefOf.Lovin ? job.targetA.Pawn : job.def.defName == "RomanceOnTheRim_PillowTalk" ? job.targetB.Pawn : null;
            Building_Bed bed = pawn.CurrentBed();
            if (partner == null || bed == null || partner.CurrentBed() != bed || partner.GetPosture() == PawnPosture.Standing)
            {
                return 0f;
            }
            Vector3 local = Quaternion.Inverse(Quaternion.AngleAxis(yaw, Vector3.up)) * (partner.DrawPos - pawn.DrawPos);
            return Mathf.Abs(local.x) < 0.05f ? 0f : Mathf.Sign(local.x);
        }

        // Whether this IsoRim has the blanket to draw.
        private static bool BlanketReady()
        {
            return Api.Version >= BlanketApi;
        }

        private static bool DrawSkinned(FigureDrawContext ctx)
        {
            Pawn pawn = ctx.Pawn;
            Vector3 drawLoc = ctx.DrawLoc;
            Hold hold;
            // Just set down, they are drawn with the rest of the map, before or after the carrier, so last frame's place in
            // its arms will do. (Carried, the carrier draws them itself, DrawLoad.)
            if (holds.TryGetValue(pawn.thingIDNumber, out hold) && (hold.frame == Time.frameCount || hold.released && hold.frame == Time.frameCount - 1))
            {
                return DrawHeld(pawn, hold);
            }
            float size = SizeOf(pawn);
            bool dead = pawn.Dead;
            float flight = skin.Has("jump") ? FlightOf(pawn) : -1f;
            // Hunkered down: flat on its front (duck_), posed where it lies, though the game has it lying. Putting out a fire
            // on itself the same way, rolling over (roll), though the game has it standing.
            bool hunkering = !dead && !pawn.Downed && (skin.Has("duck_R") && Hunkering(pawn) || skin.Has("roll") && Rolling(pawn));
            bool lying = !hunkering && (dead || pawn.GetPosture() != PawnPosture.Standing);
            bool crawling = lying && !dead && pawn.Crawling;
            // Sitting, as IsoRim has it (before the placing is read, for the facing only: the placing says).
            bool seated = !lying && !hunkering && SeatUnder(pawn) != null;
            // Where it is between the walk (0) and the full run (groundPerCycle.Length - 1); the stride follows smoothly.
            Ease ease = EaseOf(pawn);
            float level = ease.run * (groundPerCycle.Length - 1);
            int below = Mathf.Min(Mathf.FloorToInt(level), groundPerCycle.Length - 1);
            int above = Mathf.Min(below + 1, groundPerCycle.Length - 1);
            // The stride's shares: the gait, the limp in place of it, the wade in place of both (by how deep the water is,
            // up to shallow), and swimming in place of them all; the stride handed to the tracker follows them.
            float swim = !lying && skin.Has("swim") && skin.Has("tread") ? ease.swim : 0f;
            // How far down flat under fire (CE's hunker, or rolling to put out a fire). Gone down for good from there (shot,
            // or downed), it goes limp from where it lay if it can (below), and stays lying the way it went down.
            bool canDrop = !lying && flight < 0f && swim <= 0f;
            float wasDucked = ease.duck;
            float duck = canDrop && (hunkering || ease.duck > 0f) ? DuckOf(pawn, ease, hunkering) : 0f;
            ease.duck = duck;
            float wade = skin.Has("wade") ? Mathf.Clamp01(ease.depth / Shallow) : 0f;
            float limp = ease.limpClip != null && skin.Has(ease.limpClip) ? ease.limp : 0f;
            float ground = Mathf.Lerp(groundPerCycle[below], groundPerCycle[above], level - below);
            float favouring = limp > 0f && ease.limpClip.StartsWith("hop_", StringComparison.Ordinal) ? hopGround : limpGround;
            ground = crawling ? crawlGround : Mathf.Lerp(Mathf.Lerp(Mathf.Lerp(ground, favouring, limp), wadeGround, wade), swimGround, swim);
            // Fighting hand to hand, it faces its foe, and casting its target; at work, it turns to face the work, as IsoRim's
            // figure does. Aiming, it turns to face the aim as drawn, which IsoRim's placing does by itself (no face given);
            // whether it aims is known from the last placing until this one is read. Between attacks the game drops the aim
            // for a few ticks and faces a drafted pawn south, which would swing the figure round and back, so it keeps facing
            // its last aim for AimHold.
            int now = Find.TickManager.TicksGame;
            float aimedGame, aimedDrawn;
            bool aimedBefore = !dead && !lying && !ease.hipUsing && AimOf(pawn, out aimedGame, out aimedDrawn);
            float? face = dead ? null : duck > 0f && ease.duckFace.HasValue ? ease.duckFace : aimedBefore ? (float?)null
                : !lying && ease.strikeClip != null && ease.strikeFace.HasValue ? ease.strikeFace
                : !lying && ease.cast > 0f && ease.castFace.HasValue ? ease.castFace
                : !lying && ease.hipUsing && ease.hipFace.HasValue ? ease.hipFace
                : !lying && ease.work > 0f && ease.workKind != null ? ease.workFace
                : !lying && !seated && ease.chatAt >= 0f && ease.chatFace.HasValue ? ease.chatFace
                : !lying && !seated && ease.toss > 0f && ease.tossFace.HasValue ? ease.tossFace
                : !lying && now - ease.aimTick <= AimHold ? ease.aimFace : (float?)null;
            // IsoRim's placing of the figure (its tracker stepped by our stride, turned to our facing): where it stands, sits or
            // lies, how high it is lifted (in mid-jump, or carried by a figure IsoRim draws), its yaw and stride.
            FigurePlacement placed;
            if (!ctx.ReadPlacement(new FigurePlacementOptions { Stride = ground * size, Face = face }, out placed))
            {
                return false;
            }
            if (placed.Situation == FigureSituation.Attached || placed.Situation == FigureSituation.InVat || placed.Bones)
            {
                return false;   // IsoRim's own: on a host, upright in a vat (no IsoRim model floats them so yet), gone to bones
            }
            if (placed.Situation == FigureSituation.Gibbet)
            {
                return DrawCaged(ctx, placed, size);
            }
            SawAim(pawn, placed);
            // Off screen: nothing posed or drawn, and IsoRim told it was, so it draws nothing either. What eases over time
            // waits meanwhile, and the one-off events (a fall, getting up) go by frames in a row, so one missed off screen is
            // not played late. Another mod's draw (a portrait, a measure) is always made.
            if (!ctx.ForOtherMod && !SkinnedFigure.MaybeOnScreen(placed.Position, size))
            {
                return true;
            }
            float lift = placed.Lift;
            drawLoc.x = placed.Position.x;
            drawLoc.z = placed.Position.z;
            seated = placed.Situation == FigureSituation.Seated && !lying && !hunkering && lift <= 0f;
            // The seat: its top, how far the game's own shift onto it has got, and the height IsoRim puts the feet at for that.
            float seatLift = seated ? placed.Position.y - lift : 0f;
            float seatAmount = seated ? SeatAmount(pawn, ctx.DrawLoc) : 0f;
            float seatHeight = seatAmount > 1e-3f ? seatLift / seatAmount : 0f;
            // What it lies on, as IsoRim lays its figure there: a bed's surface or its slot (a bunk's level), a couch, a
            // corpse's shelf; the ground.
            float lyingFloor = lying ? placed.Position.y - lift : 0f;
            bool limpFromProne = false;
            string limpPose = null;   // the clip it goes limp from, lying there
            if (lying && wasDucked > 0f)
            {
                limpFromProne = !crawling && Ragdolls && skin.CanRagdoll && lift <= 0f && pawn.CurrentBed() == null && !(pawn.ParentHolder is IThingHolderWithDrawnPawn);
                limpPose = ease.duckLoop ?? (ease.duckHand != null ? "duck_" + ease.duckHand : null);
                ease.fellYaw = ease.duckFace ?? ease.upYaw;
                ease.fallClip = null;
                ease.fallen = 1f;
            }
            // Up from lying on its back (woken, or no longer downed): the getup, from where it lay.
            bool laid = lying && !crawling && !dead;
            if (!lying && ease.lay && Time.frameCount - ease.layFrame <= 2 && flight < 0f && skin.Has("getup"))
            {
                ease.risen = 0f;
                ease.risenTick = Find.TickManager.TicksGame;
                ease.risenRoll = Gen.HashCombineInt(pawn.thingIDNumber, ease.risenTick);
            }
            if (laid)
            {
                ease.layFrame = Time.frameCount;
            }
            else if (lying)
            {
                ease.risen = 1f;
            }
            ease.lay = laid;
            // Gone down where it stood (downed, or killed): a fall from standing into the lying loop, on the ground only.
            bool onGround = lying && !crawling && lift <= 0f && (dead || (pawn.CurrentBed() == null && !(pawn.ParentHolder is IThingHolderWithDrawnPawn)));
            if (onGround && Time.frameCount - ease.upFrame <= 2)
            {
                string fall = dead ? "die" : pawn.Downed ? "knockdown" : null;
                if (fall != null && skin.Has(fall))
                {
                    // A variant per fall, any of them for a ragdoll; with none, its own, which ends as the loop it blends into begins.
                    if (Ragdolls && skin.CanRagdoll)
                    {
                        fall = skin.Variant(fall, Gen.HashCombineInt(pawn.thingIDNumber, Find.TickManager.TicksGame));
                    }
                    ease.fallClip = fall;
                    ease.fallen = 0f;
                    ease.fallenTick = Find.TickManager.TicksGame;
                    ease.fellYaw = ease.upYaw - FallFacing(fall);   // turned so the fall starts facing the way it stood
                    // Falling limp from it (made from its first frame, once it is posed below).
                    DropDoll(pawn, ease);
                    ease.dollNew = Ragdolls && skin.CanRagdoll;
                }
            }
            // Flung while it already lay there with no ragdoll (on the ground since before it was drawn, or just lying):
            // it goes limp as it goes.
            Vector3 flungAt, flungSpeed;
            if (onGround && ease.doll == null && !ease.dollNew && Ragdolls && skin.CanRagdoll && (limpFromProne || Flings.PlaceOf(pawn, out flungAt, out flungSpeed)))
            {
                ease.dollNew = true;
            }
            if (!onGround)
            {
                ease.fellYaw = null;
                ease.fallen = 1f;
                ease.dollNew = false;
            }
            float risePhase = 1f;
            float rise = !lying && flight < 0f && skin.Has("getup") ? RiseOf(ease, out risePhase) : 0f;
            // A ragdoll lies where it fell until the body is moved off the ground or out of its cell (carried, put in a
            // bed, hauled) or crawls; getting up, the getup is eased out of it, and then it is done with.
            if (ease.doll != null && (!Ragdolls || (lying ? !onGround || ease.doll.Cell != pawn.PositionHeld : rise <= 0f || risePhase >= RiseFromDoll)))
            {
                DropDoll(pawn, ease);
            }
            float yaw, phase = 0f, gait = 0f, twist = 0f;
            bool aiming = false;
            if (dead)
            {
                yaw = ease.fellYaw ?? placed.Yaw;   // as IsoRim turns a corpse, unless it fell here
            }
            else
            {
                yaw = placed.Yaw;
                phase = placed.StridePhase;
                gait = placed.Gait;
                float aimDrawn = placed.AimDrawn;
                aiming = !lying && !ease.hipUsing && placed.Aiming;
                if (aiming)
                {
                    ease.aimFace = aimDrawn;
                    ease.aimTick = now;
                    face = face ?? aimDrawn;
                }
                bool upright = !lying && !crawling && !seated && flight < 0f && swim <= 0f && rise <= 0f && duck <= 0f;
                yaw = TurnedFor(pawn, ease, yaw, face, aiming, upright);
                yaw = StepOf(ease, yaw, upright, gait, out twist);
                // Seated at a shared reading: the reader and its watcher drawn together (below) and turned on their seats
                // toward each other.
                Pawn sharer = seated && !lying ? SharePartnerOf(pawn) : null;
                if (sharer != null && sharer.Spawned)
                {
                    Vector3 to = sharer.DrawPos - pawn.DrawPos;
                    ease.shareWay = new Vector3(to.x, 0f, to.z);
                }
                ease.share = Mathf.MoveTowards(ease.share, sharer != null ? 1f : 0f, ShowEase * Time.deltaTime);
                if (seated && ease.share > 0f && ease.shareWay.sqrMagnitude > 1e-4f)
                {
                    yaw += Mathf.Clamp(Mathf.DeltaAngle(yaw, ease.shareWay.AngleFlat()), -ShareTurn, ShareTurn) * Mathf.SmoothStep(0f, 1f, ease.share);
                }
                // Seated, talking to someone: the trunk (and the head with it) turned toward them, the seat's way kept.
                if (seated && !lying && ease.chatAt >= 0f && ease.chatFace.HasValue)
                {
                    twist = Mathf.Clamp(Mathf.DeltaAngle(yaw, ease.chatFace.Value), -ChatSeatTwist, ChatSeatTwist) * BoutWeight(ease.chatAt);
                }
            }
            // The idle eased into the stride as the gait comes up, the stride blended between the gait steps either side of
            // the pace, and the sitting loop eased in as the game slides the pawn onto its seat. Drafted, the standing pose
            // is the drafted stance's, and its arms, chest and head are laid over the stride (or the seat) too. Lying and
            // crawling are made upright and laid down whole (whole, model units), as IsoRim lays down its figure.
            try
            {
                SkinnedFigure.Shape shape = ShapeFor(pawn);
                SkinnedFigure.Posed fig = skin.For(pawn.thingIDNumber, shape);
                // At a lesson, the head turned toward the other (not lying or on the move), eased on real time.
                // Under the blanket at lovin, toward the partner beside it (LovinOf).
                float lovinSide = lying && !dead ? LovinOf(pawn, placed, yaw) : 0f;
                float lovinBare = lying && !dead ? LovinBareOf(pawn, placed, yaw) : 0f;   // (or with no blanket, on the side facing them)
                float looked = !lying && !crawling && flight < 0f ? LessonLookOf(pawn, ease, yaw) : lovinSide * LovinLook;
                ease.look = Mathf.MoveTowards(ease.look, looked, LookSpeed * Time.deltaTime);
                float headHeight = skin.HeadHeight(shape);
                float floor = 0f;   // cells: what it lies on
                float sat = seated && skin.Has("sit") ? seatAmount : 0f;   // without the clip: standing on the seat
                Matrix4x4 whole = Matrix4x4.identity;
                ease.shownWork = 0f;   // (set below if it stands at work)
                ease.armsOut = 0f;     // (and if it holds a weapon in the stance)
                skin.Begin(fig);
                CutLimbs(pawn, fig);
                skin.Twist(fig, twist);
                skin.Look(fig, ease.look);
                if (seated && ease.share > 0f && ease.shareWay.sqrMagnitude > 1e-4f)
                {
                    // At a shared reading: leant toward the other (on the figure's right if it lies to +x as the figure faces).
                    Vector3 side = Quaternion.Inverse(Quaternion.AngleAxis(yaw, Vector3.up)) * ease.shareWay;
                    skin.Lean(fig, (side.x > 0f ? -ShareLean : ShareLean) * Mathf.SmoothStep(0f, 1f, ease.share));
                }
                // A leg gone: the weight over the other foot in whatever it is doing on its feet (the hop has it already).
                string standOn = ease.limpBy <= 0f ? null : ease.limpClip == "hop_L" ? "R" : ease.limpClip == "hop_R" ? "L" : null;
                bool sitsDown = seated || rise > 0f || duck > 0f || ease.workKind == "meditate" || ease.workKind == "play" || ease.workKind == "research_floor" || ease.workKind == "visit" || ease.workKind == "stir_campfire";
                if (standOn == null || lying || crawling || flight >= 0f || swim > 0f)
                {
                    ease.balance = 0f;
                }
                else
                {
                    ease.balance = Time.frameCount - ease.balanceFrame <= 2 ? Mathf.MoveTowards(ease.balance, sitsDown ? 0f : 1f, BalanceEase * Time.deltaTime) : sitsDown ? 0f : 1f;
                    skin.Balance(fig, standOn, ease.balance);
                }
                ease.balanceFrame = Time.frameCount;
                if (crawling)
                {
                    // Face down, head first the way they go, tipped up by the game's crawl angle; the game's lurch is in
                    // its drawn position, and other mods' turn or shift of the sprite in IsoRim's placing.
                    skin.Add(fig, "crawl", phase / (2f * Mathf.PI), 1f);
                    whole = Matrix4x4.Translate(new Vector3(0f, 0f, -LieMiddle * headHeight))
                        * Matrix4x4.Rotate(Quaternion.AngleAxis(90f - placed.ChestLift, Vector3.right));
                }
                else if (lying)
                {
                    // On their back, head toward the south of the figure's own space before it is turned.
                    float along = LieMiddle * headHeight;
                    // IsoRim's placing has the yaw and what it lies on: in a bed, along the bed, on its surface or the pawn's
                    // own slot (a bunk's level; not tucked under the blanket, as IsoRim's bedding is too thin to hide the body
                    // without it showing through); on the couch of a building that shows who is inside (gene extractor,
                    // biosculpter pod), along the body angle the game draws them at, straining against a holding
                    // platform's chains as the game does; else a corpse on a shelf, or the ground.
                    bool inBed = placed.Situation == FigureSituation.InBed;
                    bool onCouch = placed.Situation == FigureSituation.OnCouch;
                    floor = lyingFloor;
                    if (inBed)
                    {
                        along = headHeight;   // in a bed the person's place is where their head is
                    }
                    else if (!onCouch && ease.fellYaw.HasValue)
                    {
                        yaw = ease.fellYaw.Value;   // lying where it fell, the way it fell
                    }
                    string lieClip = lovinBare != 0f ? (lovinBare > 0f ? "lovin_R" : "lovin_L") : LieClip(pawn, dead, inBed, inBed || onCouch);
                    if (inBed && lieClip != BedMiddleClip && skin.Has(BedMiddleClip))
                    {
                        // Every lying clip with its body's middle where the sleep's is: their hips are in one place, but
                        // the awake lie's straight legs put its middle further toward the foot of the bed, so a pawn
                        // waking or falling asleep slid along it.
                        along += skin.LaidMiddleOf(shape, BedMiddleClip) - skin.LaidMiddleOf(shape, lieClip);
                    }
                    float fallPhase;
                    float fall = FallOf(ease, out fallPhase);
                    // Gone limp where it lay hunkered: the ragdoll starts from that, posed where it lies, so not laid down.
                    bool fromProne = limpFromProne && limpPose != null;
                    // (turned from the loop before over LieTurnSeconds: curled on its side, laid on its back to be tended)
                    float turn = fromProne ? 1f : LieTurnOf(ease, lieClip);
                    if (turn < 1f)
                    {
                        string was = ease.lieWas;
                        skin.Add(fig, was, skin.FramesOf(was) <= 1 || dead && was == "lie" ? 0f : LoopPhase(pawn, was), (1f - fall) * (1f - turn));
                    }
                    skin.Add(fig, fromProne ? limpPose : lieClip, fromProne || skin.FramesOf(lieClip) <= 1 || dead && lieClip == "lie" ? 0f : LoopPhase(pawn, lieClip), (1f - fall) * turn);
                    whole = fromProne ? Matrix4x4.identity : Matrix4x4.Translate(new Vector3(0f, 0f, along)) * Matrix4x4.Rotate(Quaternion.AngleAxis(-90f, Vector3.right));
                    if (!fromProne)
                    {
                        // On its back, the big head would keep the body off the ground (the figure rests on its lowest
                        // point): the trunk bent up over the hips until the back is down with the head, as on a pillow,
                        // by how big the head is in this shape (the head shrink); less as it falls into it.
                        float recline = turn < 1f ? Mathf.Lerp(skin.ReclineOf(shape, ease.lieWas), skin.ReclineOf(shape, lieClip), turn) : skin.ReclineOf(shape, lieClip);
                        skin.Recline(fig, recline * (1f - fall));
                    }
                    if (fall > 0f)
                    {
                        // Falling: the fall, laid down as the loop is (the export made it so), its hips starting over the
                        // pawn's place as it stood and coming to where the loop's lie.
                        int frames = skin.FramesOf(ease.fallClip);
                        skin.Add(fig, ease.fallClip, fallPhase * (frames - 1) / frames, fall);
                        Vector3 start = whole.MultiplyPoint3x4(skin.RootOf(ease.fallClip)) - skin.RootOf("idle");
                        float stood = 1f - Mathf.SmoothStep(0f, 1f, fallPhase);
                        whole = Matrix4x4.Translate(new Vector3(-start.x, 0f, -start.z) * stood) * whole;
                    }
                    ease.lieYaw = yaw;
                    ease.lieAlong = along - skin.RootOf(lieClip).y;   // the hips' place along the figure, as laid down
                    ease.lieFloor = floor;
                }
                else if (flight >= 0f)
                {
                    // In mid-jump: the jump by how far along the flight it is (never round past its last frame, the
                    // landing), sprung out of whatever crouch it took off from.
                    // A variant per flight: the flyer is made new for each.
                    Thing flyer = pawn.ParentHolder as Thing;
                    string jump = skin.Variant("jump", Gen.HashCombineInt(pawn.thingIDNumber, flyer != null ? flyer.thingIDNumber : 0));
                    int frames = skin.FramesOf(jump);
                    float spring = skin.Has("land") ? ease.crouch * (1f - Mathf.SmoothStep(0f, 1f, flight / TakeOff)) : 0f;
                    skin.Add(fig, jump, flight * (frames - 1) / frames, 1f - spring);
                    skin.Add(fig, skin.Variant("land", ease.landedRoll), 0f, spring);
                }
                else
                {
                    float moving = Mathf.SmoothStep(0f, 1f, gait);
                    float cycle = phase / (2f * Mathf.PI);
                    // The stance for the weapon held, crossfaded from the last one's when it swaps.
                    float drafted = ease.stance != null ? ease.drafted * (1f - ease.carry) : 0f;
                    float swap = ease.fromStance != null ? ease.swap : 1f;
                    // Bending to pick something up or set it down. Standing, a squat taken from all of it but the seat; on
                    // the move, a fold at the waist over the stride (bend_walk), so the feet keep pace. A person carried
                    // keeps the carrying arms and chest, tipped down with the hips, and only squats; a thing's carrying
                    // arms give way to the reach for the ground (and the thing comes up from there in the hands).
                    bool person = ease.carry > 0f && ease.carryClip != null && ease.carryClip != "carry_item";
                    float bend = sat > 0f ? 0f : BendOf(ease, moving);
                    float onTheMove = skin.Has("bend_walk") && !person ? moving : 0f;
                    // Swimming takes the place of all of it: the front crawl on the move, laid down below, and treading
                    // water upright when still. So does getting up, the whole figure, which then eases into the rest.
                    string duckDive = "dive_" + ease.duckHand;
                    bool duckDives = skin.Has(duckDive);
                    float ducked = duckDives ? Mathf.SmoothStep(0f, 1f, duck / DropIn) : duck;
                    float land = (1f - swim) * (1f - rise) * (1f - ducked);
                    if (duck > 0f)
                    {
                        // Hunkered under fire: down flat on its front by the dive (dive_), then lying there with
                        // the hands by the head (or rolling over, putting out a fire), the whole figure in place of the rest;
                        // the same way back up.
                        string duckClip = ease.duckLoop ?? "duck_" + ease.duckHand;
                        float settled = duckDives ? Mathf.SmoothStep(0f, 1f, (duck - DropLoop) / (1f - DropLoop)) : 1f;
                        if (duckDives)
                        {
                            int frames = skin.FramesOf(duckDive);
                            skin.Add(fig, duckDive, duck * (frames - 1) / frames, ducked * (1f - settled));
                        }
                        skin.Add(fig, duckClip, LoopPhase(pawn, duckClip), ducked * settled);
                    }
                    if (rise > 0f)
                    {
                        string getup = skin.Variant("getup", ease.risenRoll);
                        int frames = skin.FramesOf(getup);
                        skin.Add(fig, getup, risePhase * (frames - 1) / frames, rise);
                    }
                    // Crouched to jump, or just landed: standing still only (setting off, the stride takes over).
                    float crouch = skin.Has("land") && sat <= 0f ? CrouchOf(pawn, ease) * (1f - moving) : 0f;
                    ease.crouch = crouch;
                    // Behind cover: crouched low, standing still only (on the move the stride takes over): whatever the
                    // standing pose is (the drafted stance, the aim, a blow) with its hips let down over its own planted
                    // feet (SkinnedFigure.Crouch), so its legs stay its own and what it holds points as it does standing.
                    float cover = sat <= 0f ? CoverOf(pawn, ease, size) * (1f - moving) * (1f - ease.carry) * (1f - ease.work) * (1f - crouch) : 0f;
                    float up = (1f - bend * (1f - onTheMove)) * (1f - crouch) * land;
                    float stride = moving * (1f - sat) * up;
                    // Cowering standing still: crouched on bent knees in place of the idle (the cower laid over it
                    // below has the same arms, chest and head).
                    float hunker = skin.Has("hunker") ? ease.cower * (1f - ease.carry) : 0f;
                    // Stunned: dazed on soft knees in place of the idle, and in place of the hunker and the drafted
                    // stance too (the slumped arms, chest and head are laid over everything below).
                    float daze = skin.Has("daze") ? ease.daze * (1f - ease.carry) * land : 0f;
                    drafted *= 1f - daze;
                    hunker *= 1f - daze;
                    // Staggered: down on one knee and up, the whole figure standing still in place of the rest (on the
                    // move, from the waist up, below); the daze over it.
                    string reelClip = ease.reelClip ?? "reel";
                    float reel = skin.Has(reelClip) ? ease.reel * (1f - ease.carry) * (1f - daze) * land : 0f;
                    float reelPhase = ease.reelAt * (skin.FramesOf(reelClip) - 1) / Mathf.Max(1, skin.FramesOf(reelClip));
                    drafted *= 1f - reel * (1f - moving);
                    hunker *= 1f - reel;
                    // At work: the stroke of its tool (or the work by its place), the whole figure, in place of the idle
                    // (standing only: the game has it stand still at the work; work sat down is laid over the sit below).
                    string workClip = ease.workKind != null ? skin.Variant(WorkClipOf(ease.workKind), ease.workRoll) : null;
                    // Cooking: a bout of stirring now and then in its place (StirOf), the spoon in hand once it is half in.
                    float stir = ease.stirClip != null ? BoutWeight(ease.stirAt) : 0f;
                    ease.toolKind = stir >= 0.5f ? ease.stirClip.Substring("work_".Length)
                        : workClip != null ? workClip.Substring("work_".Length) : null;
                    float work = workClip != null && skin.Has(workClip) && sat <= 0f && !SeatedWork(ease.workKind) ? ease.work * (1f - ease.carry) * (1f - hunker) * (1f - reel) : 0f;
                    // Pitching horseshoes or hoopstone: one underhand toss round each throw, the whole figure in place of
                    // the idle (seated, from the waist up, below); between throws the idle.
                    string tossClip = ease.tossClip ?? "toss";
                    float tossing = ease.tossAt >= 0f && skin.Has(tossClip)
                        ? ease.toss * TossWeight(ease.tossAt) * (1f - ease.carry) * (1f - ease.drafted) * (1f - hunker) * (1f - reel) * (1f - daze) * (1f - work) : 0f;
                    float tossPhase = ease.tossAt * (skin.FramesOf(tossClip) - 1) / Mathf.Max(1, skin.FramesOf(tossClip));
                    skin.Add(fig, "idle", LoopPhase(pawn, "idle"), (1f - moving) * (1f - drafted) * (1f - sat) * (1f - hunker) * (1f - work) * (1f - daze) * (1f - reel) * (1f - tossing) * up);
                    if (tossing > 0f)
                    {
                        skin.Add(fig, tossClip, tossPhase, (1f - moving) * (1f - drafted) * (1f - sat) * tossing * up);
                    }
                    skin.Add(fig, "daze", LoopPhase(pawn, "daze"), (1f - moving) * (1f - sat) * daze * up);
                    skin.Add(fig, reelClip, reelPhase, (1f - moving) * (1f - sat) * reel * up);
                    skin.Add(fig, "hunker", LoopPhase(pawn, "hunker"), (1f - moving) * (1f - drafted) * (1f - sat) * hunker * up);
                    if (work > 0f)
                    {
                        float workPhase = workClip == "work_pickaxe" ? PickPhase(pawn, ease, workClip)
                            : ease.workKind == "stomp" ? BeatPhase(pawn, ease, workClip, StompHit, 1)
                            : ease.workKind == "pat" ? BeatPhase(pawn, ease, workClip, PatHit, PatHits)
                            : LoopPhase(pawn, workClip);
                        skin.Add(fig, workClip, workPhase, (1f - moving) * (1f - drafted) * work * (1f - stir) * up);
                        if (stir > 0f)
                        {
                            skin.Add(fig, ease.stirClip, ease.stirAt, (1f - moving) * (1f - drafted) * work * stir * up);
                        }
                    }
                    ease.shownWork = work * (1f - moving) * (1f - drafted) * up;
                    // Aiming: the weapon brought up out of the drafted stance and pointed at the target. Standing still,
                    // the whole aim clip (its own stance) in place of the drafted one; on the move or seated, its waist
                    // up over whatever the legs do (below).
                    float aimed = ease.aimName != null ? ease.aim * (1f - ease.carry) * (1f - bend) * land * (ease.drawAt >= 0f ? ease.drafted : 1f) : 0f;
                    // A melee blow or a throw: in the same way, by how far from the guard it is (Swing), so between blows
                    // the stance shows.
                    float struck = ease.strikeClip != null ? Swing(ease.strikePhase, ease.strikeClip == "throw_pack" ? PackTake : StrikeRaised) * (1f - ease.carry) * (1f - bend) * land : 0f;
                    float stanceShown = (1f - aimed) * (1f - struck);
                    if (swap < 1f)
                    {
                        skin.Add(fig, ease.fromStance, LoopPhase(pawn, ease.fromStance), (1f - moving) * drafted * (1f - sat) * (1f - swap) * up * stanceShown);
                    }
                    skin.Add(fig, ease.stance ?? "idle", LoopPhase(pawn, ease.stance ?? "idle"), (1f - moving) * drafted * (1f - sat) * swap * up * stanceShown);
                    if (aimed > 0f)
                    {
                        LayAim(pawn, ease, fig, (1f - moving) * drafted * (1f - sat) * up * aimed * (1f - struck), size, true);
                    }
                    if (struck > 0f)
                    {
                        skin.Add(fig, ease.strikeClip, ease.strikePhase, (1f - moving) * drafted * (1f - sat) * up * struck);
                    }
                    skin.Add(fig, clipNames[below], cycle, stride * (1f - (level - below)) * (1f - limp) * (1f - wade));
                    skin.Add(fig, clipNames[above], cycle, stride * (level - below) * (1f - limp) * (1f - wade));
                    if (limp > 0f)
                    {
                        skin.Add(fig, ease.limpClip, cycle, stride * limp * (1f - wade));
                    }
                    skin.Add(fig, "wade", cycle, stride * wade);
                    // Stepping round on the spot to face the way the trunk has turned (StepOf).
                    skin.Add(fig, clipNames[0], ease.stepCycle, StepShare * ease.step * (1f - moving) * (1f - sat) * up);
                    skin.Add(fig, "sit", LoopPhase(pawn, "sit"), sat * land);
                    skin.Add(fig, skin.Variant("bend", ease.bentRoll), 0f, bend * (1f - onTheMove) * land);
                    skin.Add(fig, skin.Variant("land", ease.landedRoll), 0f, crouch * land);
                    skin.Crouch(fig, cover * (1f - bend * (1f - onTheMove)) * land);
                    float stroke = swim > 0f ? StrokeOf(ease, moving) : moving;
                    skin.Add(fig, "swim", cycle, stroke * swim);
                    skin.Add(fig, "tread", LoopPhase(pawn, "tread"), (1f - stroke) * swim);
                    if (swap < 1f)
                    {
                        skin.Over(fig, ease.fromStance, LoopPhase(pawn, ease.fromStance), drafted * (1f - swap) * (1f - bend) * land * stanceShown);
                    }
                    if (ease.stance != null)
                    {
                        skin.Over(fig, ease.stance, LoopPhase(pawn, ease.stance), drafted * swap * (1f - bend) * land * stanceShown);
                        // The stance's hands held out past armour, aiming too (the aim holds it in front of the chest as well),
                        // but not in a blow, which swings them about.
                        ease.armsOut = drafted * (1f - bend) * land * (1f - struck);
                    }
                    if (aimed > 0f)
                    {
                        LayAim(pawn, ease, fig, aimed * (1f - struck), size, false);
                    }
                    if (struck > 0f)
                    {
                        skin.Over(fig, ease.strikeClip, ease.strikePhase, struck);
                    }
                    // Eating, still: the hand to the mouth and back from the waist up over the sit or the idle, in place of
                    // the carrying arms (what it eats is on the table, or in the other hand: DrawLoad).
                    float eat = ease.eatClip != null && skin.Has(ease.eatClip) ? ease.eat * (1f - moving) * (1f - bend) * land : 0f;
                    if (ease.carryClip != null)
                    {
                        // A cradled baby rocked as it is played with: carry_play eased in over the cradle (Ease.play).
                        // Seated, the carry gives way to the sit, but for a baby being fed, which the game sits down with
                        // in its arms (FeedsBaby): for now the cradle laid over the sit.
                        // A cuddled pet is held sat on a chair too (Cuddling), and stroked while still (carry_cuddle_stroke).
                        float seatedCarry = (ease.carryClip == "carry_cradle" && FeedsBaby(pawn)) || ease.carryClip == "carry_cuddle" ? 1f : 1f - sat;
                        float carried = ease.carry * seatedCarry * (person ? 1f : 1f - bend) * land * (1f - eat);
                        string carryOver = CarryOverOf(ease.carryClip);
                        float play = carryOver != null && skin.Has(carryOver) ? ease.play : 0f;
                        skin.Over(fig, ease.carryClip, LoopPhase(pawn, ease.carryClip), carried * (1f - play));
                        if (play > 0f)
                        {
                            skin.Over(fig, carryOver, LoopPhase(pawn, carryOver), carried * play);
                        }
                    }
                    // Reading with someone sat beside watching (ShowSideOf): the book held out to them, read_show_<side>
                    // crossfaded over the plain read by Ease.readShow.
                    string showSide = ease.eatClip == "read" && eat > 0f ? ShowSideOf(pawn) : null;
                    ease.readShow = Mathf.MoveTowards(ease.readShow, showSide != null ? 1f : 0f, ShowEase * Time.deltaTime);
                    ease.readShowSide = showSide ?? ease.readShowSide;
                    string shown = ease.readShowSide != null ? "read_show_" + ease.readShowSide : null;
                    float show = ease.eatClip == "read" && shown != null && skin.Has(shown) ? Mathf.SmoothStep(0f, 1f, ease.readShow) : 0f;
                    if (eat > 0f)
                    {
                        skin.Over(fig, ease.eatClip, ease.eatClip == "pill" ? ease.pillAt : LoopPhase(pawn, ease.eatClip), eat * (1f - show));
                    }
                    if (eat > 0f && show > 0f)
                    {
                        // (on the read's own clock, so the page turns carry on through it)
                        skin.Over(fig, shown, LoopPhase(pawn, "read"), eat * show);
                    }
                    // Playing with a baby on the floor (PlayStatic), on an export without work_play (the two joined at the
                    // waist, so upright): the carry_play rocking laid over the meditation's sat legs (or the sit, if it
                    // happens to be seated), the baby in its arms (HoldBabyPlayed). Laid parent by parent, it takes the
                    // meditation's tipped-back pelvis and leans far back.
                    if (ease.workKind == "play" && skin.Has("carry_play") && !skin.Has("work_play"))
                    {
                        float rocking = ease.work * (1f - ease.carry) * (1f - moving) * (1f - drafted) * land;
                        skin.Over(fig, "carry_play", LoopPhase(pawn, "carry_play"), rocking);
                    }
                    else if (ease.workKind == "play" && sat > 0f && skin.Has("carry_play"))
                    {
                        // (seated after all: the play carry's arms over the sit, which leans forward as a carry stands)
                        skin.Over(fig, "carry_play", LoopPhase(pawn, "carry_play"), ease.work * (1f - ease.carry) * (1f - moving) * (1f - drafted) * sat * land);
                    }
                    // Working sat down (researching, at a bench, or on the lap with no bench): from the waist up over
                    // the sit (the other work clips are the whole figure, standing only).
                    if (sat > 0f && SeatedWork(ease.workKind) && workClip != null && skin.Has(workClip))
                    {
                        float desk = ease.work * (1f - ease.carry) * (1f - moving) * (1f - drafted) * sat * land;
                        skin.Over(fig, workClip, LoopPhase(pawn, workClip), desk);
                        ease.shownWork = desk;
                    }
                    // Holding the worst wound, unless the hands are busy with a weapon or a load: the hand all the way
                    // there, the hunch by how hurt.
                    if (ease.hurtClip != null && skin.Has(ease.hurtClip))
                    {
                        float free = (1f - ease.drafted) * (1f - ease.carry) * (1f - bend) * (1f - ease.shownWork) * (1f - tossing) * land;
                        skin.Over(fig, ease.hurtClip, LoopPhase(pawn, ease.hurtClip), ease.hurt * free, ease.hold * free);
                    }
                    // Cowering: hunched with both hands drawn up in front of the chest, from the waist up over the run (or
                    // the hunker when still), not while drafted, carrying or bending.
                    if (skin.Has("cower"))
                    {
                        skin.Over(fig, "cower", LoopPhase(pawn, "cower"), ease.cower * (1f - ease.drafted) * (1f - ease.carry) * (1f - bend) * land);
                    }
                    // Leading on a rope: the right arm reached a little back, over whatever the rest is doing, not
                    // while drafted, carrying or bending.
                    if (skin.Has("lead"))
                    {
                        skin.Over(fig, "lead", LoopPhase(pawn, "lead"), ease.lead * (1f - ease.drafted) * (1f - ease.carry) * (1f - bend) * land);
                    }
                    // Talking with the hands (a board game, a speech, now and then at a party) and rubbing them for
                    // warmth now and then while cold (only at leisure: AtLeisure): from the waist up over whatever the legs
                    // do, not while drafted, carrying, eating, working or bending.
                    float idleHands = (1f - ease.drafted) * (1f - ease.carry) * (1f - bend) * (1f - ease.shownWork) * (1f - eat) * (1f - ease.cower) * (1f - tossing) * land;
                    if (tossing > 0f && sat > 0f)
                    {
                        skin.Over(fig, tossClip, tossPhase, tossing * sat * (1f - bend) * land);
                    }
                    float chat = BoutWeight(ease.chatAt);
                    if (skin.Has("talk") && (ease.talk > 0f || chat > 0f))
                    {
                        bool bout = chat > ease.talk;
                        skin.Over(fig, "talk", bout ? ease.chatAt : LoopPhase(pawn, "talk"), Mathf.Max(ease.talk, chat) * idleHands);
                    }
                    if (skin.Has("rub") && ease.rubAt >= 0f)
                    {
                        skin.Over(fig, "rub", ease.rubAt, BoutWeight(ease.rubAt) * ease.rubFree * idleHands);
                    }
                    // A sad wander: slumped, the head hung, from the waist up over the gait (the arms only part way, so
                    // they still swing a little), not while drafted, carrying, working or bending.
                    if (ease.sad > 0f && skin.Has("sad"))
                    {
                        skin.Over(fig, "sad", LoopPhase(pawn, "sad"), ease.sad * (1f - ease.drafted) * (1f - ease.carry) * (1f - bend) * (1f - ease.shownWork) * (1f - eat) * land);
                    }
                    // On fire and not rolling it out: patting at the flames on itself from the waist up, over the
                    // drafted stance and anything else the arms do (Top), but not over an aim, a load or a bend.
                    if (ease.burn > 0f && skin.Has("burning"))
                    {
                        skin.Top(fig, "burning", LoopPhase(pawn, "burning"), ease.burn * (1f - ease.aim) * (1f - ease.carry) * (1f - bend) * (1f - ease.shownWork) * land * (1f - daze));
                    }
                    // A wave in passing at whom it chatted with: the right arm only, over the rest (Top), with the hands free.
                    if (ease.waveAt >= 0f && skin.Has("wave"))
                    {
                        skin.Top(fig, "wave", ease.waveAt, BoutWeight(ease.waveAt) * idleHands * (1f - ease.burn));
                    }
                    // Drunk: swaying from the waist, the head lolling, added on top of whatever it does (Nudge), steadier
                    // while aiming; the arms hanging loose only with the hands free. On the move it weaves with the sway.
                    ease.weave = 0f;
                    if (ease.drunk > 0f && skin.Has("drunk"))
                    {
                        float drunkPhase = LoopPhase(pawn, "drunk");
                        // (not over throwing up, a drunk pawn's vomiting: that clip is bent double already)
                        float sway = ease.drunk * land * (1f - (1f - DrunkAiming) * ease.aim)
                            * (ease.workKind == "vomit" ? 1f - ease.shownWork : 1f);
                        skin.Nudge(fig, "drunk", drunkPhase, sway);
                        skin.Nudge(fig, "drunk_arms", drunkPhase, sway * idleHands);
                        // (the trunk leans to the figure's left in the loop's first half: drawn that way, to its right less)
                        ease.weave = -DrunkWeave * size * ease.drunk * moving * land * Mathf.Sin(2f * Mathf.PI * drunkPhase);
                    }
                    skin.Over(fig, skin.Variant("bend_walk", ease.bentRoll), 0f, bend * onTheMove * land);
                    // The shield guard (a shield shown in the left hand): the left fist out in front of the shoulder, over
                    // all of that (Top, the left arm only), giving way to a reload, a cast, a stagger and a daze.
                    if (ease.shield > 0f)
                    {
                        skin.Top(fig, "shield", LoopPhase(pawn, "shield"), ease.shield * (1f - ease.carry) * (1f - bend) * land * (1f - daze)
                            * (1f - ease.reload) * (1f - ease.cast) * (1f - ease.reel));
                    }
                    // A shield carried on the back being taken into the guard (or put back): the left hand round to its
                    // edge and back, the trunk turning to meet it, over the same, giving way as the guard does.
                    LayShieldDraw(ease, fig, (1f - ease.carry) * (1f - bend) * land * (1f - daze) * (1f - ease.reload) * (1f - ease.cast) * (1f - ease.reel));
                    // The hip gear in the left hand (drafted or using it): taken from the shoulder, held ready, pointed at
                    // the target; over all of that (Top), giving way to a reload, a cast, a stagger and a daze.
                    LayHip(pawn, ease, fig, (1f - ease.carry) * (1f - bend) * land * (1f - daze) * (1f - ease.reload) * (1f - ease.cast) * (1f - ease.reel), size);
                    // Reloading: the left hand down to the belt for a magazine and back, the weapon kept in the right, over
                    // the drafted stance (Top, so the stance's arms give way to it all the way).
                    if (ease.reload > 0f)
                    {
                        skin.Top(fig, "reload", ease.reloadAt, ease.reload * (1f - ease.carry) * (1f - bend) * land * (1f - daze));
                    }
                    // Drawing the weapon from its holster or putting it back (backwards): the hand to its handle and to
                    // the guard, over all of that (Top, so whatever the arm was doing gives way).
                    if (ease.drawAt >= 0f)
                    {
                        int drawFrames = skin.FramesOf(ease.drawClip);
                        skin.Top(fig, ease.drawClip, ease.drawAt * (drawFrames - 1) / drawFrames, DrawWeight(ease.drawAt) * (1f - ease.carry) * (1f - bend) * land * (1f - daze));
                    }
                    // Casting: the left hand raised to the target, over all of that (the right keeps its weapon or its
                    // swing). Staggered: down on one knee. Stunned: slumped. Each laid over the
                    // stances above rather than blended with them (Top), the daze over the rest.
                    if (ease.castClip != null)
                    {
                        skin.Top(fig, ease.castClip, LoopPhase(pawn, ease.castClip), ease.cast * (1f - ease.carry) * (1f - bend) * land * (1f - daze));
                    }
                    skin.Top(fig, reelClip, reelPhase, reel * (1f - bend));
                    skin.Top(fig, "daze", LoopPhase(pawn, "daze"), daze);
                    // Face down and head first, as the crawl is laid down, but with the head end raised.
                    float flat = stroke * swim;
                    if (flat > 0f)
                    {
                        whole = Matrix4x4.Translate(new Vector3(0f, 0f, -LieMiddle * headHeight * flat))
                            * Matrix4x4.Rotate(Quaternion.AngleAxis(flat * (90f - SwimLift), Vector3.right));
                    }
                    // Getting up: its hips start where they lay (the clip's start at its own place) and come back to the
                    // pawn's place as it rises, and it turns from the way it lay to the way it now faces as it eases out.
                    if (rise > 0f)
                    {
                        whole = Matrix4x4.Translate(new Vector3(0f, 0f, ease.lieAlong * (1f - Mathf.SmoothStep(0f, 1f, risePhase)))) * whole;
                        yaw = Mathf.LerpAngle(yaw, ease.lieYaw, rise);
                    }
                }
                // Flinching from a hit, added on top of whatever it is doing (not a corpse).
                if (!dead)
                {
                    FlinchOf(pawn, fig, yaw);
                }
                // A ragdoll at rest from before (the game was saved and loaded), lying as it was.
                if (lying && ease.doll == null && !ease.dollNew && Ragdolls && ease.fallen >= 1f && onGround)
                {
                    ease.doll = SavedDoll(pawn, shape, size);
                    if (ease.doll != null)
                    {
                        ease.fellYaw = ease.doll.Yaw;
                        ease.dollSaved = true;
                    }
                }
                if (ease.doll != null || ease.dollNew)
                {
                    if (lying && !dead)
                    {
                        skin.Nudge(fig, "lie", LoopPhase(pawn, "lie"), 1f);   // breathing, laid on what it lies as
                    }
                    RagdollOf(pawn, ease, fig, whole, size, lying, dead, rise, risePhase, ease.doll != null && lying ? ease.doll.Yaw : yaw, ref drawLoc);
                }
                else
                {
                    skin.Chain(fig, whole);
                    if (!lying && !crawling && flight < 0f)
                    {
                        if (skin.ArmsOut(fig, whole, ease.stance, ease.armsOut))
                        {
                            skin.Reached(fig, whole);
                        }
                        GripCarried(pawn, ease, fig, whole, size);
                        StrokePet(pawn, ease, fig, whole, size);
                    }
                    skin.Finish(fig);
                }
                // Rested on what is under it: the feet on the floor, the seat on the chair's, the back on the bed or
                // ground.
                // Only where something below rests on it (lying, diving, getting up, seated, and the tail getting up or
                // diving): standing or on the move it isn't read, and finding it skins the whole body on the CPU, which with
                // GPU skinning is the one thing left doing so for a figure on its feet.
                float lowest = 0f, bodyTop = 0f;
                if (lying || duck > 0f || rise > 0f || sat > 0f)
                {
                    skin.Extent(fig, out lowest, out bodyTop);
                }
                if (lying && onGround && ease.depth > 0f)
                {
                    // Lying in water, which IsoRim draws as an opaque sheet: floating, sunk about half its thickness as it
                    // lies (not the head, bodyTop), never deeper than the water.
                    floor -= Mathf.Min(ease.depth, FloatSink * Mathf.Max(0f, bodyTop - lowest) * size);
                }
                float y = Lift * size;
                Vector3 underGlass = Vector3.zero;   // cells, the move keeping a figure on a couch under its glass (UnderGlass)
                if (lying && ease.doll != null)
                {
                    // As the ragdoll lies (its joints on the ground at 0), but never into the ground; at rest, on its lowest
                    // point, as a figure lying is.
                    y = floor - (lowest < 0f || ease.doll.Settled ? lowest : 0f) * size + Lift * size;
                    yaw = ease.doll.Yaw;
                }
                else if (lying)
                {
                    y = floor - lowest * size + Lift * size;
                    if (placed.Situation == FigureSituation.OnCouch)
                    {
                        underGlass = UnderGlass(pawn, fig, new Vector3(drawLoc.x, y + lift, drawLoc.z), yaw, size, floor);
                    }
                }
                else if (duck > 0f)
                {
                    // Diving: only kept out of the ground while it is in the air, then let down on its lowest point.
                    float down = duck;
                    y = Mathf.Lerp(y - Mathf.Min(lowest, 0f) * size, (Lift - lowest) * size, Mathf.SmoothStep(0f, 1f, (down - DropRest) / (1f - DropRest)));
                }
                else if (rise > 0f)
                {
                    // Getting up: on its lowest point, coming down off the bed it lay on.
                    y = Mathf.Lerp(y, ease.lieFloor * (1f - Mathf.SmoothStep(0f, 1f, risePhase)) - lowest * size + Lift * size, rise);
                }
                else if (sat > 0f)
                {
                    y = Mathf.Max(Mathf.Lerp(y, seatHeight - skin.SeatOf(fig) * size, sat), -lowest * size);   // feet not through the floor
                }
                else if (seated)
                {
                    y += seatLift;
                }
                else if (ease.depth > 0f || swim > 0f)
                {
                    // Sunk into the water, which IsoRim draws as an opaque sheet, so what is under it is hidden: wading, by
                    // the water's depth (a child's neck kept out of it); swimming, to the neck.
                    Vector3 neck;
                    if (!skin.JointOf(fig, NeckBone, out neck))
                    {
                        neck = new Vector3(0f, headHeight, 0f);
                    }
                    float wading = y - Mathf.Min(ease.depth, Mathf.Max(0f, (neck.y - HeadClear) * size));
                    y = Mathf.Lerp(wading, WaterLine - neck.y * size, swim);
                }
                Vector3 at = new Vector3(drawLoc.x, y + lift, drawLoc.z) + underGlass;
                if (!lying && ease.weave != 0f)
                {
                    // Weaving drunk on the move, to either side of its path.
                    at += Quaternion.Euler(0f, yaw, 0f) * Vector3.right * ease.weave;
                }
                if (ease.workKind == "bench" || ease.workKind == "hammer_bench")
                {
                    // Stood at a bench: the game keeps it in the middle of the cell beside it, out of reach of its top, so
                    // it is drawn stepped up to it, as far as it has taken up the stance.
                    at += Quaternion.Euler(0f, ease.workFace, 0f) * Vector3.forward * (BenchCloser * ease.shownWork);
                }
                else if (ease.workKind == "stir_campfire")
                {
                    // Sat at a campfire: drawn shuffled up to it, so the hands reach its stone ring.
                    at += Quaternion.Euler(0f, ease.workFace, 0f) * Vector3.forward * (FireCloser * ease.shownWork);
                }
                else if (ease.workKind == "tame" && pawn.CurJobDef == JobDefOf.FeedPatient)
                {
                    // Feeding a patient in a bed stood at it: stepped up to the bed, so the meal held out reaches them.
                    at += Quaternion.Euler(0f, ease.workFace, 0f) * Vector3.forward * (FeedCloser * ease.shownWork);
                }
                else if (ease.eatClip == "kiss" && ease.eat > 0f)
                {
                    // Kissing: the two stand a cell apart, so each is drawn half the way to KissGap (for its size) toward
                    // the other, as far as it has taken up the kiss.
                    if (KissOf(pawn) != null && pawn.CurJob.targetA.Thing is Pawn lover && lover.Spawned)
                    {
                        Vector3 to = lover.DrawPos - pawn.DrawPos;
                        ease.kissWay = new Vector3(to.x, 0f, to.z);
                    }
                    float apart = ease.kissWay.magnitude;
                    if (apart > 0.01f)
                    {
                        at += ease.kissWay / apart * (Mathf.Max(0f, apart - KissGap * size) * 0.5f * ease.eat);
                    }
                }
                else if (seated && ease.share > 0f && ease.shareWay.sqrMagnitude > 1e-4f)
                {
                    // At a shared reading: drawn toward the one beside it (its turn on the seat is above).
                    at += ease.shareWay.normalized * (ShareCloser * Mathf.SmoothStep(0f, 1f, ease.share));
                }
                else if (ease.listen > 0f || ListeningTo(pawn) != null)
                {
                    // Listening to a performance: drawn stepping in toward the performer from the cell beside it.
                    Pawn performer = ListeningTo(pawn);
                    if (performer != null && performer.Spawned)
                    {
                        Vector3 to = performer.DrawPos - pawn.DrawPos;
                        ease.listenWay = new Vector3(to.x, 0f, to.z);
                    }
                    ease.listen = Mathf.MoveTowards(ease.listen, performer != null ? 1f : 0f, ListenEase * Time.deltaTime);
                    float apart = ease.listenWay.magnitude;
                    if (apart > 0.01f)
                    {
                        float step = Mathf.Min(ListenCloser, Mathf.Max(0f, apart - ListenGap * size));
                        at += ease.listenWay / apart * (step * Mathf.SmoothStep(0f, 1f, ease.listen));
                    }
                }
                else if (ease.workKind != null && InstrumentCloser.TryGetValue(ease.workKind, out float closer))
                {
                    // Playing an instrument: drawn stepped up to it in the same way (seated, scooted forward on the seat).
                    at += Quaternion.Euler(0f, ease.workFace, 0f) * Vector3.forward * (closer * ease.shownWork);
                }
                else if (ease.workBeside.HasValue && ease.shownWork > 0f)
                {
                    // Working on someone lying on the ground: drawn kneeling beside them, its hands on their chest, as the
                    // game often stands it on their own cell, over their middle.
                    Vector3 beside = ease.workBeside.Value;
                    at.x = Mathf.Lerp(at.x, beside.x, ease.shownWork);
                    at.z = Mathf.Lerp(at.z, beside.z, ease.shownWork);
                }
                if (!lying && !seated && flight < 0f && swim <= 0f && lift <= 0f && duck <= 0f)
                {
                    ease.upFrame = Time.frameCount;   // on its feet: it can fall from here
                    ease.onBack = false;
                    ease.upYaw = yaw;
                }
                // What the tail stays above: what it lies on, or getting up, the lowest of the body. Under a blanket it is hidden,
                // and pushed up to the blanket it would show through it.
                float tailFloor = lying ? (floor - at.y) / size : rise > 0f || duck > 0f ? lowest : float.NegativeInfinity;
                // A bed that shows only the head: IsoRim's blanket over the body, as over its own figure, and only the head
                // drawn (fig.tucked, BedTuck.cs), as the chibi's body, arms crossed on the chest, stood up through the lump.
                Building_Bed blanketBed = lying && !dead && !ctx.ForOtherMod && placed.Situation == FigureSituation.InBed && placed.HeadOnlyInBed
                    && BedBlankets && BlanketReady() ? pawn.CurrentBed() : null;
                fig.tucked = blanketBed != null;
                Vector3 blanketAt = placed.Position;
                if (blanketBed != null)
                {
                    at += TuckedDown(blanketBed, fig, ease, at, yaw, size, placed.Yaw);
                    // At lovin, the head and its lump drawn across the bed toward the partner's, eased in snapped steps
                    // (IsoRim builds the lump again whenever it moves).
                    float shiftTo = Mathf.Abs(lovinSide) * LovinCloser;
                    ease.lovinShift = Mathf.MoveTowards(ease.lovinShift, shiftTo, LovinEase * Time.deltaTime);
                    float snapped = ease.lovinShift >= shiftTo - 1e-4f && shiftTo > 0f ? shiftTo : Mathf.Round(ease.lovinShift / LovinSnap) * LovinSnap;
                    if (snapped > 0f && lovinSide != 0f)
                    {
                        Vector3 toward = Quaternion.AngleAxis(yaw, Vector3.up) * Vector3.right * (Mathf.Sign(lovinSide) * snapped);
                        at += toward;
                        blanketAt += toward;
                    }
                }
                else if (lying && (lovinBare != 0f || ease.lovinShift > 0f))
                {
                    // At lovin with no blanket, drawn across the bed toward the partner, eased in and out.
                    ease.lovinShift = Mathf.MoveTowards(ease.lovinShift, lovinBare != 0f ? LovinSideCloser : 0f, LovinEase * Time.deltaTime);
                    if (lovinBare != 0f)
                    {
                        ease.lovinWay = Mathf.Sign(lovinBare);
                    }
                    at += Quaternion.AngleAxis(yaw, Vector3.up) * Vector3.right * (ease.lovinWay * ease.lovinShift);
                }
                else
                {
                    ease.lovinShift = 0f;
                }
                GarmentMask mask = DrawDressed(pawn, fig, at, yaw, size, sat, tailFloor, onGround);
                fig.tucked = false;   // (only for this draw: carried out of bed, it is drawn by its carrier, DrawHeld)
                if (blanketBed != null)
                {
                    DrawTuckedIn(pawn, blanketBed, fig, ease, at, yaw, size, blanketAt, placed.Yaw, lyingFloor);
                }
                Vector3 chest, hips, head;
                if (lying && skin.JointOf(fig, ChestBone, out chest) && skin.JointOf(fig, "spine", out hips) && skin.JointOf(fig, "spine.006", out head))
                {
                    // Kept for a doctor or anyone else working on it, who kneels beside the body as drawn (WorkOf).
                    Quaternion turned = Quaternion.AngleAxis(yaw, Vector3.up);
                    ease.lyingChest = at + turned * (chest * size);
                    Vector3 along = turned * (head - hips);
                    along.y = 0f;
                    ease.lyingAlong = along.sqrMagnitude > 1e-6f ? along.normalized : turned * Vector3.forward;
                    if (ease.lyingJoints == null)
                    {
                        ease.lyingJoints = new Vector3[LyingBones.Length];
                    }
                    for (int j = 0; j < LyingBones.Length; j++)
                    {
                        Vector3 joint;
                        ease.lyingJoints[j] = skin.JointOf(fig, LyingBones[j], out joint) ? at + turned * (joint * size) : ease.lyingChest;
                    }
                    ease.lyingSize = size;
                    ease.lyingFrame = Time.frameCount;
                }
                bool inHands = false;
                // Drawn for another mod (Api.DrawFigure: a portrait, a preview), without what it holds or carries.
                bool forOther = ctx.ForOtherMod;
                Thing load = pawn.carryTracker != null && !forOther ? pawn.carryTracker.CarriedThing : null;
                if (!forOther && !lying && !crawling && flight < 0f && ease.dropped != 0 && Dropping(ease))
                {
                    HoldPerson(pawn, ease.dropped, ease, fig, at, yaw, size, true);
                }
                if (!forOther && !lying && !crawling && flight < 0f && load == null && ease.workKind == "play")
                {
                    HoldBabyPlayed(pawn, ease, fig, at, yaw, size);
                }
                if (!lying && !crawling && load != null)
                {
                    DrawLoad(pawn, load, ease, fig, at, yaw, size);
                }
                else if (!forOther && !lying && !crawling && ease.stance != null && (ease.drawAt >= 0f ? DrawnShare(ease) > 0f : ease.drafted >= WeaponShown && WeaponShows(pawn)))
                {
                    DrawWeapon(pawn, ease.stance, aiming, fig, at, yaw, size, mask, ease);
                    inHands = true;
                }
                else if (!forOther && !lying && !crawling && flight < 0f && ease.shownWork >= ToolShown)
                {
                    DrawTool(pawn, ease.toolKind, fig, at, yaw, size);
                }
                if (!forOther && !lying && !crawling && flight < 0f && load == null && ease.shownWork >= ToolShown)
                {
                    DrawRolledJoint(pawn, ease, fig, at, yaw, size, lift);
                }
                // What it has but isn't holding, on the body (not lying, where it would go through the bed or ground, nor
                // swimming).
                if (!lying && !crawling && swim <= 0f)
                {
                    DrawHolstered(pawn, inHands, mask, fig, at, yaw, size);
                    DrawWornGear(pawn, mask, fig, at, yaw, size, HipShare(ease));
                }
            }
            catch (Exception e)
            {
                Log.Error(Tag + "drawing the skinned chibi failed, IsoRim's figures are kept: " + e);
                skin = null;
                ready = false;   // the baked frames were not loaded
                return false;
            }
            return true;
        }

        // The baked frames: standing or walking only.
        private static bool DrawBaked(FigureDrawContext ctx)
        {
            Pawn pawn = ctx.Pawn;
            if (!Standing(pawn))
            {
                return false;
            }
            float size = SizeOf(pawn);
            // The pose shows the nearest baked step between the walk and the full run; the stride follows smoothly.
            Ease ease = EaseOf(pawn);
            float level = ease.run * (groundPerCycle.Length - 1);
            int below = Mathf.Min(Mathf.FloorToInt(level), groundPerCycle.Length - 1);
            int above = Mathf.Min(below + 1, groundPerCycle.Length - 1);
            float ground = Mathf.Lerp(groundPerCycle[below], groundPerCycle[above], level - below);
            FigurePlacement placed;
            if (!ctx.ReadPlacement(new FigurePlacementOptions { Stride = ground * size }, out placed) || placed.Situation != FigureSituation.Standing)
            {
                return false;
            }
            float yaw = placed.Yaw, phase = placed.StridePhase, gait = placed.Gait;
            Vector3 at = new Vector3(placed.Position.x, Lift * size, placed.Position.z);
            ModelHandle model = idle;
            FaceLayers.Patch face = idleFace;
            if (gait >= WalkingGait)
            {
                int shown = Mathf.RoundToInt(level);
                ModelHandle[] cycle = cycles[shown];
                int frame = Mathf.FloorToInt(Mathf.Repeat(phase / (2f * Mathf.PI), 1f) * cycle.Length) % cycle.Length;
                model = cycle[frame];
                face = cycleFaces != null ? cycleFaces[shown][frame] : null;
            }
            Api.DrawModel(model, at, yaw, new Vector3(size, size, size), pawn.story.SkinColor);
            FaceLayers.Draw(pawn, face, at, yaw, size);
            return true;
        }
    }
}
