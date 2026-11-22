using System;
using System.Collections.Generic;
using System.Reflection;
using IsoRim;
using UnityEngine;
using Verse;

namespace M3AT
{
    // Facial Animation's face motion on the chibi: where its eyes look, and its brows, lids and mouth shifting. It moves
    // none of these by changing a texture (it has one eyeball shape, "normal"): each animation frame sets an offset per
    // controller (eyeballOffset for both eyes, eyeballOffsetL/R for one, lidOffset, browOffset, mouthOffset), which the
    // controller eases toward (Lerp 0.4 a frame) and its render node adds to where the layer is drawn, in world units
    // (cells) on the 2D head, x right and z up. Here each layer is read at its texture place less that offset over its
    // graphic's drawSize, so it moves by the same share of the face as in 2D; the shift goes to the shader in a per-draw
    // storage buffer (_MarusFaceShift), as the stacked materials are kept for each mix of layers and it changes every frame.
    // Left out: the race's fixed offset (drawOffsetForEachRace, already in the face's placing) and the head controller's
    // (it moves the whole 2D head, which the 3D head does its own way).
    //
    // A prosthetic eye's iris (Wounds.ProstheticEyes) is fitted to Facial Animation's own iris for that eye: measured from
    // the iris layer's texture (IrisOf: its middle and size), laid there at that size, cut to its alpha and moved with its
    // shift, so it has the iris's shape and follows the gaze.
    public static partial class FaceLayers
    {
        // How far the eyes move against Facial Animation's own (the mod options' eye movement): 1 as in 2D.
        public static float GazeStrength
        {
            get { return SettingsMod.Settings != null ? SettingsMod.Settings.gazeStrength : Settings.DefaultGazeStrength; }
        }

        private static readonly int shiftId = Shader.PropertyToID("_MarusFaceShift");
        private const int ShiftSlots = 4;   // vec4s: two layers each, eight layers (MarusFaceStack's StackSize)
        // Then each stacked layer's spread (RaceEyeSpread), a float each, four a vec4, from slot ShiftSlots (MarusFaceStack's
        // SpreadOf); a single layer's is its own slot's z.
        private const int SpreadSlots = 2;
        // Then each stacked layer's tuning by the player (FaceTuning.cs, TuneOf), a vec4 each from slot ShiftSlots +
        // SpreadSlots (MarusFaceStack's TuneOf); a single layer's is its second slot. All zero: as drawn untuned.
        private const int TuneSlots = 8;
        private static readonly Vector4[] shifts = new Vector4[ShiftSlots + SpreadSlots + TuneSlots];
        private static readonly Vector4[] noShifts = new Vector4[ShiftSlots + SpreadSlots + TuneSlots];

#if AUTOTEST
        // A shift added to every eye layer, in Facial Animation's offset units (cells on the 2D head, x right, z up), to
        // see which way and how far a gaze goes (AutoTest's prosthetics scenario).
        internal static Vector3 TestGaze;
#endif

        // A layer's shift on the face texture for its draw (one layer: the first slot), or none. Set on every block these
        // shaders are given, as the engine binds a storage slot a block leaves unset to the first one it does set (the
        // bones), which would read as a shift.
        private static void SetShift(MaterialPropertyBlock block, Vector2 shift, float spread = 0f, Vector3 tune = default(Vector3))
        {
            if (block == null)
            {
                return;
            }
            shifts[0] = new Vector4(shift.x, shift.y, spread, 0f);
            shifts[1] = new Vector4(tune.x, tune.y, tune.z, 0f);   // MarusFace.frag.glsl reads it there
            for (int i = 2; i < shifts.Length; i++)
            {
                shifts[i] = Vector4.zero;
            }
            block.SetVectorArray(shiftId, shifts);
        }

        internal static void SetNoShift(MaterialPropertyBlock block)
        {
            if (block != null)
            {
                block.SetVectorArray(shiftId, noShifts);
            }
        }

        // Layers stack[from .. from + count) (DrawStacked), two to a vec4.
        private static void SetStackShifts(MaterialPropertyBlock block, int from, int count)
        {
            for (int i = 0; i < shifts.Length; i++)
            {
                shifts[i] = Vector4.zero;
            }
            for (int i = 0; i < count && i < 2 * ShiftSlots; i++)
            {
                shifts[ShiftSlots + i / 4][i % 4] = stack[from + i].spread;
                Vector3 t = stack[from + i].tune;
                shifts[ShiftSlots + SpreadSlots + i] = new Vector4(t.x, t.y, t.z, 0f);
                Vector2 s = stack[from + i].shift;
                if ((i & 1) == 0)
                {
                    shifts[i / 2].x = s.x;
                    shifts[i / 2].y = s.y;
                }
                else
                {
                    shifts[i / 2].z = s.x;
                    shifts[i / 2].w = s.y;
                }
            }
            block.SetVectorArray(shiftId, shifts);
        }

        // ---- Reading the offsets.

        private class OffsetFields
        {
            public FieldInfo offset, offsetL, offsetR;
        }

        private static readonly Dictionary<Type, OffsetFields> offsetFields = new Dictionary<Type, OffsetFields>();
        private static bool offsetsFailed;

        // A protected field of Facial Animation's ControllerBaseComp<T, S>, found up the controller's own type's bases.
        private static FieldInfo FieldUp(Type t, string name)
        {
            for (; t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (f != null && f.FieldType == typeof(Vector3))
                {
                    return f;
                }
            }
            return null;
        }

        private static OffsetFields FieldsOf(Type t)
        {
            OffsetFields fields;
            if (!offsetFields.TryGetValue(t, out fields))
            {
                fields = new OffsetFields { offset = FieldUp(t, "Offset"), offsetL = FieldUp(t, "OffsetL"), offsetR = FieldUp(t, "OffsetR") };
                if (fields.offset == null && !offsetsFailed)
                {
                    offsetsFailed = true;
                    Log.Warning(Tag + "can't read Facial Animation's face offsets (its code has changed?): the eyes, brows and mouth won't shift on the chibi.");
                }
                offsetFields[t] = fields;
            }
            return fields;
        }

        // Where this layer is moved to on the face's texture places (u right, v up) by Facial Animation's animation now.
        private static Vector2 ShiftOf(PawnRenderNode node, Graphic g, string kind, string layer)
        {
            if (kind == "HeadControllerComp" || kind == "SkinControllerComp" || g == null)
            {
                return Vector2.zero;
            }
            object controller = controllerField.GetValue(node);
            if (controller == null)
            {
                return Vector2.zero;
            }
            OffsetFields fields = FieldsOf(controller.GetType());
            if (fields.offset == null)
            {
                return Vector2.zero;
            }
            Vector3 o = (Vector3)fields.offset.GetValue(controller);
            if (kind == "EyeballControllerComp")
            {
                if (layer.StartsWith("L_", StringComparison.Ordinal) && fields.offsetL != null)
                {
                    o += (Vector3)fields.offsetL.GetValue(controller);
                }
                else if (layer.StartsWith("R_", StringComparison.Ordinal) && fields.offsetR != null)
                {
                    o += (Vector3)fields.offsetR.GetValue(controller);
                }
#if AUTOTEST
                o += TestGaze;
#endif
                o *= GazeStrength;
            }
            Vector2 size = g.drawSize;
            float w = size.x > 0.01f ? size.x : 1.5f, h = size.y > 0.01f ? size.y : 1.5f;
            return new Vector2(o.x / w, o.z / h);
        }

        // ---- Another race's mouth, put where a human's is.

        // The face patch is laid out for Facial Animation's human textures, whose mouths all sit about MouthCentre's row
        // (rows 0.614 .. 0.637 of the texture from the top in vanilla, Experimentals and its retexture). A race's own face
        // mod may draw its mouth elsewhere for its own 2D head (Kiiro Race Facial Animation's at 0.601, so on the chibi it
        // sat well up toward the eyes), so a race other than the human has its mouth moved to that row: measured once per
        // race from the first mouth texture drawn for it that can be (alpha-weighted middle row), and the same move kept for all its
        // shapes, so opening and closing it doesn't jump. A move under MouthRowSlack is left alone.
        private const int MouthReadMost = 256;   // pixels across a mouth texture is read at, at most
        private const float MouthRowSlack = 0.004f;   // texture widths
        private const float MouthLeastAlpha = 0.5f;   // pixels' alpha summed, at the size read, to go by
        private static readonly Dictionary<ThingDef, float> raceMouthRows = new Dictionary<ThingDef, float>();
        private static readonly HashSet<Texture> mouthUnmeasured = new HashSet<Texture>();

        // The move (texture widths, v up) for this race's mouth layers.
        internal static float RaceMouthShift(Pawn pawn, Graphic g)
        {
            ThingDef race = pawn.def;
            float shift;
            string mod = ModOf(g).key;
            CheckPins(pawn, mod, g);
            if (PinnedMouth(mod, out shift))
            {
                return shift;   // the fit the player tuned this face mod on (FacePins.cs)
            }
            if (race == RimWorld.ThingDefOf.Human)
            {
                return 0f;
            }
            if (!raceMouthRows.TryGetValue(race, out shift))
            {
                Texture tex = LayerTexture(g);
                if (tex == null || mouthUnmeasured.Contains(tex))
                {
                    return 0f;   // measured from the next mouth texture drawn for it
                }
                bool measured = false;
                string why = "unreadable";
                try
                {
                    // Every pixel by its alpha, with no cut-off: Kiiro's closed mouth is a faint line whose alpha reaches
                    // only 0.36, which a cut-off at 0.3 all but missed.
                    int read = Math.Min(MouthReadMost, Math.Max(16, tex.width));
                    Color32[] px = Api.ReadSpritePixels(tex, read);   // rows bottom first
                    if (px != null && px.Length >= read * read)
                    {
                        double sum = 0, sv = 0;
                        for (int i = 0; i < read * read; i++)
                        {
                            float a = px[i].a / 255f;
                            sum += a;
                            sv += a * ((i / read) + 0.5) / read;
                        }
                        why = "alpha " + sum.ToString("F1");
                        if (sum > MouthLeastAlpha)
                        {
                            float row = (float)(sv / sum);   // v up
                            shift = MouthCentre.y - row;
                            if (Math.Abs(shift) < MouthRowSlack)
                            {
                                shift = 0f;
                            }
                            measured = true;
                            why = "middle at row " + (1f - row).ToString("F3") + " from the top";
                        }
                    }
                }
                catch (Exception e)
                {
                    why = e.Message;
                }
                if (!measured)
                {
                    // Not taken as no move: another of its mouth shapes (the open one) is tried when drawn.
                    mouthUnmeasured.Add(tex);
                    Log.Warning(Tag + "couldn't measure " + race.defName + "'s Facial Animation mouth from " + tex.name + " (" + why + "); its next mouth texture will be tried.");
                    return 0f;
                }
                raceMouthRows[race] = shift;
                Log.Message(Tag + race.defName + "'s Facial Animation mouth (" + tex.name + ", " + why + ") " + (shift == 0f ? "left where it is, as a human's." : "moved " + (shift < 0 ? "down " : "up ") + Math.Abs(shift).ToString("F3") + " of the face texture, to where a human's is."));
            }
            return shift;
        }

        // ---- Another race's eyes, put where a human's are, at a human's size and as far apart.

        // A race's own face mod draws its eyes for its own 2D head, so they may sit elsewhere on the texture than the human
        // faces the patch is laid out for, be another size or be set closer together: Kiiro Race Facial Animation's irises
        // are 0.100 of the texture apart, Milira's 0.094, the human sets' 0.114 .. 0.120 (on the chibi Kiiro's looked
        // pinched together); Ratkin's and Wolfein's sit 0.02 .. 0.03 lower than a human's, so with their mouths moved up to
        // the human row the face was squeezed between them; Wolfein's are a fifth wider. So for a race other than the human
        // the eye, lid, brow and lid option layers (SpreadsWithEyes) are fitted, measured once per race from its first
        // eyeball texture with both irises (each half's alpha-weighted middle and spread, as anim_scripts' measurement of the
        // sets had it): scaled about the eyes' middle so the whole eye is a human's size (EyeSizeOf: the eye whites, the
        // lid's bottom layer, of every eye style of the race's face mod averaged, against the human set loaded; by the
        // irises Wolfein's eyes, wide but with bigger irises still, came out a fifth too small, and Kiiro's, big irises in
        // small eyes, narrower still; through the layer's tuning, WithEyeFit), each half moved out by its spread to stand HumanIrisApart apart once scaled (the shader's
        // Spread), and moved up so its whites' middle is the human set's (EyeRowOf; HumanEyeRow by the irises if unreadable, through its shift). Each is left alone within its slack and kept within its most.
        private const float HumanIrisApart = 0.118f;   // texture widths between the irises' middles, a human face's
        private const float HumanEyeRow = 0.453f;      // and their middles' row, v up (Facial Animation 0.451, Experimentals 0.456)
        private const float HumanEyeSize = 0.058f;     // a whole eye's size (EyeSizeOf) if the human set's can't be measured: Facial Animation - Retexture's
        private const float EyeSpreadSlack = 0.003f;   // a spread under this is left alone
        private const float EyeSpreadMost = 0.03f;
        private const float EyeRowSlack = 0.004f;      // a move up or down under this is left alone
        private const float EyeRowMost = 0.05f;
        private const float EyeScaleSlack = 0.04f;     // a scale within this of 1 is left alone
        private const float EyeScaleLeast = 0.75f, EyeScaleMost = 1.3f;

        private struct EyeFit
        {
            public float spread;   // texture widths each half is moved out
            public float rise;     // texture widths the eyes are moved up
            public float scale;    // how many times bigger the eyes are drawn
            public float row;      // the race's own eye row (v up), the middle they are scaled about
        }

        private static readonly Dictionary<ThingDef, EyeFit> raceEyeFits = new Dictionary<ThingDef, EyeFit>();
        private static readonly Dictionary<ThingDef, string> raceEyeMods = new Dictionary<ThingDef, string>();   // the face mod each race's eyes come from
        // FaceLayers.Draw: the mod a race's irises (its _Basic eyeball layer) come from.
        internal static void NoteEyeMod(Pawn pawn, Graphic g)
        {
            FaceMod m = ModOf(g);
            if (m != null && m.key != "?")
            {
                raceEyeMods[pawn.def] = m.key;
                if (pawn.def == RimWorld.ThingDefOf.Human)
                {
                    NoteHumanEyeMod(pawn, m.key);   // a human's face set fitted by its own mod (FaceSetFit.cs)
                }
            }
        }

#if AUTOTEST
        internal static readonly Dictionary<int, float> TestEyeScales = new Dictionary<int, float>();   // each pawn's eyes' scale as last drawn (facepins)

        // The facepins scenario: a race's measured eye scale changed, as an update to the measuring would change it.
        internal static bool TestSetRaceEyeScale(ThingDef race, float scale)
        {
            EyeFit fit;
            if (!raceEyeFits.TryGetValue(race, out fit))
            {
                return false;
            }
            fit.scale = scale;
            raceEyeFits[race] = fit;
            return true;
        }

        internal static string TestEyeModOf(ThingDef race)
        {
            string mod;
            return raceEyeMods.TryGetValue(race, out mod) ? mod : null;
        }

        internal static float TestRaceEyeScale(ThingDef race)
        {
            EyeFit fit;
            return raceEyeFits.TryGetValue(race, out fit) ? fit.scale : 1f;
        }
#endif
        private static readonly HashSet<Texture> eyesUnmeasured = new HashSet<Texture>();
        private static float drawSpread;   // the pawn being drawn's (Draw), for its eye layers (SpreadOf)
        private static EyeFit drawEyeFit = new EyeFit { scale = 1f };   // and the rest of its eyes' fit (EyeRiseOf, WithEyeFit)

        // True for the layers that are moved with the eyes.
        private static bool SpreadsWithEyes(string kind)
        {
            return kind == "EyeballControllerComp" || kind == "LidControllerComp" || kind == "BrowControllerComp" || kind == "LidOptionControllerComp";
        }

        private static float SpreadOf(string kind)
        {
            return SpreadsWithEyes(kind) ? drawSpread + DevEyes.y : 0f;   // the dev window's on top (FaceDevTune.cs)
        }

        // The move up for one of the pawn being drawn's layers (its shift's y).
        private static float EyeRiseOf(string kind)
        {
            return SpreadsWithEyes(kind) ? drawEyeFit.rise + DevEyes.x : 0f;
        }

        // A layer's tuning (TuneOf's: its texture places times 1 + x, moved by (y, z)) with the eyes' scale laid on for an
        // eye layer: read at P + (t - P) / k about the race's eyes' middle P, after the tuning t, so the art comes out k
        // times bigger there and the player's tuning still moves it.
        private static Vector3 WithEyeFit(Vector3 tune, string kind)
        {
            float k = drawEyeFit.scale * DevEyes.z;
            if (!SpreadsWithEyes(kind) || k <= 0f || Mathf.Abs(k - 1f) < 1e-4f)
            {
                return tune;
            }
            // About the race's own eye row (a human's eyes, with no fit, about HumanEyeRow), moved up with the dev window's
            // rise as the eyes are, so its size slider doesn't move them.
            Vector2 middle = new Vector2(0.5f, (drawEyeFit.row > 0f ? drawEyeFit.row : HumanEyeRow) + DevEyes.x);
            float a = (1f + tune.x) / k;
            Vector2 b = new Vector2(tune.y, tune.z) / k + middle * (1f - 1f / k);
            return new Vector3(a - 1f, b.x, b.y);
        }

        // Sets the pawn being drawn's eye fit from what was measured of its race (Draw, before its layers, so the lids and
        // brows drawn before the eyeball take it); none for the human or before it is measured.
        private static void TakeEyeFit(Pawn pawn)
        {
            EyeFit fit;
            string mod;
            if (pawn.def == RimWorld.ThingDefOf.Human)
            {
                if (!HumanFitOf(pawn, out fit))
                {
                    fit = new EyeFit { scale = 1f };
                }
                drawEyeFit = fit;
                drawSpread = fit.spread;
            }
            else if (raceEyeMods.TryGetValue(pawn.def, out mod) && PinnedEyes(mod, out fit))
            {
                drawEyeFit = fit;   // the fit the player tuned this face mod on (FacePins.cs)
                drawSpread = fit.spread;
            }
            else if (pawn.def != RimWorld.ThingDefOf.Human && raceEyeFits.TryGetValue(pawn.def, out fit))
            {
                drawEyeFit = fit;
                drawSpread = fit.spread;
            }
            else
            {
                drawEyeFit = new EyeFit { scale = 1f };
                drawSpread = 0f;
            }
        }

        // The spread (texture widths each half is moved out) for this race, measured with the rest of its fit from g (an
        // eyeball layer's graphic) the first time one can be; 0 for the human or until measured.
        internal static float RaceEyeSpread(Pawn pawn, Graphic g)
        {
            ThingDef race = pawn.def;
            EyeFit fit;
            // The mod its irises come from (FaceLayers.Draw notes it from the _Basic layer; a race's face may take other
            // eyeball layers from Facial Animation's own), whose pinned fit this race's eyes take.
            string mod;
            if (!raceEyeMods.TryGetValue(race, out mod))
            {
                mod = null;
            }
            if (mod != null)
            {
                CheckPins(pawn, mod, null);
            }
            if (PinnedEyes(mod, out fit))
            {
                drawEyeFit = fit;
#if AUTOTEST
                TestEyeScales[pawn.thingIDNumber] = fit.scale;
#endif
                return fit.spread;
            }
            if (race == RimWorld.ThingDefOf.Human)
            {
                // By its own face mod (FaceSetFit.cs), not the race: a human's set may be any of the loaded ones.
                EyeFit human;
                if (!HumanFitOf(pawn, out human))
                {
                    human = new EyeFit { scale = 1f };
                }
                drawEyeFit = human;
#if AUTOTEST
                TestEyeScales[pawn.thingIDNumber] = human.scale;
#endif
                return human.spread;
            }
            if (raceEyeFits.TryGetValue(race, out fit))
            {
                drawEyeFit = fit;
#if AUTOTEST
                TestEyeScales[pawn.thingIDNumber] = fit.scale;
#endif
                return fit.spread;
            }
            Texture tex = LayerTexture(g);
            if (tex == null || eyesUnmeasured.Contains(tex))
            {
                return 0f;
            }
            string why = "unreadable";
            bool measured = false;
            fit = new EyeFit { scale = 1f };
            try
            {
                int read = Math.Min(MouthReadMost, Math.Max(16, tex.width));
                Color32[] px = Api.ReadSpritePixels(tex, read);   // rows bottom first
                if (px != null && px.Length >= read * read)
                {
                    double[] sum = new double[2], su = new double[2], sv = new double[2], suu = new double[2], svv = new double[2];
                    for (int i = 0; i < read * read; i++)
                    {
                        int c = i % read, r = i / read;
                        int half = c < read / 2 ? 0 : 1;
                        float a = px[i].a / 255f;
                        double u = (c + 0.5) / read, v = (r + 0.5) / read;
                        sum[half] += a;
                        su[half] += a * u;
                        sv[half] += a * v;
                        suu[half] += a * u * u;
                        svv[half] += a * v * v;
                    }
                    why = "alpha " + sum[0].ToString("F1") + " and " + sum[1].ToString("F1");
                    if (sum[0] > MouthLeastAlpha && sum[1] > MouthLeastAlpha)
                    {
                        double size = 0, row = 0;
                        for (int h = 0; h < 2; h++)
                        {
                            double mu = su[h] / sum[h], mv = sv[h] / sum[h];
                            size += (Math.Sqrt(Math.Max(0, suu[h] / sum[h] - mu * mu)) + Math.Sqrt(Math.Max(0, svv[h] / sum[h] - mv * mv))) / 4;
                            row += mv / 2;
                        }
                        float apart = (float)(su[1] / sum[1] - su[0] / sum[0]);
                        fit.row = (float)row;
                        float own = EyeSizeOf(RootOf(g)), human = HumanEyeSizeNow();
                        fit.scale = own > 0f ? Mathf.Clamp(human / own, EyeScaleLeast, EyeScaleMost) : 1f;
                        if (Mathf.Abs(fit.scale - 1f) < EyeScaleSlack)
                        {
                            fit.scale = 1f;
                        }
                        fit.spread = Mathf.Clamp((HumanIrisApart - apart * fit.scale) / 2f, -EyeSpreadMost, EyeSpreadMost);
                        if (Math.Abs(fit.spread) < EyeSpreadSlack)
                        {
                            fit.spread = 0f;
                        }
                        // The height by the eyes' whites, the whole eye, averaged over the styles (EyeSizeOf): Ratkin's and
                        // Milira's whites sit higher round their irises than the human sets', so with the irises on the
                        // human row their eyes read about 0.005 high. The irises' row only if the whites can't be read.
                        float ownRow = EyeRowOf(RootOf(g)), humanRow = HumanEyeRowNow();
                        if (ownRow > 0f && humanRow > 0f)
                        {
                            fit.row = ownRow;
                            fit.rise = Mathf.Clamp(humanRow - ownRow, -EyeRowMost, EyeRowMost);
                        }
                        else
                        {
                            fit.rise = Mathf.Clamp(HumanEyeRow - fit.row, -EyeRowMost, EyeRowMost);
                        }
                        if (Math.Abs(fit.rise) < EyeRowSlack)
                        {
                            fit.rise = 0f;
                        }
                        measured = true;
                        why = "irises " + apart.ToString("F3") + " apart, the eyes' middle at row " + fit.row.ToString("F3") + " (v up); whole eye " + (own > 0f ? own.ToString("F4") : "unmeasured")
                            + " against a human's " + human.ToString("F4");
                    }
                }
            }
            catch (Exception e)
            {
                why = e.Message;
            }
            if (!measured)
            {
                eyesUnmeasured.Add(tex);
                Log.Warning(Tag + "couldn't measure " + race.defName + "'s Facial Animation eyes from " + tex.name + " (" + why + "); its next eye texture will be tried.");
                return 0f;
            }
            raceEyeFits[race] = fit;
            drawEyeFit = fit;
            bool none = fit.spread == 0f && fit.rise == 0f && fit.scale == 1f;
            Log.Message(Tag + race.defName + "'s Facial Animation eyes (" + tex.name + ", " + why + ") " + (none ? "left as they are, as a human's."
                : "fitted to a human's: drawn " + fit.scale.ToString("F2") + " times the size, each moved " + (fit.spread >= 0 ? "out " : "in ") + Math.Abs(fit.spread).ToString("F3")
                + " and " + (fit.rise >= 0 ? "up " : "down ") + Math.Abs(fit.rise).ToString("F3") + " of the face texture."));
            return fit.spread;
        }

        // ---- The whole eye's size, over a face mod's eye styles.

        private static readonly Dictionary<string, float> eyeSizes = new Dictionary<string, float>();
        private static readonly Dictionary<string, float> eyeRows = new Dictionary<string, float>();   // the whites' middle row, v up


        // A face set's eye whites' middle row (v up, the mean over both eyes of every style), 0 if it has none.
        private static float EyeRowOf(string root)
        {
            if (root == null)
            {
                return 0f;
            }
            EyeSizeOf(root);
            float row;
            return eyeRows.TryGetValue(root, out row) ? row : 0f;
        }

        // The human face set's, as loaded (as HumanEyeSizeNow).
        private static float HumanEyeRowNow()
        {
            return RefEyeRow;   // the reference, which human sets are fitted to too (FaceSetFit.cs), whichever is loaded
        }

        // The face set's folder an eye layer's texture is in: its path up to its Eyes folder ("Things/Pawn/Ratkin/"), or null.
        private static string RootOf(Graphic g)
        {
            string path = g != null ? g.path : null;
            int at = path != null ? path.IndexOf("/Eyes/", StringComparison.Ordinal) : -1;
            return at >= 0 ? path.Substring(0, at + 1) : null;
        }

        // The whole eye's size (texture widths) of a face set: in each of its lid styles' eye whites (Lids/<style>/<gender>/
        // normal_bottom_south, as drawn: the last loaded mod's at each path, so a retexture's), each eye's opaque box, the root
        // of its width by its height, averaged over both eyes and every style. 0 if it has none.
        private static float EyeSizeOf(string root)
        {
            if (root == null)
            {
                return 0f;
            }
            float size;
            if (eyeSizes.TryGetValue(root, out size))
            {
                return size;
            }
            Dictionary<string, Texture2D> found = new Dictionary<string, Texture2D>();
            string lids = root + "Lids/";
            foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
            {
                ModContentHolder<Texture2D> holder = mod.GetContentHolder<Texture2D>();
                if (holder == null)
                {
                    continue;
                }
                foreach (KeyValuePair<string, Texture2D> t in holder.contentList)
                {
                    if (t.Value != null && t.Key.StartsWith(lids, StringComparison.Ordinal) && t.Key.EndsWith("/normal_bottom_south", StringComparison.Ordinal))
                    {
                        found[t.Key] = t.Value;   // a later mod's over an earlier's, as the game finds it
                    }
                }
            }
            double sum = 0, rows = 0;
            int n = 0;
            const int Read = 256;
            foreach (Texture2D tex in found.Values)
            {
                Color32[] px;
                try
                {
                    px = Api.ReadSpritePixels(tex, Read);
                }
                catch (Exception)
                {
                    px = null;
                }
                if (px == null || px.Length < Read * Read)
                {
                    continue;
                }
                for (int half = 0; half < 2; half++)
                {
                    int c0 = Read, c1 = -1, r0 = Read, r1 = -1;
                    for (int r = 0; r < Read; r++)
                    {
                        for (int c = half * Read / 2; c < (half + 1) * Read / 2; c++)
                        {
                            if (px[r * Read + c].a >= 128)
                            {
                                c0 = Math.Min(c0, c);
                                c1 = Math.Max(c1, c);
                                r0 = Math.Min(r0, r);
                                r1 = Math.Max(r1, r);
                            }
                        }
                    }
                    if (c1 >= c0 + 2 && r1 >= r0 + 2)
                    {
                        sum += Math.Sqrt((c1 - c0 + 1) * (r1 - r0 + 1)) / Read;
                        rows += (r0 + r1 + 1) / 2.0 / Read;   // rows bottom first, so v up
                        n++;
                    }
                }
            }
            size = n > 0 ? (float)(sum / n) : 0f;
            eyeSizes[root] = size;
            eyeRows[root] = n > 0 ? (float)(rows / n) : 0f;
            Log.Message(Tag + "the whole eye of the face set in " + root + ": " + (n > 0 ? size.ToString("F4") + " of the face texture, its middle at row " + eyeRows[root].ToString("F4") + " (v up), over " + found.Count + " eye styles." : "no eye whites found."));
            return size;
        }

        // The human face set's whole eye as loaded (Experimentals' folder, which its retextures fill, else the base set's),
        // HumanEyeSize if neither can be measured.
        private static float HumanEyeSizeNow()
        {
            return RefEyeSize;   // as HumanEyeRowNow
        }

        // ---- Facial Animation's iris, measured.

        // An iris texture's middle (texture places, u right, v up) and radius (of the face's width), as its alpha has it.
        private struct Iris
        {
            public bool found;
            public Vector2 middle;
            public float radius;
            public string why;   // what was read (the autotest's notes)
        }

        private const int IrisReadMost = 512;   // pixels across it is read at, at most (the texture's own if fewer)
        // The art's iris across its square, as a share of the square's width from its middle: make_prosthetic_decals.py's
        // IRIS over CELL (keep in step).
        private const float IrisArtShare = 34f / 128f;
        // By texture and half (Facial Animation's retexture draws both irises in one texture, and each eye's layer takes its
        // half by its mask, normal_L / normal_R).
        private static readonly Dictionary<KeyValuePair<Texture, bool>, Iris> irises = new Dictionary<KeyValuePair<Texture, bool>, Iris>();
        private static bool irisReadFailed;

        // Measured once per texture and half (left: u under 0.5): the alpha-weighted middle of what is solid there, and the
        // radius a solid disc with the same spread would have (each way's variance is r²/4), the mean of across and up, as
        // the art is round and is cut to the iris's own shape.
        private static Iris IrisOf(Texture tex, bool left)
        {
            Iris iris;
            if (tex == null)
            {
                return default(Iris);
            }
            KeyValuePair<Texture, bool> key = new KeyValuePair<Texture, bool>(tex, left);
            if (irises.TryGetValue(key, out iris))
            {
                return iris;
            }
            iris = default(Iris);
            int read = Mathf.Clamp(tex.width, 32, IrisReadMost);
            try
            {
                Color32[] px = Api.ReadSpritePixels(tex, read);   // rows bottom first
                iris.why = px == null ? "unreadable" : px.Length + " px";
                if (px != null && px.Length >= read * read)
                {
                    double sum = 0, su = 0, sv = 0, suu = 0, svv = 0, sl = 0;
                    for (int r = 0; r < read; r++)
                    {
                        for (int c = left ? 0 : read / 2; c < (left ? read / 2 : read); c++)
                        {
                            float a = px[r * read + c].a / 255f;
                            if (a < 0.5f)
                            {
                                continue;
                            }
                            double u = (c + 0.5) / read, v = (r + 0.5) / read;
                            sum += a;
                            sl += a * (px[r * read + c].r + px[r * read + c].g + px[r * read + c].b) / 765.0;
                            su += a * u;
                            sv += a * v;
                            suu += a * u * u;
                            svv += a * v * v;
                        }
                    }
                    iris.why += ", alpha " + sum.ToString("F0") + (sum > 0 ? ", brightness " + (sl / sum).ToString("F2") : "");
                    if (sum > 8)
                    {
                        double mu = su / sum, mv = sv / sum;
                        double varU = Math.Max(0, suu / sum - mu * mu), varV = Math.Max(0, svv / sum - mv * mv);
                        iris.middle = new Vector2((float)mu, (float)mv);
                        iris.radius = (float)(Math.Sqrt(varU) + Math.Sqrt(varV));   // the mean of 2 sqrt(var) each way
                        iris.found = iris.radius > 0.005f && iris.radius < 0.1f && Mathf.Abs(iris.middle.x - 0.5f) > iris.radius;
                    }
                }
            }
            catch (Exception e)
            {
                if (!irisReadFailed)
                {
                    irisReadFailed = true;
                    Log.Warning(Tag + "IsoRim could not read Facial Animation's iris, so prosthetic eyes keep their own size: " + e.Message);
                }
            }
            irises[key] = iris;
            return iris;
        }

        // This frame's eye layers (Draw): the iris layers (L_Basic, R_Basic) with their textures and shifts.
        private struct EyeLayer
        {
            public Texture tex;
            public Vector2 shift;
            public bool left;   // the half of the face it draws (HalfOf its mask)
        }

        // Which half of the face a layer's mask leaves it (true: u under 0.5), measured once per mask; by its name (L_ the
        // left half, as Facial Animation's normal_L mask is) without one or if it can't be read.
        private static readonly Dictionary<Texture, int> halves = new Dictionary<Texture, int>();
        private const int HalfRead = 32;

        private static bool HalfOf(Texture mask, string layer)
        {
            bool byName = layer.StartsWith("L_", StringComparison.Ordinal);
            if (mask == null)
            {
                return byName;
            }
            int half;
            if (!halves.TryGetValue(mask, out half))
            {
                half = -1;
                try
                {
                    Color32[] px = Api.ReadSpritePixels(mask, HalfRead);
                    if (px != null && px.Length >= HalfRead * HalfRead)
                    {
                        double sum = 0, su = 0;
                        for (int i = 0; i < HalfRead * HalfRead; i++)
                        {
                            float a = px[i].a / 255f * (px[i].r + px[i].g + px[i].b) / 765f;
                            sum += a;
                            su += a * ((i % HalfRead) + 0.5) / HalfRead;
                        }
                        if (sum > 1)
                        {
                            double mu = su / sum;
                            half = mu < 0.4 ? 1 : mu > 0.6 ? 0 : -1;
                        }
                    }
                }
                catch (Exception)
                {
                }
                halves[mask] = half;
            }
            return half < 0 ? byName : half == 1;
        }

        // An iris layer of this frame's (Draw), noted for a prosthetic eye on its half.
        private static void NoteEyeLayer(Graphic g, string layer, Vector2 shift)
        {
            Material m = g != null && !IsDummy(g) ? g.MatSouth : null;
            if (m == null || m.mainTexture == null)
            {
                return;
            }
            Texture mask = m.HasProperty(ShaderPropertyIDs.MaskTex) ? m.GetTexture(ShaderPropertyIDs.MaskTex) : null;
            eyeLayers.Add(new EyeLayer { tex = m.mainTexture, shift = shift, left = HalfOf(mask, layer) });
        }

        private static readonly List<EyeLayer> eyeLayers = new List<EyeLayer>();

#if AUTOTEST
        // The iris layers drawn last, as measured (the autotest's notes).
        internal static string IrisReport()
        {
            List<string> said = new List<string>();
            foreach (EyeLayer e in eyeLayers)
            {
                Iris i = IrisOf(e.tex, e.left);
                said.Add(e.tex.name + (e.left ? " left " : " right ") + (i.found ? "" : "not fitted, ") + "middle " + i.middle.ToString("F3") + " radius " + i.radius.ToString("F3") + " (" + i.why + ", " + e.tex.width + "x" + e.tex.height + " " + e.tex.GetType().Name + ")" + " shift " + e.shift.ToString("F4"));
            }
            return said.Count > 0 ? string.Join("; ", said) : "no iris layers";
        }
#endif

        // The iris layer on the half of the face at u, measured, with its shift; false without one.
        private static bool IrisOn(float u, out Iris iris, out EyeLayer layer)
        {
            for (int i = 0; i < eyeLayers.Count; i++)
            {
                if (eyeLayers[i].left != (u < 0.5f))
                {
                    continue;
                }
                Iris found = IrisOf(eyeLayers[i].tex, eyeLayers[i].left);
                if (found.found)
                {
                    iris = found;
                    layer = eyeLayers[i];
                    return true;
                }
            }
            iris = default(Iris);
            layer = default(EyeLayer);
            return false;
        }
    }
}
