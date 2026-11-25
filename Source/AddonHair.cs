using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // Hair drawn as Humanoid Alien Races body add-ons. Some races (Kiiro Race) give every pawn a blank hairstyle
    // (Kiiro_Null) and draw the hair as add-ons on the head instead: a front piece, a back piece drawn behind the body and
    // a tuft, each in variants HAR picks per pawn. The builder only knew hairstyles, so such a pawn was bald.
    //
    // Here those add-ons are put together into one hairstyle's sprites and given to the builder as any other: for each
    // view, the add-ons drawn behind the body first (with the race's own head cut out of them, as in 2D it is drawn over
    // them), then the pawn's own hairstyle, then the add-ons in front; then the whole moved and scaled so the race's head
    // in its sprite lands on the human head the builder fits hair to (HeadSprites), as the race's hair is drawn round its
    // own head, which may be another size and place in its frame (Kiiro's is about two thirds as wide as the human one).
    // A stand-in HairDef, never in the DefDatabase, names the combination (its texPath, which is the style's name in the
    // cache), and Read takes its sprites from here (Combined) rather than from a texture, so the cache, hats, the shell and
    // its outline treat it as any hairstyle.
    //
    // A hair add-on is one aligned with the head, coloured by the hair colour channel, with "hair" in its path, and not
    // small: one whose front sprite has under AddonLeast of the head's area in it is left out (Kiiro's ahoge, a thin
    // tuft, came out as its black outline painted on the crown). HAR is read by reflection, so it is not referenced.
    // Not yet: an add-on's own draw size and offsets (Kiiro's are 1 and none).
    internal sealed partial class HairBuilder
    {
        private const float AddonLeast = 0.15f;   // of the race's head's solid pixels, in the front view
        private const string AddonPrefix = "M3AT_HAR/";   // the stand-in hairstyles' texPath (and so cache folder)

        private static readonly Dictionary<string, Sprites> combined = new Dictionary<string, Sprites>();
        private static readonly Dictionary<Pawn, AddonHairOf> addonHairs = new Dictionary<Pawn, AddonHairOf>();
        private static readonly Dictionary<ThingDef, List<object>> raceHairAddons = new Dictionary<ThingDef, List<object>>();
        private static readonly Dictionary<string, HairDef> standIns = new Dictionary<string, HairDef>();
        private static readonly Dictionary<Texture, bool> smallAddon = new Dictionary<Texture, bool>();
        private static int[][] humanBoxes;   // the human head's box per view: south, east, north (c0, c1, r0, r1)
        private static bool addonFailed;

        private sealed class AddonHairOf
        {
            public object graphics;   // the AlienComp's addonGraphics list it was made from
            public int count;
            public HairDef own;
            public HairDef hair;
        }

        // HAR's members, found once.
        private static Type alienCompType;
        private static FieldInfo addonGraphicsField, alienRaceField, generalSettingsField, partGeneratorField, bodyAddonsField;
        private static FieldInfo addonPathField, alignWithHeadField, inFrontField;
        private static PropertyInfo colorChannelProp, universalProp;
        private static bool harTried;

        // The hairstyle to draw for this pawn: its hair add-ons with its own hairstyle, or its own hairstyle if it has none.
        internal static HairDef HairOf(Pawn pawn)
        {
            HairDef pack = ExpansionApi.HairFor(pawn);
            if (pack != null)
            {
                return pack;   // a content pack's for its race (ExpansionApi 2)
            }
            HairDef own = pawn.story != null ? pawn.story.hairDef : null;
            if (addonFailed || pawn.story == null || !HarReady())
            {
                return own;
            }
            try
            {
                List<object> addons = RaceAddons(pawn.def);
                if (addons == null)
                {
                    return RaceFitted(pawn, own);   // its own hairstyle, drawn round the race's head
                }
                ThingComp comp = null;
                foreach (ThingComp c in pawn.AllComps)
                {
                    if (alienCompType.IsInstanceOfType(c))
                    {
                        comp = c;
                        break;
                    }
                }
                IList graphics = comp != null ? addonGraphicsField.GetValue(comp) as IList : null;
                if (graphics == null)
                {
                    return own;
                }
                AddonHairOf was;
                if (addonHairs.TryGetValue(pawn, out was) && was.graphics == graphics && was.count == graphics.Count && was.own == own)
                {
                    return was.hair ?? own;
                }
                HairDef made = Make(pawn, own, addons, graphics);
                if (addonHairs.Count > 512)
                {
                    addonHairs.Clear();
                }
                addonHairs[pawn] = new AddonHairOf { graphics = graphics, count = graphics.Count, own = own, hair = made };
                return made ?? own;
            }
            catch (Exception e)
            {
                addonFailed = true;
                Log.Warning(Tag + "couldn't read Humanoid Alien Races' hair add-ons, so those races' hair is their hairstyle alone: " + e.Message);
                return own;
            }
        }

        // The sprites of a stand-in hairstyle, for Read.
        private static bool Combined(string texPath, out Sprites sp)
        {
            sp = null;
            return texPath != null && texPath.StartsWith(AddonPrefix, StringComparison.Ordinal) && combined.TryGetValue(texPath, out sp);
        }

        private static bool HarReady()
        {
            if (harTried)
            {
                return alienCompType != null;
            }
            harTried = true;
            Type race = GenTypes.GetTypeInAnyAssembly("AlienRace.ThingDef_AlienRace");
            Type comp = GenTypes.GetTypeInAnyAssembly("AlienRace.AlienPartGenerator+AlienComp");
            Type settings = GenTypes.GetTypeInAnyAssembly("AlienRace.ThingDef_AlienRace+AlienSettings");
            Type general = GenTypes.GetTypeInAnyAssembly("AlienRace.GeneralSettings");
            Type generator = GenTypes.GetTypeInAnyAssembly("AlienRace.AlienPartGenerator");
            Type addon = GenTypes.GetTypeInAnyAssembly("AlienRace.AlienPartGenerator+BodyAddon");
            Type utilities = GenTypes.GetTypeInAnyAssembly("AlienRace.Utilities");
            if (race == null || comp == null || settings == null || general == null || generator == null || addon == null)
            {
                return false;   // HAR isn't loaded
            }
            BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            alienRaceField = race.GetField("alienRace", all);
            generalSettingsField = settings.GetField("generalSettings", all);
            partGeneratorField = general.GetField("alienPartGenerator", all);
            bodyAddonsField = generator.GetField("bodyAddons", all);
            addonGraphicsField = comp.GetField("addonGraphics", all);
            addonPathField = addon.GetField("path", all | BindingFlags.FlattenHierarchy);
            for (Type t = addon; addonPathField == null && t != null; t = t.BaseType)
            {
                addonPathField = t.GetField("path", all);
            }
            alignWithHeadField = addon.GetField("alignWithHead", all);
            inFrontField = addon.GetField("inFrontOfBody", all);
            colorChannelProp = addon.GetProperty("ColorChannel", all);
            universalProp = utilities != null ? utilities.GetProperty("UniversalBodyAddons", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) : null;
            if (alienRaceField == null || generalSettingsField == null || partGeneratorField == null || bodyAddonsField == null || addonGraphicsField == null
                || addonPathField == null || alignWithHeadField == null || inFrontField == null || colorChannelProp == null)
            {
                Log.Warning(Tag + "Humanoid Alien Races' add-ons aren't where they were (its code has changed?), so hair drawn as add-ons is left off.");
                return false;
            }
            alienCompType = comp;
            return true;
        }

        // The race's add-ons that could be hair (by its definition; whether each is big enough is seen from its sprite),
        // or null if none: every add-on, its own and HAR's universal ones, in the order HAR makes their graphics.
        private static List<object> RaceAddons(ThingDef race)
        {
            List<object> found;
            if (raceHairAddons.TryGetValue(race, out found))
            {
                return found;
            }
            found = null;
            object settings = alienRaceField.DeclaringType.IsInstanceOfType(race) ? alienRaceField.GetValue(race) : null;
            object general = settings != null ? generalSettingsField.GetValue(settings) : null;
            object generator = general != null ? partGeneratorField.GetValue(general) : null;
            IEnumerable own = generator != null ? bodyAddonsField.GetValue(generator) as IEnumerable : null;
            List<object> addons = new List<object>();
            if (own != null)
            {
                foreach (object a in own)
                {
                    addons.Add(a);
                }
            }
            IEnumerable universal = universalProp != null ? universalProp.GetValue(null) as IEnumerable : null;
            if (universal != null)
            {
                foreach (object a in universal)
                {
                    addons.Add(a);
                }
            }
            foreach (object a in addons)
            {
                if (IsHairAddon(a))
                {
                    found = addons;
                    break;
                }
            }
            raceHairAddons[race] = found;
            if (found != null)
            {
                Log.Message(Tag + race.defName + " draws its hair as Humanoid Alien Races add-ons; the chibi's hair is built from them.");
            }
            return found;
        }

        private static readonly Dictionary<string, bool> raceHasAddon = new Dictionary<string, bool>();

        // The races whose ears and tail have been looked at and are cat-like enough to be drawn as the chibi's cat ears
        // and tail. Any race with an ear or tail add-on would get them otherwise, which for rabbit or fox ears or a lizard's
        // tail would look like poor support rather than a stand-in, so a race not here keeps neither until it is checked.
        private static readonly HashSet<string> CatLikeRaces = new HashSet<string> { "Kiiro_Race" };

        // True if the race is one of CatLikeRaces and has an add-on with this word ("ear", "tail"): RaceHasAddon.
        internal static bool CatLikeAddon(ThingDef race, string word)
        {
            return race != null && CatLikeRaces.Contains(race.defName) && RaceHasAddon(race, word);
        }

        // True if the race has a Humanoid Alien Races add-on with this word (lower case) in its file name, split at
        // capitals and anything not a letter ("RightEar": right, ear), plural or not: its ears or tail, which the chibi
        // draws as its own cat ears and tail (CatEars.cs, CatTail.cs).
        internal static bool RaceHasAddon(ThingDef race, string word)
        {
            if (addonFailed || race == null || !HarReady())
            {
                return false;
            }
            string key = race.defName + "/" + word;
            bool has;
            if (raceHasAddon.TryGetValue(key, out has))
            {
                return has;
            }
            has = false;
            try
            {
                object settings = alienRaceField.DeclaringType.IsInstanceOfType(race) ? alienRaceField.GetValue(race) : null;
                object general = settings != null ? generalSettingsField.GetValue(settings) : null;
                object generator = general != null ? partGeneratorField.GetValue(general) : null;
                IEnumerable own = generator != null ? bodyAddonsField.GetValue(generator) as IEnumerable : null;
                if (own != null)
                {
                    foreach (object a in own)
                    {
                        string path = addonPathField.GetValue(a) as string;
                        if (path != null && WordsOf(System.IO.Path.GetFileName(path)).Exists(w => w == word || w == word + "s"))
                        {
                            has = true;
                            break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "couldn't read Humanoid Alien Races' add-ons of " + race.defName + ": " + e.Message);
            }
            raceHasAddon[key] = has;
            if (has)
            {
                Log.Message(Tag + race.defName + " has a Humanoid Alien Races " + word + " add-on; the chibi gives it its own " + word + (word == "ear" ? "s" : "") + ".");
            }
            return has;
        }

        private static List<string> WordsOf(string name)
        {
            List<string> words = new List<string>();
            System.Text.StringBuilder w = new System.Text.StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (!char.IsLetter(c) || char.IsUpper(c) && w.Length > 0 && !char.IsUpper(name[i - 1]))
                {
                    if (w.Length > 0)
                    {
                        words.Add(w.ToString().ToLowerInvariant());
                    }
                    w.Clear();
                }
                if (char.IsLetter(c))
                {
                    w.Append(c);
                }
            }
            if (w.Length > 0)
            {
                words.Add(w.ToString().ToLowerInvariant());
            }
            return words;
        }

        // For AddonParts.cs: whether a HAR add-on is one of the hair ones built into the hair here.
        internal static bool HairAddon(object addon)
        {
            return !addonFailed && HarReady() && IsHairAddon(addon);
        }

        // For AddonParts.cs: whether a graphic of the race's is one of its hair add-ons (its add-on found by path, as Make
        // finds it), which the hair is built from and so isn't a part of its own.
        internal static bool HairGraphic(ThingDef race, string path)
        {
            if (addonFailed || race == null || path == null || !HarReady())
            {
                return false;
            }
            List<object> addons = RaceAddons(race);
            if (addons == null)
            {
                return false;   // none of its add-ons is hair
            }
            object best = null;
            int bestLength = -1;
            foreach (object a in addons)
            {
                string own = addonPathField.GetValue(a) as string;
                if (own != null && own.Length > bestLength && path.StartsWith(own, StringComparison.OrdinalIgnoreCase))
                {
                    best = a;
                    bestLength = own.Length;
                }
            }
            return best != null && IsHairAddon(best);
        }

        // For AddonParts.cs: a file name's words (WordsOf).
        internal static List<string> AddonWords(string name)
        {
            return WordsOf(name ?? "");
        }

        private static bool IsHairAddon(object addon)
        {
            string path = addonPathField.GetValue(addon) as string;
            return path != null && (bool)alignWithHeadField.GetValue(addon) && (colorChannelProp.GetValue(addon) as string) == "hair"
                && path.IndexOf("hair", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string OwnKey(HairDef own)
        {
            return own != null && own.texPath != null ? "+" + GenText.SanitizeFilename(own.defName) : "";
        }

        // The stand-in hairstyle for these add-on graphics, its sprites put together; null if none of them is hair.
        private static HairDef Make(Pawn pawn, HairDef own, List<object> addons, IList graphics)
        {
            // Each graphic's add-on: the one whose path its own starts with (the longest such), as HAR leaves out of its
            // list any add-on with no graphic, so the two lists don't line up by index.
            // HAR's list can hold a graphic twice (Kiiro's did), which would lay its art over itself, so each path once.
            List<Graphic> behind = new List<Graphic>(), front = new List<Graphic>();
            HashSet<string> seen = new HashSet<string>();
            foreach (object o in graphics)
            {
                Graphic g = o as Graphic;
                if (g == null || g.path == null || !seen.Add(g.path))
                {
                    continue;
                }
                object best = null;
                int bestLength = -1;
                foreach (object a in addons)
                {
                    string path = addonPathField.GetValue(a) as string;
                    if (path != null && path.Length > bestLength && g.path.StartsWith(path, StringComparison.OrdinalIgnoreCase))
                    {
                        best = a;
                        bestLength = path.Length;
                    }
                }
                if (best == null || !IsHairAddon(best))
                {
                    continue;
                }
                ((bool)inFrontField.GetValue(best) ? front : behind).Add(g);
            }
            if (behind.Count == 0 && front.Count == 0)
            {
                return null;
            }
            Color32[][] headPx = HeadPixels(pawn);
            if (headPx == null || !HumanBoxes())
            {
                return null;   // no head to fit it by
            }
            int headArea = 0;
            foreach (Color32 c in headPx[0])
            {
                headArea += c.a >= Solid ? 1 : 0;
            }
            List<Graphic> kept = new List<Graphic>();
            string key = "";
            Color32[][] layers = new Color32[3][];
            for (int v = 0; v < 3; v++)
            {
                layers[v] = new Color32[Size * Size];
            }
            // Behind the body, under the head; then the pawn's own hairstyle, drawn on the race's head as these are; then in front.
            foreach (Graphic g in behind)
            {
                if (AddLayer(layers, g, headPx, headArea))
                {
                    key += (key.Length > 0 ? "+" : "") + System.IO.Path.GetFileName(g.path);
                }
            }
            Sprites ownSprites = own != null && own.texPath != null ? Read(own.texPath) : null;
            bool ownHair = false;
            if (ownSprites != null)
            {
                for (int v = 0; v < 3; v++)
                {
                    Color32[] over = v == 1 ? ownSprites.px[1] : v == 2 ? ownSprites.px[2] : ownSprites.px[0];
                    foreach (Color32 c in over)
                    {
                        ownHair |= c.a >= Solid;
                    }
                    Over(layers[v], over);
                }
            }
            foreach (Graphic g in front)
            {
                if (AddLayer(layers, g, null, headArea))
                {
                    key += (key.Length > 0 ? "+" : "") + System.IO.Path.GetFileName(g.path);
                }
            }
            if (key.Length == 0)
            {
                return null;
            }
            key = AddonPrefix + GenText.SanitizeFilename(pawn.def.defName) + "/" + GenText.SanitizeFilename(key) + (ownHair ? OwnKey(own) : "");
            HairDef def;
            if (standIns.TryGetValue(key, out def))
            {
                return def;
            }
            Color32[][] fitted = new Color32[3][];
            for (int v = 0; v < 3; v++)
            {
                fitted[v] = FitToHuman(layers[v], headPx[v], humanBoxes[v]);
            }
            combined[key] = From(fitted[0], fitted[1], fitted[2]);
            def = new HairDef { defName = "M3AT_" + key.Replace('/', '_'), label = own != null ? own.label : "hair", texPath = key };
            standIns[key] = def;
            return def;
        }

        // The pawn's head sprite's south, east and north views (the east from the west mirrored if it has none), or null.
        private static Color32[][] HeadPixels(Pawn pawn)
        {
            string head = pawn.story != null && pawn.story.headType != null ? pawn.story.headType.graphicPath : null;
            if (head == null)
            {
                return null;
            }
            Texture2D[] heads = new Texture2D[3];
            string[] views = { "_south", "_east", "_north" };
            for (int v = 0; v < 3; v++)
            {
                heads[v] = ContentFinder<Texture2D>.Get(head + views[v], false);
            }
            if (heads[1] == null)
            {
                heads[1] = ContentFinder<Texture2D>.Get(head + "_west", false);   // mirrored below
            }
            Color32[][] headPx = new Color32[3][];
            for (int v = 0; v < 3; v++)
            {
                headPx[v] = heads[v] != null ? Pixels(heads[v], v == 1 && heads[1].name.EndsWith("_west", StringComparison.Ordinal)) : null;
                if (headPx[v] == null)
                {
                    return null;
                }
            }
            return headPx;
        }

        // ---- Another race's hairstyle, fitted as its add-on hair is.

        // In 2D a hairstyle is drawn over the pawn's own head sprite as it is, so on a race whose head is another size or
        // place in its frame the hair sits round that head: Ratkin's is 0.28 of its frame wide against the human head's
        // 0.36, and lower; Milira's wider and shorter. The builder fits every style to the human head (HeadSprites), so on
        // those races the hair came out too narrow and too low, the fringe down over the eyes Facial Animation draws for
        // the human layout. So for a race other than the human (one of Humanoid Alien Races' races, whose hair isn't its
        // add-ons) whose head box is more than RaceHeadSlack of the frame off the human one on any side, its hairstyle is
        // given to the builder fitted from that head to the human one (FitToHuman, as the add-on hair is), as a stand-in
        // named for the race, the head and the style (each built and cached once).
        private const float RaceHeadSlack = 0.012f;   // of the frame (1.5 px at 128)
        private static readonly Dictionary<string, HairDef> raceFitted = new Dictionary<string, HairDef>();
        private static readonly Dictionary<string, bool> headsAlike = new Dictionary<string, bool>();

        private static HairDef RaceFitted(Pawn pawn, HairDef own)
        {
            if (own == null || own.texPath == null || pawn.def == ThingDefOf.Human || !alienRaceField.DeclaringType.IsInstanceOfType(pawn.def)
                || pawn.story.headType == null || !HumanBoxes())
            {
                return own;
            }
            string headPath = pawn.story.headType.graphicPath;
            string key = AddonPrefix + GenText.SanitizeFilename(pawn.def.defName) + "/" + GenText.SanitizeFilename(System.IO.Path.GetFileName(headPath)) + "/" + GenText.SanitizeFilename(own.defName);
            HairDef def;
            if (raceFitted.TryGetValue(key, out def))
            {
                return def ?? own;
            }
            def = null;
            bool alike;
            Color32[][] headPx = null;
            if (!headsAlike.TryGetValue(headPath, out alike))
            {
                headPx = HeadPixels(pawn);
                alike = headPx == null || HeadLikeHuman(headPx);
                headsAlike[headPath] = alike;
                if (headPx != null)
                {
                    int[] b = BoxOf(headPx[0]);
                    Log.Message(Tag + "the head " + headPath + " of " + pawn.def.defName + (b == null ? " is empty" : " spans columns " + b[0] + "-" + b[1] + ", rows " + b[2] + "-" + b[3]
                        + " (the human's " + humanBoxes[0][0] + "-" + humanBoxes[0][1] + ", " + humanBoxes[0][2] + "-" + humanBoxes[0][3] + ")")
                        + (alike ? ": hair is built as for a human's." : ": hair worn on it is fitted from it to the human head."));
                }
            }
            if (!alike)
            {
                headPx = headPx ?? HeadPixels(pawn);
                Sprites sprites = headPx != null ? Read(own.texPath) : null;
                if (sprites != null)
                {
                    Color32[][] fitted = new Color32[3][];
                    for (int v = 0; v < 3; v++)
                    {
                        fitted[v] = FitToHuman(sprites.px[v], headPx[v], humanBoxes[v]);
                    }
                    combined[key] = From(fitted[0], fitted[1], fitted[2]);
                    def = new HairDef { defName = "M3AT_" + key.Replace('/', '_'), label = own.label, texPath = key };
                }
            }
            if (raceFitted.Count > 2048)
            {
                raceFitted.Clear();
            }
            raceFitted[key] = def;
            return def ?? own;
        }

        // Whether a head's box is the human head's in every view, within RaceHeadSlack.
        private static bool HeadLikeHuman(Color32[][] headPx)
        {
            int slack = Mathf.CeilToInt(RaceHeadSlack * Size);
            for (int v = 0; v < 3; v++)
            {
                int[] b = BoxOf(headPx[v]);
                if (b == null)
                {
                    return true;   // nothing to fit by
                }
                for (int k = 0; k < 4; k++)
                {
                    if (Math.Abs(b[k] - humanBoxes[v][k]) > slack)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        // Lays one add-on graphic's three views over the layers, cut by the head where it is drawn behind it (under not
        // null). One drawn behind the body is left out of the front view altogether: there it shows below the chin and
        // beside the neck, hanging behind them, but the builder can't tell that from one picture and built a thick
        // collar of hair round the front of the neck. Its back and side views still give the hair at the back. False if it is too small to count as hair (its front view under AddonLeast of the head's area).
        private static bool AddLayer(Color32[][] layers, Graphic g, Color32[][] under, int headArea)
        {
            Texture2D south = g.MatSouth != null ? g.MatSouth.mainTexture as Texture2D : null;
            Texture2D east = g.MatEast != null ? g.MatEast.mainTexture as Texture2D : null;
            Texture2D north = g.MatNorth != null ? g.MatNorth.mainTexture as Texture2D : null;
            if (south == null || east == null || north == null)
            {
                return false;
            }
            bool small;
            Color32[] s = Pixels(south, false);
            if (s == null)
            {
                return false;
            }
            if (!smallAddon.TryGetValue(south, out small))
            {
                int area = 0;
                foreach (Color32 c in s)
                {
                    area += c.a >= Solid ? 1 : 0;
                }
                small = area < AddonLeast * headArea;
                smallAddon[south] = small;
            }
            if (small)
            {
                return false;
            }
            Color32[][] px = { s, Pixels(east, false), Pixels(north, false) };
            for (int v = 0; v < 3; v++)
            {
                if (px[v] == null)
                {
                    return false;
                }
            }
            for (int v = 0; v < 3; v++)
            {
                if (under != null && v == 0)
                {
                    continue;   // behind the body: not in the front view
                }
                if (under != null)
                {
                    for (int i = 0; i < px[v].Length; i++)
                    {
                        Color32 c = px[v][i];
                        c.a = (byte)(c.a * (255 - under[v][i].a) / 255);
                        px[v][i] = c;
                    }
                }
                Over(layers[v], px[v]);
            }
            return true;
        }

        // top over bottom, in place in bottom (straight alpha).
        private static void Over(Color32[] bottom, Color32[] top)
        {
            for (int i = 0; i < bottom.Length; i++)
            {
                Color32 t = top[i], b = bottom[i];
                if (t.a == 0)
                {
                    continue;
                }
                float ta = t.a / 255f, ba = b.a / 255f * (1f - ta), a = ta + ba;
                if (a <= 0f)
                {
                    continue;
                }
                bottom[i] = new Color32((byte)((t.r * ta + b.r * ba) / a), (byte)((t.g * ta + b.g * ba) / a), (byte)((t.b * ta + b.b * ba) / a), (byte)(a * 255f + 0.5f));
            }
        }

        // The human head's box in each view (south, east, north), from the first of HeadSprites that reads.
        private static bool HumanBoxes()
        {
            if (humanBoxes != null)
            {
                return humanBoxes.Length == 3;
            }
            humanBoxes = new int[0][];
            foreach (string path in HeadSprites)
            {
                Sprites head = Read(path);
                if (head == null)
                {
                    continue;
                }
                int[][] boxes = new int[3][];
                for (int v = 0; v < 3; v++)
                {
                    int[] b = BoxOf(head.px[v]);
                    if (b == null)
                    {
                        break;
                    }
                    boxes[v] = b;
                }
                if (boxes[2] != null)
                {
                    humanBoxes = boxes;
                    return true;
                }
            }
            return false;
        }

        private static int[] BoxOf(Color32[] px)
        {
            bool[] m = new bool[Size * Size];
            for (int i = 0; i < m.Length; i++)
            {
                m[i] = px[i].a >= Solid;
            }
            int c0, c1, r0, r1;
            return Box(m, out c0, out c1, out r0, out r1) ? new[] { c0, c1, r0, r1 } : null;
        }

        // The layer moved and scaled (across and down apart) so the race's head box in it lands on the human one,
        // bilinear, straight alpha weighted by alpha so clear pixels don't darken the edges.
        private static Color32[] FitToHuman(Color32[] layer, Color32[] head, int[] human)
        {
            int[] race = BoxOf(head);
            Color32[] o = new Color32[Size * Size];
            if (race == null)
            {
                return o;
            }
            float ax = (race[1] + 1 - race[0]) / (float)(human[1] + 1 - human[0]);
            float ay = (race[3] + 1 - race[2]) / (float)(human[3] + 1 - human[2]);
            for (int r = 0; r < Size; r++)
            {
                float y = race[2] + (r + 0.5f - human[2]) * ay - 0.5f;
                for (int c = 0; c < Size; c++)
                {
                    float x = race[0] + (c + 0.5f - human[0]) * ax - 0.5f;
                    o[r * Size + c] = Sample(layer, x, y);
                }
            }
            return o;
        }

        private static Color32 Sample(Color32[] px, float x, float y)
        {
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float fx = x - x0, fy = y - y0;
            float r = 0, g = 0, b = 0, a = 0;
            for (int j = 0; j < 2; j++)
            {
                for (int i = 0; i < 2; i++)
                {
                    int xx = x0 + i, yy = y0 + j;
                    if (xx < 0 || yy < 0 || xx >= Size || yy >= Size)
                    {
                        continue;
                    }
                    float w = (i == 0 ? 1 - fx : fx) * (j == 0 ? 1 - fy : fy);
                    Color32 c = px[yy * Size + xx];
                    float wa = w * c.a;
                    r += c.r * wa;
                    g += c.g * wa;
                    b += c.b * wa;
                    a += wa;
                }
            }
            return a <= 0f ? new Color32(0, 0, 0, 0) : new Color32((byte)(r / a), (byte)(g / a), (byte)(b / a), (byte)Mathf.Clamp(a, 0f, 255f));
        }
    }
}
