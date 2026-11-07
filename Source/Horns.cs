using System;
using System.Collections.Generic;
using System.IO;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // Horns for the Headbone_MiniHorns and Headbone_CenterHorn genes, on the skinned figure: Models/chibi/horns_<name>.txt,
    // made by anim_scripts/make_horns.py longer than the sprites would make them, so they come through the hair. They ride
    // the head bone, as hair does, each root put where it is on this shape's head and the horn scaled by the head's width. They
    // are the same colour whatever the pawn's hair or skin, as the game's are.
    public sealed partial class SkinnedFigure
    {
        private const float HornNewborn = 0.5f;   // a newborn's size against its head, growing to all of it as the Babylimbs key goes: it has no hair to clear
        private static readonly Color HornColour = new Color(0.85f, 0.81f, 0.69f);   // IsoRim's an_horn
        private const float HornRootShade = 0.72f;   // the colour at the root, of the rest

        private sealed class Horns
        {
            public Piece piece;
            public Vector3[] roots;      // each horn's root on the skin, at rest in the base shape
            public int[] first, count;   // and its vertices
            public bool[] root;          // the vertices coloured darker
            public PieceShape plain;     // its normals and outline, which no shape changes
            public Vector3 headLow, headSize;   // the box round the base shape's head
        }

        private readonly Dictionary<string, Horns> horns = new Dictionary<string, Horns>();

        // A kind of horns, read when a pawn with the gene is first drawn; null if the export has none.
        private Horns HornsOf(string name)
        {
            Horns h;
            if (horns.TryGetValue(name, out h))
            {
                return h;
            }
            string path = Path.Combine(folder, "horns_" + name + ".txt");
            if (File.Exists(path))
            {
                try
                {
                    h = ReadHorns(path);
                }
                catch (Exception e)
                {
                    Log.Warning(Tag + "could not read the horns in " + path + ", they are left off: " + e.Message);
                }
            }
            horns[name] = h;
            return h;
        }

        private Horns ReadHorns(string path)
        {
            Horns h = new Horns();
            h.piece = ReadMesh(path, false, null);
            if (turnRound)
            {
                TurnRound(h.piece.tris);
            }
            int n = h.piece.rest.Length;
            if (n == 0)
            {
                throw new InvalidDataException(path + ": no vertices");
            }
            // All of it on the head bone.
            h.piece.firstWeight = new int[n + 1];
            h.piece.weightBone = new int[n];
            h.piece.weight = new float[n];
            for (int i = 0; i < n; i++)
            {
                h.piece.firstWeight[i + 1] = i + 1;
                h.piece.weightBone[i] = headBone;
                h.piece.weight[i] = 1f;
            }
            h.root = new bool[n];
            List<Vector3> roots = new List<Vector3>();
            List<int> first = new List<int>(), count = new List<int>();
            foreach (string raw in File.ReadAllLines(path))
            {
                string[] p = raw.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 0)
                {
                    continue;
                }
                if (p[0] == "horn")
                {
                    roots.Add(Vec(p, 1));
                    first.Add(int.Parse(p[4]));
                    count.Add(int.Parse(p[5]));
                }
                else if (p[0] == "root")
                {
                    for (int i = 1; i < p.Length; i++)
                    {
                        h.root[int.Parse(p[i])] = true;
                    }
                }
            }
            h.roots = roots.ToArray();
            h.first = first.ToArray();
            h.count = count.ToArray();
            h.plain = ShapePiece(h.piece, new List<KeyValuePair<string, float>>());
            Shape b = BaseShape();
            MeasureHead(b);
            h.headLow = b.headLow;
            h.headSize = b.headHigh - b.headLow;
            return h;
        }

        // The horns on this shape's head, made once per shape: a root keeps its place in the head's box, so stays on the
        // skin, and the horn on it keeps its form.
        private PieceShape HornShape(Horns h, Shape s)
        {
            PieceShape shape;
            if (s.hairs.TryGetValue(h.piece, out shape))
            {
                return shape;
            }
            MeasureHead(s);
            Vector3 size = s.headHigh - s.headLow;
            float scale = size.x / h.headSize.x;
            for (int k = 0; k < s.mix.Count; k++)
            {
                if (s.mix[k].Key == BabyLimbsKey)
                {
                    scale *= Mathf.Lerp(1f, HornNewborn, s.mix[k].Value);
                }
            }
            Vector3[] rest = new Vector3[h.piece.rest.Length];
            for (int r = 0; r < h.roots.Length; r++)
            {
                Vector3 from = h.roots[r];
                Vector3 to = s.headLow + Vector3.Scale(from - h.headLow, new Vector3(size.x / h.headSize.x, size.y / h.headSize.y, size.z / h.headSize.z));
                for (int i = h.first[r]; i < h.first[r] + h.count[r]; i++)
                {
                    rest[i] = to + (h.piece.rest[i] - from) * scale;
                }
            }
            shape = new PieceShape { rest = rest, hull = h.plain.hull, normal = h.plain.normal };
            s.hairs[h.piece] = shape;
            return shape;
        }

        // Draws a kind of horns on the head.
        public void DrawHorns(Posed p, string name, Vector3 position, float yaw, float size)
        {
            Horns h = HornsOf(name);
            Material solid = h != null ? SolidMaterial() : null;
            if (solid == null)
            {
                return;
            }
            if (p.hornsPiece != h.piece)
            {
                RetireHorns(p);
                p.horns = NewPieceMeshes(h.piece);
                p.hornsPiece = h.piece;
            }
            Pose(p, h.piece, HornShape(h, p.shape), p.horns);
            // The root goes in the scalp's place: DrawMeshes shades that ScalpShade, which is taken back out here.
            DrawMeshes(p, p.horns, h.piece.rest.Length, solid, position, yaw, size, HornColour, null, default(Color), h.root, HornColour * HornRootShade / ScalpShade);
        }

        private static void RetireHorns(Posed p)
        {
            if (p.horns != null)
            {
                DynamicModels.Retire(p.horns.solid);
                DynamicModels.Retire(p.horns.outline);
                p.horns = null;
            }
        }
    }

    public static partial class WalkFigures
    {
        private static GeneDef miniHornsGene, centerHornGene;
        private static bool hornGenesLooked;

        // The horns the pawn's genes give it, by horns_<name>.txt's name; null none.
        private static string HornsOf(Pawn pawn)
        {
            if (!hornGenesLooked)
            {
                hornGenesLooked = true;
                miniHornsGene = DefDatabase<GeneDef>.GetNamedSilentFail("Headbone_MiniHorns");
                centerHornGene = DefDatabase<GeneDef>.GetNamedSilentFail("Headbone_CenterHorn");
            }
            if (pawn.genes == null)
            {
                return null;
            }
            if (miniHornsGene != null && pawn.genes.HasActiveGene(miniHornsGene))
            {
                return "mini";
            }
            return centerHornGene != null && pawn.genes.HasActiveGene(centerHornGene) ? "center" : null;
        }
    }
}
