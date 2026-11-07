using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Xml;
using HarmonyLib;
using IsoRim;
using Verse;

namespace M3AT
{
    // The RetexturedWeaponModels option, second half (the first is ArtPostfix). A mod can take a vanilla weapon's model away
    // altogether: the IsoRim Waylith Combat Extended pack removes IsoRim's ModelDef for every vanilla gun and grenade CE
    // redraws (its Patches/CE_RetexturedWeapons.xml), so the weapon is bound only by its category, which WeaponModels.Uses
    // never shows, and the art check never comes into it. So just before IsoRim binds its models (ModelLibrary.LoadAll, from
    // its own startup class, which runs before ours: hence patched from the mod's constructor, SettingsMod), each ModelDef
    // in IsoRim's own Defs that names one of the game's own weapons (Core and the DLCs) is put back if no ModelDef names
    // that weapon any more. Always, whatever the option says, so it still takes at once: ArtPostfix draws a weapon put back
    // here as its sprite while the option is off, as the mod that removed it meant.
    // A class of its own, not WalkFigures: it runs from the mod's constructor, long before WalkFigures' static constructor may.
    public static class RestoredModels
    {
        private const string Tag = "[M3AT] ";

        // The weapons whose ModelDef was put back (ArtPostfix).
        private static readonly HashSet<ThingDef> restored = new HashSet<ThingDef>();

        public static bool Has(ThingDef def)
        {
            return restored.Contains(def);
        }

        // From SettingsMod's constructor: IsoRim's assembly is loaded by then, its defs not yet.
        internal static void Patch()
        {
            try
            {
                MethodInfo loadAll = AccessTools.Method(typeof(ModelLibrary), nameof(ModelLibrary.LoadAll));
                if (loadAll == null)
                {
                    Log.Warning(Tag + "IsoRim has no ModelLibrary.LoadAll: weapon models another mod removed stay removed.");
                    return;
                }
                new Harmony("MaruMirai.M3AT.RestoreModels").Patch(loadAll,
                    prefix: new HarmonyMethod(typeof(RestoredModels), nameof(RestoreModelsPrefix)));
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "weapon models another mod removed stay removed: " + e.Message);
            }
        }

        public static void RestoreModelsPrefix()
        {
            try
            {
                RestoreModels();
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "putting back removed weapon models failed: " + e.Message);
            }
        }

        private static void RestoreModels()
        {
            ModContentPack isoRim = ModOf(typeof(ModelDef).Assembly);
            if (isoRim == null)
            {
                return;
            }
            // The weapons some ModelDef still names (a style def only restyles what another binds).
            HashSet<string> named = new HashSet<string>();
            foreach (ModelDef md in DefDatabase<ModelDef>.AllDefsListForReading)
            {
                if (!md.thingDef.NullOrEmpty())
                {
                    named.Add(md.thingDef);
                }
            }
            string defs = Path.Combine(isoRim.RootDir, "Defs");
            if (!Directory.Exists(defs))
            {
                return;
            }
            List<string> put = new List<string>();
            foreach (string file in Directory.GetFiles(defs, "*.xml", SearchOption.AllDirectories))
            {
                XmlDocument doc = new XmlDocument();
                doc.Load(file);
                if (doc.DocumentElement == null)
                {
                    continue;
                }
                foreach (XmlNode node in doc.DocumentElement.ChildNodes)
                {
                    if (node.NodeType != XmlNodeType.Element || node.Name != "IsoRim.ModelDef" || node["style"] != null)
                    {
                        continue;
                    }
                    XmlNode thingNode = node["thingDef"], nameNode = node["defName"];
                    string thingName = thingNode != null ? thingNode.InnerText.Trim() : null;
                    if (thingName.NullOrEmpty() || nameNode == null || named.Contains(thingName))
                    {
                        continue;
                    }
                    ThingDef thing = DefDatabase<ThingDef>.GetNamedSilentFail(thingName);
                    if (thing == null || !thing.IsWeapon || thing.modContentPack == null || !thing.modContentPack.IsOfficialMod
                        || DefDatabase<ModelDef>.GetNamedSilentFail(nameNode.InnerText.Trim()) != null)
                    {
                        continue;
                    }
                    ModelDef md = DirectXmlToObject.ObjectFromXml<ModelDef>(node, false);
                    md.modContentPack = isoRim;   // its model's path is inside IsoRim's folder
                    md.ResolveReferences();
                    md.PostLoad();
                    DefDatabase<ModelDef>.Add(md);
                    named.Add(thingName);
                    restored.Add(thing);
                    put.Add(thingName);
                }
            }
            if (put.Count > 0)
            {
                Log.Message(Tag + "put back IsoRim's models another mod removed for " + put.Count + " of the game's weapons ("
                    + string.Join(", ", put) + "); the \"Override vanilla weapon retextures\" option decides whether they show.");
            }
        }

        private static ModContentPack ModOf(Assembly assembly)
        {
            foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
            {
                if (mod.assemblies != null && mod.assemblies.loadedAssemblies.Contains(assembly))
                {
                    return mod;
                }
            }
            return null;
        }
    }
}
