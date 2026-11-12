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
        private static readonly Vector4[] shifts = new Vector4[ShiftSlots];
        private static readonly Vector4[] noShifts = new Vector4[ShiftSlots];

#if AUTOTEST
        // A shift added to every eye layer, in Facial Animation's offset units (cells on the 2D head, x right, z up), to
        // see which way and how far a gaze goes (AutoTest's prosthetics scenario).
        internal static Vector3 TestGaze;
#endif

        // A layer's shift on the face texture for its draw (one layer: the first slot), or none. Set on every block these
        // shaders are given, as the engine binds a storage slot a block leaves unset to the first one it does set (the
        // bones), which would read as a shift.
        private static void SetShift(MaterialPropertyBlock block, Vector2 shift)
        {
            if (block == null)
            {
                return;
            }
            shifts[0] = new Vector4(shift.x, shift.y, 0f, 0f);
            for (int i = 1; i < ShiftSlots; i++)
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
            for (int i = 0; i < ShiftSlots; i++)
            {
                shifts[i] = Vector4.zero;
            }
            for (int i = 0; i < count && i < 2 * ShiftSlots; i++)
            {
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
                    double sum = 0, su = 0, sv = 0, suu = 0, svv = 0;
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
                            su += a * u;
                            sv += a * v;
                            suu += a * u * u;
                            svv += a * v * v;
                        }
                    }
                    iris.why += ", alpha " + sum.ToString("F0");
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
