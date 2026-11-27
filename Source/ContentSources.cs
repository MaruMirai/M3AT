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
    // a race's face, hair, how hats fit, body shape, skin colour, its clothing, and its gene parts: the race's ears and
    // tail and the parts drawn for genes, one switch. M3AT's side is its own models where it has them and its stand-ins
    // built from the race's pictures elsewhere; the pack's side is the pack's (its ears and tail patches or API files, its
    // gene part garments). Until Nov 2026 the ears and tail were a switch of their own (EarsTail, read as GeneParts now).
    internal enum ContentPart
    {
        Face, Hair, HeadwearFit, BodyShape, SkinColour, Clothing, Armour, GeneParts, Poses, Other,
        Shared,   // a patch the pack's other patches rely on (the pawn being dressed): on while any of its parts is
    }

    // The player's choice, per content pack and part, of the pack's content or M3AT's own (ContentWindow.cs shows it), and
    // the defaults when none is made: the pack's for every part (Default). The packs side by side (Nov 2026, the packclothes
    // and addoncost autotests): a pack is better at what M3AT has only a generic stand-in for (Miho's face, hair and fox
    // ears; the shapes of dresses, veils and stockings), M3AT at faces it fits from Facial Animation (Wolfein's), armour and
    // helmets, clothing colours, and the races it has its own ears and tails for (Wolfein's); those are a click away.
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
            { "EarFileOf", ContentPart.GeneParts }, { "RaceEars", ContentPart.GeneParts }, { "HasCatTail", ContentPart.GeneParts },
            { "StepTail", ContentPart.GeneParts }, { "DrawCatTail", ContentPart.GeneParts }, { "TailOf", ContentPart.GeneParts },
            { "LoadTail", ContentPart.GeneParts }, { "DrawGeneEars", ContentPart.GeneParts },
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
            ContentPart.Clothing, ContentPart.Armour, ContentPart.GeneParts, ContentPart.Hair, ContentPart.Face,
            ContentPart.SkinColour, ContentPart.HeadwearFit, ContentPart.BodyShape, ContentPart.Poses, ContentPart.Other,
        };

        // A patch on reading the chibi (ReadMesh, Load) whose code's class is named for the body changes its mesh or shape
        // (the Miho pack's MihoBustMesh cuts the chest of the body and every garment finer, MihoBustShape adds its key):
        // that is the body shape's. Any other there is shared (adding garments, as MihoGarments does).
        private static readonly Regex BodyPatchClass = new Regex("body|bust|shape", RegexOptions.IgnoreCase);

        internal static ContentPart PartOf(MethodBase target, MethodInfo patch)
        {
            ContentPart part = PartOf(target);
            if (part == ContentPart.Shared && patch != null && target.DeclaringType == typeof(SkinnedFigure)
                && (target.Name == "ReadMesh" || target.Name == "Load") && BodyPatchClass.IsMatch(patch.DeclaringType.Name))
            {
                return ContentPart.BodyShape;
            }
            return part;
        }

        // Whether this is a patch that changes the body's mesh as it is read (paid by every pawn of every race).
        internal static bool ChangesBodyMesh(MethodBase target, MethodInfo patch)
        {
            return target.DeclaringType == typeof(SkinnedFigure) && target.Name == "ReadMesh" && PartOf(target, patch) == ContentPart.BodyShape;
        }

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

        private static bool migrated;

        // Choices saved for the old ears and tail switch (key part EarsTail) become the gene parts' choice, unless that
        // pack has one for gene parts already.
        private static void Migrate(Settings s)
        {
            if (migrated || s == null || s.contentChoices == null)
            {
                return;
            }
            migrated = true;
            const string old = "|EarsTail";
            List<string> keys = s.contentChoices.Keys.Where(k => k.EndsWith(old, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (string key in keys)
            {
                string now = key.Substring(0, key.Length - old.Length) + "|" + ContentPart.GeneParts;
                if (!s.contentChoices.ContainsKey(now))
                {
                    s.contentChoices[now] = s.contentChoices[key];
                }
                s.contentChoices.Remove(key);
            }
            if (keys.Count > 0)
            {
                s.Write();
            }
        }

        // The player's choice, null for none (the default).
        internal static bool? Chosen(string packageId, ContentPart part)
        {
            Settings s = SettingsMod.Settings;
            Migrate(s);
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
            NoteBody(packageId, part, pack);
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

        // Sets several parts at once (each null: back to its default), saved and applied once, for the switcher's
        // whole-pack columns and its swap.
        internal static void SetMany(string packageId, IEnumerable<KeyValuePair<ContentPart, bool?>> choices)
        {
            Settings s = SettingsMod.Settings;
            if (s == null)
            {
                return;
            }
            foreach (KeyValuePair<ContentPart, bool?> c in choices)
            {
                NoteBody(packageId, c.Key, c.Value);
                string key = Key(packageId, c.Key);
                if (c.Value == null || c.Value.Value == Default(packageId, c.Key))
                {
                    s.contentChoices.Remove(key);
                }
                else
                {
                    s.contentChoices[key] = c.Value.Value ? PackWord : M3atWord;
                }
            }
            s.Write();
            Apply();
        }

        private static string Key(string packageId, ContentPart part)
        {
            return packageId.ToLowerInvariant() + "|" + part;
        }

        // The default: the pack's, for every part (the player asked for packs to show as their makers meant, M3AT's a click
        // away). The one exception is the body: only one pack's body change may be on (OneBody), so of the packs that
        // change it only the first (DefaultBody) is on by default.
        internal static bool Default(string packageId, ContentPart part)
        {
            if (part == ContentPart.BodyShape && AddOns.ChangesBodyMesh(packageId))
            {
                return string.Equals(packageId, DefaultBody(), StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }

        // The choice, else the default.
        private static bool Effective(string packageId, ContentPart part)
        {
            return Chosen(packageId, part) ?? Default(packageId, part);
        }

        // The pack whose body change is on (OneBody leaves one at most), or null: M3AT's body. As chosen now, which may
        // differ from the body loaded until a restart.
        internal static AddOns.AddOn BodyPack()
        {
            return Packs().FirstOrDefault(a => AddOns.ChangesBodyMesh(a.packageId) && Effective(a.packageId, ContentPart.BodyShape));
        }

        private static string defaultBody;
        private static int defaultBodyVersion = -1;

        // The first content pack (as Packs lists them, OneBody's first too) whose patches change the body's mesh, or null.
        private static string DefaultBody()
        {
            if (defaultBodyVersion != AddOns.Version)
            {
                AddOns.AddOn first = Packs().FirstOrDefault(a => AddOns.ChangesBodyMesh(a.packageId));
                defaultBody = first != null ? first.packageId : null;
                defaultBodyVersion = AddOns.Version;
            }
            return defaultBody;
        }

        // Stores a body choice as a key only where it differs from the default.
        private static void StoreBody(Settings s, string packageId, bool pack)
        {
            string key = Key(packageId, ContentPart.BodyShape);
            if (pack == Default(packageId, ContentPart.BodyShape))
            {
                s.contentChoices.Remove(key);
            }
            else
            {
                s.contentChoices[key] = pack ? PackWord : M3atWord;
            }
        }

        // Takes the choices into effect: the gate on the packs' patches, then what is cached from them.
        internal static void Apply()
        {
            OneBody();
            answers.Clear();
            packRaces.Clear();
            AddOns.Gate();
            packs = null;
        }

        // The pack whose body change was chosen last (Set, SetMany), kept when another's has to go.
        private static string bodyChosen;

        private static void NoteBody(string packageId, ContentPart part, bool? pack)
        {
            if (part == ContentPart.BodyShape && pack == true && AddOns.ChangesBodyMesh(packageId))
            {
                bodyChosen = packageId;
            }
        }

        // Only one pack's body change at a time. Each patches M3AT's body (and every garment) as it is read, each on what
        // the one before left in Harmony's order: two that cut the mesh finer multiply its triangles, and two that disagree
        // can leave garments naming body triangles that aren't there, which fails the load and leaves M3AT's figures off
        // for the session. So choosing one pack's turns any other's back to M3AT's (the one chosen last kept, else the
        // first; also settings saved before this rule), with a message. Body changes take a restart either way.
        private static void OneBody()
        {
            Settings s = SettingsMod.Settings;
            if (s == null || s.contentChoices == null)
            {
                return;
            }
            List<AddOns.AddOn> on = Packs().Where(a => AddOns.ChangesBodyMesh(a.packageId) && Effective(a.packageId, ContentPart.BodyShape)).ToList();
            if (on.Count < 2)
            {
                return;
            }
            AddOns.AddOn keep = on.FirstOrDefault(a => string.Equals(a.packageId, bodyChosen, StringComparison.OrdinalIgnoreCase)) ?? on[0];
            List<string> off = new List<string>();
            foreach (AddOns.AddOn a in on)
            {
                if (a != keep)
                {
                    StoreBody(s, a.packageId, false);
                    off.Add(a.name);
                }
            }
            s.Write();
            string names = string.Join(", ", off);
            Log.Message(WalkFigures.Tag + "only one content pack may change the body: kept " + keep.name + "'s, turned off " + names + "'s.");
            Messages.Message("M3AT_BodyOneOnly".Translate(keep.name, names), MessageTypeDefOf.CautionInput, false);
        }

        // The one body for everyone (the switcher's BodyChoice): this pack's body change, or none (null: M3AT's), every
        // other pack's turned back to M3AT's, saved and applied once.
        internal static void SetBody(string packageId)
        {
            Settings s = SettingsMod.Settings;
            if (s == null)
            {
                return;
            }
            foreach (AddOns.AddOn a in Packs().Where(a => AddOns.ChangesBodyMesh(a.packageId)))
            {
                StoreBody(s, a.packageId, string.Equals(a.packageId, packageId, StringComparison.OrdinalIgnoreCase));
            }
            if (packageId != null)
            {
                bodyChosen = packageId;
            }
            s.Write();
            Apply();
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
            // Packs that made loading the figures slow named once a session, with how long each took.
            List<string> slow = new List<string>();
            double slowSum = 0.0;
            foreach (AddOns.AddOn a in Packs())
            {
                double t = SkinnedFigure.LoadSecondsOf(a.packageId);
                if (t >= 1.0)
                {
                    // Whether its garments were fitted this load (the first with it, or after it or M3AT changed) or taken
                    // from the fits kept on disk (FitCache.cs).
                    SkinnedFigure.FitsOf(a.packageId, out int made, out int cached);
                    string how = made > 0 && cached == 0 ? "M3AT_ContentLoadFresh".Translate(made)
                        : made > 0 ? "M3AT_ContentLoadSome".Translate(made, cached)
                        : cached > 0 ? "M3AT_ContentLoadCached".Translate(cached) : (TaggedString)"";
                    slow.Add(a.name + " " + t.ToString("0") + " s" + (how.NullOrEmpty() ? "" : " (" + how + ")"));
                    slowSum += t;
                }
            }
            if (slowSum >= SkinnedFigure.LoadWarnSeconds)
            {
                Messages.Message("M3AT_ContentSlowLoad".Translate(slowSum.ToString("0"), string.Join(", ", slow)), MessageTypeDefOf.CautionInput, false);
            }
            if (Packs().Count > 0)
            {
                Log.Message(WalkFigures.Tag + "content packs in use: " + string.Join(", ", Packs().Select(a => a.name))
                    + ". Choose the pack's or M3AT's content per part in the Content tab of M3AT's options, the M3AT button or M3AT look on a pawn.");
                if (s.contentNoticeShown)
                {
                    return;
                }
#if AUTOTEST
                if (!AutoTest.Wants("contentnotice"))
                {
                    return;   // it would cover the other scenarios' pictures; contentnotice shows it itself
                }
#endif
                QueueNotice();
            }
        }

        // ---- the content manager's notice

        // Once ever, the first time any content pack is loaded (Settings.contentNoticeShown): a message box saying the content manager is
        // there and changes nothing by itself. It only says the manager exists; it doesn't weigh M3AT's content against the
        // pack's, which is the player's call (Maru's wish, 2026-11-27). Held until NoticeDelay real seconds after the
        // figures loaded, with a map up and no other message box open (another mod's notice at the start, Wolfein's),
        // so it isn't lost under the first frames or stacked on another; it counts as shown only once it has shown.
        private const float NoticeDelay = 4f;
        private static bool noticeDue;
        private static float noticeAt;

        internal static void QueueNotice()
        {
            if (!noticeDue)
            {
                IsoRim.Api.BeforeViewUpdate += NoticeFrame;
            }
            noticeDue = true;
            noticeAt = UnityEngine.Time.realtimeSinceStartup + NoticeDelay;
        }

        private static void NoticeFrame()
        {
            if (!noticeDue || UnityEngine.Time.realtimeSinceStartup < noticeAt || Find.CurrentMap == null || Find.WindowStack == null
                || Find.WindowStack.Windows.Any(w => w is Dialog_MessageBox) || LongEventHandler.AnyEventNowOrWaiting)
            {
                return;
            }
            IsoRim.Api.BeforeViewUpdate -= NoticeFrame;
            noticeDue = false;
            Settings s = SettingsMod.Settings;
            if (s == null)
            {
                return;
            }
            s.contentNoticeShown = true;
            s.Write();
            if (Packs().Count == 0)
            {
                return;
            }
            MainButtonDef button = DefDatabase<MainButtonDef>.AllDefs.FirstOrDefault(d => d.tabWindowClass == typeof(ContentTabWindow));
            Dialog_MessageBox box = new Dialog_MessageBox("M3AT_NoticeText".Translate(),
                "M3AT_NoticeOpen".Translate(), () =>
                {
                    if (button != null && Find.MainTabsRoot != null)
                    {
                        Find.MainTabsRoot.SetCurrentTab(button, false);
                    }
                },
                "M3AT_NoticeClose".Translate(), null, "M3AT_NoticeTitle".Translate());
            Find.WindowStack.Add(box);
        }

        // ---- gene parts, race by race

        private static readonly Dictionary<string, bool> packRaces = new Dictionary<string, bool>();
        private static int packRacesVersion = -1;

        // True if a content pack the player has draw its gene parts names this pawn's race: then the pack's ears, tails,
        // horns and the like are the only ones drawn on it, and none of M3AT's own (its gene ears, tail and horns, its
        // models of a race's parts, the stand-ins built from the race's pictures), even for a part the pack has none of.
        // The packs' patches (EarFileOf, HasCatTail) fill in an empty result, so they still draw theirs. Races no such pack
        // names keep M3AT's, part by part; a pack's gene parts alone (no race) replace M3AT's gene by gene (GenePartCovers).
        internal static bool PackOwnsGeneParts(Pawn pawn)
        {
            if (pawn == null || pawn.def == null)
            {
                return false;
            }
            if (packRacesVersion != AddOns.Version)
            {
                packRaces.Clear();
                packRacesVersion = AddOns.Version;
            }
            bool owns;
            if (!packRaces.TryGetValue(pawn.def.defName, out owns))
            {
                string race = pawn.def.defName;
                owns = Packs().Any(a => a.parts.Contains(ContentPart.GeneParts) && a.races.Contains(race)
                    && !AddOns.IsOff(a.packageId) && UsesPack(a.packageId, ContentPart.GeneParts));
                packRaces[race] = owns;
            }
            return owns;
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
            return facialRaces.ContainsKey(race) || FaceLayers.BorrowsFaces(race);
        }

        // ---- clothing, item by item

        // Named armour or a helmet, or in the game's armour categories. Not by armour rating: the Miho pack's dresses are
        // as well armoured as a flak vest, and were drawn as M3AT's shirt and trousers.
        private static readonly Regex ArmourWords = new Regex(@"\b(helm(et)?s?|armou?rs?)\b", RegexOptions.IgnoreCase);
        private static readonly Dictionary<ThingDef, bool> armour = new Dictionary<ThingDef, bool>();

        // Armour and helmets, whose garments M3AT's were plainly better at than the packs' (their helmets a blob over the
        // face, the arms bare under power armour): a row of their own, so the player can have M3AT's for them alone.
        // Which part a pack's garment for this apparel belongs to: armour and helmets their own, the rest clothing.
        internal static ContentPart ClothingPartOf(ThingDef def)
        {
            return ArmourByDefault(def) ? ContentPart.Armour : ContentPart.Clothing;
        }

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
