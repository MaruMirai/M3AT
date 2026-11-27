using System;
using System.Collections.Generic;
using System.Linq;
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
            public bool mask;               // a garment of Masks; apparel with one of MaskWords in its name or label
            public bool helmet;             // apparel named a helmet but not armour, a hat or a mask (SoftForHelmet)
            public TechLevel tech;
            public HashSet<string> stuff = new HashSet<string>();
            public string[] tokens;         // a garment's: the words of its def's name (NameTokens)
            public string text;             // an apparel's: its defName and label, lower case, for the tokens to be found in
            public HashSet<string> headNouns; // an apparel's: what its label says it is (HeadNouns)
        }

        private List<Profile> standInProfiles;   // every garment that can stand in, made on first use
        private readonly Dictionary<ThingDef, int[]> standInsOf = new Dictionary<ThingDef, int[]>();
        private int standInsVersion;   // AddOns.Version they were worked out for
        private static readonly int[] NoStandIns = new int[0];

        private const float StandInLeast = 0.35f;   // the least score drawn: below it the apparel is left undrawn
        private const float PairCost = 0.03f;       // taken off two garments drawn together, so one as good wins
        private const float OtherLayerStep = 0.6f;  // the score's share one body layer apart (Underwear, OnSkin, Middle, Shell); squared two apart
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
        private const float HeadNounBonus = 0.5f;   // and more if its label ends on one of them: a priest robe is a robe
                                                    // and a psyker jacket a jacket, whatever their groups say (GrimWorld's
                                                    // were drawn as a flak vest and plate armour)
        private const float GenericBonus = 0.03f;   // added for the plain garments (Generic), so they win ties
        private const float ChildOnlyShare = 0.7f;  // a garment for children's apparel only (the kid romper) on anything
        private const float NarrowShare = 0.7f;     // a Narrow garment on apparel with none of its name words

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
        // The body layers in order. A mod's layer is taken as the one it counts as (SkinnedFigure.BodyLayerOf): any
        // underwear layer (UnderWhere's Underwear and UnderwearTop, Kiiro's Kiiro_Underwear) as "Underwear", so every
        // underwear mod's apparel takes the underwear garments, and Kiiro's trousers layer, under the skin layer, as OnSkin.
        private const string UnderLayer = SkinnedFigure.UnderwearLayer;
        private static readonly string[] BodyLayers = { UnderLayer, "OnSkin", "Middle", "Shell" };
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
            "CE_SimpleNightVision", "CE_AdvancedNightVision", "Scarf", "Boots", "Sandals", "Socks", "Goggles", "Gorget",
            "Glasses", "Sunglasses", "Hairband", "Ribbon", "MaidHeaddress", "Stockings", "Tights",
            "Leotard", "ShoulderBag", "SkirtMini", "SkirtMedium", "SkirtMidi", "DressMini", "DressShort", "DressMidi",
            "SleevelessDressMini", "SleevelessDressShort", "SleevelessDress", "SleevelessDressMidi", "SleevelessDressLong",
        };
        // Other words that name the same thing as a NoStandIn garment's name word, and phrases that don't though they hold
        // it (a headscarf is worn over the hair, not round the neck).
        private static readonly Dictionary<string, string[]> NameAlike = new Dictionary<string, string[]>
        {
            { "scarf", new[] { "neckerchief", "kerchief", "muffler", "shemagh", "keffiyeh", "snood" } },
            { "goggles", new[] { "goggle" } },   // a mod's goggle (Kiiro's, Wolfein's)
            { "glasses", new[] { "glass", "spectacle", "monocle" } },   // a mod's glasses, single glass or spectacles
            { "sunglasses", new[] { "shades" } },
            { "short", new[] { "hotpant", "hot pant" } },   // hotpants are the short shorts, not the shorts
            { "crop", new[] { "halter", "tube top" } },
            { "tanktop", new[] { "tank top", "singlet", "camisole" } },   // a tank top is the tank top's, not the crop top's
            // CE's exoframe (name words exo and frame) is any exoskeleton or exosuit: Passive Exoskeletons' and The Dead
            // Man's Switch's, which scored as power armour or flak by their coverage.
            { "frame", new[] { "exoskelet", "exosuit" } },
            // CE's radio pack is any pack with a radio: GrimWorld's vox-caster backpacks.
            { "radio", new[] { "vox", "comms", "transceiver", "communicat" } },
            // Footwear: the shoes are any footwear not named boots, sandals or socks (heels, sneakers, slippers).
            { "boots", new[] { "boot" } },
            { "sandals", new[] { "sandal", "flip flop", "flipflop" } },
            { "socks", new[] { "sock", "feetwrap", "footwrap", "feet wrap", "foot wrap" } },
            { "shoes", new[] { "shoe", "heel", "sneaker", "loafer", "slipper", "clog", "moccasin" } },
        };
        private static readonly Dictionary<string, string[]> NameNot = new Dictionary<string, string[]>
        {
            { "scarf", new[] { "headscarf", "head scarf" } },
            { "pants", new[] { "hotpant", "underpant" } },   // hotpants are shorts, underpants underwear
            { "socks", new[] { "cassock" } },   // (words are found anywhere in a word, as mods run them together: fieldcap, VoxBackpack)
            { "glasses", new[] { "sunglass", "hourglass", "glass armo", "glassworks" } },   // sunglasses are the sunglasses'
        };
        // Words in an apparel's name or label saying it is a garment's kind, though not in the garment's own name words: the
        // garment may stand in for it even if NoStandIn or a mask, its coverage at least HintCover and HintBonus ahead (less
        // than a garment named outright, so Kiiro's airwire headset keeps the airwire headset's). Mostly the race mods'
        // clothes (Ratkin, Kiiro, Milira, Wolfein): their dresses were drawn as a shirt and trousers, their headsets as the
        // eltex helmet, their bows and hairbands as a tuque.
        private static readonly Dictionary<string, string[]> Hints = new Dictionary<string, string[]>
        {
            { "IntegratorHeadset", new[] { "headset", "earpiece", "ear piece" } },
            { "Vacsuit", new[] { "space suit", "spacesuit", "airtight suit", "pressure suit" } },
            { "VacsuitHelmet", new[] { "space suit helmet", "spacesuit helmet", "airtight helmet" } },
            { "HatLadies", new[] { "sun hat", "sunhat", "straw hat", "summer hat", "sun bonnet" } },
            { "BowlerHat", new[] { "melonik", "pith", "explorer hat", "safari", "school cap", "fedora", "trilby", "homburg", "panama hat" } },
            // A peaked (service, officer's) cap or kepi: the authority cap, not the tuque (Cane Aternitigno's army caps).
            { "AuthorityCap", new[] { "peaked cap", "visor cap", "service cap", "officer cap", "officers cap", "officer's cap", "kepi" } },
            { "Shadecone", new[] { "rain hat", "conical hat", "bamboo hat" } },
            { "LabCoat", new[] { "lab gown", "labgown", "medical gown", "research gown", "white coat" } },
            { "Parka", new[] { "winter coat", "wintercoat", "puffer", "down jacket" } },
            { "SimpleHelmet", new[] { "miner hat", "minerhat", "hard hat", "hardhat", "safety helmet", "military helmet" } },
            { "Coronet", new[] { "tiara", "diadem" } },
            // Knitwear and sweatshirts have no collar (Cane Aternitigno's sweater and hoodie took the collared shirt).
            { "BasicShirt", new[] { "sweater", "jumper", "pullover", "hoodie", "sweatshirt" } },
            // A side (garrison) cap is soft and has no peak: the beret, not the authority cap.
            { "Beret", new[] { "side cap", "garrison", "overseas cap" } },
            { "Hairband", new[] { "hairband", "hair band", "headband", "head band", "headbond", "alice band" } },
            { "Ribbon", new[] { "ribbon", "bowknot", "hairtie", "hair tie", "hair bow", "corsage", "flower headwear", "hairclip",
                                "hair clip", "scrunchie" } },
            { "MaidHeaddress", new[] { "maid cap", "maid head", "maidcap", "maid hat" } },
            { "Broadwrap", new[] { "nun ", "veil", "wimple", "sister hat" } },   // a nun's veil: the broadwrap, clear of the back
            { "Stockings", new[] { "stocking", "thigh high", "thighhigh", "knee high", "suspender" } },
            // Straps over the shoulders to the waist (Cane Aternitigno's army suspenders took the royal vest).
            { "BodyStrap", new[] { "suspenders", "braces" } },
            { "Tights", new[] { "pantyhose", "pantynose", "tights", "leggings" } },
            { "Leotard", new[] { "leotard", "swimsuit", "swim suit", "bunny girl" } },
            { "ShoulderBag", new[] { "messenger", "satchel", "shoulder bag" } },
            { "Skirt", new[] { "skirt" } },
            { "SkirtMini", new[] { "mini skirt", "miniskirt", "micro skirt", "microskirt" } },
            { "SkirtMedium", new[] { "knee skirt", "knee length skirt", "knee-length skirt", "pencil skirt" } },
            { "SkirtMidi", new[] { "midi skirt", "midiskirt", "calf skirt" } },
            { "SkirtLong", new[] { "long skirt", "longskirt", "longuette", "maxi skirt" } },
            { "DressMini", new[] { "mini dress", "minidress" } },
            { "DressShort", new[] { "short dress", "cocktail dress" } },
            { "Dress", new[] { "dress", "onepiece", "one piece", "frock", "maid outfit", "maid uniform" } },
            { "SleevelessDress", new[] { "sleeveless dress", "sundress", "sun dress", "slip dress", "strapless dress", "camisole dress", "halter dress" } },
            { "DressMidi", new[] { "midi dress", "mididress", "tea dress" } },
            // (A cassock or a nun's habit hangs to the feet: Cane Aternitigno's took the socks, by "sock" in cassock, and
            // a shirt and trousers.)
            { "DressLong", new[] { "long dress", "gown", "nun outfit", "nun dress", "sister dress", "evening dress", "wedding dress",
                                   "maxi dress", "cassock", "soutane", "habit" } },
        };
        // Phrases holding a hint that don't say it: a headdress is no dress, suspender trousers no stockings.
        private static readonly Dictionary<string, string[]> HintNot = new Dictionary<string, string[]>
        {
            { "Dress", new[] { "headdress", "dressing", "long dress", "nun dress", "sister dress", "evening dress", "wedding dress",
                               "maxi dress", "mini dress", "minidress", "short dress", "cocktail dress", "midi dress",
                               "mididress", "tea dress", "sleeveless dress", "sundress", "sun dress", "slip dress", "strapless dress", "camisole dress", "halter dress" } },
            { "DressLong", new[] { "lab gown", "labgown", "medical gown", "research gown", "graduation gown", "sleep gown" } },
            { "Hairband", new[] { "bandage" } },
            { "Skirt", new[] { "long skirt", "longskirt", "longuette", "maxi skirt", "knee skirt", "knee length skirt", "mini skirt",
                               "miniskirt", "micro skirt", "microskirt",
                               "knee-length skirt", "pencil skirt", "midi skirt", "midiskirt", "calf skirt" } },
            { "Stockings", new[] { "suspender trouser", "suspender pant", "suspender skirt", "suspender short", "suspenders" } },
            { "Broadwrap", new[] { "war veil", "warveil" } },
        };
        private const float HintCover = 0.5f;
        private const float HintCoverHead = 0.8f;   // for headgear, which takes one garment: a nun's hat the broadwrap, which covers far more than a cap
        private const float HintBonus = 0.6f;
        // Headgear named a helmet or helm is matched as one even if not in an armour category (Wolfein's power armour
        // helmets and Kiiro's military helmets were taken for hats and drawn as a tuque), unless its label says it is a hat.
        private static readonly Regex HelmetWords = new Regex(@"\bhelm(et)?s?\b");
        // Named a bag of some kind (Kiiro's back bag and messenger bag, on its own Kiiro_Bag layer and Middle): a pack.
        private static readonly Regex BagWords = new Regex(@"\bbags?\b|satchel|rucksack|knapsack|oxygen tank");
        // A NoStandIn garment named by the apparel (all its name words) is the thing itself, whatever the groups say (a mod's
        // airwire headset over the eyes alone, a scarf pulled up over the chin given FullHead): its coverage is taken as at
        // least NamedCover, and it is ahead of any other by NamedBonus, though it must still share a layer.
        private const float NamedCover = 0.5f;
        private const float NamedBonus = 1f;

        // Garments over the face, which stand in only for apparel named as one (MaskWords): mods often give a plain hat
        // FullHead, or a cap UpperHead and Eyes, which the gas mask (FullHead, Overhead, not armour) matched exactly.
        private static readonly HashSet<string> Masks = new HashSet<string>
        {
            "GasMask", "ClothMask", "WarMask", "VisageMask", "WarVeil", "CultistMask", "CeremonialCultistMask",
        };
        private static readonly string[] MaskWords = { "mask", "respirator", "balaclava", "veil" };

        // The plain garments, which win a tie.
        private static readonly HashSet<string> Generic = new HashSet<string>
        {
            "BasicShirt", "Pants", "Jacket", "Duster", "FlakVest", "SimpleHelmet", "AdvancedHelmet", "Tuque", "CollarShirt",
            "PowerArmor", "Parka", "GasMask", "ClothMask", "CE_Backpack", "Shoes",
        };

        // Garments that are a narrower kind of a plain one (a crop top of a shirt, shorts of trousers): drawn for apparel
        // whose name says so (a tank top, hotpants, a short skirt), but behind the plain one on coverage alone, as they
        // were only 0.03 behind it on a plain sweater or jeans.
        private static readonly HashSet<string> Narrow = new HashSet<string> { "CropTop", "Tanktop", "Shorts", "ShortShorts" };

        // A pack: apparel worn only on a backpack layer (CE's and GrimWorld's Backpack, Ratkin's BackPack), or named a
        // backpack, whatever groups it gives (Torso, Shoulders, Waist), is matched as PackLayer over PackGroup alone, so
        // every pack takes a pack garment: GrimWorld's covered Torso where CE's backpack covers Shoulders, and none was
        // drawn. Webbing (apparel named as it, WebbingWords, not a pack) is matched as CE's tactical vest is, its layer and
        // Torso: GrimWorld's combat webbing, on Shell and Belt over the whole body, was drawn as nothing or a robe.
        private const string PackLayer = "Backpack";
        private const string PackGroup = "Back";
        private const string WebbingLayer = "Webbing";
        private const string ShoulderLayer = "Shoulder";
        private static readonly string[] NotPackWords = { "cape", "cloak" };   // never a pack (GrimWorld's scout cape)
        // Headgear whose label says it is one of these is a hat, even if armour: GrimWorld's Space Marine hoods cover the
        // whole head as armour and took the power armour helmet.
        private static readonly HashSet<string> HatNouns = new HashSet<string> { "hood", "cap", "hat", "beret", "bandana", "headwrap", "turban", "bonnet" };
        private static readonly string[] WebbingWords = { "webbing", "chest rig", "load bearing", "load-bearing", "load carrier" };   // (Kiiro's load carrier took the royal vest)

        // Name words too common to tell garments apart.
        private static readonly HashSet<string> CommonWords = new HashSet<string> { "hat", "helmet", "armor", "armour", "apparel", "kid" };

        // The garments drawn for this apparel, which has none of its own, best first; empty for none: the player's choice
        // in the clothing tool if any (ClothingTool.cs, WalkFigures.ChosenStandIns), else the best match, cached per def.
        public int[] StandInsFor(ThingDef def)
        {
            if (def == null || def.apparel == null)
            {
                return NoStandIns;
            }
            int[] chosen;
            if (WalkFigures.ChosenStandIns(this, def, out chosen))
            {
                return chosen;
            }
            return AutoStandInsFor(def);
        }

        // The garments the scoring picks for this apparel, whatever the player chose. Cached per def.
        public int[] AutoStandInsFor(ThingDef def)
        {
            if (def == null || def.apparel == null)
            {
                return NoStandIns;
            }
            int[] found;
            if (standInsVersion != AddOns.Version)
            {
                // Which add-ons are on changed (the benchmark's turns): the pool and every answer worked out again.
                standInsVersion = AddOns.Version;
                standInsOf.Clear();
                standInProfiles = null;
            }
            if (!standInsOf.TryGetValue(def, out found))
            {
                found = WorkOutStandIns(def);
                standInsOf[def] = found;
            }
            return found;
        }

        // The stand-ins worked out so far dropped, for the pool to be made again: after the budgets matched other mods'
        // garments against this mod's own alone while loading (GarmentBudget.cs, OursOf).
        internal void ForgetStandIns()
        {
            standInsOf.Clear();
            standInProfiles = null;
        }

        // This mod's garments the scoring picks for this apparel, whatever the player chose and not cached: for a mod
        // garment's budget (OursOf), while the pieces are only partly read.
        internal int[] OwnMatchFor(ThingDef def)
        {
            return def != null && def.apparel != null ? WorkOutStandIns(def) : NoStandIns;
        }

        // The garments the player may pick for apparel in the clothing tool: every one that can stand in (StandInProfiles),
        // those kept for apparel named as them too, as the player picks it by eye.
        public List<int> ChoosableGarments()
        {
            List<Profile> all = StandInProfiles();
            List<int> found = new List<int>(all.Count);
            for (int i = 0; i < all.Count; i++)
            {
                found.Add(all[i].piece);
            }
            return found;
        }

        // A garment by its name (its file's, garment_<name>.txt), or -1.
        public int GarmentNamed(string name)
        {
            for (int g = 1; name != null && g < pieces.Length; g++)
            {
                if (pieces[g].name == name)
                {
                    return g;
                }
            }
            return -1;
        }

        // A garment as the player knows it: the label of the first apparel it is drawn for that the game has, else its name.
        public string GarmentLabel(int g)
        {
            Piece piece = pieces[g];
            for (int i = 0; piece.defs != null && i < piece.defs.Length; i++)
            {
                ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail(piece.defs[i]);
                if (d != null && d.apparel != null)
                {
                    return d.LabelCap;
                }
            }
            return GenText.SplitCamelCase(piece.name ?? "?");
        }

        // The best garment for it, or -1.
        public int StandInFor(ThingDef def)
        {
            int[] all = StandInsFor(def);
            return all.Length > 0 ? all[0] : -1;
        }

        private int[] WorkOutStandIns(ThingDef def)
        {
            // A garment's stands_for line names the apparel outright, so it wins even over NeverStoodIn: Vanilla Weapons
            // Expanded - Heavy Weapons' exoskeleton frame is worn on the Belt layer, drawn as a pack.
            int[] named = StandsFor(def);
            if (named.Length > 0)
            {
                return named;
            }
            if (NeverStoodIn(def))
            {
                return NoStandIns;
            }
            Profile a = ProfileOf(def, 0);
            a.mask = IsMaskNamed(def, a.text);
            a.headNouns = HeadNouns(def.label);
            bool hatNamed = a.headNouns.Overlaps(HatNouns);
            a.helmet = !a.armour && !hatNamed && !a.mask && HelmetWords.IsMatch(a.text);
            if ((!a.armour || hatNamed) && !a.mask && a.groups.Contains("UpperHead") && (hatNamed || !HelmetWords.IsMatch(a.text)))
            {
                // A hat, however much of the head its groups say: matched as a hat, not as a mask or full helmet. Armour
                // too if its label says it is a hat (HatNouns).
                a.groups.Remove("Eyes");
                a.groups.Remove("Mouth");
            }
            if (a.groups.Count == 0)
            {
                return NoStandIns;
            }
            List<Profile> pool = new List<Profile>();
            List<Profile> all = StandInProfiles();
            for (int i = 0; i < all.Count; i++)
            {
                // Apparel with a content pack's garment comes here only because that garment was turned down (the player
                // chose M3AT's for this item, its clothing or its armour row): so M3AT's own garments, not that one nor
                // another pack garment whose name matches (the Wolfein pack's AIPT power armour for its heavy power armour).
                if (IsPackGarment(all[i].piece) && PackGarmentOf(def) > 0)
                {
                    continue;
                }
                // A garment over the skull (not a mask, which FullHead only says is, nor one the apparel is named as) only
                // for apparel over it: a scarf over the mouth or neck scored just enough for the power armour helmet.
                bool hint = Hinted(a.text, all[i].name);
                bool skull = !all[i].mask && !all[i].namedOnly && !hint && all[i].groups.Contains("UpperHead") && !a.groups.Contains("UpperHead");
                // An open-fronted garment only for apparel drawn open down the front (OpenFronts.cs).
                if ((!all[i].namedOnly || hint || NameShare(a.text, all[i].tokens) >= 1f) && (!all[i].mask || a.mask || hint) && !skull
                    && FrontFits(all[i], def))
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

#if AUTOTEST
        // The scoring laid out for the autotest: the apparel's profile and every garment named in names with its score, or
        // why it isn't in the pool.
        internal string StandInScores(ThingDef def, string[] names)
        {
            Profile a = ProfileOf(def, 0);
            a.mask = IsMaskNamed(def, a.text);
            a.headNouns = HeadNouns(def.label);
            a.helmet = !a.armour && !a.headNouns.Overlaps(HatNouns) && !a.mask && HelmetWords.IsMatch(a.text);
            List<string> parts = new List<string> { "text '" + a.text + "' layers " + string.Join(" ", a.layers) + " groups "
                + string.Join(" ", a.groups) + " never " + NeverStoodIn(def) };
            foreach (Profile g in StandInProfiles())
            {
                if (Array.IndexOf(names, g.name) >= 0)
                {
                    parts.Add(g.name + ": layers " + string.Join(" ", g.layers) + " groups " + string.Join(" ", g.groups)
                        + " namedOnly " + g.namedOnly + " hint " + Hinted(a.text, g.name) + " score " + Score(a, g).ToString("F2"));
                }
            }
            return string.Join("; ", parts);
        }
#endif

        // A garment's score for apparel a: how much of the groups they cover is covered by both, times the layers'
        // closeness and the kinds' (Factors), plus the kinds' small amounts.
        private static float Score(Profile a, Profile g)
        {
            float layer = LayerShare(a.layers, g.layers);
            float covered = Coverage(a.groups, g.groups, null);
            bool named = g.namedOnly && NameShare(a.text, g.tokens) >= 1f;
            if (named)
            {
                covered = Math.Max(covered, NamedCover);
            }
            if (Hinted(a.text, g.name))
            {
                bool head = a.layers.Count > 0 && a.layers.TrueForAll(x => Array.IndexOf(HeadLayers, x) >= 0);
                covered = Math.Max(covered, head ? HintCoverHead : HintCover);
                if (head && layer > 0f)
                {
                    layer = 1f;   // another head layer is no matter for headgear named as the garment (CE puts the broadwrap on OnHead)
                }
            }
            if (layer <= 0f || covered <= 0f)
            {
                return 0f;
            }
            float times, plus;
            Factors(a, g, out times, out plus);
            return covered * layer * times + plus + (named ? NamedBonus : 0f);
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
            // A hinted garment's bonus counts whole, not halved with the other's: a shirt with a skirt (Wolfein's (Skirt)
            // uniforms) is the collared shirt and the skirt, not the skirt alone.
            float hint = HintBonus / 2f * ((Hinted(a.text, g.name) ? 1 : 0) + (Hinted(a.text, h.name) ? 1 : 0));
            return Coverage(a.groups, g.groups, h.groups) * layer * (tg + th) / 2f + (pg + ph) / 2f + hint - PairCost;
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

        // Whether g is soft headgear (not armour, no Helmet in its name) and a a helmet by name only: behind as armour is for
        // a garment that isn't, as Cane Aternitigno's chemical protection helmet, a plain hat by its def, took the broadwrap
        // over the helmets. (Not for a helmet also named a mask, which may take the gas mask; the vacsuit helmet is a helmet.)
        private static bool SoftForHelmet(Profile a, Profile g)
        {
            return a.helmet && !g.armour && (g.name ?? "").IndexOf("Helmet", StringComparison.OrdinalIgnoreCase) < 0;
        }

        // What a garment's kind makes of its score for a: a share it is multiplied by and an amount added.
        private static void Factors(Profile a, Profile g, out float times, out float plus)
        {
            times = 1f;
            plus = 0f;
            if (Hinted(a.text, g.name))
            {
                plus += HintBonus;
            }
            if (a.armour != g.armour || SoftForHelmet(a, g))
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
            float named = NameShare(a.text, g.tokens);
            if (Narrow.Contains(g.name) && named <= 0f)
            {
                times *= NarrowShare;
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
            plus += NameWords * named;
            if (named >= 1f && a.headNouns != null && Array.Exists(g.tokens, x => a.headNouns.Contains(Singular(x))))
            {
                plus += HeadNounBonus;
            }
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
                || (a.Contains("Arms") && b.Contains("Arms")) || (a.Contains("UpperHead") && b.Contains("UpperHead"))
                || (a.Contains(PackGroup) && b.Contains(PackGroup));   // (never two packs)
        }

        // Whether the apparel's name or label has one of the garment's Hints.
        private static bool Hinted(string text, string garment)
        {
            string[] words;
            if (text == null || garment == null || !Hints.TryGetValue(garment, out words))
            {
                return false;
            }
            string[] not;
            if (HintNot.TryGetValue(garment, out not))
            {
                for (int k = 0; k < not.Length; k++)
                {
                    text = text.Replace(not[k], " ");
                }
            }
            return Array.Exists(words, w => text.Contains(w));
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
                string t = text;
                string[] not;
                if (NameNot.TryGetValue(tokens[i], out not))
                {
                    for (int k = 0; k < not.Length; k++)
                    {
                        t = t.Replace(not[k], " ");
                    }
                }
                string[] alike;
                bool hit = t.Contains(tokens[i]);
                if (!hit && NameAlike.TryGetValue(tokens[i], out alike))
                {
                    hit = Array.Exists(alike, w => t.Contains(w));
                }
                if (hit)
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

        // What a label says the apparel is: its last word and the word before its first "of", lower case, without a
        // plural s and anything in brackets (a priest robe is a robe, robes of Malcador robes), which says more than its
        // groups, as mods give a long coat the groups of plate armour.
        private static readonly Regex Brackets = new Regex(@"\(.*?\)");
        private static readonly Regex LowerWords = new Regex("[a-z]+");

        private static HashSet<string> HeadNouns(string label)
        {
            HashSet<string> found = new HashSet<string>();
            MatchCollection words = LowerWords.Matches(Brackets.Replace((label ?? "").ToLowerInvariant(), " "));
            if (words.Count > 0)
            {
                found.Add(Singular(words[words.Count - 1].Value));
            }
            for (int i = 1; i < words.Count; i++)
            {
                if (words[i].Value == "of")
                {
                    found.Add(Singular(words[i - 1].Value));
                    break;
                }
            }
            return found;
        }

        private static string Singular(string w)
        {
            return w.Length > 3 && w.EndsWith("s") && !w.EndsWith("ss") ? w.Substring(0, w.Length - 1) : w;
        }

        // A defName as words, lower case: ElysianHelmetOpen held "top" run together, which the top hat took.
        private static readonly Regex LowerUpper = new Regex("([a-z])([A-Z])");

        private static string WordsOf(string defName)
        {
            return LowerUpper.Replace((defName ?? "").Replace('_', ' '), "$1 $2").ToLowerInvariant();
        }

        // Packs, webbing, pauldrons and eyewear matched as what they are, their layers and groups replaced: webbing (by
        // name) as CE's tactical vest; a pack (packLayers, or named a backpack, but not a cape or cloak) as PackLayer over
        // PackGroup; a cape or cloak on a backpack layer as on the Shell (GrimWorld's scout cape); pauldrons (worn only on
        // a shoulder layer, GrimWorld's GW_Shoulder) as ShoulderLayer over the Shoulders; worn only over the eyes
        // (EyeCover) as over the eyes alone, as goggles or a headset giving UpperHead took the simple helmet.
        private static void AsWhatItIs(Profile p, bool packLayers, bool shoulderLayers)
        {
            if (IsWebbing(p.text))
            {
                Replace(p, WebbingLayer, "Torso");
            }
            else if (IsPack(p.text, packLayers))
            {
                Replace(p, PackLayer, PackGroup);
            }
            else if (packLayers)
            {
                p.layers.Clear();
                p.layers.Add("Shell");
            }
            else if (shoulderLayers)
            {
                Replace(p, ShoulderLayer, "Shoulders");
            }
            else if (p.layers.Count > 0 && p.layers.TrueForAll(x => x == "EyeCover") && p.groups.Count > 0)
            {
                p.groups.Clear();
                p.groups.Add("Eyes");
            }
        }

        private static void Replace(Profile p, string layer, string group)
        {
            p.layers.Clear();
            p.layers.Add(layer);
            p.groups.Clear();
            p.groups.Add(group);
        }

        private static bool IsWebbing(string text)
        {
            return Array.Exists(WebbingWords, w => text.Contains(w));
        }

        private static bool IsPack(string text, bool packLayers)
        {
            return (packLayers || text.Contains("backpack") || BagWords.IsMatch(text)) && !Array.Exists(NotPackWords, w => text.Contains(w));
        }

        // Worn on a backpack layer and nothing else but the belt (a jump pack is on both).
        private static bool PackLayers(List<string> names, List<string> labels)
        {
            bool any = false;
            for (int i = 0; i < names.Count; i++)
            {
                bool pack = (names[i] + " " + labels[i]).IndexOf("backpack", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!pack && names[i] != "Belt")
                {
                    return false;
                }
                any |= pack;
            }
            return any;
        }

        // Worn only on shoulder layers (GrimWorld's GW_Shoulder, its pauldrons).
        private static bool ShoulderLayers(List<string> names, List<string> labels)
        {
            for (int i = 0; i < names.Count; i++)
            {
                if ((names[i] + " " + labels[i]).IndexOf("shoulder", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return false;
                }
            }
            return names.Count > 0;
        }

        private static void LayerNames(ApparelProperties ap, out List<string> names, out List<string> labels)
        {
            names = new List<string>();
            labels = new List<string>();
            for (int i = 0; ap.layers != null && i < ap.layers.Count; i++)
            {
                names.Add(ap.layers[i].defName);
                labels.Add(ap.layers[i].label ?? "");
            }
        }

        // A pack of power armour: one of Spacer tech or later fitted over the neck and shoulders too (GrimWorld's Space
        // Marine packs), which the power armour garments draw with their own backpack, so it takes no stand-in.
        private static bool ArmourPack(ThingDef def)
        {
            if (def.techLevel < TechLevel.Spacer)
            {
                return false;
            }
            HashSet<string> groups = new HashSet<string>();
            for (int i = 0; def.apparel.bodyPartGroups != null && i < def.apparel.bodyPartGroups.Count; i++)
            {
                AddGroup(groups, def.apparel.bodyPartGroups[i].defName);
            }
            return groups.Contains("Neck") && groups.Contains("Shoulders");
        }

        // Named a mask (MaskWords), unless its label names it a helmet and no mask: Cane Aternitigno's CG_HeavyMask is a
        // "Heavy Infantry Helmet", a closed visored helmet, which took the gas mask.
        private static bool IsMaskNamed(ThingDef def, string text)
        {
            string label = (def.label ?? "").ToLowerInvariant();
            if (HelmetWords.IsMatch(label) && !Array.Exists(MaskWords, w => label.Contains(w)))
            {
                return false;
            }
            for (int i = 0; i < MaskWords.Length; i++)
            {
                if (text != null && text.Contains(MaskWords[i]))
                {
                    return true;
                }
            }
            return false;
        }

        // Shields (CE's, VFE's, by layer or category); anything on the belt or energy-shielding unless also worn on a body
        // layer, and for a shield in it covering the arms or legs too (packs, lances: IsoRim draws them as worn gear,
        // WornGears.cs; but GrimWorld's Terminator armour lists the belt and its Custodes plate has a shield in it, while
        // Biotech's heavy shield unit, Middle over the torso, is a shield); power armour's packs (ArmourPack): never drawn
        // as clothes. A pack or webbing on the belt (AsWhatItIs) takes a pack or webbing garment.
        private static readonly Regex Jewellery = new Regex(@"\b(rings?|psyrings?|bracelets?|anklets?|bangles?|pendants?|necklaces?|amulets?|lockets?)\b");
        private static bool NeverStoodIn(ThingDef def)
        {
            ApparelProperties ap = def.apparel;
            string text = WordsOf(def.defName) + " " + (def.label ?? "").ToLowerInvariant();
            List<string> names, labels;
            LayerNames(ap, out names, out labels);
            bool packLayers = PackLayers(names, labels);
            // Jewellery (a ring, bracelet or anklet) worn on the hands or feet is not a glove or a boot, nor a pendant on
            // the neck a vest (Milira's psychic defense pendant, Middle over the torso, took the psyfocus vest).
            if (Jewellery.IsMatch(text))
            {
                return true;
            }
            if (IsWebbing(text))
            {
                return false;
            }
            if (IsPack(text, packLayers))
            {
                return ArmourPack(def);
            }
            if (packLayers)
            {
                return false;
            }
            bool belt = false, body = false;
            for (int i = 0; ap.layers != null && i < ap.layers.Count; i++)
            {
                if (ap.layers[i].defName.IndexOf("Shield", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
                belt |= ap.layers[i] == ApparelLayerDefOf.Belt;
                body |= Array.IndexOf(BodyLayers, BodyLayerOf(ap.layers[i])) >= 0;
            }
            for (int i = 0; def.thingCategories != null && i < def.thingCategories.Count; i++)
            {
                if (def.thingCategories[i].defName.IndexOf("Shield", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            bool limbs = false;
            for (int i = 0; ap.bodyPartGroups != null && i < ap.bodyPartGroups.Count; i++)
            {
                string g = ap.bodyPartGroups[i].defName;
                limbs |= g == "Arms" || g == "Legs";
            }
            return (!body && belt) || (def.HasComp(typeof(CompShield)) && !(body && limbs));
        }

        private static Profile ProfileOf(ThingDef def, int piece)
        {
            ApparelProperties ap = def.apparel;
            Profile p = new Profile { piece = piece, name = def.defName, tech = def.techLevel };
            for (int i = 0; ap.layers != null && i < ap.layers.Count; i++)
            {
                string layer = BodyLayerOf(ap.layers[i]);
                if (!p.layers.Contains(layer))
                {
                    p.layers.Add(layer);
                }
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
            p.text = WordsOf(def.defName) + " " + (def.label ?? "").ToLowerInvariant();
            List<string> names, labels;
            LayerNames(ap, out names, out labels);
            AsWhatItIs(p, PackLayers(names, labels), ShoulderLayers(names, labels));
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

        // The garments whose stands_for lines name this apparel, in the export's order: a set the scoring can't tell
        // apart (Kiiro's bra and briefs from its bandeau and briefs) named by hand. Empty for none.
        internal int[] StandsFor(ThingDef def)
        {
            List<int> found = null;
            for (int g = 1; g < pieces.Length; g++)
            {
                if (pieces[g].standsFor != null && Array.IndexOf(pieces[g].standsFor, def.defName) >= 0)
                {
                    (found ?? (found = new List<int>())).Add(g);
                }
            }
            return found != null ? found.ToArray() : NoStandIns;
        }

        // Every garment that can stand in: by the first of its defs the game has, else by its own layer and groups lines.
        // Not a held one (a shield), the baby romper nor the fur; those in NoStandIn only by name (namedOnly).
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
                if (piece.held || piece.forBaby || piece.genes != null || OffAddOn(g))
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
                string[] lineLayers = string.IsNullOrEmpty(piece.layer) ? new string[0]
                    : piece.layer.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);   // may name several (a scarf is worn on any)
                if (def != null)
                {
                    p = ProfileOf(def, g);
                    // Its layer and groups lines add to the def's: the tank top is UnderWhere's tank top and a top worn
                    // on the skin too.
                    for (int i = 0; i < lineLayers.Length; i++)
                    {
                        if (!p.layers.Contains(lineLayers[i]))
                        {
                            p.layers.Add(lineLayers[i]);
                        }
                    }
                    for (int i = 0; piece.groups != null && i < piece.groups.Length; i++)
                    {
                        AddGroup(p.groups, piece.groups[i]);
                    }
                    List<string> names, labels;   // again, as the lines may have added to a pack's groups
                    LayerNames(def.apparel, out names, out labels);
                    AsWhatItIs(p, PackLayers(names, labels), ShoulderLayers(names, labels));
                }
                else if (lineLayers.Length > 0 && piece.groups != null)
                {
                    p = new Profile { piece = g, tech = TechLevel.Undefined, text = (piece.name ?? "").ToLowerInvariant() };
                    p.layers.AddRange(lineLayers);
                    for (int i = 0; i < piece.groups.Length; i++)
                    {
                        AddGroup(p.groups, piece.groups[i]);
                    }
                    List<string> names = new List<string>(lineLayers), labels = names.ConvertAll(x => "");
                    AsWhatItIs(p, PackLayers(names, labels), ShoulderLayers(names, labels));
                }
                else
                {
                    continue;
                }
                // Its kind (tech level, stuff, armour) from the apparel its like line names, if the game has it, over its
                // def's: the shorts aren't behind the trousers on those alone, and the crop top isn't an Animal-tech
                // undergarment because UnderWhere's tank top is (as it was its garment once).
                ThingDef like = piece.like != null ? DefDatabase<ThingDef>.GetNamedSilentFail(piece.like) : null;
                if (like != null && like.apparel != null)
                {
                    Profile kind = ProfileOf(like, g);
                    p.tech = kind.tech;
                    p.stuff = kind.stuff;
                    p.armour = kind.armour;
                    p.noble = kind.noble;
                }
                // Its name words are its own name's, not those of whichever def describes it (UnderWhere_Tanktop for
                // the tank top).
                p.tokens = NameTokens(piece.name ?? "");
                if (NoStandIn.Contains(piece.name ?? "") && piece.name.StartsWith("Hat"))
                {
                    // A NoStandIn hat needs "hat" too: the top hat's "top" alone was found in GrimWorld's topbanded helmet.
                    p.tokens = new[] { "hat" }.Concat(p.tokens).ToArray();
                }
                p.name = piece.name;   // the garment's own name, which Generic goes by
                p.namedOnly = NoStandIn.Contains(piece.name ?? "");
                p.mask = Masks.Contains(piece.name ?? "");
                standInProfiles.Add(p);
            }
            return standInProfiles;
        }
    }
}
