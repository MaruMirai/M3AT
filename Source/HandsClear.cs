using System.Collections.Generic;
using IsoRim;
using UnityEngine;
using Verse;

namespace M3AT
{
    // A weapon holstered last draw (DrawHolstered): what it is, its slot and how much further out than its Clearance it
    // went, so it can be placed again in this draw's pose before the body is posed.
    internal struct HolsterSeen
    {
        public Thing weapon;
        public int slot;
        public float extra;
    }

    // Keeping the hands out of the weapons holstered on the body: a hand hanging at the side went through a pistol on the
    // hip. After Chain, each hand (wrist, knuckles and fingertips, as discs HandRadius round) is tested against every
    // weapon drawn holstered last draw, placed again in this pose: its picture's solid pixels across its face and its
    // half depth out of it. A hand in one is moved the shortest way out (off either face, or across the face past the
    // art's edge, whichever is less), the wrist taken there by bending the arm (SkinnedFigure.Reach), eased so a change
    // of way doesn't jump. Not while drawing or holstering, where the hand goes to the weapon on purpose.
    public static partial class WalkFigures
    {
        private static readonly List<HolsterSeen> drawnHolsters = new List<HolsterSeen>();

        private const float HandRadius = 0.022f;   // model units: half the hand's thickness about its bones
        private const float ClearGap = 0.006f;     // model units left between the hand and the weapon
        private const float ClearMost = 0.25f;     // model units at most a wrist is moved
        private const float ClearRate = 14f;       // per real second: how fast the move eases toward what is wanted
        private const int ClearWays = 16;          // ways across the face tried
        private const int ArtCells = 32;           // the picture's solid pixels read at this many a side

        // Off for now: moved every frame, the hands looked restless, always adjusting. M3AT_HANDCLEAR set turns it on (to
        // look at it again, ./autotest.sh handholster).
        private static readonly bool NoHandsClear = string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("M3AT_HANDCLEAR"));

        private static readonly string[] HandPoints = { "hand.{0}", "hand.{0}.001", "hand.{0}.002" };
        private static readonly Vector3[] handAt = new Vector3[HandPoints.Length + 1];

        private static void SeenHolstered(Ease ease, GarmentMask mask)
        {
            ease.holstered.Clear();
            ease.holstered.AddRange(drawnHolsters);
            ease.holsterMask = mask;
        }

        // After Chain (and the other hand reaches): each free hand moved out of the weapons holstered on the body.
        private static void HandsClear(Ease ease, SkinnedFigure.Posed fig, Matrix4x4 whole, float size)
        {
            if (NoHandsClear)
            {
                return;
            }
            float k = 1f - Mathf.Exp(-Mathf.Clamp(Time.unscaledDeltaTime, 1f / 240f, 0.1f) * ClearRate);
            bool any = false;
            for (int s = 0; s < 2; s++)
            {
                string side = s == 0 ? "R" : "L";
                Vector3 want = ease.drawAt >= 0f || ease.holstered.Count == 0 ? Vector3.zero : HandOut(ease, fig, side, size);
                Vector3 now = Vector3.Lerp(s == 0 ? ease.clearR : ease.clearL, want, k);
                if (s == 0)
                {
                    ease.clearR = now;
                }
                else
                {
                    ease.clearL = now;
                }
                if (now.sqrMagnitude > 1e-8f)
                {
                    skin.ReachBy(fig, whole, side, whole.inverse.MultiplyVector(now), 1f);
                    any = true;
                }
            }
            if (any)
            {
                skin.Reached(fig, whole);
            }
        }

        // How far the hand on side has to move (model units, the whole move in) to be clear of every holstered weapon.
        private static Vector3 HandOut(Ease ease, SkinnedFigure.Posed fig, string side, float size)
        {
            int n = 0;
            for (int i = 0; i < HandPoints.Length; i++)
            {
                if (skin.JointOf(fig, string.Format(HandPoints[i], side), out Vector3 j))
                {
                    handAt[n++] = j;
                }
            }
            if (n < 2)
            {
                return Vector3.zero;
            }
            handAt[n] = handAt[n - 1] + (handAt[n - 1] - handAt[n - 2]);   // the fingertips, a finger bone on
            n++;
            Vector3 moved = Vector3.zero;
            // A few rounds, as getting out of one weapon may put it into another lying beside.
            for (int round = 0; round < 3; round++)
            {
                Vector3 worst = Vector3.zero;
                for (int w = 0; w < ease.holstered.Count; w++)
                {
                    HolsterSeen h = ease.holstered[w];
                    if (h.weapon == null || h.weapon.Destroyed || h.slot >= skin.Holsters)
                    {
                        continue;
                    }
                    Vector3 grip, a, top;
                    float behind;
                    SlotPlace(h.weapon, fig, h.slot, ease.holsterMask, h.extra, size, out grip, out a, out top, out behind);
                    Graphic g = h.weapon.Graphic;
                    Material mat = g != null ? g.MatSingleFor(h.weapon) : null;
                    bool[] art = ArtOf(mat);
                    if (art == null)
                    {
                        continue;
                    }
                    Matrix4x4 slab = SlabMatrix(h.weapon, g, ScaleOf(h.weapon), grip, a, top, behind, size);
                    float depth = HalfDepth(h.weapon) / WeaponSizeOf(size);
                    for (int p = 0; p < n; p++)
                    {
                        Vector3 out1 = PointOut(handAt[p] + moved, slab, art, depth);
                        if (out1.sqrMagnitude > worst.sqrMagnitude)
                        {
                            worst = out1;
                        }
                    }
                }
                if (worst.sqrMagnitude < 1e-10f)
                {
                    break;
                }
                moved += worst;
            }
            return moved.sqrMagnitude > ClearMost * ClearMost ? Vector3.zero : moved;   // too far: leave it rather than fling the arm
        }

        // The shortest move (model units) taking a disc HandRadius round point clear of a weapon's slab: its art across
        // the face (true for a solid cell, rows bottom first) and depth either way out of it. Zero if clear already.
        private static Vector3 PointOut(Vector3 point, Matrix4x4 slab, bool[] art, float depth)
        {
            Vector3 xAxis = slab.GetColumn(0), yAxis = slab.GetColumn(1), zAxis = slab.GetColumn(2);
            float wide = xAxis.magnitude, high = yAxis.magnitude, cell = zAxis.magnitude;
            if (wide < 1e-5f || high < 1e-5f || cell < 1e-6f)
            {
                return Vector3.zero;
            }
            Vector3 xw = xAxis / wide, yw = yAxis / high, zw = zAxis / cell;
            Vector3 d = point - (Vector3)slab.GetColumn(3);
            float px = Vector3.Dot(d, xw), py = Vector3.Dot(d, yw), pz = Vector3.Dot(d, zw);
            float r = HandRadius + ClearGap, faceOut = depth + r - Mathf.Abs(pz);
            if (faceOut <= 0f || ArtClear(art, px, py, wide, high, r))
            {
                return Vector3.zero;
            }
            Vector3 best = zw * (pz >= 0f ? faceOut : -faceOut);
            float bestLength = faceOut;
            // Across the face: out each way in steps of half a cell of the art, as far as the face way's move.
            float step = 0.5f * Mathf.Min(wide, high) / ArtCells;
            for (int i = 0; i < ClearWays; i++)
            {
                float t = i * 2f * Mathf.PI / ClearWays, cx = Mathf.Cos(t), cy = Mathf.Sin(t);
                for (float go = step; go < bestLength; go += step)
                {
                    if (ArtClear(art, px + cx * go, py + cy * go, wide, high, r))
                    {
                        bestLength = go;
                        best = xw * (cx * go) + yw * (cy * go);
                        break;
                    }
                }
            }
            return best;
        }

        // Whether a disc r round (x, y) (model units from the picture's middle, across its wide by high) misses its art.
        private static bool ArtClear(bool[] art, float x, float y, float wide, float high, float r)
        {
            float cw = wide / ArtCells, ch = high / ArtCells;
            int i0 = Mathf.FloorToInt((x - r) / cw + ArtCells * 0.5f), i1 = Mathf.FloorToInt((x + r) / cw + ArtCells * 0.5f);
            int j0 = Mathf.FloorToInt((y - r) / ch + ArtCells * 0.5f), j1 = Mathf.FloorToInt((y + r) / ch + ArtCells * 0.5f);
            i0 = Mathf.Max(i0, 0);
            j0 = Mathf.Max(j0, 0);
            i1 = Mathf.Min(i1, ArtCells - 1);
            j1 = Mathf.Min(j1, ArtCells - 1);
            for (int j = j0; j <= j1; j++)
            {
                for (int i = i0; i <= i1; i++)
                {
                    if (!art[j * ArtCells + i])
                    {
                        continue;
                    }
                    // The nearest point of the cell to (x, y).
                    float left = (i - ArtCells * 0.5f) * cw, bottom = (j - ArtCells * 0.5f) * ch;
                    float nx = Mathf.Clamp(x, left, left + cw) - x, ny = Mathf.Clamp(y, bottom, bottom + ch) - y;
                    if (nx * nx + ny * ny < r * r)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static readonly Dictionary<Material, bool[]> artCells = new Dictionary<Material, bool[]>();

        // A picture's solid pixels, ArtCells a side, rows bottom first; null if it can't be read (tried again later).
        private static bool[] ArtOf(Material mat)
        {
            if (mat == null || mat.mainTexture == null)
            {
                return null;
            }
            if (artCells.TryGetValue(mat, out bool[] art))
            {
                return art;
            }
            const int read = ArtCells * 4;
            Color32[] px = Api.ReadSpritePixels(mat.mainTexture, read);
            if (px == null || px.Length < read * read)
            {
                return null;
            }
            art = new bool[ArtCells * ArtCells];
            for (int j = 0; j < read; j++)
            {
                for (int i = 0; i < read; i++)
                {
                    if (px[j * read + i].a > 128)
                    {
                        art[(j / 4) * ArtCells + i / 4] = true;
                    }
                }
            }
            artCells[mat] = art;
            return art;
        }
    }
}
