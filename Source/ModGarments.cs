using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Verse;

namespace M3AT
{
    // Garments and gene parts from other mods (the modding guides: Docs/Apparel.md, Docs/GeneParts.md in the mod's folder).
    //
    // Any other active mod may have a folder M3ATgarments at its root, as it may have M3AThair: each .txt in it is a
    // garment in the export's own form (garment_<name>.txt, the name less garment_), with its image beside it. What
    // goes by the body (each vertex's bone weights, its move in every body key, and the body triangles it hides) is
    // worked out here, as the exporter (anim_scripts/export_frames.py, write_garment) works it out for this mod's own,
    // unless the file was exported against this very body (its body line is skin.txt's hash): so a garment made with
    // only its places, triangles and texture places keeps fitting when the body is reshaped or remeshed by an update,
    // and its maker needs no copy of the rig. <config>/M3AT/M3ATgarments is read the same way, to try garments out. The lines a file may have besides the export's are read in ReadMesh:
    //   base <Name>        a retexture: this mod's garment Name's mesh, weights, keys and hiding, with this file's lines
    //                      (defs, texture, tint, layer, groups, ...) over its own; no v or f lines needed.
    //   body <hash>        the skin.txt it was exported against (its first 16 hex digits, Stamp); its w, key and hide
    //                      lines count only on that body.
    //   bone <name>        it rides that bone whole (a hat, a mask: spine.006, the head), as the exporter's bone prop.
    //   bones <names>      the bone each w line's numbers mean, by name; with own_weights yes its w lines are kept
    //                      on any body (a skirt weighted by hand), each number taken to the bone of that name.
    //   hard yes|no        moved by its bones as a key moves the joints, not reshaped (plate armour, and by default
    //                      anything with a bone line); rigid yes: moved whole.
    //   own_keys <names>   its key lines kept for these keys; every other body key fitted.
    //   hides no           it hides no skin (a see-through veil); hide_bones <prefixes>: also hides every body triangle
    //                      with HideGroupWeight of each corner on bones starting with one of them (armour hiding a limb).
    //   genes <defNames>   a gene part, drawn while the pawn has one of these genes active, not for apparel;
    //                      colour_from skin|hair|own its colour (own: its colour line, or its texture as it is).
    // A garment whose defs name apparel this mod already has a garment for is drawn for it in that one's place, so a
    // mod can replace one of this mod's garments as well as add its own.
    public sealed partial class SkinnedFigure
    {
        public const string ModGarmentFolder = "M3ATgarments";   // at another mod's root
        // <config>/M3AT/M3ATgarments: garments to try out without making a mod, read as a mod's are.

        // The exporter's numbers (export_frames.py), in cells.
        private const float HideReach = 0.04f;      // HIDE_REACH: the garment's nearest point this close to the skin
        private const float HideLook = 0.25f;       // HIDE_LOOK: and lying straight out from it within this
        private const float HideRidesWith = 0.5f;   // HIDE_RIDES_WITH: a garment on one bone hides skin with this much on it
        private const float HideGroupWeight = 0.5f; // HIDE_GROUP_WEIGHT, for hide_bones
        private const float HangFrom = 0.02f, HangFull = 0.05f, HangKeep = 0.03f;   // HANG_FROM, HANG_FULL, HANG_KEEP
        private const int HangPasses = 30;          // HANG_PASSES
        private const float FillShare = 0.5f;       // FILL_SHARE
        private const int FillPasses = 8, FillWayPasses = 4;   // FILL_PASSES, FILL_WAY_PASSES
        private static readonly string[] AlwaysFitted = { "thin", "fat", "hulk" };   // ALWAYS_FITTED
        private static readonly string[] HardFitted = { "shrinkhead" };   // HARD_FITTED
        private static readonly string[] HangBones = { "thigh", "shin", "foot", "toe" };   // HANG_BONES
        private static readonly string[] HangNot = { "shoulder", "upper_arm", "forearm", "hand", "palm", "thumb", "f_" };   // HANG_NOT

        private string[] boneNames = new string[0];   // anim.txt's bones by number, read ahead of ReadAnim
        private int[] boneParents = new int[0];
        private Vector3[] boneHeads = new Vector3[0];   // and their rest heads, cells
        private const int JointNearest = 16;   // JOINT_NEAREST: body vertices a hard garment's bone is tied to
        private readonly List<int> geneParts = new List<int>();   // the pieces drawn for genes (their genes lines)
        private readonly Dictionary<int, GeneDef[]> geneDefs = new Dictionary<int, GeneDef[]>();   // and their genes, looked up when first drawn

        // Reads every other active mod's garments into all (after this mod's own), fits each to the body and adds its defs.
        private void AddModGarments(List<Piece> all, string animPath)
        {
            ReadBoneNames(animPath);
            Piece body = all[0];
            Dictionary<string, Piece> own = new Dictionary<string, Piece>(StringComparer.OrdinalIgnoreCase);
            for (int g = 1; g < all.Count; g++)
            {
                own[all[g].name] = all[g];
            }
            HashSet<string> names = new HashSet<string>(own.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> found in ModGarmentFiles())
            {
                string path = found.Key, mod = found.Value;
                Piece g;
                try
                {
                    g = ReadModGarment(path, mod, own, body);
                }
                catch (Exception e)
                {
                    Log.Warning(Tag + mod + "'s garment " + Path.GetFileName(path) + " could not be read, so it is not drawn: " + e.Message);
                    continue;
                }
                if (g == null)
                {
                    continue;
                }
                if (!names.Add(g.name))
                {
                    Log.Warning(Tag + mod + "'s garment " + g.name + " has the name of one already loaded, so it is not drawn: give its file another name.");
                    continue;
                }
                foreach (string def in g.defs)
                {
                    garmentOf[def] = all.Count;   // after this mod's own, so it wins for apparel both name
                }
                if (g.genes != null && g.genes.Length > 0)
                {
                    geneParts.Add(all.Count);
                }
                all.Add(g);
                Log.Message(Tag + "garment " + g.name + " from " + mod + (g.baseName != null ? " (on " + g.baseName + ")" : "") + ": "
                    + (g.genes != null ? "for the genes " + string.Join(", ", g.genes) : "for " + string.Join(", ", g.defs))
                    + ", " + g.rest.Length + " vertices, hides " + g.hides.Length + " body triangles.");
            }
            if (all.Count - 1 > MaxHiding)
            {
                Log.Warning(Tag + (all.Count - 1) + " garments are loaded, but worn-garment masks hold " + MaxHiding + ": those past it hide nothing.");
            }
        }

        // The garment files of every other active mod (path, the mod's name), then the config folder's.
        private IEnumerable<KeyValuePair<string, string>> ModGarmentFiles()
        {
            List<KeyValuePair<string, string>> dirs = new List<KeyValuePair<string, string>>();
            string ownRoot = Path.GetFullPath(Path.Combine(folder, "..", ".."));
            try
            {
                foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
                {
                    string dir = Path.Combine(mod.RootDir, ModGarmentFolder);
                    if (Directory.Exists(dir) && Path.GetFullPath(mod.RootDir).TrimEnd('/', '\\') != ownRoot.TrimEnd('/', '\\'))
                    {
                        dirs.Add(new KeyValuePair<string, string>(dir, mod.Name));
                    }
                }
            }
            catch (Exception)
            {
                // (outside the game, in a harness: no mods)
            }
            try
            {
                string local = Path.Combine(GenFilePaths.ConfigFolderPath, "M3AT", ModGarmentFolder);
                if (Directory.Exists(local))
                {
                    dirs.Add(new KeyValuePair<string, string>(local, "the config folder's M3AT/" + ModGarmentFolder));
                }
            }
            catch (Exception)
            {
            }
            foreach (KeyValuePair<string, string> d in dirs)
            {
                string[] files = Directory.GetFiles(d.Key, "*.txt");
                Array.Sort(files, StringComparer.Ordinal);
                foreach (string file in files)
                {
                    yield return new KeyValuePair<string, string>(file, d.Value);
                }
            }
        }

        // anim.txt's bone lines: each bone's name and parent, by number, as the w lines and ReadAnim number them.
        private void ReadBoneNames(string animPath)
        {
            List<string> names = new List<string>();
            List<int> parents = new List<int>();
            List<Vector3> heads = new List<Vector3>();
            foreach (string raw in File.ReadLines(animPath))
            {
                if (!raw.StartsWith("bone "))
                {
                    if (names.Count > 0 && raw.StartsWith("clip "))
                    {
                        break;   // the bones come first
                    }
                    continue;
                }
                string[] p = raw.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                names.Add(p[1]);
                heads.Add(Vec(p, 2));
                parents.Add(p.Length > 5 ? int.Parse(p[5]) : -1);
            }
            boneNames = names.ToArray();
            boneParents = parents.ToArray();
            boneHeads = heads.ToArray();
        }

        private int BoneNumber(string name)
        {
            return Array.IndexOf(boneNames, name);
        }

        // One mod garment, read and fitted: null (and a message) if it can't be drawn.
        private Piece ReadModGarment(string path, string mod, Dictionary<string, Piece> own, Piece body)
        {
            Piece g = ReadMesh(path, false, null);
            if (g.name.StartsWith("garment_"))
            {
                g.name = g.name.Substring("garment_".Length);
            }
            g.modRoot = Path.GetDirectoryName(path);
            g.modName = mod;
            if (g.defs == null)
            {
                g.defs = new string[0];
            }
            if (g.baseName != null)
            {
                Piece b;
                if (!own.TryGetValue(g.baseName, out b))
                {
                    Log.Warning(Tag + mod + "'s garment " + g.name + " is based on " + g.baseName + ", which this version of M3AT has not, so it is not drawn.");
                    return null;
                }
                g = Retextured(b, g);
            }
            else
            {
                if (g.rest.Length == 0 || g.tris.Length == 0)
                {
                    Log.Warning(Tag + mod + "'s garment " + g.name + " has no vertices or triangles (and no base line), so it is not drawn.");
                    return null;
                }
                FitToBody(g, body);
            }
            if (g.genes == null && g.defs.Length == 0 && string.IsNullOrEmpty(g.layer) && g.standsFor == null)
            {
                Log.Warning(Tag + mod + "'s garment " + g.name + " names no apparel (defs), layer, stands_for or genes, so nothing would wear it.");
            }
            if (g.textureFile != null && g.texture == null)
            {
                ReadTexture(g, Path.Combine(g.modRoot, g.textureFile));
            }
            if (g.glass != null)
            {
                g.shellTris = Kept(g.tris, g.glass);
                g.visor = VisorOf(g);
            }
            return g;
        }

        // A retexture: the base garment's mesh, weights, keys and hiding, with what the file says over its own.
        private static Piece Retextured(Piece b, Piece file)
        {
            Piece g = new Piece
            {
                name = file.name,
                modRoot = file.modRoot,
                modName = file.modName,
                baseName = b.name,
                defs = file.defs.Length > 0 ? file.defs : b.defs,
                layer = !string.IsNullOrEmpty(file.layer) ? file.layer : b.layer,
                groups = file.groups.Length > 0 ? file.groups : b.groups,
                like = file.like ?? b.like,
                standsFor = file.standsFor ?? b.standsFor,
                textureFile = file.textureFile,
                tint = file.tint >= 0 ? file.tint : b.tint,
                hideHair = file.hideHair >= 0 ? file.hideHair : b.hideHair,
                hideFace = file.hideFace >= 0 ? file.hideFace : b.hideFace,
                overHair = b.overHair || file.overHair,
                hidesClothes = b.hidesClothes || file.hidesClothes,
                colour = file.colour ?? b.colour,
                genes = file.genes,
                colourFrom = file.colourFrom,
                held = b.held,
                heldOnBack = b.heldOnBack,
                heldAt = b.heldAt,
                heldGrip = b.heldGrip,
                withLegwear = b.withLegwear,
                hides = file.hidesWanted == 0 ? new int[0] : b.hides,
                opens = b.opens,
                rest = b.rest,
                tris = (int[])b.tris.Clone(),
                firstWeight = b.firstWeight,
                weightBone = b.weightBone,
                weight = b.weight,
                uvs = b.uvs,
                normal = b.normal,
                glass = b.glass,
                weightsGiven = true,
            };
            foreach (KeyValuePair<string, Vector3[]> k in b.keys)
            {
                g.keys[k.Key] = k.Value;
            }
            if (g.textureFile == null)
            {
                // No image of its own: the base's, from this mod's folder.
                g.textureFile = b.textureFile;
                g.texture = b.texture;
                g.mean = b.mean;
                g.modRoot = null;
            }
            else if (g.uvs == null)
            {
                throw new InvalidDataException("its base, " + b.name + ", has no texture places, so it can't take an image");
            }
            return g;
        }

        // Fits a garment read from a file to the body, as the exporter does: what the file gives counts only if it was
        // exported against this body (its body line), but for own weights kept by name and the keys of its own_keys.
        private void FitToBody(Piece g, Piece body)
        {
            bool thisBody = g.bodyStamp != null && stamp.StartsWith(g.bodyStamp, StringComparison.OrdinalIgnoreCase);
            Vector3[] bodyNormal = Normals(body.rest, body.tris);
            int count = g.rest.Length;
            // Where each vertex sits over the body: the nearest triangle, the shares of its corners, and how far off it is.
            int[] over = new int[count];
            Vector3[] share = new Vector3[count];
            float[] gap = new float[count];
            for (int i = 0; i < count; i++)
            {
                Vector3 point;
                gap[i] = Mathf.Sqrt(NearestTriangle(body, g.rest[i], out over[i], out point));
                int t = 3 * over[i];
                share[i] = Barycentric(g.rest[i], body.rest[body.tris[t]], body.rest[body.tris[t + 1]], body.rest[body.tris[t + 2]]);
            }
            g.hard = g.hardLine == 1 || (g.hardLine == -1 && g.boneName != null);
            // Which vertices lie inside the skin at rest (where a garment dips into it): the push back out after a key
            // leaves them be, as it would push them out in every key, and the keys add up.
            bool[] inside = new bool[count];
            for (int i = 0; i < count; i++)
            {
                int t = 3 * over[i];
                Vector3 a = body.rest[body.tris[t]], b = body.rest[body.tris[t + 1]], c = body.rest[body.tris[t + 2]];
                Vector3 on = a * share[i].x + b * share[i].y + c * share[i].z;
                inside[i] = Vector3.Dot(Vector3.Cross(b - a, c - a), g.rest[i] - on) < 0f && gap[i] > 1e-4f;
            }
            Orient(g, body, bodyNormal, over, share);
            FitWeights(g, body, over, share, thisBody);
            FitKeys(g, body, bodyNormal, over, share, gap, inside, thisBody);
            if (!(thisBody && g.hides.Length > 0))
            {
                g.hides = g.hidesWanted == 0 || g.held ? new int[0] : BodyUnder(g, body, bodyNormal);
            }
            if (!thisBody)
            {
                g.opens = new int[0];   // (hide_edges.py's, for that body's triangles)
            }
            HideBones(g, body);
            for (int k = 0; k < g.hides.Length; k++)
            {
                if (g.hides[k] < 0 || g.hides[k] >= body.tris.Length / 3)
                {
                    throw new InvalidDataException("hides body triangle " + g.hides[k] + ", but the body has " + body.tris.Length / 3 + " (its body line names another body?)");
                }
            }
        }

        // The garment's triangles wound as the body's: each one's facing against the skin's under its middle, all of them
        // turned round if most face in (a file written the other way round would be drawn inside out).
        private static void Orient(Piece g, Piece body, Vector3[] bodyNormal, int[] over, Vector3[] share)
        {
            Vector3[] n = Normals(g.rest, g.tris);
            float agree = 0f;
            for (int i = 0; i < g.rest.Length; i++)
            {
                agree += Mathf.Sign(Vector3.Dot(n[i], SkinNormal(body, bodyNormal, over[i], share[i])));
            }
            if (agree < 0f)
            {
                TurnRound(g.tris);
                if (g.normal != null)
                {
                    for (int i = 0; i < g.normal.Length; i++)
                    {
                        g.normal[i] = -g.normal[i];
                    }
                }
            }
        }

        private static Vector3 SkinNormal(Piece body, Vector3[] bodyNormal, int tri, Vector3 s)
        {
            int t = 3 * tri;
            return (bodyNormal[body.tris[t]] * s.x + bodyNormal[body.tris[t + 1]] * s.y + bodyNormal[body.tris[t + 2]] * s.z).normalized;
        }

        // The weights: on one bone whole (bone), its own (own_weights with a bones line, or exported against this body),
        // else the body's blended from the place under each vertex, so it bends exactly as the skin under it.
        private void FitWeights(Piece g, Piece body, int[] over, Vector3[] share, bool thisBody)
        {
            int count = g.rest.Length;
            List<int> first = new List<int>(count + 1), bones = new List<int>();
            List<float> weights = new List<float>();
            if (g.boneName != null)
            {
                int b = BoneNumber(g.boneName);
                if (b < 0)
                {
                    throw new InvalidDataException("its bone line names " + g.boneName + ", which the chibi has not");
                }
                for (int i = 0; i < count; i++)
                {
                    first.Add(bones.Count);
                    bones.Add(b);
                    weights.Add(1f);
                }
            }
            else if (g.weightsGiven && (thisBody || (g.ownWeights && g.weightBones != null)))
            {
                // Its own, each number taken to the bone of that name where it names them.
                for (int i = 0; i < count; i++)
                {
                    first.Add(bones.Count);
                    float total = 0f;
                    int from = bones.Count;
                    for (int k = g.firstWeight[i]; k < g.firstWeight[i + 1]; k++)
                    {
                        int b = g.weightBone[k];
                        if (g.weightBones != null)
                        {
                            b = b >= 0 && b < g.weightBones.Length ? BoneNumber(g.weightBones[b]) : -1;
                        }
                        if (b < 0 || b >= boneNames.Length || g.weight[k] <= 0f)
                        {
                            continue;
                        }
                        bones.Add(b);
                        weights.Add(g.weight[k]);
                        total += g.weight[k];
                    }
                    if (total <= 0f)
                    {
                        bones.RemoveRange(from, bones.Count - from);
                        weights.RemoveRange(from, weights.Count - from);
                        AddBodyWeights(body, over[i], share[i], bones, weights);
                    }
                    else
                    {
                        for (int k = from; k < weights.Count; k++)
                        {
                            weights[k] /= total;
                        }
                    }
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    first.Add(bones.Count);
                    AddBodyWeights(body, over[i], share[i], bones, weights);
                }
            }
            first.Add(bones.Count);
            g.firstWeight = first.ToArray();
            g.weightBone = bones.ToArray();
            g.weight = weights.ToArray();
        }

        private static void AddBodyWeights(Piece body, int tri, Vector3 s, List<int> bones, List<float> weights)
        {
            Dictionary<int, float> sum = new Dictionary<int, float>();
            for (int c = 0; c < 3; c++)
            {
                int v = body.tris[3 * tri + c];
                float t = s[c];
                for (int k = body.firstWeight[v]; k < body.firstWeight[v + 1]; k++)
                {
                    float was;
                    sum.TryGetValue(body.weightBone[k], out was);
                    sum[body.weightBone[k]] = was + body.weight[k] * t;
                }
            }
            float total = 0f;
            foreach (float w in sum.Values)
            {
                if (w > 1e-4f)
                {
                    total += w;
                }
            }
            foreach (KeyValuePair<int, float> bw in sum)
            {
                if (bw.Value > 1e-4f)
                {
                    bones.Add(bw.Key);
                    weights.Add(total > 0f ? bw.Value / total : 1f);
                }
            }
        }

        private float BonesShare(Piece piece, int vertex, Func<int, bool> which)
        {
            float s = 0f;
            for (int k = piece.firstWeight[vertex]; k < piece.firstWeight[vertex + 1]; k++)
            {
                int b = piece.weightBone[k];
                if (b >= 0 && b < boneNames.Length && which(b))
                {
                    s += piece.weight[k];
                }
            }
            return s;
        }

        private bool Starts(int bone, string[] prefixes)
        {
            for (int k = 0; k < prefixes.Length; k++)
            {
                if (boneNames[bone].StartsWith(prefixes[k], StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        // Every body key's move of each vertex, as write_garment fits them: the body's under it blended (moved whole for
        // rigid, by its bones' joints for hard), smoothed over what hangs clear of the legs, pushed back out where the
        // reshaped skin came through it, and its dents filled.
        private void FitKeys(Piece g, Piece body, Vector3[] bodyNormal, int[] over, Vector3[] share, float[] gap, bool[] inside, bool thisBody)
        {
            int count = g.rest.Length;
            Dictionary<string, Vector3[]> given = new Dictionary<string, Vector3[]>(g.keys, StringComparer.OrdinalIgnoreCase);
            g.keys.Clear();
            int[][] neighbours = NeighboursByPlace(g);
            Vector3[] outward = new Vector3[count];
            float[] hang = new float[count];
            for (int i = 0; i < count; i++)
            {
                outward[i] = SkinNormal(body, bodyNormal, over[i], share[i]);
                float legs = 0f;
                for (int c = 0; c < 3; c++)
                {
                    legs += share[i][c] * BonesShare(body, body.tris[3 * over[i] + c], b => Starts(b, HangBones));
                }
                float h = Mathf.Clamp01((gap[i] - HangFrom) / (HangFull - HangFrom));
                h = (1f - Mathf.Cos(Mathf.PI * h)) / 2f;   // walk.smooth
                // (not a sleeve: at rest the arms hang by the hips, so the body nearest a sleeve's end can be the thigh)
                hang[i] = h * legs * Mathf.Max(0f, 1f - 2f * BonesShare(g, i, b => Starts(b, HangNot)));
            }
            for (int i = 0; i < FillWayPasses; i++)
            {
                outward = Averaged(outward, neighbours);
            }
            KeyValuePair<int, float>[][] anchors = g.hard ? BoneAnchors(g, body) : null;
            foreach (KeyValuePair<string, Vector3[]> key in body.keys)
            {
                string name = key.Key;
                string lower = name.ToLowerInvariant();
                Vector3[] mine;
                if (given.TryGetValue(name, out mine) && Moves(mine)
                    && ((g.ownKeys != null && Array.Exists(g.ownKeys, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)))
                        || (thisBody && Array.IndexOf(AlwaysFitted, lower) < 0)))
                {
                    g.keys[name] = mine;
                    continue;
                }
                Vector3[] deltas = key.Value;
                Vector3[] fitted = new Vector3[count];
                if (g.hard && Array.IndexOf(HardFitted, lower) < 0)
                {
                    // By the bones, as bone_anchors(): each bone's middle tied to the body vertices round it, and each
                    // vertex moved as its bones' middles are.
                    Vector3[] moved = new Vector3[boneNames.Length];
                    for (int b = 0; b < moved.Length; b++)
                    {
                        moved[b] = AnchorMove(body, anchors[b], deltas);
                    }
                    for (int i = 0; i < count; i++)
                    {
                        Vector3 m = Vector3.zero;
                        for (int k = g.firstWeight[i]; k < g.firstWeight[i + 1]; k++)
                        {
                            m += moved[g.weightBone[k]] * g.weight[k];
                        }
                        fitted[i] = m;
                    }
                }
                else
                {
                    for (int i = 0; i < count; i++)
                    {
                        int t = 3 * over[i];
                        fitted[i] = deltas[body.tris[t]] * share[i].x + deltas[body.tris[t + 1]] * share[i].y + deltas[body.tris[t + 2]] * share[i].z;
                    }
                    if (g.rigid)
                    {
                        Vector3 mean = Vector3.zero;
                        for (int i = 0; i < count; i++)
                        {
                            mean += fitted[i];
                        }
                        mean /= Mathf.Max(1, count);
                        for (int i = 0; i < count; i++)
                        {
                            fitted[i] = mean;
                        }
                    }
                    else if (Moves(fitted))
                    {
                        Hung(fitted, hang, neighbours);
                        PushedOut(g, body, deltas, fitted, gap, hang, inside);
                        Filled(fitted, outward, neighbours);
                    }
                }
                if (Moves(fitted))
                {
                    g.keys[name] = fitted;
                }
            }
        }

        // Each bone's middle (halfway to its first child's head; a bone with none, the middle of the garment's vertices on
        // it) tied to the JointNearest body vertices nearest it, by inverse squared distance.
        private KeyValuePair<int, float>[][] BoneAnchors(Piece g, Piece body)
        {
            KeyValuePair<int, float>[][] anchors = new KeyValuePair<int, float>[boneNames.Length][];
            for (int b = 0; b < boneNames.Length; b++)
            {
                int child = Array.IndexOf(boneParents, b);
                Vector3 middle = boneHeads[b];
                if (child >= 0)
                {
                    middle = (boneHeads[b] + boneHeads[child]) / 2f;
                }
                else
                {
                    Vector3 sum = Vector3.zero;
                    float total = 0f;
                    for (int i = 0; i < g.rest.Length; i++)
                    {
                        for (int k = g.firstWeight[i]; k < g.firstWeight[i + 1]; k++)
                        {
                            if (g.weightBone[k] == b)
                            {
                                sum += g.rest[i] * g.weight[k];
                                total += g.weight[k];
                            }
                        }
                    }
                    if (total > 0f)
                    {
                        middle = sum / total;
                    }
                }
                List<KeyValuePair<int, float>> near = new List<KeyValuePair<int, float>>();
                for (int j = 0; j < body.rest.Length; j++)
                {
                    near.Add(new KeyValuePair<int, float>(j, (body.rest[j] - middle).sqrMagnitude));
                }
                near.Sort((x, y) => x.Value.CompareTo(y.Value));
                int n = Math.Min(JointNearest, near.Count);
                KeyValuePair<int, float>[] tie = new KeyValuePair<int, float>[n];
                float sumW = 0f;
                for (int k = 0; k < n; k++)
                {
                    float w = 1f / (near[k].Value + 1e-6f);
                    tie[k] = new KeyValuePair<int, float>(near[k].Key, w);
                    sumW += w;
                }
                for (int k = 0; k < n; k++)
                {
                    tie[k] = new KeyValuePair<int, float>(tie[k].Key, tie[k].Value / sumW);
                }
                anchors[b] = tie;
            }
            return anchors;
        }

        private static Vector3 AnchorMove(Piece body, KeyValuePair<int, float>[] tie, Vector3[] deltas)
        {
            Vector3 m = Vector3.zero;
            for (int k = 0; k < tie.Length; k++)
            {
                m += deltas[tie[k].Key] * tie[k].Value;
            }
            return m;
        }

        private static bool Moves(Vector3[] moves)
        {
            for (int i = 0; moves != null && i < moves.Length; i++)
            {
                if (moves[i].sqrMagnitude > 1e-12f)
                {
                    return true;
                }
            }
            return false;
        }

        // Each vertex's neighbours along the triangles' edges, the copies split at a texture seam (one place) as one, so
        // they move alike and the seam holds.
        private static int[][] NeighboursByPlace(Piece g)
        {
            int count = g.rest.Length;
            Dictionary<Vector3, int> at = new Dictionary<Vector3, int>();
            int[] place = new int[count];
            List<int> firstOf = new List<int>();
            for (int i = 0; i < count; i++)
            {
                Vector3 key = new Vector3((float)Math.Round(g.rest[i].x, 5), (float)Math.Round(g.rest[i].y, 5), (float)Math.Round(g.rest[i].z, 5));
                int k;
                if (!at.TryGetValue(key, out k))
                {
                    k = at.Count;
                    at[key] = k;
                    firstOf.Add(i);
                }
                place[i] = k;
            }
            HashSet<int>[] near = new HashSet<int>[at.Count];
            for (int k = 0; k < near.Length; k++)
            {
                near[k] = new HashSet<int>();
            }
            for (int t = 0; t + 2 < g.tris.Length; t += 3)
            {
                for (int e = 0; e < 3; e++)
                {
                    int a = place[g.tris[t + e]], b = place[g.tris[t + (e + 1) % 3]];
                    if (a != b)
                    {
                        near[a].Add(b);
                        near[b].Add(a);
                    }
                }
            }
            int[][] neighbours = new int[count][];
            for (int i = 0; i < count; i++)
            {
                HashSet<int> n = near[place[i]];
                neighbours[i] = new int[n.Count];
                int j = 0;
                foreach (int k in n)
                {
                    neighbours[i][j++] = firstOf[k];
                }
            }
            return neighbours;
        }

        private static Vector3[] Averaged(Vector3[] ways, int[][] neighbours)
        {
            Vector3[] out_ = new Vector3[ways.Length];
            for (int i = 0; i < ways.Length; i++)
            {
                Vector3 s = ways[i];
                foreach (int j in neighbours[i])
                {
                    s += ways[j];
                }
                out_[i] = s.normalized;
            }
            return out_;
        }

        // hung(): the moves smoothed over the garment where it hangs clear of the legs, each pass moving a vertex its hang
        // of the way to its neighbours' mean.
        private static void Hung(Vector3[] moves, float[] hang, int[][] neighbours)
        {
            bool any = false;
            for (int i = 0; i < hang.Length && !any; i++)
            {
                any = hang[i] > 0f;
            }
            if (!any)
            {
                return;
            }
            Vector3[] next = new Vector3[moves.Length];
            for (int pass = 0; pass < HangPasses; pass++)
            {
                for (int i = 0; i < moves.Length; i++)
                {
                    Vector3 m = moves[i];
                    int[] nb = neighbours[i];
                    if (hang[i] > 0f && nb.Length > 0)
                    {
                        Vector3 mean = Vector3.zero;
                        foreach (int j in nb)
                        {
                            mean += moves[j];
                        }
                        m += (mean / nb.Length - m) * hang[i];
                    }
                    next[i] = m;
                }
                Array.Copy(next, moves, moves.Length);
            }
        }

        // Where the reshaped skin came up through a vertex (the body under it changed which way it faces, between the
        // thighs), the vertex is pushed back out to the gap it had (a smoothed one kept at least as far off as it stood,
        // up to HangKeep).
        private static void PushedOut(Piece g, Piece body, Vector3[] deltas, Vector3[] fitted, float[] gap, float[] hang, bool[] inside)
        {
            int tris = body.tris.Length / 3;
            Vector3[] shaped = new Vector3[body.rest.Length];
            for (int i = 0; i < shaped.Length; i++)
            {
                shaped[i] = body.rest[i] + deltas[i];
            }
            Vector3[] low = new Vector3[tris], high = new Vector3[tris];
            for (int k = 0; k < tris; k++)
            {
                Vector3 a = shaped[body.tris[3 * k]], b = shaped[body.tris[3 * k + 1]], c = shaped[body.tris[3 * k + 2]];
                low[k] = Vector3.Min(a, Vector3.Min(b, c));
                high[k] = Vector3.Max(a, Vector3.Max(b, c));
            }
            for (int i = 0; i < fitted.Length; i++)
            {
                if (inside[i] || fitted[i].sqrMagnitude < 1e-12f)
                {
                    continue;   // (in the skin at rest, or not moved by this key)
                }
                Vector3 q = g.rest[i] + fitted[i];
                float best = float.MaxValue;
                int bestTri = -1;
                Vector3 loc = q;
                for (int k = 0; k < tris; k++)
                {
                    Vector3 outside = Vector3.Max(Vector3.zero, Vector3.Max(low[k] - q, q - high[k]));
                    if (outside.sqrMagnitude >= best)
                    {
                        continue;
                    }
                    Vector3 on = ClosestOnTriangle(q, shaped[body.tris[3 * k]], shaped[body.tris[3 * k + 1]], shaped[body.tris[3 * k + 2]]);
                    float d = (on - q).sqrMagnitude;
                    if (d < best)
                    {
                        best = d;
                        bestTri = k;
                        loc = on;
                    }
                }
                if (bestTri < 0)
                {
                    continue;
                }
                Vector3 pa = shaped[body.tris[3 * bestTri]], pb = shaped[body.tris[3 * bestTri + 1]], pc = shaped[body.tris[3 * bestTri + 2]];
                Vector3 n = Vector3.Cross(pb - pa, pc - pa).normalized;
                float least = hang[i] > 0f ? Mathf.Min(gap[i], HangKeep) : 0f;
                if (Vector3.Dot(n, q - loc) < least)
                {
                    fitted[i] = loc + n * (least == 0f ? gap[i] : least) - g.rest[i];
                }
            }
        }

        // filled(): each move lifted along its outward way toward its neighbours' mean where that is further out, never lowered.
        private static void Filled(Vector3[] moves, Vector3[] outward, int[][] neighbours)
        {
            Vector3[] next = new Vector3[moves.Length];
            for (int pass = 0; pass < FillPasses; pass++)
            {
                for (int i = 0; i < moves.Length; i++)
                {
                    Vector3 m = moves[i];
                    int[] nb = neighbours[i];
                    if (nb.Length > 0)
                    {
                        Vector3 mean = Vector3.zero;
                        foreach (int j in nb)
                        {
                            mean += moves[j];
                        }
                        float behind = Vector3.Dot(mean / nb.Length - m, outward[i]);
                        if (behind > 0f)
                        {
                            m += outward[i] * (behind * FillShare);
                        }
                    }
                    next[i] = m;
                }
                Array.Copy(next, moves, moves.Length);
            }
        }

        // hidden_under(): the body triangles under the garment at rest. A body vertex is under it if the garment's nearest
        // point is within HideReach and a line along the skin's normal, from HideReach inside it to HideLook out, meets
        // it (nearness alone would hide the skin in an opening, a collar, an open coat's chest); a garment riding one bone
        // hides only skin with HideRidesWith of its weights on that bone or below. A triangle is hidden if its corners
        // and middle all are under it.
        private int[] BodyUnder(Piece g, Piece body, Vector3[] bodyNormal)
        {
            bodyNormal = AngleNormals(body.rest, body.tris);   // as Blender's, which the exporter's test casts along
            int count = g.tris.Length / 3;
            Vector3[] low = new Vector3[count], high = new Vector3[count];
            for (int k = 0; k < count; k++)
            {
                Vector3 a = g.rest[g.tris[3 * k]], b = g.rest[g.tris[3 * k + 1]], c = g.rest[g.tris[3 * k + 2]];
                low[k] = Vector3.Min(a, Vector3.Min(b, c));
                high[k] = Vector3.Max(a, Vector3.Max(b, c));
            }
            bool[] glass = g.glass;
            bool[] riding = null;
            int ridden = g.boneName != null ? BoneNumber(g.boneName) : -1;
            if (ridden >= 0)
            {
                riding = new bool[boneNames.Length];
                for (int b = 0; b < boneNames.Length; b++)
                {
                    for (int up = b; up >= 0; up = boneParents[up])
                    {
                        if (up == ridden)
                        {
                            riding[b] = true;
                            break;
                        }
                    }
                }
            }
            bool[] under = new bool[body.rest.Length];
            for (int i = 0; i < under.Length; i++)
            {
                if (riding != null && BonesShare(body, i, b => riding[b]) < HideRidesWith)
                {
                    continue;
                }
                under[i] = Covered(g, glass, low, high, body.rest[i], bodyNormal[i]);
            }
            List<int> hide = new List<int>();
            for (int k = 0; k < body.tris.Length / 3; k++)
            {
                int a = body.tris[3 * k], b = body.tris[3 * k + 1], c = body.tris[3 * k + 2];
                // (and the middles of its edges: at the garment's edge, a triangle half out from under it would leave a gap
                // there once posed, and the corners and middle alone hid more there than the exporter's test does)
                if (under[a] && under[b] && under[c]
                    && Covered(g, glass, low, high, (body.rest[a] + body.rest[b] + body.rest[c]) / 3f, (bodyNormal[a] + bodyNormal[b] + bodyNormal[c]).normalized)
                    && Covered(g, glass, low, high, (body.rest[a] + body.rest[b]) / 2f, (bodyNormal[a] + bodyNormal[b]).normalized)
                    && Covered(g, glass, low, high, (body.rest[b] + body.rest[c]) / 2f, (bodyNormal[b] + bodyNormal[c]).normalized)
                    && Covered(g, glass, low, high, (body.rest[c] + body.rest[a]) / 2f, (bodyNormal[c] + bodyNormal[a]).normalized))
                {
                    hide.Add(k);
                }
            }
            return hide.ToArray();
        }

        private static bool Covered(Piece g, bool[] glass, Vector3[] low, Vector3[] high, Vector3 p, Vector3 n)
        {
            // The nearest point within reach.
            bool near = false;
            for (int k = 0; k < low.Length && !near; k++)
            {
                if ((glass != null && glass[k]) || Vector3.Max(Vector3.zero, Vector3.Max(low[k] - p, p - high[k])).sqrMagnitude > HideReach * HideReach)
                {
                    continue;
                }
                Vector3 on = ClosestOnTriangle(p, g.rest[g.tris[3 * k]], g.rest[g.tris[3 * k + 1]], g.rest[g.tris[3 * k + 2]]);
                near = (on - p).sqrMagnitude <= HideReach * HideReach;
            }
            if (!near)
            {
                return false;
            }
            // And the line out along the skin's normal meets it.
            Vector3 from = p - n * HideReach, d = n * (HideReach + HideLook);
            Vector3 lo = Vector3.Min(from, from + d), hi = Vector3.Max(from, from + d);
            for (int k = 0; k < low.Length; k++)
            {
                if ((glass != null && glass[k]) || high[k].x < lo.x || low[k].x > hi.x || high[k].y < lo.y || low[k].y > hi.y || high[k].z < lo.z || low[k].z > hi.z)
                {
                    continue;
                }
                Vector3 a = g.rest[g.tris[3 * k]];
                Vector3 e1 = g.rest[g.tris[3 * k + 1]] - a, e2 = g.rest[g.tris[3 * k + 2]] - a;
                Vector3 q = Vector3.Cross(d, e2);
                float det = Vector3.Dot(e1, q);
                if (Mathf.Abs(det) < 1e-12f)
                {
                    continue;
                }
                float inv = 1f / det;
                Vector3 s = from - a;
                float u = Vector3.Dot(s, q) * inv;
                if (u < 0f || u > 1f)
                {
                    continue;
                }
                Vector3 r = Vector3.Cross(s, e1);
                float v = Vector3.Dot(d, r) * inv;
                if (v < 0f || u + v > 1f)
                {
                    continue;
                }
                float t = Vector3.Dot(e2, r) * inv;
                if (t >= 0f && t <= 1f)
                {
                    return true;
                }
            }
            return false;
        }

        // Vertex normals as Blender has them: each face's normal weighted by its corner's angle there.
        private static Vector3[] AngleNormals(Vector3[] pos, int[] tris)
        {
            Vector3[] n = new Vector3[pos.Length];
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                Vector3 a = pos[tris[t]], b = pos[tris[t + 1]], c = pos[tris[t + 2]];
                Vector3 f = Vector3.Cross(b - a, c - a).normalized;
                n[tris[t]] += f * Vector3.Angle(b - a, c - a) * Mathf.Deg2Rad;
                n[tris[t + 1]] += f * Vector3.Angle(c - b, a - b) * Mathf.Deg2Rad;
                n[tris[t + 2]] += f * Vector3.Angle(a - c, b - c) * Mathf.Deg2Rad;
            }
            for (int i = 0; i < n.Length; i++)
            {
                n[i] = n[i].normalized;
            }
            return n;
        }

        // hide_bones: also every body triangle each of whose corners has HideGroupWeight of its weights on those bones.
        private void HideBones(Piece g, Piece body)
        {
            if (g.hideBones == null || g.hideBones.Length == 0)
            {
                return;
            }
            HashSet<int> hide = new HashSet<int>(g.hides);
            bool[] on = new bool[body.rest.Length];
            for (int i = 0; i < on.Length; i++)
            {
                on[i] = BonesShare(body, i, b => Starts(b, g.hideBones)) >= HideGroupWeight;
            }
            for (int k = 0; k < body.tris.Length / 3; k++)
            {
                if (on[body.tris[3 * k]] && on[body.tris[3 * k + 1]] && on[body.tris[3 * k + 2]])
                {
                    hide.Add(k);
                }
            }
            int[] all = new int[hide.Count];
            hide.CopyTo(all);
            Array.Sort(all);
            g.hides = all;
        }

        // Gene parts (genes lines): the mask of those the pawn has a gene for, active.
        public GarmentMask GenePartsOf(Pawn pawn)
        {
            GarmentMask mask = 0;
            if (geneParts.Count == 0 || pawn.genes == null)
            {
                return mask;
            }
            for (int i = 0; i < geneParts.Count; i++)
            {
                int g = geneParts[i];
                GeneDef[] defs;
                if (!geneDefs.TryGetValue(g, out defs))
                {
                    List<GeneDef> found = new List<GeneDef>();
                    foreach (string name in pieces[g].genes)
                    {
                        GeneDef d = DefDatabase<GeneDef>.GetNamedSilentFail(name);
                        if (d != null)
                        {
                            found.Add(d);
                        }
                    }
                    defs = found.ToArray();
                    geneDefs[g] = defs;
                }
                for (int k = 0; k < defs.Length; k++)
                {
                    if (pawn.genes.HasActiveGene(defs[k]))
                    {
                        mask |= GarmentBit(g);
                        break;
                    }
                }
            }
            return mask;
        }

        // The gene parts in mask, by piece.
        public IEnumerable<int> GenePartsIn(GarmentMask mask)
        {
            for (int i = 0; i < geneParts.Count; i++)
            {
                if ((mask & GarmentBit(geneParts[i])) != 0)
                {
                    yield return geneParts[i];
                }
            }
        }

        // A gene part's colour_from: 0 the skin's, 1 the hair's, 2 its own.
        public int GeneColourFrom(int g)
        {
            return pieces[g].colourFrom;
        }

        // Leaves out of a gene part its triangles under the garments worn (mask), as HideGarments does for a garment under
        // another: every garment worn goes over a gene part. Call after End and before drawing it.
        public void HideGenePart(Posed p, int g, GarmentMask mask)
        {
            PieceMeshes m = Pose(p, g);
            GarmentMask over = mask & ~NoHideMask;
            if (m.hidden == over && m.tucked == p.tucked)
            {
                return;
            }
            int[] tris = Tucked(p, GarmentTris(g, over), pieces[g]);
            m.solid.SetTriangles(tris, 0, false);
            m.outline.SetTriangles(tris, 0, false);
            m.hidden = over;
            m.tucked = p.tucked;
        }
    }
}
