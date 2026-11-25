using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // ExpansionApi 2: content packs. A mod that gives a race its own ears, tail, face, hair or skin colour, or adds garments,
    // registers them here (Docs/RacePacks.md) rather than patching M3AT's methods: M3AT then asks it only where the player
    // has that part drawn by the pack (ContentSources), and it keeps working as M3AT changes inside. Everything registered
    // is the pack's, by its packageId.
    //
    //   ExpansionApi.ContentPack pack = ExpansionApi.RegisterPack("me.mypack");
    //   pack.RaceEars("My_Race", pawn => "/abs/path/ears.txt").RaceTail("My_Race", pawn => "/abs/path/tail.txt")
    //       .Garments("/abs/path/M3ATgarments", "/abs/path/textures").Face("My_Race", MyFaceDrawer);
    //
    // Files are in M3AT's own forms (ears.txt, tail.txt, garment_*.txt: Docs/Apparel.md, Docs/GeneParts.md); a path may be
    // absolute or relative to M3AT's Models/chibi. Register from a [StaticConstructorOnStartup] class: garments must be
    // known before the chibi loads (the first figure drawn).
    public static partial class ExpansionApi
    {
        // Draws the face itself, in place of M3AT's: position, yaw and size as M3AT would draw its face patch, speaking
        // 0 .. 1 how far the mouth is open. True if it drew; false has M3AT draw its own.
        public delegate bool FaceDrawer(Pawn pawn, Vector3 position, float yaw, float size, float speaking);

        public sealed class ContentPack
        {
            public readonly string packageId;
            internal readonly Dictionary<string, Func<Pawn, string>> ears = new Dictionary<string, Func<Pawn, string>>();
            internal readonly Dictionary<string, Func<Pawn, string>> tails = new Dictionary<string, Func<Pawn, string>>();
            internal readonly Dictionary<string, FaceDrawer> faces = new Dictionary<string, FaceDrawer>();
            internal readonly Dictionary<string, Func<Pawn, HairDef>> hairs = new Dictionary<string, Func<Pawn, HairDef>>();
            internal readonly Dictionary<string, Func<Pawn, Color?>> skins = new Dictionary<string, Func<Pawn, Color?>>();
            internal readonly List<KeyValuePair<string, string>> garmentFolders = new List<KeyValuePair<string, string>>();

            internal ContentPack(string id)
            {
                packageId = id;
            }

            // The ears this race's pawns have (an ears.txt), by pawn (a race with ear variants); null: none from the pack.
            public ContentPack RaceEars(string race, Func<Pawn, string> earFile)
            {
                ears[race] = earFile;
                return this;
            }

            // The tail this race's pawns have (a tail.txt), moved on M3AT's tail springs; null: none from the pack.
            public ContentPack RaceTail(string race, Func<Pawn, string> tailFile)
            {
                tails[race] = tailFile;
                return this;
            }

            // A folder of garments as an M3ATgarments folder, their images in textureFolder (null: beside them), for a pack
            // whose images are made at load.
            public ContentPack Garments(string folder, string textureFolder = null)
            {
                garmentFolders.Add(new KeyValuePair<string, string>(folder, textureFolder));
                return this;
            }

            public ContentPack Face(string race, FaceDrawer draw)
            {
                faces[race] = draw;
                return this;
            }

            // The hairstyle the chibi's hair is built from (its texPath's sprites), in place of the pawn's own; null: its own.
            public ContentPack Hair(string race, Func<Pawn, HairDef> hair)
            {
                hairs[race] = hair;
                return this;
            }

            public ContentPack SkinColour(string race, Func<Pawn, Color?> colour)
            {
                skins[race] = colour;
                return this;
            }

            internal IEnumerable<string> Races
            {
                get { return ears.Keys.Concat(tails.Keys).Concat(faces.Keys).Concat(hairs.Keys).Concat(skins.Keys).Distinct(); }
            }
        }

        private static readonly Dictionary<string, ContentPack> packs = new Dictionary<string, ContentPack>(StringComparer.OrdinalIgnoreCase);

        // The pack's handle (the same one again for the same packageId).
        public static ContentPack RegisterPack(string packageId)
        {
            ContentPack p;
            if (string.IsNullOrEmpty(packageId))
            {
                throw new ArgumentException("a content pack needs its mod's packageId");
            }
            if (!packs.TryGetValue(packageId, out p))
            {
                p = new ContentPack(packageId);
                packs[packageId] = p;
                AddOns.Version++;
            }
            return p;
        }

        // ---- asked by M3AT

        internal static IEnumerable<string> PackIds
        {
            get { return packs.Keys.ToArray(); }
        }

        internal static IEnumerable<ContentPart> PartsOf(string packageId)
        {
            ContentPack p;
            if (!packs.TryGetValue(packageId, out p))
            {
                yield break;
            }
            if (p.ears.Count > 0 || p.tails.Count > 0)
            {
                yield return ContentPart.EarsTail;
            }
            if (p.faces.Count > 0)
            {
                yield return ContentPart.Face;
            }
            if (p.hairs.Count > 0)
            {
                yield return ContentPart.Hair;
            }
            if (p.skins.Count > 0)
            {
                yield return ContentPart.SkinColour;
            }
        }

        internal static IEnumerable<string> RacesOf(string packageId)
        {
            ContentPack p;
            return packs.TryGetValue(packageId, out p) ? p.Races : Enumerable.Empty<string>();
        }

        // Every pack's garment folders (folder, texture folder, packageId), read with the M3ATgarments folders.
        internal static IEnumerable<KeyValuePair<string, KeyValuePair<string, string>>> GarmentFolders()
        {
            foreach (ContentPack p in packs.Values)
            {
                foreach (KeyValuePair<string, string> f in p.garmentFolders)
                {
                    yield return new KeyValuePair<string, KeyValuePair<string, string>>(p.packageId, f);
                }
            }
        }

        // The first answer for this pawn's race from a pack the player has draw that part, else default.
        private static T Ask<T>(Pawn pawn, ContentPart part, Func<ContentPack, Dictionary<string, T>> table) where T : class
        {
            if (pawn == null || pawn.def == null || packs.Count == 0)
            {
                return null;
            }
            foreach (ContentPack p in packs.Values)
            {
                T found;
                if (table(p).TryGetValue(pawn.def.defName, out found) && !AddOns.IsOff(p.packageId) && ContentSources.UsesPack(p.packageId, part))
                {
                    return found;
                }
            }
            return null;
        }

        internal static string EarFileFor(Pawn pawn)
        {
            Func<Pawn, string> f = Ask(pawn, ContentPart.EarsTail, p => p.ears);
            return Safe(f, pawn, "ears");
        }

        internal static string TailFileFor(Pawn pawn)
        {
            Func<Pawn, string> f = Ask(pawn, ContentPart.EarsTail, p => p.tails);
            return Safe(f, pawn, "tail");
        }

        internal static HairDef HairFor(Pawn pawn)
        {
            Func<Pawn, HairDef> f = Ask(pawn, ContentPart.Hair, p => p.hairs);
            return Safe(f, pawn, "hair");
        }

        internal static Color? SkinFor(Pawn pawn)
        {
            Func<Pawn, Color?> f = Ask(pawn, ContentPart.SkinColour, p => p.skins);
            if (f == null)
            {
                return null;
            }
            try
            {
                return f(pawn);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(WalkFigures.Tag + "a content pack's skin colour failed: " + e, 0x4D33A701);
                return null;
            }
        }

        // True if a pack drew this pawn's face.
        internal static bool DrawFace(Pawn pawn, Vector3 position, float yaw, float size, float speaking)
        {
            FaceDrawer f = Ask(pawn, ContentPart.Face, p => p.faces);
            if (f == null)
            {
                return false;
            }
            try
            {
                return f(pawn, position, yaw, size, speaking);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(WalkFigures.Tag + "a content pack's face failed, M3AT's is drawn: " + e, 0x4D33A702);
                return false;
            }
        }

        private static T Safe<T>(Func<Pawn, T> f, Pawn pawn, string what) where T : class
        {
            if (f == null)
            {
                return null;
            }
            try
            {
                return f(pawn);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(WalkFigures.Tag + "a content pack's " + what + " failed: " + e, what.GetHashCode());
                return null;
            }
        }
    }
}
