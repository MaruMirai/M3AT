using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // A race's own parts on the chibi (the raceParts mod option): every Humanoid Alien Races body add-on the game draws on
    // the pawn (ears, tails, horns, wings, ...) built in 3D from its pictures (AddonBuilder.cs), as hair is built from its
    // sprites, so a race with no models of its own still has its look. Read from the game's own render tree, as IsoRim's
    // addon cards are (AlienCards.cs there): each add-on node's picture, mesh and matrix against the race's head or body
    // node, per facing, with HAR's offsets, sizes, mirroring and its own hiding (CanDrawNow: a hat over the ears) all in
    // them. Left out: the hair add-ons (AddonHair.cs builds them into the hair), a cat-like race's ears and tail (the
    // chibi's own, CatEars.cs, CatTail.cs), and an add-on that lies on the head or body in every picture it has, as face
    // markings do, which a hull would stand out of the face as a lump. Looked at again every AddonLook ticks or when what
    // is worn changes. The chibi's own ears are taken off (No_Human_Ears) for a race with ears among them.
    public static partial class WalkFigures
    {
        private const int AddonLook = 600;           // ticks between looks at a pawn's add-ons
        private const float AddonDecalShare = 0.85f; // of an add-on's pixels on the head (body) in every picture: a marking, left out

        private sealed class RaceParts
        {
            public int tick;
            public int worn;
            public int version;   // AddOns.Version they were gathered for (a content pack's source changed: gathered again)
            public readonly List<SkinnedFigure.AddonSource> sources = new List<SkinnedFigure.AddonSource>();
            public readonly List<Color> tints = new List<Color>();
            public readonly List<bool> tails = new List<bool>();   // a body part below the waist the armour option hides, as the cat tail
            public bool ears;
            public string modelEars, modelTail;   // the race's own model ears and tail drawn in place of those add-ons (RaceModels), null for none
            public Color earTint, tailTint;       // in the add-on's colour times its art's
            public readonly float[] earLean = new float[2];   // degrees each model ear rests leaned out, by its add-on's picture (EarLeanOf)
            public bool wingL, wingR;             // a winged race's model wings (RaceModels' wings), by its wing parts
            public Color wingTint;
        }

        private static readonly Dictionary<int, RaceParts> raceParts = new Dictionary<int, RaceParts>();
        private static bool racePartsFailed;

        private delegate void AdjustParmsCall(PawnRenderTree tree, ref PawnDrawParms parms);
        private static AdjustParmsCall adjustParms;
        private static bool adjustLooked;
        private static FieldInfo addonField;

        internal static bool RacePartsOn
        {
            get { return SettingsMod.Settings == null || SettingsMod.Settings.raceParts; }
        }

        // True if the pawn's race parts (as last looked at) have ears, so the chibi's own are taken off.
        private static bool HasRaceEars(Pawn pawn)
        {
            RaceParts r;
            return raceParts.TryGetValue(pawn.thingIDNumber, out r) && ((RacePartsOn && r.ears) || (RaceModelsOn && r.modelEars != null));
        }

        // For the autotest: the race parts as last looked at, by name, "" for none.
        internal static string RacePartNames(Pawn pawn)
        {
            RaceParts r;
            if (!raceParts.TryGetValue(pawn.thingIDNumber, out r))
            {
                return "";
            }
            List<string> names = new List<string>();
            foreach (SkinnedFigure.AddonSource s in r.sources)
            {
                SkinnedFigure.Piece built = s.glow != null ? null : skin.AddonPiece(s);
                System.Text.StringBuilder boxes = new System.Text.StringBuilder();
                for (int f = 0; f < 4; f++)
                {
                    Vector4 b = s.reference[f];
                    boxes.Append(" f").Append(f).Append(s.px[f] == null ? " -" : " ref " + b.x.ToString("0.00") + ".." + b.y.ToString("0.00") + " x " + b.z.ToString("0.00") + ".." + b.w.ToString("0.00")
                        + " uv " + string.Join(",", Array.ConvertAll(s.toUv[f], a => a.ToString("0.00"))));
                }
                names.Add(s.name.Substring(s.name.IndexOf('/') + 1) + (s.onHead ? " (head" : " (body") + (s.glow != null ? ", a glow card" : built != null ? ", " + built.tris.Length / 3 + " triangles" : ", nothing built") + boxes + ")");
            }
            return string.Join(", ", names);
        }

        // For the autotest: each race part's picture atlas (north, east, south, west) written into folder.
        internal static void SaveRaceParts(Pawn pawn, string folder, string prefix)
        {
            RaceParts r;
            if (!raceParts.TryGetValue(pawn.thingIDNumber, out r))
            {
                return;
            }
            foreach (SkinnedFigure.AddonSource s in r.sources)
            {
                SkinnedFigure.Piece built = s.glow != null ? null : skin.AddonPiece(s);
                if (built != null && built.texture != null)
                {
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder, prefix + "_" + GenText.SanitizeFilename(s.name.Replace('/', '_')) + ".png"), built.texture.EncodeToPNG());
                }
            }
        }

        private static RaceParts RacePartsOf(Pawn pawn, int worn)
        {
            RaceParts r;
            int now = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            if (raceParts.TryGetValue(pawn.thingIDNumber, out r) && r.worn == worn && r.version == AddOns.Version && now - r.tick < AddonLook && now >= r.tick)
            {
                return r;
            }
            r = new RaceParts { tick = now, worn = worn, version = AddOns.Version };
            if (raceParts.Count > 512)
            {
                raceParts.Clear();
            }
            raceParts[pawn.thingIDNumber] = r;
            try
            {
                Gather(pawn, r);
            }
            catch (Exception e)
            {
                racePartsFailed = true;
                Log.Warning(Tag + "couldn't read the race parts of " + pawn.def.defName + " from the game, so race parts are left off: " + e.Message);
            }
            return r;
        }

        private static void Gather(Pawn pawn, RaceParts r)
        {
            PawnRenderTree tree = pawn.Drawer != null && pawn.Drawer.renderer != null ? pawn.Drawer.renderer.renderTree : null;
            if (tree == null)
            {
                return;
            }
            if (!tree.Resolved)
            {
                tree.EnsureInitialized(PawnRenderFlags.Clothes | PawnRenderFlags.Headgear);
            }
            if (tree.rootNode == null)
            {
                return;
            }
            RaceModel winged;
            if (RaceModelsOn && RaceModels.TryGetValue(pawn.def.defName, out winged) && winged.wings != null && skin.HasModelTail(winged.wings[0]) && skin.HasModelTail(winged.wings[1]))
            {
                WingsOf(pawn, tree, out r.wingL, out r.wingR, out r.wingTint);
            }
            List<PawnRenderNode> nodes = new List<PawnRenderNode>();
            Walk(tree.rootNode, nodes);
            if (nodes.Count == 0)
            {
                return;
            }
            if (!adjustLooked)
            {
                adjustLooked = true;
                MethodInfo m = AccessTools.Method(typeof(PawnRenderTree), "AdjustParms");
                adjustParms = m != null ? (AdjustParmsCall)Delegate.CreateDelegate(typeof(AdjustParmsCall), null, m) : null;
            }
            PawnRenderNode headNode, bodyNode;
            tree.TryGetNodeByTag(PawnRenderNodeTagDefOf.Head, out headNode);
            tree.TryGetNodeByTag(PawnRenderNodeTagDefOf.Body, out bodyNode);
            SkinnedFigure.AddonSource[] made = new SkinnedFigure.AddonSource[nodes.Count];
            Color[] colours = new Color[nodes.Count];
            bool[] onHead = new bool[nodes.Count];
            string[] skip = new string[nodes.Count];
            bool[] hairAddon = new bool[nodes.Count];
            string[] model = new string[nodes.Count];
            bool[] modelIsEars = new bool[nodes.Count];
            // Ears or a tail the pawn has already, from its genes or a content pack (its ears and tail for the race, by
            // ExpansionApi or its patches on EarFileOf and HasCatTail): the race's ear and tail add-ons are left out, so
            // they aren't drawn twice.
            bool earsElsewhere = EarFileOf(pawn) != null, tailElsewhere = HasCatTail(pawn);
            for (int i = 0; i < nodes.Count; i++)
            {
                PawnRenderNode n = nodes[i];
                onHead[i] = headNode != null && Under(n, headNode);
                object addon = AddonOf(n);
                Graphic g = n.PrimaryGraphic;
                string path = g != null ? g.path : n.Props != null ? n.Props.texPath : null;
                if ((addon != null && HairBuilder.HairAddon(addon)) || HairBuilder.HairGraphic(pawn.def, path))
                {
                    skip[i] = "hair";   // unless it is small (below), which the hair leaves out: a tuft (an ahoge) is built as a part
                    hairAddon[i] = true;
                }
                else if (path != null)
                {
                    List<string> words = HairBuilder.AddonWords(System.IO.Path.GetFileName(path));
                    foreach (string w in new[] { "ear", "tail" })
                    {
                        if ((words.Contains(w) || words.Contains(w + "s")) && HairBuilder.CatLikeAddon(pawn.def, w))
                        {
                            skip[i] = "the chibi's own " + w;
                        }
                        else if ((words.Contains(w) || words.Contains(w + "s")) && (w == "ear" ? earsElsewhere : tailElsewhere))
                        {
                            skip[i] = w + " from genes or a content pack";
                        }
                    }
                }
                if (skip[i] == null && path != null)
                {
                    model[i] = ModelFileFor(pawn.def, System.IO.Path.GetFileName(path), out modelIsEars[i]);   // its pictures still read, for its colour
                }
                List<string> named = path != null ? HairBuilder.AddonWords(System.IO.Path.GetFileName(path)) : new List<string>();
                bool thin = named.Exists(w => w == "ear" || w == "ears" || w == "wing" || w == "wings" || w == "fin" || w == "fins" || w == "feather" || w == "feathers");
                bool isTail = named.Contains("tail") || named.Contains("tails");
                bool isEar = named.Contains("ear") || named.Contains("ears");
                made[i] = new SkinnedFigure.AddonSource { onHead = onHead[i], thin = thin, tail = isTail, ear = isEar, name = pawn.def.defName + "/" + (path != null ? System.IO.Path.GetFileName(path) : "addon" + i) };
            }
            for (int f = 0; f < 4; f++)
            {
                PawnDrawParms parms = PawnDrawParms.Default;
                parms.pawn = pawn;
                parms.matrix = Matrix4x4.identity;
                parms.facing = new Rot4(f);
                parms.rotDrawMode = RotDrawMode.Fresh;
                parms.posture = PawnPosture.Standing;
                parms.flags = PawnRenderFlags.Clothes | PawnRenderFlags.Headgear;
                parms.dead = false;
                if (adjustParms != null)
                {
                    adjustParms(tree, ref parms);
                }
                Matrix4x4 headM = Matrix4x4.identity, bodyM = Matrix4x4.identity;
                bool haveHead = headNode != null && tree.TryGetMatrix(headNode, parms, out headM);
                bool haveBody = bodyNode != null && tree.TryGetMatrix(bodyNode, parms, out bodyM);
                Vector4 headBox = haveHead ? ReferenceBox(pawn, headNode, parms, f, true) : default(Vector4);
                Vector4 bodyBox = haveBody ? ReferenceBox(pawn, bodyNode, parms, f, false) : default(Vector4);
                for (int i = 0; i < nodes.Count; i++)
                {
                    PawnRenderNode n = nodes[i];
                    if ((skip[i] != null && !hairAddon[i]) || (onHead[i] && !haveHead) || (!onHead[i] && !haveBody) || !n.Worker.CanDrawNow(n, parms))
                    {
                        continue;
                    }
                    Material mat = n.Worker.GetFinalizedMaterial(n, parms);
                    Mesh mesh = n.GetMesh(parms);
                    Matrix4x4 nm;
                    if (mat == null || mat.mainTexture == null || mesh == null || !tree.TryGetMatrix(n, parms, out nm))
                    {
                        continue;
                    }
                    float[] toUv = UvMap(mesh, (onHead[i] ? headM : bodyM).inverse * nm);
                    Color32[] px = toUv != null ? PixelsOf(mat.mainTexture) : null;
                    if (px == null)
                    {
                        continue;
                    }
                    made[i].px[f] = px;
                    string shader = mat.shader != null ? mat.shader.name : "";
                    if (shader.IndexOf("Glow", StringComparison.OrdinalIgnoreCase) >= 0 || shader.IndexOf("Mote", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (f == 2 || made[i].glow == null)
                        {
                            made[i].glow = mat;   // a glow (a halo): drawn as a glow card of its own material
                        }
                    }
                    made[i].toUv[f] = toUv;
                    made[i].reference[f] = onHead[i] ? headBox : bodyBox;
                    if (f == 2 || colours[i].a == 0f)
                    {
                        colours[i] = mat.color;
                    }
                }
            }
            for (int i = 0; i < nodes.Count; i++)
            {
                SkinnedFigure.AddonSource s = made[i];
                if (hairAddon[i] && SmallTuft(s))
                {
                    skip[i] = null;
                }
                if (skip[i] != null || (s.px[0] == null && s.px[1] == null && s.px[2] == null && s.px[3] == null))
                {
                    continue;
                }
                if (model[i] != null)
                {
                    Color tint = colours[i] * SkinnedFigure.AddonMean(s);
                    if (modelIsEars[i])
                    {
                        r.modelEars = model[i];
                        r.earTint = tint;
                        RaceModel lean;
                        if (RaceModels.TryGetValue(pawn.def.defName, out lean))
                        {
                            int side;
                            float by = EarLeanOf(lean, s, out side);
                            r.earLean[side] = Mathf.Max(r.earLean[side], by);   // (each ear its own add-on: Miho's EarLeft, EarRight)
                        }
                    }
                    else
                    {
                        r.modelTail = model[i];
                        r.tailTint = tint;
                    }
                    continue;
                }
                if (s.glow == null && Marking(pawn, s, onHead[i] ? headNode : bodyNode, tree))
                {
                    continue;
                }
                System.Text.StringBuilder key = new System.Text.StringBuilder(s.name).Append(s.onHead ? "|h" : "|b");
                for (int f = 0; f < 4; f++)
                {
                    if (s.px[f] == null)
                    {
                        key.Append("|-");
                        continue;
                    }
                    key.Append('|').Append(Hash(s.px[f]));
                    foreach (float a in s.toUv[f])
                    {
                        key.Append(',').Append(a.ToString("0.###"));
                    }
                    Vector4 b = s.reference[f];
                    key.Append(';').Append(b.x.ToString("0.###")).Append(',').Append(b.y.ToString("0.###")).Append(',').Append(b.z.ToString("0.###")).Append(',').Append(b.w.ToString("0.###"));
                }
                s.key = key.ToString();
                r.sources.Add(s);
                r.tints.Add(colours[i]);
                List<string> words = HairBuilder.AddonWords(s.name.Substring(s.name.IndexOf('/') + 1));
                bool tail = words.Contains("tail") || words.Contains("tails");
                r.tails.Add(!s.onHead && tail);
                r.ears |= s.onHead && (words.Contains("ear") || words.Contains("ears"));
            }
        }

        private const float TuftShare = 0.15f;   // AddonHair's AddonLeast: a hair add-on under this of the head's area in front is a tuft

        // A hair add-on the hair leaves out as too small (AddonHair.cs), by its front picture against the head's box there
        // (about three quarters of which the head fills).
        private static bool SmallTuft(SkinnedFigure.AddonSource s)
        {
            int f = s.px[2] != null ? 2 : 0;
            Vector4 box = s.reference[f];
            if (s.px[f] == null || box.y <= box.x)
            {
                return false;
            }
            float[] a = s.toUv[f];
            float pixelArea = 1f / (SkinnedFigure.AddonTexSize * SkinnedFigure.AddonTexSize * Mathf.Abs(a[1] * a[5] - a[2] * a[4]));
            int n = 0;
            foreach (Color32 c in s.px[f])
            {
                n += c.a >= 128 ? 1 : 0;
            }
            return n * pixelArea < TuftShare * 0.75f * (box.y - box.x) * (box.w - box.z);
        }

        private static readonly Dictionary<Texture, Color32[]> addonPixels = new Dictionary<Texture, Color32[]>();

        // A texture's pixels at AddonTexSize, read once (IsoRim may read them back from the graphics card).
        private static Color32[] PixelsOf(Texture t)
        {
            Color32[] px;
            if (t == null)
            {
                return null;
            }
            if (!addonPixels.TryGetValue(t, out px))
            {
                if (addonPixels.Count > 1024)
                {
                    addonPixels.Clear();
                }
                px = Api.ReadSpritePixels(t, SkinnedFigure.AddonTexSize);
                addonPixels[t] = px;
            }
            return px;
        }

        private static int Hash(Color32[] px)
        {
            int h = 17;
            for (int i = 0; i < px.Length; i += 7)
            {
                h = h * 31 + px[i].a + (px[i].r << 8);
            }
            return h;
        }

        // Every HAR body add-on node under the root.
        private static void Walk(PawnRenderNode n, List<PawnRenderNode> into)
        {
            if (n == null)
            {
                return;
            }
            if (n.GetType().Name.IndexOf("BodyAddon", StringComparison.Ordinal) >= 0)
            {
                into.Add(n);
            }
            if (n.children != null)
            {
                for (int i = 0; i < n.children.Length; i++)
                {
                    Walk(n.children[i], into);
                }
            }
        }

        private static object AddonOf(PawnRenderNode n)
        {
            if (addonField == null || !addonField.DeclaringType.IsInstanceOfType(n))
            {
                addonField = AccessTools.Field(n.GetType(), "addon");
            }
            return addonField != null ? addonField.GetValue(n) : null;
        }

        private static bool Under(PawnRenderNode n, PawnRenderNode ancestor)
        {
            for (PawnRenderNode p = n.parent; p != null; p = p.parent)
            {
                if (p == ancestor)
                {
                    return true;
                }
            }
            return false;
        }

        // Where the pawn's head lies over its body picture as the game draws it, per facing (SpriteClothes.cs: what the head
        // hides there in the game, a shirt's neck hole or a collar's back, is left off the painted garment): the head's
        // picture (its node's, and its head type's in case that one is blank, as Facial Animation's can be) and the map
        // from a place on the body's picture to one on the head's: hu = m0 + m1 u + m2 v, hv = m3 + m4 u + m5 v. Through the
        // render tree, as the race parts are, so the body type's head offset, a child's head size and HAR's offsets and
        // sizes are all in it. A facing left null can't be told (then nothing is taken as covered).
        internal sealed class HeadCover
        {
            public readonly Texture[] art = new Texture[4];
            public readonly Texture[] typeArt = new Texture[4];
            public readonly float[][] map = new float[4][];
        }

        internal static HeadCover HeadCoverOf(Pawn pawn)
        {
            PawnRenderTree tree = pawn?.Drawer?.renderer?.renderTree;
            if (tree == null || tree.rootNode == null)
            {
                return null;
            }
            if (!adjustLooked)
            {
                adjustLooked = true;
                MethodInfo m = AccessTools.Method(typeof(PawnRenderTree), "AdjustParms");
                adjustParms = m != null ? (AdjustParmsCall)Delegate.CreateDelegate(typeof(AdjustParmsCall), null, m) : null;
            }
            PawnRenderNode headNode, bodyNode;
            if (!tree.TryGetNodeByTag(PawnRenderNodeTagDefOf.Head, out headNode) || !tree.TryGetNodeByTag(PawnRenderNodeTagDefOf.Body, out bodyNode)
                || headNode == null || bodyNode == null)
            {
                return null;
            }
            HeadCover cover = new HeadCover();
            string[] suffix = { "_north", "_east", "_south", "_west" };
            for (int f = 0; f < 4; f++)
            {
                PawnDrawParms parms = PawnDrawParms.Default;
                parms.pawn = pawn;
                parms.matrix = Matrix4x4.identity;
                parms.facing = new Rot4(f);
                parms.rotDrawMode = RotDrawMode.Fresh;
                parms.posture = PawnPosture.Standing;
                parms.flags = PawnRenderFlags.Clothes | PawnRenderFlags.Headgear;
                parms.dead = false;
                if (adjustParms != null)
                {
                    adjustParms(tree, ref parms);
                }
                Matrix4x4 headM, bodyM;
                Mesh headMesh = headNode.GetMesh(parms), bodyMesh = bodyNode.GetMesh(parms);
                if (headMesh == null || bodyMesh == null || !tree.TryGetMatrix(headNode, parms, out headM) || !tree.TryGetMatrix(bodyNode, parms, out bodyM))
                {
                    continue;
                }
                float[] body = UvMap(bodyMesh, Matrix4x4.identity);              // body uv from the body's frame
                float[] head = UvMap(headMesh, bodyM.inverse * headM);         // head uv from the body's frame
                if (body == null || head == null)
                {
                    continue;
                }
                float det = body[1] * body[5] - body[2] * body[4];
                if (Mathf.Abs(det) < 1e-9f)
                {
                    continue;
                }
                // The body's frame from its uv: X = (b5 (u - b0) - b2 (v - b3)) / det, Z = (-b4 (u - b0) + b1 (v - b3)) / det.
                float xu = body[5] / det, xv = -body[2] / det, x0 = -(xu * body[0] + xv * body[3]);
                float zu = -body[4] / det, zv = body[1] / det, z0 = -(zu * body[0] + zv * body[3]);
                cover.map[f] = new[]
                {
                    head[0] + head[1] * x0 + head[2] * z0, head[1] * xu + head[2] * zu, head[1] * xv + head[2] * zv,
                    head[3] + head[4] * x0 + head[5] * z0, head[4] * xu + head[5] * zu, head[4] * xv + head[5] * zv,
                };
                Material mat = headNode.Worker.GetFinalizedMaterial(headNode, parms);
                cover.art[f] = mat != null ? mat.mainTexture : null;
                if (pawn.story != null && pawn.story.headType != null)
                {
                    cover.typeArt[f] = ContentFinder<Texture2D>.Get(pawn.story.headType.graphicPath + suffix[f], false)
                        ?? (f == 3 ? ContentFinder<Texture2D>.Get(pawn.story.headType.graphicPath + suffix[1], false) : null);
                }
            }
            return cover;
        }

        // A drawn quad's map from its parent's frame (X across, Z up the picture, cells) to its texture's uv, from three
        // of its corners: u = a0 + a1 X + a2 Z, v = a3 + a4 X + a5 Z. Null if its corners can't be read or lie on a line.
        private static float[] UvMap(Mesh mesh, Matrix4x4 rel)
        {
            Vector3[] v;
            Vector2[] uv;
            try
            {
                v = mesh.vertices;
                uv = mesh.uv;
            }
            catch (Exception)
            {
                return null;
            }
            if (v == null || uv == null || v.Length < 3 || uv.Length != v.Length)
            {
                return null;
            }
            for (int a = 0; a < v.Length; a++)
            {
                for (int b = a + 1; b < v.Length; b++)
                {
                    for (int c = b + 1; c < v.Length; c++)
                    {
                        Vector3 p = rel.MultiplyPoint3x4(v[a]), q = rel.MultiplyPoint3x4(v[b]), s = rel.MultiplyPoint3x4(v[c]);
                        float x1 = q.x - p.x, z1 = q.z - p.z, x2 = s.x - p.x, z2 = s.z - p.z;
                        float det = x1 * z2 - x2 * z1;
                        if (Mathf.Abs(det) < 1e-6f)
                        {
                            continue;
                        }
                        // Solve [x1 z1; x2 z2] [du/dX du/dZ]' = [du1 du2]'.
                        float du1 = uv[b].x - uv[a].x, du2 = uv[c].x - uv[a].x, dv1 = uv[b].y - uv[a].y, dv2 = uv[c].y - uv[a].y;
                        float ux = (du1 * z2 - du2 * z1) / det, uz = (x1 * du2 - x2 * du1) / det;
                        float vx = (dv1 * z2 - dv2 * z1) / det, vz = (x1 * dv2 - x2 * dv1) / det;
                        return new[] { uv[a].x - ux * p.x - uz * p.z, ux, uz, uv[a].y - vx * p.x - vz * p.z, vx, vz };
                    }
                }
            }
            return null;
        }

        // The race's head (body) as drawn in facing f: its picture's opaque box in its own node's frame (x0, x1, z0, z1),
        // from its node's material, else (Facial Animation may draw the head with nothing of its own) the head type's art.
        private static Vector4 ReferenceBox(Pawn pawn, PawnRenderNode node, PawnDrawParms parms, int f, bool head)
        {
            Mesh mesh = node.GetMesh(parms);
            float[] map = mesh != null ? UvMap(mesh, Matrix4x4.identity) : null;
            if (map == null)
            {
                return default(Vector4);
            }
            Material mat = node.Worker.GetFinalizedMaterial(node, parms);
            Vector4 box = mat != null && mat.mainTexture != null ? BoxOn(PixelsOf(mat.mainTexture), map) : default(Vector4);
            if (box.y <= box.x && head && pawn.story != null && pawn.story.headType != null)
            {
                string[] suffix = { "_north", "_east", "_south", "_east" };
                Texture2D t = ContentFinder<Texture2D>.Get(pawn.story.headType.graphicPath + suffix[f], false);
                box = t != null ? BoxOn(PixelsOf(t), map) : box;
            }
            return box;
        }

        private static Vector4 BoxOn(Color32[] px, float[] a)
        {
            if (px == null)
            {
                return default(Vector4);
            }
            int s = SkinnedFigure.AddonTexSize;
            int c0 = s, c1 = -1, r0 = s, r1 = -1;
            for (int r = 0; r < s; r++)
            {
                for (int c = 0; c < s; c++)
                {
                    if (px[r * s + c].a >= 128)
                    {
                        c0 = Mathf.Min(c0, c);
                        c1 = Mathf.Max(c1, c);
                        r0 = Mathf.Min(r0, r);
                        r1 = Mathf.Max(r1, r);
                    }
                }
            }
            if (c1 < 0)
            {
                return default(Vector4);
            }
            float det = a[1] * a[5] - a[2] * a[4];
            if (Mathf.Abs(det) < 1e-9f)
            {
                return default(Vector4);
            }
            float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
            foreach (Vector2 uv in new[] { new Vector2(c0, r0), new Vector2(c1 + 1, r0), new Vector2(c0, r1 + 1), new Vector2(c1 + 1, r1 + 1) })
            {
                float u = uv.x / s - a[0], v = uv.y / s - a[3];
                float X = (a[5] * u - a[2] * v) / det, Z = (-a[4] * u + a[1] * v) / det;
                x0 = Mathf.Min(x0, X);
                x1 = Mathf.Max(x1, X);
                z0 = Mathf.Min(z0, Z);
                z1 = Mathf.Max(z1, Z);
            }
            return new Vector4(x0, x1, z0, z1);
        }

        // True if in every picture it has, nearly all of it lies on the race's head (body) as drawn: a marking.
        private static bool Marking(Pawn pawn, SkinnedFigure.AddonSource s, PawnRenderNode refNode, PawnRenderTree tree)
        {
            for (int f = 0; f < 4; f++)
            {
                if (s.px[f] == null)
                {
                    continue;
                }
                Vector4 box = s.reference[f];
                if (box.y <= box.x)
                {
                    return false;
                }
                float[] a = s.toUv[f];
                float det = a[1] * a[5] - a[2] * a[4];
                int on = 0, all = 0, n = SkinnedFigure.AddonTexSize;
                for (int r = 0; r < n; r++)
                {
                    for (int c = 0; c < n; c++)
                    {
                        if (s.px[f][r * n + c].a < 128)
                        {
                            continue;
                        }
                        float u = (c + 0.5f) / n - a[0], v = (r + 0.5f) / n - a[3];
                        float X = (a[5] * u - a[2] * v) / det, Z = (-a[4] * u + a[1] * v) / det;
                        all++;
                        if (X >= box.x && X <= box.y && Z >= box.z && Z <= box.w)
                        {
                            on++;
                        }
                    }
                }
                if (all > 0 && on < AddonDecalShare * all)
                {
                    return false;   // this picture has it standing out past the head (body)
                }
            }
            return true;
        }

        // Draws the pawn's race parts (from DrawDressed): the head's left off under what hides the ears (as the gene's ears)
        // or with no head; a tail under the armour that hides the cat tail; none under a blanket.
        private static void DrawRaceParts(Pawn pawn, SkinnedFigure.Posed fig, GarmentMask mask, int worn, bool headless, Vector3 at, float yaw, float size, float sat, float tailFloor, bool tailOnGround)
        {
            if ((!RacePartsOn && !RaceModelsOn) || racePartsFailed || fig.tucked || !(pawn.def is ThingDef) || pawn.def.defName == "Human"
                || ContentSources.PackOwnsGeneParts(pawn))
            {
                return;   // (the pack's race with its gene parts on: only the pack's are drawn)
            }
            RaceParts r = RacePartsOf(pawn, worn);
            if (r.sources.Count == 0 && r.modelEars == null && r.modelTail == null && !r.wingL && !r.wingR)
            {
                return;
            }
            bool earsHidden = EarHiding != EarHiding.Never && skin.HidesEars(mask, wornBuffer, EarHiding == EarHiding.Helmets);
            bool tailHidden = ArmourHidesTail && skin.HidesTail(mask, wornBuffer);
            RaceModel m;
            if (RaceModelsOn && (r.modelEars != null || r.modelTail != null || r.wingL || r.wingR) && RaceModels.TryGetValue(pawn.def.defName, out m))
            {
                if (r.modelEars != null && !headless && !earsHidden)
                {
                    DrawModelEars(pawn, fig, r.modelEars, r.earLean, at, yaw, size, r.earTint, Color.Lerp(r.earTint, m.lining, m.liningShare));
                }
                if (r.modelTail != null)
                {
                    DrawModelTail(pawn, fig, r.modelTail, r.modelTail, tailHidden, at, yaw, size, sat, tailFloor, tailOnGround, r.tailTint, Color.Lerp(r.tailTint, m.tip, m.tipShare));
                }
                // Wings: both on one set of springs, mirrored, so they open and close together.
                Color wingTip = Color.Lerp(r.wingTint, m.tip, m.tipShare);
                if (r.wingL && m.wings != null)
                {
                    DrawModelTail(pawn, fig, m.wings[0], "wings", false, at, yaw, size, sat, tailFloor, tailOnGround, r.wingTint, wingTip);
                }
                if (r.wingR && m.wings != null)
                {
                    DrawModelTail(pawn, fig, m.wings[1], "wings", false, at, yaw, size, sat, tailFloor, tailOnGround, r.wingTint, wingTip);
                }
            }
            for (int i = 0; RacePartsOn && i < r.sources.Count; i++)
            {
                SkinnedFigure.AddonSource s = r.sources[i];
                if (s.glow == null && r.tails[i])
                {
                    // A tail moves on its springs (RaceTails.cs), stepped hidden too.
                    SkinnedFigure.Piece built = skin.AddonPiece(s);
                    if (built != null && skin.IsRaceTail(built))
                    {
                        DrawRaceTail(pawn, fig, built, tailHidden, at, yaw, size, sat, tailFloor, tailOnGround, r.tints[i]);
                        continue;
                    }
                }
                if ((s.onHead && (headless || earsHidden)) || (r.tails[i] && tailHidden))
                {
                    continue;
                }
                if (s.glow != null)
                {
                    SkinnedFigure.AddonGlow g = skin.AddonGlowOf(s);
                    if (g != null)
                    {
                        Api.DrawGlowCard(skin.AddonGlowAt(fig, g, at, yaw, size), g.size * size, s.glow);
                    }
                    continue;
                }
                SkinnedFigure.Piece piece = skin.AddonPiece(s);
                if (piece != null && skin.IsRaceEar(piece))
                {
                    DrawRaceEar(pawn, fig, piece, at, yaw, size, r.tints[i]);
                }
                else if (piece != null)
                {
                    skin.DrawAddon(fig, piece, s.onHead, at, yaw, size, r.tints[i]);
                }
            }
        }
    }
}
