using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using RimWorld;
using Verse;

namespace M3AT
{
    // Stand-ins: the garment(s) drawn for worn apparel with no garment of its own (another mod's, mostly). Each garment the
    // export has is described by the apparel it is drawn for (its defs, read from the game's own defs here, so no garment
    // needs tagging): its layers, body part groups, whether it is armour or noble wear, its tech level, stuff and name. An
    // apparel is matched against every garment, and against every two garments on different main groups drawn together
    // (a one-piece suit as a shirt and trousers, a full armour as a flak jacket and flak trousers), and the best is taken,
    // if good enough (StandInLeast).
    // anim_scripts/stand_ins.py is the same scoring over the defs on disk, to see what every apparel would get.
    // Change the two together.
    public sealed partial class SkinnedFigure
    {
        private sealed class Profile
        {
            public int piece;               // the garment (0: an apparel's own profile)
            public string name;             // an apparel's defName; a garment's own name, which Generic goes by
            public List<string> layers = new List<string>();
            public HashSet<string> groups = new HashSet<string>();
            public bool armour, noble, vacsuit, childOnly;
            public bool namedOnly;          // a garment of NoStandIn: stands in only for apparel with all its name words
            public TechLevel tech;
            public HashSet<string> stuff = new HashSet<string>();
            public string[] tokens;         // a garment's: the words of its def's name (NameTokens)
            public string text;             // an apparel's: its defName and label, lower case, for the tokens to be found in
        }

        private List<Profile> standInProfiles;   // every garment that can stand in, made on first use
        private readonly Dictionary<ThingDef, int[]> standInsOf = new Dictionary<ThingDef, int[]>();
        private static readonly int[] NoStandIns = new int[0];

        private const float StandInLeast = 0.35f;   // the least score drawn: below it the apparel is left undrawn
        private const float PairCost = 0.03f;       // taken off two garments drawn together, so one as good wins
        private const float OtherLayerStep = 0.6f;  // the score's share one body layer apart (OnSkin, Middle, Shell); squared two apart
        private const float OtherHeadLayer = 0.7f;  // and between two head layers (Overhead, CE's StrappedHead, EyeCover)
        private const float NotArmour = 0.75f;      // one armour and the other not
        private const float NotVacsuit = 0.3f;      // a vacsuit's garment for apparel that isn't one
        private const float BothVacsuit = 0.3f;     // added for a vacsuit's garment on another vacsuit
        private const float BothNoble = 0.05f;      // added for noble wear on noble wear
        private const float SameTech = 0.2f;        // added for the same tech level, less by half each level apart, so
                                                    // industrial armour takes the flak garments and spacer armour the power armour
        private const float TechStep = 2f;          // levels apart at which SameTech is gone
        private const float SameStuff = 0.03f;      // added for sharing a stuff category (cloth, leather, metal)
        private const float NameWords = 0.15f;      // added for all the garment's name words in the apparel's name or label
        private const float GenericBonus = 0.03f;   // added for the plain garments (Generic), so they win ties
        private const float ChildOnlyShare = 0.7f;  // a garment for children's apparel only (the kid romper) on anything

        // How much each body part group counts in the overlap. FullHead is its parts (FullHead, UpperHead, Eyes, Mouth).
        private static readonly Dictionary<string, float> GroupWeight = new Dictionary<string, float>
        {
            { "Torso", 3f }, { "Legs", 3f }, { "Arms", 2f }, { "Shoulders", 1f }, { "Neck", 0.5f }, { "Waist", 1f },
            { "Hands", 1f }, { "Feet", 1f }, { "UpperHead", 3f }, { "Eyes", 2f }, { "Mouth", 2f }, { "FullHead", 1f },
        };
        private static readonly Dictionary<string, string> GroupSame = new Dictionary<string, string>
        {
            { "Teeth", "Mouth" }, { "LeftShoulder", "Shoulders" }, { "RightShoulder", "Shoulders" }, { "LeftHand", "Hands" },
            { "RightHand", "Hands" }, { "MiddleFingers", "Hands" },
        };
        private static readonly string[] BodyLayers = { "OnSkin", "Middle", "Shell" };
        private static readonly string[] HeadLayers = { "Overhead", "OnHead", "StrappedHead", "EyeCover" };

        // Garments too much their own thing to be drawn for anything else, unless its name says it is one (a mod's cape,
        // crown or robe): variants with launchers, fins and gold, crowns, headsets, straps and webbing, the slave collar,
        // night vision, the top and ladies' hats.
        private static readonly HashSet<string> NoStandIn = new HashSet<string>
        {
            "Collar", "Blindfold", "TortureCrown", "CrownStellic", "Crown", "Coronet", "Gunlink", "AirwireHeadset",
            "ArrayHeadset", "IntegratorHeadset", "Slicecap", "Burka", "BodyStrap", "Bandolier", "CE_ExoFrame", "CE_RadioPack",
            "Sash", "Corset", "Cape", "CeremonialCultistMask", "CultistMask", "ArmorCataphractPhoenix", "ArmorCataphractPrestige",
            "ArmorLocust", "ArmorMarineGrenadier", "PowerArmorPrestige", "ArmorReconPrestige", "MechlordSuit",
            "ArmorHelmetCataphractPrestige", "ArmorHelmetReconPrestige", "ArmorMarineHelmetPrestige",
            "ArmorHelmetMechCommander", "ArmorHelmetMechlordHelmet", "RobeRoyal", "ShirtRuffle", "Shadecone", "Tailcap",
            "TribalHeaddress", "PsychicFoilHelmet", "EltexSkullcap", "Headwrap", "WarVeil", "HatTop", "HatLadies",
            "CE_SimpleNightVision", "CE_AdvancedNightVision",
        };

        // The plain garments, which win a tie.
        private static readonly HashSet<string> Generic = new HashSet<string>
        {
            "BasicShirt", "Pants", "Jacket", "Duster", "FlakVest", "SimpleHelmet", "AdvancedHelmet", "Tuque", "CollarShirt",
            "PowerArmor", "Parka", "GasMask", "ClothMask",
        };

        // Name words too common to tell garments apart.
        private static readonly HashSet<string> CommonWords = new HashSet<string> { "hat", "helmet", "armor", "armour", "apparel", "kid" };

        // The garments drawn for this apparel, which has none of its own, best first; empty for none. Cached per def.
        public int[] StandInsFor(ThingDef def)
        {
            if (def == null || def.apparel == null)
            {
                return NoStandIns;
            }
            int[] found;
            if (!standInsOf.TryGetValue(def, out found))
            {
                found = WorkOutStandIns(def);
                standInsOf[def] = found;
            }
            return found;
        }

        // The best garment for it, or -1.
        public int StandInFor(ThingDef def)
        {
            int[] all = StandInsFor(def);
            return all.Length > 0 ? all[0] : -1;
        }

        private int[] WorkOutStandIns(ThingDef def)
        {
            if (NeverStoodIn(def))
            {
                return NoStandIns;
            }
            Profile a = ProfileOf(def, 0);
            if (a.groups.Count == 0)
            {
                return NoStandIns;
            }
            List<Profile> pool = new List<Profile>();
            List<Profile> all = StandInProfiles();
            for (int i = 0; i < all.Count; i++)
            {
                if (!all[i].namedOnly || NameShare(a.text, all[i].tokens) >= 1f)
                {
                    pool.Add(all[i]);
                }
            }
            int[] best = NoStandIns;
            float bestScore = StandInLeast;
            float[] single = new float[pool.Count];
            for (int i = 0; i < pool.Count; i++)
            {
                single[i] = Score(a, pool[i]);
                if (single[i] >= bestScore)
                {
                    bestScore = single[i];
                    best = new[] { pool[i].piece };
                }
            }
            for (int i = 0; i < pool.Count; i++)
            {
                for (int k = i + 1; k < pool.Count; k++)
                {
                    if (SharesMain(pool[i].groups, pool[k].groups))
                    {
                        continue;
                    }
                    float s = PairScore(a, pool[i], pool[k]);
                    if (s > bestScore)
                    {
                        bestScore = s;
                        best = single[i] >= single[k] ? new[] { pool[i].piece, pool[k].piece } : new[] { pool[k].piece, pool[i].piece };
                    }
                }
            }
            return best;
        }

        // A garment's score for apparel a: how much of the groups they cover is covered by both, times the layers'
        // closeness and the kinds' (Factors), plus the kinds' small amounts.
        private static float Score(Profile a, Profile g)
        {
            float layer = LayerShare(a.layers, g.layers);
            float covered = Coverage(a.groups, g.groups, null);
            if (layer <= 0f || covered <= 0f)
            {
                return 0f;
            }
            float times, plus;
            Factors(a, g, out times, out plus);
            return covered * layer * times + plus;
        }

        // Two garments drawn together for a: their joined coverage at the farther layer, their kinds averaged, less PairCost.
        // Each must cover some of a.
        private static float PairScore(Profile a, Profile g, Profile h)
        {
            float layer = Math.Min(LayerShare(a.layers, g.layers), LayerShare(a.layers, h.layers));
            if (layer <= 0f || !a.groups.Overlaps(g.groups) || !a.groups.Overlaps(h.groups))
            {
                return 0f;
            }
            float tg, pg, th, ph;
            Factors(a, g, out tg, out pg);
            Factors(a, h, out th, out ph);
            return Coverage(a.groups, g.groups, h.groups) * layer * (tg + th) / 2f + (pg + ph) / 2f - PairCost;
        }

        // The weighted share of the groups either side covers that both do (with, if not null, joined to covered).
        private static float Coverage(HashSet<string> groups, HashSet<string> covered, HashSet<string> with)
        {
            float both = 0f, either = 0f;
            foreach (string x in groups)
            {
                either += Weight(x);
                if (covered.Contains(x) || (with != null && with.Contains(x)))
                {
                    both += Weight(x);
                }
            }
            foreach (string x in covered)
            {
                if (!groups.Contains(x))
                {
                    either += Weight(x);
                }
            }
            if (with != null)
            {
                foreach (string x in with)
                {
                    if (!groups.Contains(x) && !covered.Contains(x))
                    {
                        either += Weight(x);
                    }
                }
            }
            return both > 0f ? both / either : 0f;
        }

        // What a garment's kind makes of its score for a: a share it is multiplied by and an amount added.
        private static void Factors(Profile a, Profile g, out float times, out float plus)
        {
            times = 1f;
            plus = 0f;
            if (a.armour != g.armour)
            {
                times *= NotArmour;
            }
            if (g.vacsuit && !a.vacsuit)
            {
                times *= NotVacsuit;
            }
            if (g.childOnly)
            {
                times *= ChildOnlyShare;
            }
            if (g.vacsuit && a.vacsuit)
            {
                plus += BothVacsuit;
            }
            if (a.noble && g.noble)
            {
                plus += BothNoble;
            }
            if (a.tech != TechLevel.Undefined && g.tech != TechLevel.Undefined)
            {
                plus += SameTech * Math.Max(0f, 1f - Math.Abs((int)a.tech - (int)g.tech) / TechStep);
            }
            if (a.stuff.Overlaps(g.stuff))
            {
                plus += SameStuff;
            }
            plus += NameWords * NameShare(a.text, g.tokens);
            if (Generic.Contains(g.name))
            {
                plus += GenericBonus;
            }
        }

        // 1 for a layer in common, OtherLayerStep a body layer apart (squared two apart), OtherHeadLayer between head
        // layers, else 0: a belt, a backpack, webbing and any layer of a mod's own match only themselves.
        private static float LayerShare(List<string> a, List<string> g)
        {
            float best = 0f;
            for (int i = 0; i < a.Count; i++)
            {
                for (int j = 0; j < g.Count; j++)
                {
                    if (a[i] == g[j])
                    {
                        return 1f;
                    }
                    int ai = Array.IndexOf(BodyLayers, a[i]), gi = Array.IndexOf(BodyLayers, g[j]);
                    if (ai >= 0 && gi >= 0)
                    {
                        best = Math.Max(best, (float)Math.Pow(OtherLayerStep, Math.Abs(ai - gi)));
                    }
                    else if (Array.IndexOf(HeadLayers, a[i]) >= 0 && Array.IndexOf(HeadLayers, g[j]) >= 0)
                    {
                        best = Math.Max(best, OtherHeadLayer);
                    }
                }
            }
            return best;
        }

        private static float Weight(string group)
        {
            float w;
            return GroupWeight.TryGetValue(group, out w) ? w : 1f;
        }

        private static bool SharesMain(HashSet<string> a, HashSet<string> b)
        {
            return (a.Contains("Torso") && b.Contains("Torso")) || (a.Contains("Legs") && b.Contains("Legs"))
                || (a.Contains("Arms") && b.Contains("Arms")) || (a.Contains("UpperHead") && b.Contains("UpperHead"));
        }

        // The share of a garment's name words found in the apparel's name or label.
        private static float NameShare(string text, string[] tokens)
        {
            if (tokens == null || tokens.Length == 0 || text == null)
            {
                return 0f;
            }
            int found = 0;
            for (int i = 0; i < tokens.Length; i++)
            {
                if (text.Contains(tokens[i]))
                {
                    found++;
                }
            }
            return (float)found / tokens.Length;
        }

        private static readonly Regex Words = new Regex("[A-Z][a-z]+|[a-z]+");
        private static readonly Regex DefPrefix = new Regex("^(CE_)?Apparel_");

        private static string[] NameTokens(string defName)
        {
            List<string> words = new List<string>();
            foreach (Match m in Words.Matches(DefPrefix.Replace(defName, "")))
            {
                string w = m.Value.ToLowerInvariant();
                if (w.Length >= 3 && !CommonWords.Contains(w))
                {
                    words.Add(w);
                }
            }
            return words.ToArray();
        }

        // Shields (CE's, VFE's), anything else energy-shielding and anything worn on the belt (packs, lances: IsoRim draws
        // them as worn gear, WornGears.cs, though Combat Extended adds its Backpack layer to some): never drawn as clothes.
        private static bool NeverStoodIn(ThingDef def)
        {
            ApparelProperties ap = def.apparel;
            for (int i = 0; ap.layers != null && i < ap.layers.Count; i++)
            {
                if (ap.layers[i] == ApparelLayerDefOf.Belt || ap.layers[i].defName.IndexOf("Shield", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            for (int i = 0; def.thingCategories != null && i < def.thingCategories.Count; i++)
            {
                if (def.thingCategories[i].defName.IndexOf("Shield", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return def.HasComp(typeof(CompShield));
        }

        private static Profile ProfileOf(ThingDef def, int piece)
        {
            ApparelProperties ap = def.apparel;
            Profile p = new Profile { piece = piece, name = def.defName, tech = def.techLevel };
            for (int i = 0; ap.layers != null && i < ap.layers.Count; i++)
            {
                p.layers.Add(ap.layers[i].defName);
            }
            for (int i = 0; ap.bodyPartGroups != null && i < ap.bodyPartGroups.Count; i++)
            {
                AddGroup(p.groups, ap.bodyPartGroups[i].defName);
            }
            for (int i = 0; def.thingCategories != null && i < def.thingCategories.Count; i++)
            {
                string c = def.thingCategories[i].defName;
                p.armour |= c.IndexOf("Armor", StringComparison.OrdinalIgnoreCase) >= 0;
                p.noble |= c.IndexOf("Noble", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            p.vacsuit = (ap.tags != null && ap.tags.Contains("Vacsuit")) || def.defName.IndexOf("Vacsuit", StringComparison.OrdinalIgnoreCase) >= 0;
            p.childOnly = (ap.developmentalStageFilter & DevelopmentalStage.Adult) == 0;
            for (int i = 0; def.stuffCategories != null && i < def.stuffCategories.Count; i++)
            {
                p.stuff.Add(def.stuffCategories[i].defName);
            }
            p.text = (def.defName + " " + (def.label ?? "")).ToLowerInvariant();
            return p;
        }

        private static void AddGroup(HashSet<string> groups, string group)
        {
            string same;
            if (GroupSame.TryGetValue(group, out same))
            {
                group = same;
            }
            if (group == "FullHead")
            {
                groups.Add("UpperHead");
                groups.Add("Eyes");
                groups.Add("Mouth");
            }
            groups.Add(group);
        }

        // Every garment that can stand in: by the first of its defs the game has, else by its own layer and groups lines.
        // Not a held one (a shield), the shoes, the baby romper nor the fur; those in NoStandIn only by name (namedOnly).
        private List<Profile> StandInProfiles()
        {
            if (standInProfiles != null)
            {
                return standInProfiles;
            }
            standInProfiles = new List<Profile>();
            for (int g = 1; g < pieces.Length; g++)
            {
                Piece piece = pieces[g];
                if (piece.held || piece.withLegwear || piece.forBaby)
                {
                    continue;
                }
                ThingDef def = null;
                for (int i = 0; piece.defs != null && i < piece.defs.Length && def == null; i++)
                {
                    ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail(piece.defs[i]);
                    if (d != null && d.apparel != null)
                    {
                        def = d;
                    }
                }
                Profile p;
                if (def != null)
                {
                    p = ProfileOf(def, g);
                    p.tokens = NameTokens(def.defName);
                }
                else if (!string.IsNullOrEmpty(piece.layer) && piece.groups != null)
                {
                    p = new Profile { piece = g, name = piece.name, tech = TechLevel.Undefined, tokens = NameTokens(piece.name ?? "") };
                    p.layers.Add(piece.layer);
                    for (int i = 0; i < piece.groups.Length; i++)
                    {
                        AddGroup(p.groups, piece.groups[i]);
                    }
                }
                else
                {
                    continue;
                }
                p.name = piece.name;   // the garment's own name, which Generic goes by
                p.namedOnly = NoStandIn.Contains(piece.name ?? "");
                standInProfiles.Add(p);
            }
            return standInProfiles;
        }
    }
}
