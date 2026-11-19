using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml;
using IsoRim;
using Verse;

namespace M3AT
{
    // The RetexturedWeaponModels option, second half (the first is WeaponRule). Older copies of the IsoRim Waylith Combat
    // Extended pack (its GitHub zip from before 2026-11-18, which players may still have in their Mods folder) remove
    // IsoRim's ModelDef for every vanilla gun and grenade CE redraws (Patches/CE_RetexturedWeapons.xml), leaving the weapon
    // bound only by its category, which IsoRim never shows. The pack on the Workshop no longer does, and then this does
    // nothing. Otherwise an IsoRim model rule (Api 42, no IsoRim internals) gives each of the game's own weapons (Core and
    // the DLCs) that no ModelDef names any more a copy of its ModelDef read from IsoRim's own Defs, as M3AT_Restored_<thing>.
    // Always, whatever the option says, so it still takes at once: WeaponRule draws a weapon given one here as its sprite
    // while the option is off, as the mod that removed it meant.
    // A class of its own, not WalkFigures: the rule is added from the mod's constructor, as IsoRim builds its model table
    // from its own startup class, before WalkFigures' static constructor may run.
    public static class RestoredModels
    {
        private const string Tag = "[M3AT] ";
        private const string Prefix = "M3AT_Restored_";

        // The weapons given a ModelDef here (WeaponRule).
        private static readonly HashSet<ThingDef> restored = new HashSet<ThingDef>();

        // Each official weapon's ModelDef node in IsoRim's Defs, by the weapon's defName; read on the rule's first call.
        private static Dictionary<string, XmlNode> nodes;
        private static ModContentPack isoRim;
        private static readonly List<string> put = new List<string>();

        public static bool Has(ThingDef def)
        {
            return restored.Contains(def);
        }

        // From SettingsMod's constructor: IsoRim's assembly is loaded by then, its defs not yet.
        internal static void Register()
        {
            try
            {
                if (Api.Version >= 42)
                {
                    AddRule();
                }
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "weapon models another mod removed stay removed: " + e.Message);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AddRule()
        {
            Api.AddModelRule(new Func<ThingDef, string>(Rule));
        }

        // Asked by IsoRim for every thing no ModelDef names by its thingDef, once, while it builds its model table. So a
        // weapon of the game's own asked here has had its ModelDef taken away.
        private static string Rule(ThingDef thing)
        {
            if (thing == null || !thing.IsWeapon || thing.modContentPack == null || !thing.modContentPack.IsOfficialMod)
            {
                return null;
            }
            if (nodes == null)
            {
                ReadIsoRimDefs();
            }
            XmlNode node;
            if (!nodes.TryGetValue(thing.defName, out node))
            {
                return null;
            }
            string name = Prefix + thing.defName;
            if (DefDatabase<ModelDef>.GetNamedSilentFail(name) == null)
            {
                ModelDef md = DirectXmlToObject.ObjectFromXml<ModelDef>(node, false);
                md.defName = name;
                md.thingDef = null;   // bound by this rule alone, so nothing else takes it for a thingDef entry
                md.modContentPack = isoRim;   // its model's path is inside IsoRim's folder
                md.ResolveReferences();
                md.PostLoad();
                DefDatabase<ModelDef>.Add(md);
                restored.Add(thing);
                put.Add(thing.defName);
                if (put.Count == 1)
                {
                    LongEventHandler.ExecuteWhenFinished(Report);
                }
            }
            return name;
        }

        private static void Report()
        {
            Log.Message(Tag + "put back IsoRim's models another mod removed for " + put.Count + " of the game's weapons ("
                + string.Join(", ", put) + "), as an older IsoRim Waylith CE pack does; the retextured weapons option decides whether they show.");
        }

        private static void ReadIsoRimDefs()
        {
            nodes = new Dictionary<string, XmlNode>();
            isoRim = ModOf(typeof(ModelDef).Assembly);
            string defs = isoRim != null ? Path.Combine(isoRim.RootDir, "Defs") : null;
            if (defs == null || !Directory.Exists(defs))
            {
                return;
            }
            foreach (string file in Directory.GetFiles(defs, "*.xml", SearchOption.AllDirectories))
            {
                XmlDocument doc = new XmlDocument();
                try
                {
                    doc.Load(file);
                }
                catch (Exception)
                {
                    continue;
                }
                if (doc.DocumentElement == null)
                {
                    continue;
                }
                foreach (XmlNode node in doc.DocumentElement.ChildNodes)
                {
                    // A style def only restyles what another binds.
                    if (node.NodeType != XmlNodeType.Element || node.Name != "IsoRim.ModelDef" || node["style"] != null)
                    {
                        continue;
                    }
                    XmlNode thingNode = node["thingDef"];
                    string thingName = thingNode != null ? thingNode.InnerText.Trim() : null;
                    if (!thingName.NullOrEmpty() && !nodes.ContainsKey(thingName))
                    {
                        nodes[thingName] = node;
                    }
                }
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
