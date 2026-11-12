using System;
using System.Collections.Generic;
using System.IO;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // The pawn's injuries drawn on the skinned chibi's skin (SkinnedFigure.SetWounds), from the wound sheet
    // (Models/wounds.png, made by anim_scripts/key_wounds.py): a grid of Columns by Rows cells, each one wound, a row to
    // a kind (Cells). Each injury is drawn on the part it is on (PlaceOf: the bones of that part's skin), at a spot and
    // turn picked from its own load ID, so it stays put. Its size goes with its damage (the square root of it, so twice
    // the damage is twice the area), less on a thin limb (the place's room), so it shrinks as it heals. A bleeding
    // wound has blood drawn under it; a tended one is drawn as a bandage over it instead (bloodied if its kind of
    // injury bleeds, clean if not: a burn, a bruise); a scar (a permanent injury) is the same wound's sprite through the
    // shader's scar filter, in the pawn's skin colour, without blood, and a scarification is a cut's, at a set size. A part
    // that is gone shows as its stump (WalkFigures.CutLimbs) and gets none. A wound, its blood or a scar on an eye goes on
    // the eye itself, under the lids (FaceLayers.DrawEyeWounds), if Facial Animation is drawing one; its bandage stays on the face.
    //
    // They are drawn as the face layers are (FaceLayers, the MarusFaceLayer shader in its wound mode): sun-lit as the skin
    // under them, see-through and writing no depth, before the body's night light pass, so the night darkens them
    // with the body; queued after the face layers, so a wound on the face goes over them.
    //
    // A bionic prosthetic's panel seams and lights, and an archotech one's circuit traces and inset panels, are laid on the
    // same way (LimbDecals), from DecalFile's rows of cells (anim_scripts/make_prosthetic_decals.py), stacked under the
    // wound sheet's rows as it is loaded.
    public static class Wounds
    {
        private const string Tag = "[M3AT] ";
        public const string SheetFile = "Models/wounds.png";   // from the mod's root
        public const string DecalFile = "Models/prosthetic_decals.png";   // from the mod's root: more rows of cells
        private const int Columns = 3, SheetRows = 12;   // the wound sheet's grid (key_wounds.py's OUT_CELL px cells)
        private static int Rows = SheetRows;   // and the texture's, with the decals' row under it once loaded (Material)
        private const int DecalRow = SheetRows, ArchoRow = SheetRows + 1;   // the bionic and archotech decals' rows, from the top
        private const int Panel = 0, Strip = 1, JointLight = 2;   // the bionic row's cells
        private const int TraceA = 0, TraceB = 1, Inset = 2;      // the archotech row's
        private const int EyeRow = SheetRows + 2;   // the prosthetic eyes' row: bionic, archotech
        private const float EyeArtSpan = 0.09f;   // a prosthetic eye's cell on the face, texture places: past the eye's opening (about 0.065), as it is cut to the eye
        private const float DecalSpan = 0.13f;   // a decal's side, model units, on the upper arm or thigh; less further down
        private const float Span = 0.12f;   // model units: a cell's side on the trunk for a wound of SpanDamage
        private const float SpanDamage = 10f;   // damage (the injury's severity) drawn at Span; a bullet does about this
        private const float LeastSize = 0.45f, MostSize = 1.8f;   // shares of that, for the slightest and the worst wounds
        private const float BloodSize = 1.3f;   // blood under a bleeding wound, as a share of the wound's size
        private const float BandageSize = 1f;   // a bandage over a tended wound, as a share of the wound's size
        private const byte ScarAlpha = 217;   // how strongly a scar shows, of 255; under 255 is also what tells the shader it is one
        private const int ScarifiedStep = 8;   // a scarification's size, in Step's eighths: the game gives it no damage to size it by
        private const int Queue = 2999;   // after the face layers (2950 on), before IsoRim's light pass (3099)

        private const int Stab = 0, Burn = 1, Bruise = 2, Cut = 3, Scratch = 4, Gunshot = 5, Smear = 6, Drip = 7;
        private const int Bloodied = 8, Clean = 9;   // bandages

        // Each kind's cells on the sheet: (column, row from the top).
        private static readonly int[][] Cells =
        {
            new[] { 0, 0, 1, 0, 2, 0 },                    // stab
            new[] { 0, 1, 1, 1 },                          // burn
            new[] { 0, 2, 1, 2 },                          // bruise
            new[] { 0, 3, 1, 3, 2, 3 },                    // cut
            new[] { 0, 4, 1, 4, 2, 4 },                    // scratch
            new[] { 0, 6, 1, 6, 2, 6, 0, 7, 1, 7, 2, 7 },  // gunshot
            new[] { 0, 5, 1, 5, 2, 5 },                    // blood smeared or spattered
            new[] { 0, 8, 1, 8, 2, 8, 0, 9, 1, 9 },        // blood running down
            new[] { 2, 9, 0, 10, 1, 10, 2, 10 },           // a bandage, bloodied
            new[] { 0, 11, 1, 11, 2, 11 },                 // a bandage, clean
        };

        // Where the features are on the face, in Facial Animation's texture places (FaceLayers draws them there), so a
        // wound lands on the eye as drawn. The eyes as its textures have them; the nose, which it does not draw, half way
        // between them and the mouth; the jaw at the mouth and chin.
        private const float EyeU = 0.06f;   // each eye's middle, from the middle of the face
        private const float EyeV = 0.45f, NoseV = 0.415f, JawV = 0.37f;   // from the bottom
        // How far from a feature's middle a wound's middle may be, cells; less than the feature itself, so the wound stays on it.
        private const float EyeScatter = 0.015f, NoseScatter = 0.015f, JawScatter = 0.05f, EarScatter = 0.03f;
        private const float CrownScatter = 0.25f;   // a skull or brain wound: from the top of the head down to the brow
        private const float EyeWoundSpan = 0.075f;   // a wound on the eye: texture places, for a wound of SpanDamage; the eye's opening is about 0.065 wide
        private const float EyeWoundScatter = 0.012f;   // and how far off the eye's middle it may be, each way, texture places
        private const float EyeRoom = 0.5f, NoseRoom = 0.5f, JawRoom = 0.7f, EarRoom = 0.45f;   // a wound's size on each, shares of Span

        // Where on the body a part's wounds go: the bones of its skin, how much room it has (a share of Span), and, for a
        // part that is one spot on those bones (the face's features), that spot and how far from it (cells; 0: anywhere).
        private sealed class Place
        {
            public int[] bones;
            public float room;
            public Vector3 aim;
            public float scatter;
        }

        private static readonly Dictionary<BodyPartRecord, Place> places = new Dictionary<BodyPartRecord, Place>();
        private static readonly Dictionary<HediffDef, int> kinds = new Dictionary<HediffDef, int>();
        private static readonly List<SkinnedFigure.Decal> decals = new List<SkinnedFigure.Decal>();
        private static readonly List<FaceLayers.EyeWound> eyeWounds = new List<FaceLayers.EyeWound>();
        private static Texture2D sheet;
        private static Material material;
        private static bool tried;
        public static string Root;   // the mod's root folder (WalkFigures sets it)

        // The wound sheet, once Draw has loaded it.
        internal static Texture2D Sheet
        {
            get { return sheet; }
        }

        // The wounds to draw on this figure's eyes, as last laid on (Draw comes first in a frame); null with wounds off.
        public static FaceLayers.EyeWound[] EyeWoundsOn(SkinnedFigure.Posed fig)
        {
            return (WalkFigures.VisibleWounds || WalkFigures.Prosthetics) && fig.wounds != null ? fig.wounds.eyeWounds : null;
        }

        // Lays the pawn's wounds on the figure as End left it (once they or mask, the garments worn as worn, change) and
        // draws them, placed as Api.DrawModel places the model: position true space (cells), yaw degrees about the vertical,
        // size the model's scale.
        public static void Draw(Pawn pawn, SkinnedFigure skin, SkinnedFigure.Posed fig, GarmentMask mask, Apparel[] worn, Vector3 position, float yaw, float size, Color skinColour)
        {
            if ((!WalkFigures.VisibleWounds && !WalkFigures.Prosthetics) || pawn.health == null || !Api.ViewReady || Material() == null)
            {
                return;
            }
            try
            {
                int sign = SignOf(pawn, skinColour);
                if (skin.WoundsChanged(fig, sign, mask))
                {
                    Collect(pawn, skin, skinColour, fig.face != null && FaceLayers.Available);
                    skin.SetWounds(fig, decals, sign, mask, worn);
                    fig.wounds.eyeWounds = eyeWounds.ToArray();
                    decals.Clear();
                }
                Mesh mesh = skin.PoseWounds(fig);
                if (mesh == null)
                {
                    return;
                }
                // (The block's hit flash, which the wounds' mode leaves off, is the face's, as they share it.)
                MaterialPropertyBlock block = FaceLayers.BlockFor(SkinnedFigure.SmoothShading, 0);
                FaceLayers.SetNoShift(block);   // the face layers' shift, which these share the shader of
                Api.SubmitFigureMesh(mesh, material, FaceLayers.PlacedAt(position, yaw, size), block,
                    FigureMeshFlags.NoCull, 0, FaceLayers.Flash);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(Tag + "drawing wounds on the chibi failed: " + e, 61938205);
            }
        }

        // What the pawn's shown wounds are, as one number: each one's ID, size step, and whether it bleeds, is tended or
        // is a scar, and the skin colour the scars are made from.
        // Looked at every frame; the wounds are laid on again only when it changes.
        private static int SignOf(Pawn pawn, Color skinColour)
        {
            int sign = 17;
            List<Hediff> hediffs = pawn.health.hediffSet.hediffs;
            if (Rows > SheetRows)
            {
                sign = sign * 31 + WalkFigures.DecalSign(pawn);
            }
            for (int i = 0; i < hediffs.Count && WalkFigures.VisibleWounds; i++)
            {
                Hediff_Injury injury = hediffs[i] as Hediff_Injury;
                if (injury == null || injury.Part == null || KindOf(injury.def) < 0 || WalkFigures.OnProsthetic(pawn.health.hediffSet, injury.Part))
                {
                    continue;
                }
                sign = sign * 31 + injury.loadID;
                sign = sign * 31 + StepOf(injury);
                sign = sign * 31 + (injury.Bleeding ? 1 : 0) + (injury.IsPermanent() ? 2 : 0) + (injury.IsTended() ? 4 : 0);
                if (injury.IsPermanent())
                {
                    // Rounded, so a corpse's slow rot lays them on again only now and then.
                    Color32 c = skinColour;
                    sign = sign * 31 + ((c.r & 0xF8) << 16 | (c.g & 0xF8) << 8 | (c.b & 0xF8));
                }
            }
            return sign;
        }

        // An injury's size, as Step.
        private static int StepOf(Hediff_Injury injury)
        {
            return injury.def == HediffDefOf.Scarification ? ScarifiedStep : Step(injury.Severity);
        }

        // A wound's size for its damage as a whole number of eighths of Span, so healing lays it on again only now and then.
        private static int Step(float damage)
        {
            float size = Mathf.Clamp(Mathf.Sqrt(Mathf.Max(damage, 0f) / SpanDamage), LeastSize, MostSize);
            return Mathf.RoundToInt(size * 8f);
        }

        // The pawn's wounds as decals into decals: scars, then blood, then the wounds over it, then the bandages over all.
        // With eyes drawn (FaceLayers), all but a bandage on one goes into eyeWounds instead.
        private static void Collect(Pawn pawn, SkinnedFigure skin, Color skinColour, bool eyesDrawn)
        {
            decals.Clear();
            eyeWounds.Clear();
            Color32 skinTint = skinColour;
            List<Hediff> hediffs = pawn.health.hediffSet.hediffs;
            if (Rows > SheetRows)
            {
                LimbDecals(pawn, skin);   // under everything: no wound is laid on a prosthetic anyway
                if (eyesDrawn)
                {
                    ProstheticEyes(pawn);
                }
            }
            for (int pass = 0; pass < 4 && WalkFigures.VisibleWounds; pass++)
            {
                for (int i = 0; i < hediffs.Count; i++)
                {
                    Hediff_Injury injury = hediffs[i] as Hediff_Injury;
                    int kind = injury != null && injury.Part != null && !WalkFigures.OnProsthetic(pawn.health.hediffSet, injury.Part) ? KindOf(injury.def) : -1;
                    if (kind < 0)
                    {
                        continue;
                    }
                    bool scar = injury.IsPermanent(), tended = !scar && injury.IsTended();
                    if ((pass == 0) != scar || (pass == 1 && !injury.Bleeding) || (pass == 2 && tended) || (pass == 3 && !tended))
                    {
                        continue;
                    }
                    Place place = PlaceOf(injury.Part, skin);
                    if (place.bones.Length == 0)
                    {
                        continue;
                    }
                    uint hash = (uint)injury.loadID * 2654435761u;   // spread, so neighbouring IDs pick unlike cells
                    float span = Span * place.room * StepOf(injury) / 8f;
                    if (pass == 1)
                    {
                        kind = (hash >> 20) % 3 == 0 ? Smear : Drip;
                        span *= BloodSize;
                    }
                    else if (pass == 3)
                    {
                        kind = injury.def.injuryProps != null && injury.def.injuryProps.bleedRate > 0f ? Bloodied : Clean;
                        span *= BandageSize;
                    }
                    int[] cells = Cells[kind];
                    int cell = (int)((hash >> 8) % (uint)(cells.Length / 2));
                    int column = cells[2 * cell], row = cells[2 * cell + 1];
                    Color32 colour = scar ? new Color32(skinTint.r, skinTint.g, skinTint.b, ScarAlpha) : new Color32(255, 255, 255, 255);
                    if (pass != 3 && eyesDrawn && injury.Part.def.defName == "Eye")
                    {
                        eyeWounds.Add(EyeWoundOf(injury, column, row, pass == 1 ? BloodSize : 1f, kind == Drip, colour));
                        continue;
                    }
                    decals.Add(new SkinnedFigure.Decal
                    {
                        seed = injury.loadID,
                        bones = place.bones,
                        aim = place.aim,
                        scatter = place.scatter,
                        cell = new Rect((float)column / Columns, 1f - (row + 1f) / Rows, 1f / Columns, 1f / Rows),
                        span = span,
                        down = kind == Drip,
                        // A scar or bruise is in the skin, not on what covers it, and a bandage is under the clothes.
                        onClothes = !scar && injury.def != HediffDefOf.Scarification && kind != Bruise && kind != Bloodied && kind != Clean,
                        colour = colour,
                    });
                }
            }
        }

        // Each decorated limb's decals (WalkFigures.DecalLimbs). A bionic one: on each segment's skin a panel's seam, or on
        // the lower arm and leg a light strip running along it, and a lit joint plate at the elbow or knee; a hand or foot
        // one panel. An archotech one: on each segment two circuit traces and an inset panel, a hand or foot one trace.
        // Under the clothes (not onClothes), as the limb is. Their spots and turns go by the added part's load ID.
        private static void LimbDecals(Pawn pawn, SkinnedFigure skin)
        {
            decorated.Clear();
            WalkFigures.DecalLimbs(pawn, decorated);
            for (int i = 0; i < decorated.Count; i++)
            {
                string bone = decorated[i].bone;
                int seed = decorated[i].seed;
                int b = skin.BoneNamed(bone);
                if (b < 0)
                {
                    continue;
                }
                bool lower = bone.StartsWith("forearm", StringComparison.Ordinal) || bone.StartsWith("shin", StringComparison.Ordinal);
                bool end = bone.StartsWith("hand", StringComparison.Ordinal) || bone.StartsWith("foot", StringComparison.Ordinal);
                float span = DecalSpan * (end ? 0.6f : lower ? 0.85f : 1f);
                if (decorated[i].archotech)
                {
                    AddDecal(seed, b, ArchoRow, (seed & 1) == 0 ? TraceA : TraceB, span, true);   // the traces run along the limb
                    if (!end)
                    {
                        AddDecal(seed * 7 + 1, b, ArchoRow, (seed & 1) == 0 ? TraceB : TraceA, span, true);
                        AddDecal(seed * 7 + 2, b, ArchoRow, Inset, span * 0.8f, true);
                    }
                    continue;
                }
                AddDecal(seed, b, DecalRow, Panel, span, true);   // its seams across the limb
                if (lower)
                {
                    AddDecal(seed * 7 + 1, b, DecalRow, Strip, span * 1.1f, true);
                    AddDecal(seed * 7 + 2, b, DecalRow, JointLight, span * 0.75f, true);
                }
            }
        }

        private static readonly List<WalkFigures.LimbDecal> decorated = new List<WalkFigures.LimbDecal>();

        // A bionic or archotech eye (WalkFigures.ProstheticEyes) drawn over the eye Facial Animation draws: its art laid on
        // under the lids, as a wound on the eye is, so it blinks and closes with the eye; fitted to Facial Animation's own
        // iris on that half (FaceLayers.IrisOn: its place and size, cut to its shape), so it moves as the eye looks. Without
        // a readable iris, at EyeArtSpan where frame puts it, cut to the white.
        private static void ProstheticEyes(Pawn pawn)
        {
            eyes.Clear();
            WalkFigures.ProstheticEyes(pawn, eyes);
            for (int i = 0; i < eyes.Count; i++)
            {
                if (EyeRow >= Rows)
                {
                    return;   // an older decal file without the eyes' row
                }
                int column = eyes[i].archotech ? 1 : 0;
                eyeWounds.Add(new FaceLayers.EyeWound
                {
                    frame = new Vector4(EyeUOf(eyes[i].bone), EyeV, 1f / EyeArtSpan, 0f),
                    cell = new Vector4((float)column / Columns, 1f - (EyeRow + 1f) / Rows, 1f / Columns, 1f / Rows),
                    colour = new Color32(255, 255, 255, 255),
                    fitIris = true,
                });
            }
        }

        private static readonly List<WalkFigures.LimbDecal> eyes = new List<WalkFigures.LimbDecal>();

        private static void AddDecal(int seed, int bone, int row, int column, float span, bool down)
        {
            if (row >= Rows)
            {
                return;   // an older decal file without that row
            }
            decals.Add(new SkinnedFigure.Decal
            {
                seed = seed,
                bones = new[] { bone },
                cell = new Rect((float)column / Columns, 1f - (row + 1f) / Rows, 1f / Columns, 1f / Rows),
                span = span,
                down = down,   // a strip runs along the limb, its top up it at rest
                onClothes = false,
                colour = new Color32(255, 255, 255, 255),
            });
        }

        // A wound on an eye, its place and turn picked from its own load ID, so it stays put and its blood stays with it
        // (share: its size, of the wound's; down: blood running down, its top kept up the face).
        private static FaceLayers.EyeWound EyeWoundOf(Hediff_Injury injury, int column, int row, float share, bool down, Color32 colour)
        {
            System.Random r = new System.Random(injury.loadID);
            Vector2 at = new Vector2(EyeUOf(WalkFigures.SideOf(injury.Part) ?? "R"), EyeV);
            at.x += ((float)r.NextDouble() * 2f - 1f) * EyeWoundScatter;
            at.y += ((float)r.NextDouble() * 2f - 1f) * EyeWoundScatter;
            float turn = down ? 0f : (float)r.NextDouble() * 2f * Mathf.PI;
            float span = EyeWoundSpan * share * StepOf(injury) / 8f;
            return new FaceLayers.EyeWound
            {
                frame = new Vector4(at.x, at.y, Mathf.Cos(turn) / span, Mathf.Sin(turn) / span),
                cell = new Vector4((float)column / Columns, 1f - (row + 1f) / Rows, 1f / Columns, 1f / Rows),
                colour = colour,
            };
        }

        // The texture place across the face of an eye's middle, by the half of the face Facial Animation draws it on
        // (FaceLayers.MissingEyes), so it is the eye it shows as hurt or gone, which is not the figure's own left and right
        // as a limb's.
        private static float EyeUOf(string side)
        {
            return (side == "R") != FaceLayers.EyesSwapped() ? 0.5f + EyeU : 0.5f - EyeU;
        }

        // Which kind of wound an injury is drawn as, by its def's name (the game's and its expansions'; a mod's by what
        // its name says it is); -1 for one not drawn (psychic harm).
        private static int KindOf(HediffDef def)
        {
            int kind;
            if (kinds.TryGetValue(def, out kind))
            {
                return kind;
            }
            string name = def.defName;
            switch (name)
            {
                case "Stab":
                case "Bite":
                case "PorcupineQuill":
                    kind = Stab;
                    break;
                case "Burn":
                case "ChemicalBurn":
                case "AcidBurn":
                case "ElectricalBurn":
                case "VacuumBurn":
                case "BeamWound":
                case "Digested":
                    kind = Burn;
                    break;
                case "Bruise":
                case "Crush":
                case "Crack":
                case "Frostbite":
                case "Decayed":
                case "Misc":
                    kind = Bruise;
                    break;
                case "Cut":
                case "SurgicalCut":
                case "ExecutionCut":
                case "Scarification":
                    kind = Cut;
                    break;
                case "Scratch":
                case "Shredded":
                    kind = Scratch;
                    break;
                case "Gunshot":
                case "EnergyBolt":
                    kind = Gunshot;
                    break;
                default:
                    string n = name.ToLowerInvariant();
                    kind = n.Contains("psychic") ? -1
                        : n.Contains("burn") || n.Contains("beam") || n.Contains("laser") ? Burn
                        : n.Contains("shot") || n.Contains("bullet") || n.Contains("bolt") ? Gunshot
                        : n.Contains("stab") || n.Contains("bite") || n.Contains("pierc") || n.Contains("punct") ? Stab
                        : n.Contains("cut") || n.Contains("slash") ? Cut
                        : n.Contains("scratch") || n.Contains("claw") || n.Contains("shred") ? Scratch
                        : Bruise;
                    break;
            }
            kinds[def] = kind;
            return kind;
        }

        // Where a part's wounds go: found up the body from it, by the first part whose skin the chibi has bones for (an
        // organ goes on the trunk over it), else the trunk.
        private static Place PlaceOf(BodyPartRecord part, SkinnedFigure skin)
        {
            Place place;
            if (places.TryGetValue(part, out place))
            {
                return place;
            }
            string side = WalkFigures.SideOf(part) ?? "R";
            string[] bones = null;
            float room = 1f, scatter = 0f;
            Vector3 aim = Vector3.zero;
            for (BodyPartRecord p = part; p != null && bones == null; p = p.parent)
            {
                switch (p.def.defName)
                {
                    case "Head":
                        bones = new[] { "spine.006" };
                        break;
                    case "Skull":
                    case "Brain":
                        bones = new[] { "spine.006" };
                        aim = skin.HeadMost(Vector3.up);
                        scatter = CrownScatter;
                        break;
                    case "Ear":
                        // The head's widest point is level with the eyes and a little behind the middle. Left is -x.
                        bones = new[] { "spine.006" };
                        aim = skin.HeadMost(side == "L" ? Vector3.left : Vector3.right);
                        scatter = EarScatter;
                        room = EarRoom;
                        break;
                    case "Eye":
                        bones = new[] { "spine.006" };
                        scatter = skin.FacePlace(new Vector2(EyeUOf(side), EyeV), out aim) ? EyeScatter : 0f;
                        room = EyeRoom;
                        break;
                    case "Nose":
                        bones = new[] { "spine.006" };
                        scatter = skin.FacePlace(new Vector2(0.5f, NoseV), out aim) ? NoseScatter : 0f;
                        room = NoseRoom;
                        break;
                    case "Jaw":
                        bones = new[] { "spine.006" };
                        scatter = skin.FacePlace(new Vector2(0.5f, JawV), out aim) ? JawScatter : 0f;
                        room = JawRoom;
                        break;
                    case "Neck":
                        bones = new[] { "spine.004", "spine.005" };
                        room = 0.6f;
                        break;
                    case "Shoulder":
                        bones = new[] { "shoulder." + side, "upper_arm." + side };
                        room = 0.6f;
                        break;
                    case "Arm":
                        bones = new[] { "upper_arm." + side, "forearm." + side };
                        room = 0.6f;
                        break;
                    case "Humerus":
                        bones = new[] { "upper_arm." + side };
                        room = 0.6f;
                        break;
                    case "Radius":
                        bones = new[] { "forearm." + side };
                        room = 0.55f;
                        break;
                    case "Hand":
                        bones = new[] { "hand." + side };
                        room = 0.45f;
                        break;
                    case "Finger":
                        string label = p.untranslatedCustomLabel ?? "";
                        bones = label.IndexOf("thumb", StringComparison.OrdinalIgnoreCase) >= 0
                            ? new[] { "thumb." + side, "thumb." + side + ".001" }
                            : new[] { "hand." + side + ".001", "hand." + side + ".002" };
                        room = 0.3f;
                        break;
                    case "Leg":
                        bones = new[] { "thigh." + side, "shin." + side };
                        room = 0.7f;
                        break;
                    case "Femur":
                        bones = new[] { "thigh." + side };
                        room = 0.7f;
                        break;
                    case "Tibia":
                        bones = new[] { "shin." + side };
                        room = 0.6f;
                        break;
                    case "Foot":
                        bones = new[] { "foot." + side };
                        room = 0.45f;
                        break;
                    case "Toe":
                        bones = new[] { "toe." + side };
                        room = 0.3f;
                        break;
                    case "Clavicle":
                        bones = WalkFigures.SideOf(p) != null ? new[] { "shoulder." + side } : new[] { "spine.003", "spine.004" };
                        room = 0.6f;
                        break;
                    case "Ribcage":
                    case "Rib":
                    case "Sternum":
                    case "Heart":
                    case "Lung":
                        bones = new[] { "spine.002", "spine.003", "spine.004" };
                        break;
                    case "Stomach":
                    case "Liver":
                    case "Kidney":
                    case "Spine":
                        bones = new[] { "spine", "spine.001", "spine.002" };
                        break;
                    case "Pelvis":
                        bones = new[] { "spine", "pelvis.L", "pelvis.R" };
                        break;
                    case "Torso":
                        bones = new[] { "spine", "spine.001", "spine.002", "spine.003", "pelvis.L", "pelvis.R" };
                        break;
                }
            }
            bones = bones ?? new[] { "spine", "spine.001", "spine.002", "spine.003", "pelvis.L", "pelvis.R" };
            List<int> found = new List<int>();
            foreach (string b in bones)
            {
                int i = skin.BoneNamed(b);
                if (i >= 0)
                {
                    found.Add(i);
                }
            }
            place = new Place { bones = found.ToArray(), room = room, aim = aim, scatter = scatter };
            places[part] = place;
            return place;
        }

        // The wounds' material (MarusFaceLayer in its wound mode, the sheet on it), made once; null if the shader or the
        // sheet is missing (said once).
        // The wound sheet with the prosthetics' decals' rows stacked under it (Rows that many more), if that file is there
        // and its cells are the sheet's size; else the sheet as it is, and no decals are laid on.
        private static Texture2D WithDecals(Texture2D wounds)
        {
            string path = Path.Combine(Root, DecalFile);
            if (!File.Exists(path))
            {
                return wounds;
            }
            Texture2D extra = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            extra.LoadImage(File.ReadAllBytes(path));
            int cell = wounds.height / SheetRows;
            if (extra.width != wounds.width || extra.height % cell != 0 || extra.height == 0)
            {
                Log.Warning(Tag + DecalFile + " is " + extra.width + " by " + extra.height + ", not rows of the wound sheet's cells; no prosthetic decals.");
                return wounds;
            }
            Color32[] low = extra.GetPixels32(), high = wounds.GetPixels32();   // rows bottom first: the decals go under
            Color32[] all = new Color32[low.Length + high.Length];
            low.CopyTo(all, 0);
            high.CopyTo(all, low.Length);
            Texture2D both = new Texture2D(wounds.width, wounds.height + extra.height, TextureFormat.RGBA32, true);
            both.SetPixels32(all);
            Rows = SheetRows + extra.height / cell;
            return both;
        }

        private static Material Material()
        {
            if (tried)
            {
                return material;
            }
            tried = true;
            Shader shader = DefDatabase<ShaderDef>.GetNamedSilentFail("MarusFaceLayer")?.Shader;
            string path = Root != null ? Path.Combine(Root, SheetFile) : null;
            if (shader == null || path == null || !File.Exists(path))
            {
                Log.Warning(Tag + "the wound sheet (" + SheetFile + ") or the MarusFaceLayer shader is missing, so wounds are not drawn on the chibi.");
                return null;
            }
            sheet = new Texture2D(2, 2, TextureFormat.RGBA32, true);
            sheet.LoadImage(File.ReadAllBytes(path));
            sheet = WithDecals(sheet);
            sheet.wrapMode = TextureWrapMode.Clamp;
            sheet.filterMode = FilterMode.Trilinear;
            sheet.name = "Marus_Wounds";
            sheet.Apply(true, true);   // the mipmaps made, and the copy in memory let go: it is only drawn
            material = new Material(shader);
            material.name = "Marus_Wounds";
            material.mainTexture = sheet;
            material.color = Color.white;
            material.SetFloat("_FaceMode", 6f);
            material.renderQueue = Queue;
            return material;
        }
    }
}
