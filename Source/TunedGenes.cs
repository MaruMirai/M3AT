using System.Collections.Generic;
using System.Linq;
using Verse;

namespace M3AT
{
    // Other mods' ear and tail genes drawn as one of M3AT's own gene parts, picked by hand from their pictures: each is
    // taken as the vanilla gene whose part it is drawn as (EarFileOf, HasCatTail, the furry tail), so it moves, hides and
    // colours as that one does. Shown in the content switcher as "Mapped" (RaceSupport.cs). A gene not here
    // and not vanilla is not drawn (its pictures are not built, as a race's add-ons are).
    public static partial class WalkFigures
    {
        private static readonly Dictionary<string, string> TunedGeneParts = new Dictionary<string, string>
        {
            // Cane Aternitigno (War Dogs, Resurrectionem.CaneBellum): its short, long and curly (Maltese) dog ears all hang
            // at the sides of the head as the floppy ears do (the short ones shorter and closer, the Maltese fluffier); its
            // dog tail is long with a fur fringe, as the furry tail is.
            { "CGEars_FloppyShort", "Ears_Floppy" },
            { "CGEars_FloppyLong", "Ears_Floppy" },
            { "CGEars_MalteseEars", "Ears_Floppy" },
            { "CGTail_Dog", "Tail_Furry" },
        };

        private static Dictionary<string, List<GeneDef>> tunedBy;   // a vanilla gene's defName to the loaded genes drawn as it

        // The loaded genes drawn as the vanilla gene named, none if no mod with one is loaded.
        private static List<GeneDef> TunedAs(string vanilla)
        {
            if (tunedBy == null)
            {
                tunedBy = new Dictionary<string, List<GeneDef>>();
                foreach (KeyValuePair<string, string> kv in TunedGeneParts)
                {
                    GeneDef d = DefDatabase<GeneDef>.GetNamedSilentFail(kv.Key);
                    if (d == null)
                    {
                        continue;
                    }
                    if (!tunedBy.TryGetValue(kv.Value, out List<GeneDef> list))
                    {
                        tunedBy[kv.Value] = list = new List<GeneDef>();
                    }
                    list.Add(d);
                }
            }
            return tunedBy.TryGetValue(vanilla, out List<GeneDef> found) ? found : NoGenes;
        }

        private static readonly List<GeneDef> NoGenes = new List<GeneDef>();
        private static string[] earGenesTuned, tailGenesTuned;   // EarGenes and TailGenes WithTuned, made on first use

        // Whether the pawn has the vanilla gene (may be null, its expansion off) or a gene drawn as it.
        private static bool HasGeneOrTuned(Pawn pawn, GeneDef vanilla, string vanillaName)
        {
            if (pawn.genes == null)
            {
                return false;
            }
            if (vanilla != null && pawn.genes.HasActiveGene(vanilla))
            {
                return true;
            }
            List<GeneDef> tuned = TunedAs(vanillaName);
            for (int i = 0; i < tuned.Count; i++)
            {
                if (pawn.genes.HasActiveGene(tuned[i]))
                {
                    return true;
                }
            }
            return false;
        }

        // The gene lists GenePartCovers takes (EarGenes, TailGenes) with the tuned genes drawn as theirs, so a content
        // pack's part for a tuned gene is drawn in place of ours as for the vanilla gene.
        internal static string[] WithTuned(string[] vanilla)
        {
            return vanilla.Concat(TunedGeneParts.Where(kv => vanilla.Contains(kv.Value)).Select(kv => kv.Key)).ToArray();
        }

        // The loaded genes of a mod that M3AT draws as its own parts, with the vanilla gene each is drawn as (for the
        // content switcher's notes).
        internal static List<KeyValuePair<GeneDef, string>> TunedGenesOf(ModContentPack mod)
        {
            List<KeyValuePair<GeneDef, string>> found = new List<KeyValuePair<GeneDef, string>>();
            foreach (KeyValuePair<string, string> kv in TunedGeneParts)
            {
                GeneDef d = DefDatabase<GeneDef>.GetNamedSilentFail(kv.Key);
                if (d != null && d.modContentPack == mod)
                {
                    found.Add(new KeyValuePair<GeneDef, string>(d, kv.Value));
                }
            }
            return found;
        }

        // Every loaded mod with a gene M3AT draws as its own part.
        internal static IEnumerable<ModContentPack> ModsWithTunedGenes()
        {
            return TunedGeneParts.Keys.Select(k => DefDatabase<GeneDef>.GetNamedSilentFail(k)).Where(d => d != null && d.modContentPack != null)
                .Select(d => d.modContentPack).Distinct();
        }
    }
}
