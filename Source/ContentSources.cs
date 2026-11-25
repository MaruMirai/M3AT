using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using RimWorld;
using Verse;

namespace M3AT
{
    // What another mod's content replaces, by part. A content pack (a race pack: the Miho and Wolfein 3D packs) may change
    // a race's ears and tail, face, hair, how hats fit, body shape, skin colour, its clothing, and draw gene parts.
    internal enum ContentPart
    {
        EarsTail, Face, Hair, HeadwearFit, BodyShape, SkinColour, Clothing, GeneParts, Poses, Other,
        Shared,   // a patch the pack's other patches rely on (the pawn being dressed): on while any of its parts is
    }

    // The player's choice, per content pack and part, of the pack's content or M3AT's own (ContentWindow.cs shows it), and
    // the defaults when none is made. The packs side by side (Nov 2026, the packclothes and addoncost autotests): a pack is
    // better at what M3AT has only a generic stand-in for (Miho's face, hair and fox ears; Wolfein's ears, tail and skin
    // tint; the shapes of dresses, veils and stockings), M3AT at faces it fits from Facial Animation (Wolfein's), armour and
    // helmets, and clothing colours. So by default the pack's, but M3AT's face where Facial Animation has faces for the race,
    // and M3AT's clothing for armour and helmets (ArmourByDefault).
    //
    // Enforced for a pack that patches M3AT's methods by taking those patches off (AddOns.Gate, by PartOf's table of our own
    // methods), for one that registers content through ExpansionApi 2 by not asking it, and for garments by GarmentOf.
    internal static class ContentSources
    {
        private const string PackWord = "pack", M3atWord = "m3at";

        // Our methods by the part a patch on them changes. A method renamed or added for a part goes in here in the same
        // change. FaceLayers' methods are all the face's.
        private static readonly Dictionary<string, ContentPart> PartByMethod = new Dictionary<string, ContentPart>
        {
            { "EarFileOf", ContentPart.EarsTail }, { "RaceEars", ContentPart.EarsTail }, { "HasCatTail", ContentPart.EarsTail },
            { "StepTail", ContentPart.EarsTail }, { "DrawCatTail", ContentPart.EarsTail }, { "TailOf", ContentPart.EarsTail },
            { "LoadTail", ContentPart.EarsTail }, { "DrawGeneEars", ContentPart.EarsTail },
            { "HairOf", ContentPart.Hair }, { "HairUnderHats", ContentPart.Hair }, { "DrawHair", ContentPart.Hair },
            { "HideUnder", ContentPart.Hair }, { "HideFur", ContentPart.Hair }, { "DrawBody", ContentPart.Hair },
            { "WornHair", ContentPart.HeadwearFit }, { "HidesEars", ContentPart.HeadwearFit }, { "RimOf", ContentPart.HeadwearFit },
            { "TuckedUnder", ContentPart.HeadwearFit },
            { "GeneKeysOf", ContentPart.BodyShape },
            { "SkinOf", ContentPart.SkinColour },
            // Read as the chibi loads (garments added, the body cut finer): taking them off later changes nothing.
            { "DrawDressed", ContentPart.Shared }, { "ReadMesh", ContentPart.Shared }, { "Load", ContentPart.Shared },
        };

        internal static readonly ContentPart[] Shown =
        {
            ContentPart.EarsTail, ContentPart.Face, ContentPart.Hair, ContentPart.HeadwearFit, ContentPart.BodyShape,
            ContentPart.SkinColour, ContentPart.Clothing, ContentPart.GeneParts, ContentPart.Poses, ContentPart.Other,
        };

        internal static ContentPart PartOf(MethodBase target)
        {
            if (target == null || target.DeclaringType == null)
            {
                return ContentPart.Other;
            }
            for (Type t = target.DeclaringType; t != null; t = t.DeclaringType)
            {
                if (t == typeof(FaceLayers))
                {
                    return ContentPart.Face;
                }
            }
            ContentPart part;
            return PartByMethod.TryGetValue(target.Name, out part) ? part : ContentPart.Other;
        }

        // ---- the choice

        private static readonly Dictionary<string, bool[]> answers = new Dictionary<string, bool[]>(StringComparer.OrdinalIgnoreCase);
        private static int answersVersion = -1;

        // True if this pack draws this part (its choice, else the default).
        internal static bool UsesPack(string packageId, ContentPart part)
        {
            if (packageId == null)
            {
                return false;
            }
            if (answersVersion != AddOns.Version)
            {
                answers.Clear();
                answersVersion = AddOns.Version;
            }
            bool[] parts;
            if (!answers.TryGetValue(packageId, out parts))
            {
                parts = new bool[Enum.GetValues(typeof(ContentPart)).Length];
                foreach (ContentPart p in Shown)
                {
                    bool? chosen = Chosen(packageId, p);
                    parts[(int)p] = chosen ?? Default(packageId, p);
                }
                answers[packageId] = parts;
            }
            return parts[(int)part];
        }

        internal static bool UsesPackForAny(string packageId)
        {
            foreach (ContentPart p in Shown)
            {
                if (UsesPack(packageId, p))
                {
                    return true;
                }
            }
            return false;
        }

        // The player's choice, null for none (the default).
        internal static bool? Chosen(string packageId, ContentPart part)
        {
            Settings s = SettingsMod.Settings;
            string v;
            if (s == null || s.contentChoices == null || !s.contentChoices.TryGetValue(Key(packageId, part), out v))
            {
                return null;
            }
            return v == PackWord ? true : v == M3atWord ? false : (bool?)null;
        }

        // Sets the choice (null: back to the default), saves it, and has it take effect at once.
        internal static void Set(string packageId, ContentPart part, bool? pack)
        {
            Settings s = SettingsMod.Settings;
            if (s == null)
            {
                return;
            }
            string key = Key(packageId, part);
            if (pack == null || pack.Value == Default(packageId, part))
            {
                s.contentChoices.Remove(key);
            }
            else
            {
                s.contentChoices[key] = pack.Value ? PackWord : M3atWord;
            }
            s.Write();
            Apply();
        }

        internal static void Reset(string packageId)
        {
            Settings s = SettingsMod.Settings;
            if (s == null)
            {
                return;
            }
            foreach (string key in s.contentChoices.Keys.Where(k => k.StartsWith(packageId + "|", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                s.contentChoices.Remove(key);
            }
            s.Write();
            Apply();
        }

        private static string Key(string packageId, ContentPart part)
        {
            return packageId.ToLowerInvariant() + "|" + part;
        }

        // The default: the pack's, but M3AT's face where Facial Animation has faces for the pack's races (M3AT fits those).
        internal static bool Default(string packageId, ContentPart part)
        {
            if (part == ContentPart.Face)
            {
                ModContentPack mod = AddOns.ModById(packageId);
                List<string> races = mod != null ? RacesOf(mod) : new List<string>();
                return races.Count == 0 || !races.Any(FacialAnimationHas);
            }
            return true;
        }

        // Takes the choices into effect: the gate on the packs' patches, then what is cached from them.
        internal static void Apply()
        {
            answers.Clear();
            AddOns.Gate();
            packs = null;
        }

        // ---- the packs found

        private static List<AddOns.AddOn> packs;
        private static int packsVersion = -1;

        // Every content pack active, with the parts it changes; empty until the chibi has loaded.
        internal static List<AddOns.AddOn> Packs()
        {
            if (packs == null || packsVersion != AddOns.Version)
            {
                packs = AddOns.Find().Where(a => a.parts.Count > 0).ToList();
                packsVersion = AddOns.Version;
            }
            return packs;
        }

        // The first time the chibi loads: the gate set to the choices, and any pack not seen before named once.
        internal static void Started()
        {
            Apply();
            Settings s = SettingsMod.Settings;
            if (s == null)
            {
                return;
            }
            List<string> fresh = new List<string>();
            foreach (AddOns.AddOn a in Packs())
            {
                if (!s.contentSeen.Contains(a.packageId.ToLowerInvariant()))
                {
                    s.contentSeen.Add(a.packageId.ToLowerInvariant());
                    fresh.Add(a.name);
                }
            }
            if (fresh.Count > 0)
            {
                s.Write();
                string names = string.Join(", ", fresh);
                Log.Message(WalkFigures.Tag + "content packs in use: " + names + ". Choose the pack's or M3AT's content per part in the Content tab of M3AT's options, the M3AT button or M3AT look on a pawn.");
                Messages.Message("M3AT_ContentNew".Translate(names), MessageTypeDefOf.NeutralEvent, false);
            }
        }

        // ---- races

        // The races a mod's IsoRim alien packs name (its own defs, or defs a patch added naming it as their modPackageId,
        // which may be its id before a rename: the Miho pack's names saiha.IsoRimWaylith.MihoPack, its id has .LoadTimeArt),
        // and the races it registered content for (ExpansionApi).
        internal static List<string> RacesOf(ModContentPack mod)
        {
            List<string> found = new List<string>();
            foreach (IsoRim.AlienModelDef d in DefDatabase<IsoRim.AlienModelDef>.AllDefs)
            {
                bool its = d.modContentPack == mod
                    || (!string.IsNullOrEmpty(d.modPackageId) && mod.PackageId.StartsWith(d.modPackageId, StringComparison.OrdinalIgnoreCase));
                if (!its)
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(d.alien) && !found.Contains(d.alien))
                {
                    found.Add(d.alien);
                }
                if (d.aliens != null)
                {
                    found.AddRange(d.aliens.Where(r => !found.Contains(r)));
                }
            }
            found.AddRange(ExpansionApi.RacesOf(mod.PackageId).Where(r => !found.Contains(r)));
            return found;
        }

        private static Dictionary<string, bool> facialRaces;

        // Whether Facial Animation (or a face mod for it) has faces made for this race: its eye or head types naming it.
        internal static bool FacialAnimationHas(string race)
        {
            if (facialRaces == null)
            {
                facialRaces = new Dictionary<string, bool>();
                foreach (string typeName in new[] { "FacialAnimation.EyeTypeDef", "FacialAnimation.HeadTypeDef" })
                {
                    Type t = GenTypes.GetTypeInAnyAssembly(typeName);
                    FieldInfo raceName = t != null ? t.GetField("raceName", BindingFlags.Public | BindingFlags.Instance) : null;
                    if (raceName == null)
                    {
                        continue;
                    }
                    foreach (Def d in GenDefDatabase.GetAllDefsInDatabaseForDef(t))
                    {
                        if (raceName.GetValue(d) is string r && !string.IsNullOrEmpty(r))
                        {
                            facialRaces[r] = true;
                        }
                    }
                }
            }
            return facialRaces.ContainsKey(race);
        }

        // ---- clothing, item by item

        // Named armour or a helmet, or in the game's armour categories. Not by armour rating: the Miho pack's dresses are
        // as well armoured as a flak vest, and were drawn as M3AT's shirt and trousers.
        private static readonly Regex ArmourWords = new Regex(@"\b(helm(et)?s?|armou?rs?)\b", RegexOptions.IgnoreCase);
        private static readonly Dictionary<ThingDef, bool> armour = new Dictionary<ThingDef, bool>();

        // Armour and helmets, whose garments M3AT's were plainly better at than the packs' (their helmets a blob over the
        // face, the arms bare under power armour): M3AT's by default even from a pack drawing the rest of its clothing.
        internal static bool ArmourByDefault(ThingDef def)
        {
            bool found;
            if (def == null || def.apparel == null)
            {
                return false;
            }
            if (!armour.TryGetValue(def, out found))
            {
                ThingCategoryDef body = DefDatabase<ThingCategoryDef>.GetNamedSilentFail("ApparelArmor");
                ThingCategoryDef head = DefDatabase<ThingCategoryDef>.GetNamedSilentFail("ArmorHeadgear");
                found = (body != null && def.IsWithinCategory(body)) || (head != null && def.IsWithinCategory(head))
                    || ArmourWords.IsMatch(def.label ?? "") || ArmourWords.IsMatch(GenText.SplitCamelCase(def.defName).Replace("_", " "));
                armour[def] = found;
            }
            return found;
        }
    }
}
