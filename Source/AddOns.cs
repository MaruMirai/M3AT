using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace M3AT
{
    // Other mods that change how M3AT draws its chibis ("add-ons", "content packs" to the player: ContentSources.cs). What an
    // add-on is made of, by the mod it comes from:
    //   - garments and gene parts (Piece.owner: its M3ATgarments folder, or files it put on skin.txt's list);
    //   - Harmony patches on M3AT's own methods (the Miho and Wolfein packs reach M3AT's ears, tail, hair, face and hats
    //     that way), the mod found by the assembly the patch method is in, each patch's part by the method it patches
    //     (ContentSources.PartOf);
    //   - expansion posers (ExpansionApi.AddPoser), likewise by assembly.
    // One gate (Gate) decides which of their patches on M3AT's methods are on: a patch is taken off while its mod is off for
    // a benchmark turn (Benchmark.cs, SetOff), or while the player has M3AT draw its part (ContentSources.UsesPack); the
    // shared ones (the pawn being dressed, read by the rest) stay while any part of that mod is the pack's. Taken off, a
    // patch is put back as it was (owner, priority, before, after). Patches on anything else (the game, IsoRim) are never
    // touched. A mod off for a benchmark turn also has its garments counted as missing (SkinnedFigure.OffAddOn) and its
    // posers not asked.
    internal static class AddOns
    {
        internal sealed class AddOn
        {
            public string packageId, name;
            public int garments, geneParts, patches, posers;
            public readonly HashSet<ContentPart> parts = new HashSet<ContentPart>();
            public readonly List<string> races = new List<string>();   // the races its IsoRim alien pack names, if any
            public Assembly[] assemblies = new Assembly[0];
        }

        // A patch taken off, to put back as it was.
        private sealed class Taken
        {
            public MethodBase original;
            public HarmonyPatchType kind;
            public Patch patch;
        }

        private static HashSet<string> off = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<Taken> taken = new List<Taken>();
        private static HashSet<Assembly> offAssemblies = new HashSet<Assembly>();
        private static readonly Dictionary<Assembly, ModContentPack> modOfAssembly = new Dictionary<Assembly, ModContentPack>();

        // Bumped each time what is on changes (a benchmark turn, a player's choice), so cached answers (worn garments,
        // stand-ins, the content packs found) are worked out again.
        internal static int Version;

        internal static bool AnyOff
        {
            get { return off.Count > 0; }
        }

        internal static bool IsOff(string packageId)
        {
            return packageId != null && off.Count > 0 && off.Contains(packageId);
        }

        internal static bool IsOff(Assembly assembly)
        {
            return assembly != null && offAssemblies.Count > 0 && offAssemblies.Contains(assembly);
        }

        // Every add-on active now, by what it adds; none if M3AT isn't loaded yet. Patches taken off by the gate count too.
        internal static List<AddOn> Find()
        {
            Dictionary<string, AddOn> byId = new Dictionary<string, AddOn>(StringComparer.OrdinalIgnoreCase);
            Func<ModContentPack, AddOn> of = mod =>
            {
                AddOn a;
                if (!byId.TryGetValue(mod.PackageId, out a))
                {
                    a = new AddOn
                    {
                        packageId = mod.PackageId,
                        name = mod.Name,
                        assemblies = mod.assemblies != null ? mod.assemblies.loadedAssemblies.ToArray() : new Assembly[0],
                    };
                    a.races.AddRange(ContentSources.RacesOf(mod));
                    byId[mod.PackageId] = a;
                }
                return a;
            };
            SkinnedFigure skin = WalkFigures.SkinForAddOns;
            if (skin != null)
            {
                foreach (KeyValuePair<string, ContentPart> owner in skin.GarmentOwners())
                {
                    ModContentPack mod = ModById(owner.Key);
                    if (mod != null)
                    {
                        AddOn a = of(mod);
                        if (owner.Value == ContentPart.GeneParts)
                        {
                            a.geneParts++;
                        }
                        else
                        {
                            a.garments++;
                        }
                        a.parts.Add(owner.Value);   // GeneParts, Armour (armour and helmets) or Clothing
                    }
                }
            }
            foreach (KeyValuePair<MethodBase, Patch> p in PatchesOnOurs().Concat(taken.Select(t => new KeyValuePair<MethodBase, Patch>(t.original, t.patch))))
            {
                ModContentPack mod = ModOf(p.Value.PatchMethod.DeclaringType.Assembly);
                if (mod != null && !Own(mod))
                {
                    AddOn a = of(mod);
                    a.patches++;
                    ContentPart part = ContentSources.PartOf(p.Key, p.Value.PatchMethod);
                    if (part != ContentPart.Shared)
                    {
                        a.parts.Add(part);
                    }
                }
            }
            foreach (Delegate poser in ExpansionApi.Posers)
            {
                ModContentPack mod = ModOf(poser.Method.DeclaringType.Assembly);
                if (mod != null && !Own(mod))
                {
                    AddOn a = of(mod);
                    a.posers++;
                    a.parts.Add(ContentPart.Poses);
                }
            }
            foreach (string id in ExpansionApi.PackIds)
            {
                ModContentPack mod = ModById(id);
                if (mod != null)
                {
                    of(mod).parts.UnionWith(ExpansionApi.PartsOf(id));
                }
            }
            return byId.Values.OrderBy(a => a.name).ToList();
        }

        // The M3AT methods a mod patches (those taken off by the gate too), as "Type.Method", for its pack report.
        internal static List<KeyValuePair<MethodBase, string>> PatchedBy(string packageId)
        {
            List<KeyValuePair<MethodBase, string>> found = new List<KeyValuePair<MethodBase, string>>();
            foreach (KeyValuePair<MethodBase, Patch> p in PatchesOnOurs().Concat(taken.Select(t => new KeyValuePair<MethodBase, Patch>(t.original, t.patch))))
            {
                ModContentPack mod = ModOf(p.Value.PatchMethod.DeclaringType.Assembly);
                if (mod != null && string.Equals(mod.PackageId, packageId, StringComparison.OrdinalIgnoreCase)
                    && !found.Any(f => f.Key == p.Key))
                {
                    found.Add(new KeyValuePair<MethodBase, string>(p.Key, p.Key.DeclaringType.Name + "." + p.Key.Name));
                }
            }
            return found;
        }

        private static readonly Dictionary<string, bool> bodyMesh = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // Whether a mod has a patch that changes the body's mesh as it is read (ContentSources.ChangesBodyMesh), on or taken off.
        internal static bool ChangesBodyMesh(string packageId)
        {
            bool found;
            if (bodyMesh.TryGetValue(packageId, out found))
            {
                return found;
            }
            found = false;
            foreach (KeyValuePair<MethodBase, Patch> p in PatchesOnOurs().Concat(taken.Select(t => new KeyValuePair<MethodBase, Patch>(t.original, t.patch))))
            {
                ModContentPack mod = ModOf(p.Value.PatchMethod.DeclaringType.Assembly);
                if (mod != null && string.Equals(mod.PackageId, packageId, StringComparison.OrdinalIgnoreCase) && ContentSources.ChangesBodyMesh(p.Key, p.Value.PatchMethod))
                {
                    found = true;
                    break;
                }
            }
            bodyMesh[packageId] = found;
            return found;
        }

        // Turns these add-ons wholly off for a benchmark turn (none: all back on, to the player's choices).
        internal static void SetOff(ICollection<string> ids)
        {
            HashSet<string> wanted = new HashSet<string>(ids ?? new string[0], StringComparer.OrdinalIgnoreCase);
            if (wanted.SetEquals(off))
            {
                return;
            }
            off = wanted;
            offAssemblies = new HashSet<Assembly>();
            foreach (string id in off)
            {
                ModContentPack mod = ModById(id);
                if (mod != null && mod.assemblies != null)
                {
                    offAssemblies.UnionWith(mod.assemblies.loadedAssemblies);
                }
            }
            Gate();
        }

        // Takes off every patch on M3AT's methods that shouldn't be on now and puts back every one that should, changing
        // nothing if that is what is on already; then has what follows from it worked out again.
        internal static void Gate()
        {
            List<Taken> want = new List<Taken>();
            foreach (KeyValuePair<MethodBase, Patch> p in PatchesOnOurs().Concat(taken.Select(t => new KeyValuePair<MethodBase, Patch>(t.original, t.patch))))
            {
                if (ShouldBeOff(p.Key, p.Value))
                {
                    want.Add(new Taken { original = p.Key, patch = p.Value });
                }
            }
            bool same = want.Count == taken.Count && want.All(w => taken.Any(t => t.original == w.original && t.patch.PatchMethod == w.patch.PatchMethod));
            if (!same)
            {
                PutBackPatches();
                TakePatches(want);
            }
            Version++;
            WalkFigures.AddOnsChanged();
        }

        private static bool ShouldBeOff(MethodBase original, Patch patch)
        {
            Assembly assembly = patch.PatchMethod.DeclaringType.Assembly;
            if (IsOff(assembly))
            {
                return true;
            }
            ModContentPack mod = ModOf(assembly);
            if (mod == null || Own(mod))
            {
                return false;
            }
            ContentPart part = ContentSources.PartOf(original, patch.PatchMethod);
            return part == ContentPart.Shared ? !ContentSources.UsesPackForAny(mod.PackageId) : !ContentSources.UsesPack(mod.PackageId, part);
        }

        // Each patch another mod has on one of M3AT's methods (those on now).
        private static IEnumerable<KeyValuePair<MethodBase, Patch>> PatchesOnOurs()
        {
            Assembly ours = typeof(WalkFigures).Assembly;
            List<KeyValuePair<MethodBase, Patch>> found = new List<KeyValuePair<MethodBase, Patch>>();
            foreach (MethodBase m in Harmony.GetAllPatchedMethods().ToList())
            {
                if (m.DeclaringType == null || m.DeclaringType.Assembly != ours)
                {
                    continue;
                }
                Patches info = Harmony.GetPatchInfo(m);
                if (info == null)
                {
                    continue;
                }
                foreach (Patch p in info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers))
                {
                    found.Add(new KeyValuePair<MethodBase, Patch>(m, p));
                }
            }
            return found;
        }

        private static void TakePatches(List<Taken> which)
        {
            foreach (IGrouping<MethodBase, Taken> onMethod in which.GroupBy(t => t.original))
            {
                MethodBase m = onMethod.Key;
                Patches info = Harmony.GetPatchInfo(m);
                if (info == null)
                {
                    continue;
                }
                // Each owner's patches on this method are taken off together (Unpatch goes by owner), so every one of that
                // owner's here is kept to be put back, and only those meant to be off stay off.
                HashSet<string> owners = new HashSet<string>(onMethod.Select(t => t.patch.owner));
                List<Taken> all = new List<Taken>();
                Collect(all, m, HarmonyPatchType.Prefix, info.Prefixes, owners);
                Collect(all, m, HarmonyPatchType.Postfix, info.Postfixes, owners);
                Collect(all, m, HarmonyPatchType.Transpiler, info.Transpilers, owners);
                Collect(all, m, HarmonyPatchType.Finalizer, info.Finalizers, owners);
                foreach (string owner in owners)
                {
                    try
                    {
                        new Harmony(owner).Unpatch(m, HarmonyPatchType.All, owner);
                    }
                    catch (Exception e)
                    {
                        Log.Warning(WalkFigures.Tag + "could not take " + owner + "'s patch off " + m.DeclaringType.Name + "." + m.Name + ": " + e.Message);
                    }
                }
                foreach (Taken t in all)
                {
                    if (onMethod.Any(w => w.patch.PatchMethod == t.patch.PatchMethod))
                    {
                        taken.Add(t);
                    }
                    else
                    {
                        PutBack(t);   // (the same owner's patch here that is meant to stay on)
                    }
                }
            }
        }

        private static void Collect(List<Taken> into, MethodBase m, HarmonyPatchType kind, IEnumerable<Patch> patches, HashSet<string> owners)
        {
            foreach (Patch p in patches)
            {
                if (owners.Contains(p.owner))
                {
                    into.Add(new Taken { original = m, kind = kind, patch = p });
                }
            }
        }

        private static void PutBackPatches()
        {
            foreach (Taken t in taken)
            {
                PutBack(t);
            }
            taken.Clear();
        }

        private static void PutBack(Taken t)
        {
            try
            {
                HarmonyMethod how = new HarmonyMethod(t.patch.PatchMethod, t.patch.priority, t.patch.before, t.patch.after, t.patch.debug);
                Harmony h = new Harmony(t.patch.owner);
                switch (t.kind)
                {
                    case HarmonyPatchType.Prefix: h.Patch(t.original, prefix: how); break;
                    case HarmonyPatchType.Postfix: h.Patch(t.original, postfix: how); break;
                    case HarmonyPatchType.Transpiler: h.Patch(t.original, transpiler: how); break;
                    case HarmonyPatchType.Finalizer: h.Patch(t.original, finalizer: how); break;
                }
            }
            catch (Exception e)
            {
                Log.Error(WalkFigures.Tag + "could not put " + t.patch.owner + "'s patch back on " + t.original.DeclaringType.Name + "." + t.original.Name
                    + ": " + e.Message + ". Restart the game to have it back.");
            }
        }

        internal static ModContentPack ModById(string id)
        {
            return id == null ? null : LoadedModManager.RunningModsListForReading.FirstOrDefault(m => string.Equals(m.PackageId, id, StringComparison.OrdinalIgnoreCase));
        }

        internal static ModContentPack ModOf(Assembly assembly)
        {
            ModContentPack found;
            if (assembly == null)
            {
                return null;
            }
            if (!modOfAssembly.TryGetValue(assembly, out found))
            {
                found = LoadedModManager.RunningModsListForReading.FirstOrDefault(mod => mod.assemblies != null && mod.assemblies.loadedAssemblies.Contains(assembly));
                modOfAssembly[assembly] = found;
            }
            return found;
        }

        private static bool Own(ModContentPack mod)
        {
            return mod.PackageId != null && mod.PackageId.StartsWith("marumirai.m3at", StringComparison.OrdinalIgnoreCase)
                || mod.PackageId == "brrainz.harmony";
        }
    }
}
